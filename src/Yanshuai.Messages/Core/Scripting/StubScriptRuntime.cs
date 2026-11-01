// Core/Scripting/StubScriptRuntime.cs —— 桌面用的假脚本运行时
//
// 它**不执行真 Python**，而是按脚本内容返回可预期的结果。
// 存在的意义：让"脚本工具"这条链路的每一环都能在桌面上测到 ——
//   工具被调用 → 审批 → 执行 → 超时 → 输出截断 → 错误回填给模型
//
// 认得的指令（写在脚本里当注释就能触发，方便测试）：
//   #stub:ok        正常输出
//   #stub:echo=xxx  原样回显 xxx
//   #stub:stderr    往 stderr 写东西
//   #stub:error     非零退出码
//   #stub:timeout   模拟超时
//   #stub:long      输出超长内容（测截断）
//   #stub:host=CALL 调一次宿主桥
// 没写指令时：把脚本原样回显（这样"脚本内容被正确传进来"也能被验证）。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Yanshuai.Messages.Core
{
    public sealed class StubScriptRuntime : IScriptRuntime
    {
        private readonly IScriptHostBridge _bridge;

        public StubScriptRuntime() : this(NoScriptHostBridge.Instance) { }

        public StubScriptRuntime(IScriptHostBridge bridge)
        {
            _bridge = bridge ?? NoScriptHostBridge.Instance;
        }

        public string Name { get { return "python-stub"; } }
        public bool IsReal { get { return false; } }
        public bool IsAvailable { get { return true; } }
        public string Version { get { return "桩运行时（不执行真实 Python）"; } }

        public async Task<ScriptResult> RunAsync(ScriptRequest request, CancellationToken ct)
        {
            var stopwatch = Stopwatch.StartNew();
            var result = new ScriptResult();

            if (request == null || string.IsNullOrWhiteSpace(request.Source))
            {
                return ScriptResult.Fail("empty_script", "脚本内容为空。");
            }

            var source = request.Source;
            var output = new StringBuilder();

            // ── 模拟超时 ──
            if (Contains(source, "#stub:timeout"))
            {
                int waitMs = Math.Min(2000, Math.Max(50, request.TimeoutSeconds * 1000));
                try
                {
                    await Task.Delay(waitMs, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }

                stopwatch.Stop();
                result.Ok = false;
                result.TimedOut = true;
                result.ExitCode = 124;
                result.DurationMs = stopwatch.ElapsedMilliseconds;
                result.StdErr = "执行超时。";
                return result;
            }

            // ── 模拟报错 ──
            if (Contains(source, "#stub:error"))
            {
                stopwatch.Stop();
                result.Ok = false;
                result.ExitCode = 1;
                result.DurationMs = stopwatch.ElapsedMilliseconds;
                result.StdErr = "Traceback (most recent call last):\n  File \"<script>\", line 1, in <module>\nZeroDivisionError: division by zero";
                return result;
            }

            // ── 回显 ──
            var echo = ExtractValue(source, "#stub:echo=");
            if (echo != null)
            {
                output.Append(echo);
            }

            // ── stderr ──
            if (Contains(source, "#stub:stderr"))
            {
                result.StdErr = "这是一条 stderr 输出。";
            }

            // ── 超长输出（测截断）──
            if (Contains(source, "#stub:long"))
            {
                for (int i = 0; i < 5000; i++)
                {
                    output.Append("第 ").Append(i).Append(" 行输出内容，用来测试截断。\n");
                }
            }

            // ── 调宿主桥 ──
            var hostCall = ExtractValue(source, "#stub:host=");
            if (!string.IsNullOrEmpty(hostCall))
            {
                var allowed = false;
                if (request.AllowedHostCalls != null)
                {
                    foreach (var name in request.AllowedHostCalls)
                    {
                        if (string.Equals(name, hostCall.Trim(), StringComparison.Ordinal)) { allowed = true; break; }
                    }
                }

                if (!allowed)
                {
                    result.StdErr = "权限错误：宿主没有开放能力 " + hostCall.Trim() + "。";
                    result.Ok = false;
                    result.ExitCode = 1;
                    stopwatch.Stop();
                    result.DurationMs = stopwatch.ElapsedMilliseconds;
                    return result;
                }

                try
                {
                    var bridged = await _bridge.CallAsync(hostCall.Trim(), "{}", ct).ConfigureAwait(false);
                    if (output.Length > 0) output.Append('\n');
                    output.Append(bridged);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    result.StdErr = "宿主调用失败。";
                    result.Ok = false;
                    result.ExitCode = 1;
                    stopwatch.Stop();
                    result.DurationMs = stopwatch.ElapsedMilliseconds;
                    return result;
                }
            }

            // ── 什么都没写：把脚本回显，便于确认内容传对了 ──
            if (output.Length == 0 && string.IsNullOrEmpty(result.StdErr))
            {
                output.Append("（桩运行时）已收到脚本，共 ")
                      .Append(source.Length).Append(" 个字符。");
            }

            stopwatch.Stop();

            // ── 输出截断（与真运行时同一套规则）──
            var text = output.ToString();
            int limit = request.MaxOutputChars > 0 ? request.MaxOutputChars : 20000;
            if (text.Length > limit)
            {
                text = text.Substring(0, limit);
                result.OutputTruncated = true;
            }

            result.StdOut = text;
            result.Ok = true;
            result.ExitCode = 0;
            result.DurationMs = stopwatch.ElapsedMilliseconds;
            return result;
        }

        private static bool Contains(string text, string needle)
        {
            return text != null && text.IndexOf(needle, StringComparison.Ordinal) >= 0;
        }

        /// <summary>取 "#key=value" 里 value 到行尾的部分。</summary>
        private static string ExtractValue(string text, string key)
        {
            if (string.IsNullOrEmpty(text)) return null;

            int index = text.IndexOf(key, StringComparison.Ordinal);
            if (index < 0) return null;

            int start = index + key.Length;
            int end = text.IndexOf('\n', start);
            if (end < 0) end = text.Length;

            return text.Substring(start, end - start).Trim();
        }
    }
}
