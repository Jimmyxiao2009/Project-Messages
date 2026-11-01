// Core/Tools/ScriptTools.cs —— 脚本执行工具
//
// run_python 是敏感工具（会执行代码、能读档案文件），所以：
//   1) 默认在设置里关着（ScriptEnabled），开了才出现在工具清单里；
//   2) 每次调用都要用户批准（SensitiveTools）；
//   3) 工作目录被夹在档案的 files 目录内，脚本拿不到别的路径；
//   4) 有超时，且输出按上限截断，避免把上下文撑爆。
//
// 这个工具不关心跑的是真 Python 还是桩 —— 那是 IScriptRuntime 的事。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Yanshuai.Messages.Core
{
    public sealed class RunPythonTool : ToolBase
    {
        public override string Name { get { return "run_python"; } }
        public override ToolGroup Group { get { return ToolGroup.Agent; } }

        public override string Description
        {
            get
            {
                return "执行一段 Python 脚本并返回输出。适合做精确计算、批量文本处理、解析结构化数据。"
                     + "脚本在档案的文件目录内运行，可以用相对路径读写该目录下的文件。"
                     + "不要用它做网络请求或长期运行的任务。";
            }
        }

        protected override void DescribeParams(ToolSchema schema)
        {
            schema.Param("code", "string", "要执行的 Python 代码", true);
            schema.Param("timeout_seconds", "integer", "超时秒数，默认 30，最大 120", false);
        }

        public override async Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext context, CancellationToken ct)
        {
            if (context == null) return ToolResult.Failure("内部错误：缺少执行上下文。");

            // 脚本开关已移除：能否执行 run_python 由对话页三档权限把关（PermissionService）。

            if (context.ScriptRuntime == null || !context.ScriptRuntime.IsAvailable)
            {
                return ToolResult.Failure(
                    "当前设备上没有可用的脚本运行时。",
                    "runtime_unavailable");
            }

            var code = ToolArguments.GetString(argumentsJson, "code", "");
            if (string.IsNullOrWhiteSpace(code))
            {
                return ToolResult.Failure("code 不能为空。", "empty_script");
            }

            int timeout = ToolArguments.GetInt(argumentsJson, "timeout_seconds", 30);
            if (timeout < 1) timeout = 1;
            if (timeout > 120) timeout = 120;

            // 预检工作目录。os.chdir 失败只在 Python 的 stderr 里，我们这边毫无感知；
            // 而 chdir 失败后所有相对路径的文件读写会落到进程默认目录 —— 在 UWP 上那是
            // 只读的安装目录，写操作会直接卡死到超时（真机上报过的"写文件超时"）。
            // 所以跑脚本前先把档案文件目录确保存在。
            try
            {
                if (context.Store != null && !string.IsNullOrEmpty(context.FilesRoot))
                {
                    await context.Store.EnsureDirectoryAsync(context.FilesRoot).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                return ToolResult.Failure("档案文件目录不可用：" + SafeMessage(ex), "workspace");
            }

            var request = new ScriptRequest
            {
                Source = code,
                WorkingDirectory = context.FilesRoot,
                TimeoutSeconds = timeout,
                MaxOutputChars = 20000,
                AllowedHostCalls = BuildAllowedCalls(context)
            };

            ScriptResult result;
            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeout + 5));   // 运行时自己的超时更早，这里只是兜底

                try
                {
                    result = await context.ScriptRuntime.RunAsync(request, timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return ToolResult.Failure(
                        "脚本执行超过 " + timeout + " 秒被终止。请检查是否有死循环或过重的计算。",
                        "timeout");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return ToolResult.Failure("脚本执行失败：" + SafeMessage(ex), "runtime_error");
                }
            }

            if (result == null)
            {
                return ToolResult.Failure("脚本运行时没有返回结果。", "no_result");
            }

            if (!result.Ok && !result.TimedOut)
            {
                // 失败也要把 stdout 带回去 —— 报错前的输出常常正是定位问题的关键
                var text = result.FormatForModel();
                return ToolResult.Failure(
                    "脚本执行失败（退出码 " + result.ExitCode.ToString(CultureInfo.InvariantCulture) + "）。\n\n" + text,
                    string.IsNullOrEmpty(result.ErrorCode) ? "script_failed" : result.ErrorCode);
            }

            var summary = "运行 " + result.DurationMs.ToString(CultureInfo.InvariantCulture) + " ms";
            if (result.TimedOut) summary = "超时";

            return ToolResult.Success(result.FormatForModel(), summary);
        }

        /// <summary>
        /// 脚本能调的宿主能力。第一版只开放记忆检索与当前时间 ——
        /// 都是只读的，脚本即使被注入也做不了破坏性动作。
        /// 设备类能力（发短信、拨号）刻意不开放给脚本。
        /// </summary>
        private static List<string> BuildAllowedCalls(ToolContext context)
        {
            var calls = new List<string>();
            if (context.Memory != null) calls.Add("memory.search");
            if (context.Clock != null) calls.Add("time.now");
            return calls;
        }

        private static string SafeMessage(Exception ex)
        {
            if (ex == null) return "未知错误";
            var message = ex.Message;
            if (string.IsNullOrEmpty(message)) return ex.GetType().Name;
            // 只取第一行：堆栈与多行内部信息不该原样喂给模型
            int newline = message.IndexOf('\n');
            return newline > 0 ? message.Substring(0, newline) : message;
        }
    }
}
