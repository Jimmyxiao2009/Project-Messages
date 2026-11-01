// tests/CoreTests/Feature9Tests.cs —— PyUwp 协议 + 用量日志接线
//
// PyUwpProtocol 是整条脚本链路里最会"静默出错"的一环：
// 字符串拼 Python 代码、再从合并输出里切出 stdout/stderr。
// 错位不会抛异常，只会让报错被当成正常结果，或者结果少一截。
// 所以这一层必须有单测（真 Python 只能在真机上验，但这段逻辑可以在桌面上验完）。

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Yanshuai.Messages.Core;

namespace Yanshuai.Messages.Tests
{
    public static class Feature9Tests
    {
        public static void Register()
        {
            RegisterPayload();
            RegisterSplit();
            RegisterTechLogWiring();
        }

        // ═════════════ 包装脚本 ═════════════
        private static void RegisterPayload()
        {
            Runner.Add("PyUwp 协议", "用户源码以 base64 传入，不破坏包装语法", delegate
            {
                // 这段源码里塞满了能炸掉字符串拼接的东西：三引号、反斜杠、换行、引号
                var nasty = "s = '''三引号\n里面的'''\np = \"C:\\\\path\\\\x\"\nprint(s, p)\n";
                var payload = PyUwpProtocol.BuildPayload(nasty, "");

                Check.Contains(payload, "base64.b64decode", "走 base64 解码");
                Check.False(payload.Contains("三引号"), "源码本体不出现在包装脚本里");

                // 把 base64 解回来应完全一致
                var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes(nasty));
                Check.Contains(payload, expected, "base64 内容正确");
            });

            Runner.Add("PyUwp 协议", "工作目录同样以 base64 传入", delegate
            {
                var payload = PyUwpProtocol.BuildPayload("print(1)", "C:\\data\\应用\\files");

                var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes("C:\\data\\应用\\files"));
                Check.Contains(payload, expected, "路径被编码");
                Check.Contains(payload, "os.chdir", "会切到工作目录");
            });

            Runner.Add("PyUwp 协议", "包装脚本捕获 stdout 与 stderr", delegate
            {
                var payload = PyUwpProtocol.BuildPayload("print(1)", "");

                Check.Contains(payload, "sys.stdout, sys.stderr = _out, _err", "重定向输出");
                Check.Contains(payload, "traceback.print_exc(file=_err)", "异常进 stderr");
                Check.Contains(payload, "sys.stdout, sys.stderr = _saved_out, _saved_err", "恢复原输出");
                Check.Contains(payload, "flush()", "结束时刷新");
            });

            Runner.Add("PyUwp 协议", "SystemExit 的退出码被带出来", delegate
            {
                var payload = PyUwpProtocol.BuildPayload("import sys; sys.exit(3)", "");

                Check.Contains(payload, "SystemExit", "处理 SystemExit");
                Check.Contains(payload, "isinstance(_e.code, int)", "只有整数退出码才用");
            });

            Runner.Add("PyUwp 协议", "空源码也能生成合法包装", delegate
            {
                var payload = PyUwpProtocol.BuildPayload(null, null);

                Check.True(payload.Length > 100, "生成了包装脚本");
                Check.Contains(payload, "exec(compile", "仍然会执行");
            });

            Runner.Add("PyUwp 协议", "包装脚本必须一次写完标记序列", delegate
            {
                var payload = PyUwpProtocol.BuildPayload("print(1)", "");
                var m = PyUwpProtocol.Marker;

                // 三段（stdout / stderr / code）必须在同一次 write 里。
                // 分两次 write 会在中间多出一个空片段，拆解时下标整体错一位，
                // stdout 与 stderr 会互换 —— 而且不会报任何错。这条钉住它。
                Check.Contains(payload, "_o + '" + m + "' + _e2 + '", "三段在同一次 write 里");
                Check.False(payload.Contains("write('" + m + "code="),
                    "不存在只写 code 的第二次 write");
            });

            Runner.Add("PyUwp 协议", "中文与 emoji 编码后能原样还原", delegate
            {
                var source = "print('你好，世界 😀')";
                var payload = PyUwpProtocol.BuildPayload(source, "");

                var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes(source));
                Check.Contains(payload, expected, "UTF-8 编码正确");
            });
        }

        // ═════════════ 输出拆解 ═════════════
        private static void RegisterSplit()
        {
            Runner.Add("PyUwp 协议", "正常输出被正确拆开", delegate
            {
                var combined = MakeCombined("结果 42", "", 0);
                var result = new ScriptResult();

                PyUwpProtocol.SplitOutput(combined, result);

                Check.Equal("结果 42", result.StdOut, "stdout");
                Check.Equal("", result.StdErr, "stderr 为空");
                Check.EqualInt(0, result.ExitCode, "退出码 0");
                Check.True(result.Ok, "标记为成功");
            });

            Runner.Add("PyUwp 协议", "stderr 与退出码被正确拆开", delegate
            {
                var combined = MakeCombined("", "Traceback:\n  ZeroDivisionError", 1);
                var result = new ScriptResult();

                PyUwpProtocol.SplitOutput(combined, result);

                Check.Equal("", result.StdOut, "stdout 为空");
                Check.Contains(result.StdErr, "ZeroDivisionError", "stderr 内容");
                Check.EqualInt(1, result.ExitCode, "退出码 1");
                Check.False(result.Ok, "标记为失败");
            });

            Runner.Add("PyUwp 协议", "多行中文输出完整保留", delegate
            {
                var sb = new StringBuilder();
                for (int i = 0; i < 50; i++) sb.Append("第 ").Append(i).Append(" 行：内容\n");
                var expected = sb.ToString();

                var result = new ScriptResult();
                PyUwpProtocol.SplitOutput(MakeCombined(expected, "警告信息", 0), result);

                Check.Equal(expected, result.StdOut, "多行内容一字不差");
                Check.Equal("警告信息", result.StdErr, "stderr 独立");
            });

            Runner.Add("PyUwp 协议", "输出里的分隔符字样不会造成错位", delegate
            {
                // 用户脚本自己打出标记字样 —— base64 之后不可能出现，这正是选它的原因
                var tricky = "这行里有 __PYUWP_SPLIT__ 字样，还有 __PYUWP_SPLIT__ 两次";
                var result = new ScriptResult();

                PyUwpProtocol.SplitOutput(MakeCombined(tricky, "", 0), result);

                Check.Equal(tricky, result.StdOut, "内容完整且没有错位");
            });

            Runner.Add("PyUwp 协议", "标记前的额外输出接到 stdout 前面", delegate
            {
                var combined = "Python 启动警告\n" + MakeCombined("正文", "", 0);
                var result = new ScriptResult();

                PyUwpProtocol.SplitOutput(combined, result);

                Check.Contains(result.StdOut, "Python 启动警告", "前置输出保留");
                Check.Contains(result.StdOut, "正文", "正文保留");
            });

            Runner.Add("PyUwp 协议", "标记缺失时全部当 stdout，不丢信息", delegate
            {
                var result = new ScriptResult();
                PyUwpProtocol.SplitOutput("没有标记的原始输出", result);

                Check.Equal("没有标记的原始输出", result.StdOut, "不丢内容");
            });

            Runner.Add("PyUwp 协议", "空输出不报错", delegate
            {
                var result = new ScriptResult();
                PyUwpProtocol.SplitOutput("", result);
                Check.Equal("", result.StdOut, "空 stdout");

                PyUwpProtocol.SplitOutput(null, result);
                Check.Equal("", result.StdOut, "null 也不炸");
            });

            Runner.Add("PyUwp 协议", "非零退出码判为失败", delegate
            {
                var result = new ScriptResult();
                PyUwpProtocol.SplitOutput(MakeCombined("部分输出", "出错了", 3), result);

                Check.False(result.Ok, "非零即失败");
                Check.EqualInt(3, result.ExitCode, "退出码原样带出");
                Check.Equal("部分输出", result.StdOut, "失败前的输出仍保留");
            });
        }

        // ═════════════ 用量日志接线 ═════════════
        private static void RegisterTechLogWiring()
        {
            Runner.Add("用量日志接线", "没有日志对象时不报错", delegate
            {
                var chat = BuildChat(out _, out _, out _);
                chat.TechLog = null;

                var result = new AgentRunResult { Outcome = AgentOutcome.Completed };
                var observer = new AgentObserver();

                // 没有档案 → 直接返回本地失败，不该因为 TechLog 为空而抛
                var run = chat.SendAsync("你好", null, observer, CancellationToken.None).Result;
                Check.False(run.Outcome == AgentOutcome.Completed, "缺档案时给出失败而不是异常");
            });

            Runner.Add("用量日志接线", "打开会话时轮次编号接上历史", delegate
            {
                var chat = BuildChat(out var workspace, out var profile, out _);

                var conversation = workspace.CreateConversation(profile, "测试");
                conversation.Messages.Add(new Message { Role = MessageRole.User, Content = "一" });
                conversation.Messages.Add(new Message { Role = MessageRole.Assistant, Content = "答一" });
                conversation.Messages.Add(new Message { Role = MessageRole.User, Content = "二" });

                chat.OpenProfileAsync(profile).Wait();
                chat.OpenConversationAsync(conversation).Wait();

                Check.EqualInt(2, chat.TurnCounter, "接上已有的两条用户消息");
            });

            Runner.Add("用量日志接线", "日志条目不含主机之外的 URL 细节", delegate
            {
                var store = new InMemoryFileStore();
                var log = new TokenTechLog(store, NullLogger.Instance);

                log.AppendAsync("test", "conv", new TechLogEntry
                {
                    Turn = 1,
                    Model = "gpt-4o",
                    Provider = "openai",
                    Host = "api.openai.com",
                    InputTokens = 100,
                    OutputTokens = 20
                }, CancellationToken.None).Wait();

                var raw = store.Peek(TokenTechLog.PathFor("test", "conv"));

                Check.Contains(raw, "api.openai.com", "记了主机");
                Check.False(raw.Contains("https://"), "没有完整 URL");
                Check.False(raw.Contains("Bearer"), "没有鉴权头");
            });

            Runner.Add("用量日志接线", "逐轮明细含模型与耗时", delegate
            {
                var entries = new List<TechLogEntry>
                {
                    new TechLogEntry { Turn = 1, Model = "claude-sonnet-4", InputTokens = 500, OutputTokens = 100, DurationMs = 2500, ToolCalls = 2, ModelCalls = 3 }
                };

                var text = TokenTechLog.Format(TokenTechLog.Summarize(entries), entries);

                Check.Contains(text, "claude-sonnet-4", "模型名");
                Check.Contains(text, "2.5s", "耗时带一位小数");
                Check.Contains(text, "工具调用 2 次", "工具次数");
            });
        }

        // ═════════════ 辅助 ═════════════
        /// <summary>按真实运行时产出的形状拼一段合并输出（与 BuildPayload 的写法一一对应）。</summary>
        private static string MakeCombined(string stdout, string stderr, int code)
        {
            var m = PyUwpProtocol.Marker;
            var outB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(stdout ?? ""));
            var errB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(stderr ?? ""));

            return m + outB64 + m + errB64 + m + "code=" + code + m;
        }

        private static ChatService BuildChat(out Workspace workspace, out Profile profile, out InMemoryFileStore store)
        {
            store = new InMemoryFileStore();
            workspace = new Workspace(store, new InMemorySecretStore(), NullLogger.Instance, SystemClock.Instance);
            workspace.InitializeAsync().Wait();

            profile = workspace.CreateProfileAsync("测试档案", "test").Result;

            return new ChatService(
                workspace, store, NullLogger.Instance, SystemClock.Instance,
                new SilentHttp(), null, null, DenyAllInteraction.Instance, ToolRegistry.CreateDefault());
        }

        private sealed class SilentHttp : IHttpTransport
        {
            public System.Threading.Tasks.Task<HttpResponseSpec> SendAsync(HttpRequestSpec request, CancellationToken ct)
            {
                return System.Threading.Tasks.Task.FromResult(new HttpResponseSpec { StatusCode = 200, Body = "{}" });
            }

            public System.Threading.Tasks.Task StreamLinesAsync(HttpRequestSpec request, Func<string, bool> onLine, CancellationToken ct)
            {
                return System.Threading.Tasks.Task.CompletedTask;
            }
        }
    }
}
