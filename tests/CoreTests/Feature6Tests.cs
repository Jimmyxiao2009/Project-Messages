// tests/CoreTests/Feature6Tests.cs —— 脚本运行时（v1.2）
//
// 桌面跑的是桩运行时，所以这里测的是**链路**而不是 Python 本身：
//   工具是否按开关出现 → 参数校验 → 审批 → 执行 → 超时 → 截断 → 失败回填
// 真 Python 只能在真机上验（PyUwp 是 ARM-only 原生库）。

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Yanshuai.Messages.Core;

namespace Yanshuai.Messages.Tests
{
    public static class Feature6Tests
    {
        public static void Register()
        {
            RegisterRuntime();
            RegisterTool();
            RegisterSafety();
        }

        // ═════════════ 运行时 ═════════════
        private static void RegisterRuntime()
        {
            Runner.Add("脚本运行时", "桩运行时正常执行并回显", delegate
            {
                var runtime = new StubScriptRuntime();
                var result = runtime.RunAsync(new ScriptRequest
                {
                    Source = "#stub:echo=计算结果是 42"
                }, CancellationToken.None).Result;

                Check.True(result.Ok, "执行成功");
                Check.EqualInt(0, result.ExitCode, "退出码 0");
                Check.Contains(result.StdOut, "42", "输出被带回");
                Check.False(result.TimedOut, "没有超时");
                Check.False(runtime.IsReal, "桩运行时 IsReal 为假（界面要据此提示用户）");
            });

            Runner.Add("脚本运行时", "空脚本被拒绝", delegate
            {
                var runtime = new StubScriptRuntime();
                var result = runtime.RunAsync(new ScriptRequest { Source = "   " }, CancellationToken.None).Result;

                Check.False(result.Ok, "应当失败");
                Check.Equal("empty_script", result.ErrorCode, "错误码");
            });

            Runner.Add("脚本运行时", "stderr 与 stdout 分开返回", delegate
            {
                var runtime = new StubScriptRuntime();
                var result = runtime.RunAsync(new ScriptRequest
                {
                    Source = "#stub:echo=正常输出\n#stub:stderr"
                }, CancellationToken.None).Result;

                Check.True(result.Ok, "成功");
                Check.Contains(result.StdOut, "正常输出", "stdout 有内容");
                Check.Contains(result.StdErr, "stderr", "stderr 单独存放");
                Check.False(result.StdOut.Contains("stderr"), "stdout 里不混 stderr");
            });

            Runner.Add("脚本运行时", "非零退出码", delegate
            {
                var runtime = new StubScriptRuntime();
                var result = runtime.RunAsync(new ScriptRequest
                {
                    Source = "#stub:error"
                }, CancellationToken.None).Result;

                Check.False(result.Ok, "失败");
                Check.EqualInt(1, result.ExitCode, "退出码 1");
                Check.Contains(result.StdErr, "ZeroDivisionError", "带回真实报错");
            });

            Runner.Add("脚本运行时", "超时被标记", delegate
            {
                var runtime = new StubScriptRuntime();
                var result = runtime.RunAsync(new ScriptRequest
                {
                    Source = "#stub:timeout",
                    TimeoutSeconds = 1
                }, CancellationToken.None).Result;

                Check.False(result.Ok, "失败");
                Check.True(result.TimedOut, "标记为超时");
                Check.EqualInt(124, result.ExitCode, "退出码 124（与 POSIX timeout 一致）");
            });

            Runner.Add("脚本运行时", "超长输出被截断并标记", delegate
            {
                var runtime = new StubScriptRuntime();
                var result = runtime.RunAsync(new ScriptRequest
                {
                    Source = "#stub:long",
                    MaxOutputChars = 500
                }, CancellationToken.None).Result;

                Check.True(result.Ok, "成功");
                Check.True(result.OutputTruncated, "标记为已截断");
                Check.EqualInt(500, result.StdOut.Length, "截到上限");
            });

            Runner.Add("脚本运行时", "超时说明会写进给模型的文本", delegate
            {
                var runtime = new StubScriptRuntime();
                var result = runtime.RunAsync(new ScriptRequest
                {
                    Source = "#stub:timeout",
                    TimeoutSeconds = 1
                }, CancellationToken.None).Result;

                var text = result.FormatForModel();
                Check.Contains(text, "超时", "模型能看出是超时");
                Check.Contains(text, "死循环", "给出可执行的排查方向");
            });

            Runner.Add("脚本运行时", "没有输出时给出占位说明", delegate
            {
                var result = new ScriptResult { Ok = true };
                Check.Contains(result.FormatForModel(), "没有输出", "不留空白让模型猜");
            });
        }

        // ═════════════ 工具 ═════════════
        private static void RegisterTool()
        {
            Runner.Add("run_python", "默认不在工具清单里", delegate
            {
                var registry = ToolRegistry.CreateDefault();
                var profile = new Profile { Id = "p1" };   // ScriptEnabled 默认假

                var tools = registry.Resolve(profile);
                Check.False(HasTool(tools, "run_python"), "默认不出现");

                profile.Tools.ScriptEnabled = true;
                tools = registry.Resolve(profile);
                Check.True(HasTool(tools, "run_python"), "开关打开后出现");
            });

            Runner.Add("run_python", "显式列进 Enabled 也绕不过开关", delegate
            {
                var registry = ToolRegistry.CreateDefault();
                var profile = new Profile { Id = "p1" };
                profile.Tools.Enabled.Add("run_python");   // 但 ScriptEnabled 仍为假

                var tools = registry.Resolve(profile);
                Check.False(HasTool(tools, "run_python"), "仍然不出现");
            });

            Runner.Add("run_python", "是敏感工具需要审批", delegate
            {
                Check.True(SensitiveTools.IsSensitive("run_python"), "在审批白名单里");
            });

            Runner.Add("run_python", "开关关闭时直接拒绝执行", delegate
            {
                var tool = new RunPythonTool();
                var context = BuildContext(false, new StubScriptRuntime());

                var result = tool.InvokeAsync("{\"code\":\"print(1)\"}", context, CancellationToken.None).Result;

                Check.False(result.Ok, "拒绝");
                Check.Equal("script_disabled", result.ErrorCode, "错误码");
                Check.Contains(result.Content, "设置", "告诉用户去哪里打开");
            });

            Runner.Add("run_python", "空 code 被拒绝", delegate
            {
                var tool = new RunPythonTool();
                var context = BuildContext(true, new StubScriptRuntime());

                var result = tool.InvokeAsync("{\"code\":\"\"}", context, CancellationToken.None).Result;

                Check.False(result.Ok, "拒绝");
                Check.Equal("empty_script", result.ErrorCode, "错误码");
            });

            Runner.Add("run_python", "超时上限被夹到 120 秒", delegate
            {
                var tool = new RunPythonTool();
                var context = BuildContext(true, new StubScriptRuntime());

                // 传一个荒谬的超时，不该真的等那么久
                var started = DateTimeOffset.UtcNow;
                var result = tool.InvokeAsync(
                    "{\"code\":\"#stub:echo=ok\",\"timeout_seconds\":99999}",
                    context, CancellationToken.None).Result;
                var elapsed = DateTimeOffset.UtcNow - started;

                Check.True(result.Ok, "成功");
                Check.True(elapsed.TotalSeconds < 10, "没有真的等 99999 秒");
            });

            Runner.Add("run_python", "脚本失败时把输出一起带回给模型", delegate
            {
                var tool = new RunPythonTool();
                var context = BuildContext(true, new StubScriptRuntime());

                var result = tool.InvokeAsync("{\"code\":\"#stub:error\"}", context, CancellationToken.None).Result;

                Check.False(result.Ok, "失败");
                Check.Contains(result.Content, "ZeroDivisionError", "报错内容带回");
                Check.Contains(result.Content, "退出码 1", "退出码带回");
            });

            Runner.Add("run_python", "超长输出经工具后仍在上下文预算内", delegate
            {
                var tool = new RunPythonTool();
                var context = BuildContext(true, new StubScriptRuntime());

                var result = tool.InvokeAsync("{\"code\":\"#stub:long\"}", context, CancellationToken.None).Result;

                Check.True(result.Ok, "成功");
                Check.True(result.Content.Length <= 21000, "输出被夹住，不会撑爆上下文");
                Check.Contains(result.Content, "截断", "明确告诉模型被截断了");
            });

            Runner.Add("run_python", "没有运行时时给出可读错误", delegate
            {
                var tool = new RunPythonTool();
                var context = BuildContext(true, null);

                var result = tool.InvokeAsync("{\"code\":\"print(1)\"}", context, CancellationToken.None).Result;

                Check.False(result.Ok, "拒绝");
                Check.Equal("runtime_unavailable", result.ErrorCode, "错误码");
            });

            Runner.Add("run_python", "运行时会收到工作目录与输出上限", delegate
            {
                var spy = new SpyRuntime();
                var tool = new RunPythonTool();
                var context = BuildContext(true, spy);
                context.ProfileSlug = "test-profile";

                tool.InvokeAsync("{\"code\":\"print(1)\",\"timeout_seconds\":7}", context, CancellationToken.None).Wait();

                Check.True(spy.Last != null, "运行时被调用");
                Check.Equal(7, spy.Last.TimeoutSeconds, "超时被透传");
                Check.Equal(20000, spy.Last.MaxOutputChars, "输出上限被设置");
                Check.Contains(spy.Last.WorkingDirectory, "files", "工作目录落在档案 files 下");
            });
        }

        // ═════════════ 宿主桥安全 ═════════════
        private static void RegisterSafety()
        {
            Runner.Add("宿主桥", "未开放的能力被拒绝", delegate
            {
                var runtime = new StubScriptRuntime();
                var result = runtime.RunAsync(new ScriptRequest
                {
                    Source = "#stub:host=device.send_sms",
                    AllowedHostCalls = new List<string> { "time.now" }
                }, CancellationToken.None).Result;

                Check.False(result.Ok, "拒绝");
                Check.Contains(result.StdErr, "权限", "说明是权限问题");
            });

            Runner.Add("宿主桥", "开放的能力可以调用", delegate
            {
                var bridge = new ScriptHostBridge(null, SystemClock.Instance, new Profile { Id = "p1" });
                var runtime = new StubScriptRuntime(bridge);

                var result = runtime.RunAsync(new ScriptRequest
                {
                    Source = "#stub:host=time.now",
                    AllowedHostCalls = new List<string> { "time.now" }
                }, CancellationToken.None).Result;

                Check.True(result.Ok, "成功");
                Check.True(result.StdOut.Length >= 10, "拿到了时间文本");
            });

            Runner.Add("宿主桥", "只开放只读能力", delegate
            {
                var bridge = new ScriptHostBridge(null, SystemClock.Instance, new Profile { Id = "p1" });

                foreach (var call in bridge.AvailableCalls)
                {
                    Check.False(call.StartsWith("device.", StringComparison.Ordinal),
                        "不开放设备能力：" + call);
                    Check.False(call.StartsWith("file.", StringComparison.Ordinal),
                        "不开放文件写入：" + call);
                }
            });

            Runner.Add("宿主桥", "未知能力给出明确错误", delegate
            {
                var bridge = new ScriptHostBridge(null, SystemClock.Instance, new Profile { Id = "p1" });
                var text = bridge.CallAsync("nonsense.do_thing", "{}", CancellationToken.None).Result;

                Check.Contains(text, "不支持", "明确说不支持");
            });
        }

        // ═════════════ 辅助 ═════════════
        private static bool HasTool(List<ITool> tools, string name)
        {
            foreach (var tool in tools)
            {
                if (string.Equals(tool.Name, name, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static ToolContext BuildContext(bool scriptEnabled, IScriptRuntime runtime)
        {
            return new ToolContext
            {
                Profile = new Profile { Id = "p1", Slug = "test-profile" },
                ProfileSlug = "test-profile",
                Store = new InMemoryFileStore(),
                Log = NullLogger.Instance,
                Clock = SystemClock.Instance,
                ScriptEnabled = scriptEnabled,
                ScriptRuntime = runtime,
                ScriptHost = NoScriptHostBridge.Instance
            };
        }

        /// <summary>记录最后一次请求的假运行时。</summary>
        private sealed class SpyRuntime : IScriptRuntime
        {
            public ScriptRequest Last;
            public string Name { get { return "spy"; } }
            public bool IsReal { get { return false; } }
            public bool IsAvailable { get { return true; } }
            public string Version { get { return "spy"; } }

            public Task<ScriptResult> RunAsync(ScriptRequest request, CancellationToken ct)
            {
                Last = request;
                return Task.FromResult(new ScriptResult { Ok = true, StdOut = "ok" });
            }
        }
    }
}
