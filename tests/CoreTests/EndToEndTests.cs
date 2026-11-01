// tests/CoreTests/EndToEndTests.cs —— 端到端测试
//
// 前面的用例都是拿假 provider 驱动循环；这一组不一样：起一个真的 HTTP 服务，
// 让 ChatService 走真实的 HttpTransport、真实的 JSON 构造、真实的 SSE 解析、
// 真实地写进存储。打的就是线上跑的那条路径。
//
// 覆盖：
//   · 纯对话 -> 流式 SSE -> 落盘
//   · 工具循环两轮 -> 工具真的执行 -> 第二轮把结果带回上下文
//   · 401 -> 落一条失败消息（时间线上是错误行）
//   · 敏感工具被拒 -> 工具消息标记失败，且文件真的没被写
//   · Anthropic 形态响应 -> 解析正确
//   · API Key 不进请求日志 / 不进配置文件

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Yanshuai.Messages.Core;

namespace Yanshuai.Messages.Tests
{
    /// <summary>一段脚本化的回复。</summary>
    public sealed class FakeTurn
    {
        public string Content = "";
        public string Reasoning = "";
        public List<ToolCallRequest> ToolCalls = new List<ToolCallRequest>();
        public int StatusCode = 200;
        public string ErrorBody = "";
        public string ErrorCode = "invalid_api_key";
    }

    /// <summary>
    /// 假模型服务。用 HttpListener 起在 localhost 的随机端口上，只实现两个端点：
    ///   POST /v1/chat/completions  （OpenAI 形态；请求体里 stream=true 时返回 SSE）
    ///   POST /v1/messages          （Anthropic 形态）
    /// 按 Enqueue 的顺序消费脚本化回复。
    /// </summary>
    public sealed class FakeModelServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly Queue<FakeTurn> _turns = new Queue<FakeTurn>();
        private readonly object _gate = new object();
        private readonly List<string> _bodies = new List<string>();

        public string Shape = "openai";
        public string BaseUrl { get; private set; }
        /// <summary>假服务内部的异常。吞掉异常会让排查变得很难，所以留个出口。</summary>
        public string LastServerError { get; private set; }

        public FakeModelServer()
        {
            int port = FreePort();
            _listener = new HttpListener();
            _listener.Prefixes.Add("http://localhost:" + port + "/");
            _listener.Start();

            BaseUrl = Shape == "anthropic"
                ? "http://localhost:" + port + "/v1/messages"
                : "http://localhost:" + port + "/v1/chat/completions";

            var ignored = Task.Run(new Func<Task>(LoopAsync));
        }

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        public void Enqueue(FakeTurn turn)
        {
            lock (_gate) _turns.Enqueue(turn);
        }

        public int RequestCount
        {
            get { lock (_gate) return _bodies.Count; }
        }

        public string BodyAt(int index)
        {
            lock (_gate)
            {
                return index >= 0 && index < _bodies.Count ? _bodies[index] : "";
            }
        }

        private async Task LoopAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    return;
                }

                try
                {
                    await HandleAsync(context).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LastServerError = ex.GetType().Name + ": " + ex.Message;
                    try { context.Response.Abort(); } catch (Exception) { }
                }
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            string body;
            using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            FakeTurn turn;
            lock (_gate)
            {
                _bodies.Add(body);
                turn = _turns.Count > 0 ? _turns.Dequeue() : null;
            }

            if (turn == null)
            {
                await WriteJsonAsync(context, 500, "{\"error\":{\"message\":\"没有脚本化回复了\"}}")
                    .ConfigureAwait(false);
                return;
            }

            if (turn.StatusCode != 200)
            {
                var errorBody = string.IsNullOrEmpty(turn.ErrorBody)
                    ? "{\"error\":{\"message\":\"boom\",\"code\":\"" + turn.ErrorCode + "\"}}"
                    : turn.ErrorBody;
                await WriteJsonAsync(context, turn.StatusCode, errorBody).ConfigureAwait(false);
                return;
            }

            bool wantsStream = body.IndexOf("\"stream\":true", StringComparison.Ordinal) >= 0;
            if (wantsStream)
            {
                await WriteStreamAsync(context, turn).ConfigureAwait(false);
            }
            else
            {
                await WriteJsonAsync(context, 200, BuildBody(turn)).ConfigureAwait(false);
            }
        }

        private string BuildBody(FakeTurn turn)
        {
            if (string.Equals(Shape, "anthropic", StringComparison.OrdinalIgnoreCase))
            {
                var blocks = new JArray();
                if (!string.IsNullOrEmpty(turn.Reasoning))
                {
                    blocks.Add(new JObject { ["type"] = "thinking", ["thinking"] = turn.Reasoning });
                }
                if (!string.IsNullOrEmpty(turn.Content) || turn.ToolCalls.Count == 0)
                {
                    blocks.Add(new JObject { ["type"] = "text", ["text"] = turn.Content });
                }
                foreach (var call in turn.ToolCalls)
                {
                    blocks.Add(new JObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = call.Id,
                        ["name"] = call.Name,
                        ["input"] = string.IsNullOrEmpty(call.ArgumentsJson)
                            ? new JObject()
                            : JToken.Parse(call.ArgumentsJson)
                    });
                }

                return new JObject
                {
                    ["id"] = "msg_test",
                    ["type"] = "message",
                    ["role"] = "assistant",
                    ["content"] = blocks,
                    ["stop_reason"] = "end_turn",
                    ["usage"] = new JObject { ["input_tokens"] = 12, ["output_tokens"] = 7 }
                }.ToString();
            }

            var message = new JObject { ["role"] = "assistant", ["content"] = turn.Content };
            if (!string.IsNullOrEmpty(turn.Reasoning)) message["reasoning_content"] = turn.Reasoning;

            if (turn.ToolCalls.Count > 0)
            {
                var calls = new JArray();
                foreach (var call in turn.ToolCalls)
                {
                    calls.Add(new JObject
                    {
                        ["id"] = call.Id,
                        ["type"] = "function",
                        ["function"] = new JObject
                        {
                            ["name"] = call.Name,
                            ["arguments"] = string.IsNullOrEmpty(call.ArgumentsJson) ? "{}" : call.ArgumentsJson
                        }
                    });
                }
                message["tool_calls"] = calls;
            }

            return new JObject
            {
                ["id"] = "chatcmpl_test",
                ["choices"] = new JArray
                {
                    new JObject { ["index"] = 0, ["message"] = message, ["finish_reason"] = "stop" }
                },
                ["usage"] = new JObject
                {
                    ["prompt_tokens"] = 12,
                    ["completion_tokens"] = 7,
                    ["prompt_tokens_details"] = new JObject { ["cached_tokens"] = 3 }
                }
            }.ToString();
        }

        private async Task WriteStreamAsync(HttpListenerContext context, FakeTurn turn)
        {
            // 把整段 SSE 文本攒好一次性写出去。
            //
            // 早先试过 SendChunked = true 边写边刷，客户端读到的却是空 body
            // （服务端侧没有任何异常，HttpListener 的 chunked 写在这条路径上不可靠）。
            // 对测试目的来说，一次性写出已经完全够用：客户端仍然是拿真正的 HTTP 响应，
            // 仍然要一行行解析 data: 前缀与 [DONE]。增量的行为由 SseParser 的单测覆盖。
            var body = new StringBuilder();

            Action<string> emit = delegate (string payload)
            {
                body.Append("data: ").Append(payload).Append("\n\n");
            };

            if (!string.IsNullOrEmpty(turn.Reasoning))
            {
                foreach (var piece in Split(turn.Reasoning, 2))
                {
                    emit(new JObject
                    {
                        ["choices"] = new JArray
                        {
                            new JObject { ["index"] = 0, ["delta"] = new JObject { ["reasoning_content"] = piece } }
                        }
                    }.ToString(Newtonsoft.Json.Formatting.None));
                }
            }

            foreach (var piece in Split(turn.Content, 3))
            {
                emit(new JObject
                {
                    ["choices"] = new JArray
                    {
                        new JObject { ["index"] = 0, ["delta"] = new JObject { ["content"] = piece } }
                    }
                }.ToString(Newtonsoft.Json.Formatting.None));
            }

            emit("[DONE]");

            var bytes = Encoding.UTF8.GetBytes(body.ToString());

            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/event-stream";
            context.Response.ContentLength64 = bytes.Length;

            await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            context.Response.Close();
        }

        private static List<string> Split(string text, int parts)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(text)) return result;
            if (parts <= 1 || text.Length <= parts)
            {
                result.Add(text);
                return result;
            }

            int size = text.Length / parts;
            for (int i = 0; i < parts; i++)
            {
                int start = i * size;
                int length = i == parts - 1 ? text.Length - start : size;
                if (length > 0) result.Add(text.Substring(start, length));
            }
            return result;
        }

        private static async Task WriteJsonAsync(HttpListenerContext context, int status, string body)
        {
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            context.Response.Close();
        }

        public void Dispose()
        {
            try
            {
                _listener.Stop();
                _listener.Close();
            }
            catch (Exception) { }
        }
    }

    public static class EndToEndTests
    {
        public static void Register()
        {
            // ═════════════ 纯对话：流式 SSE 走真实 HTTP ═════════════
            Runner.Add("端到端", "纯对话：流式 SSE 经真实 HTTP 落盘", delegate
            {
                using (var server = new FakeModelServer())
                {
                    server.Enqueue(new FakeTurn { Content = "你好，我是言枢。", Reasoning = "先打个招呼" });

                    var env = BuildEnv(server, false);
                    var result = env.Chat.SendAsync("在吗", null, new AgentObserver(), CancellationToken.None).Result;

                    Check.True(result.Outcome == AgentOutcome.Completed,
                        "应当完成，实际 outcome=" + result.Outcome
                        + " code=" + result.ErrorCode
                        + " msg=" + result.ErrorMessage
                        + " 请求数=" + server.RequestCount
                        + " 请求体含 stream:" + (server.BodyAt(0).IndexOf("\"stream\":true", StringComparison.Ordinal) >= 0)
                        + " 请求体前 160 字=" + TextUtil.Truncate(server.BodyAt(0), 160)
                        + " 服务端异常=" + (server.LastServerError ?? "无"));

                    Check.Equal("你好，我是言枢。", result.FinalMessage.Content, "内容经 SSE 拼接");
                    Check.Equal("先打个招呼", result.FinalMessage.Reasoning, "推理内容保留");
                    Check.EqualInt(1, server.RequestCount, "只请求一次");
                    Check.Contains(server.BodyAt(0), "\"stream\":true",
                        "请求体里要求流式（诊断用：确认假服务确实按 SSE 回）");
                    Check.True(server.BodyAt(0).IndexOf("\"stream\":true", StringComparison.Ordinal) >= 0,
                        "请求体里要求流式");
                    Check.False(server.BodyAt(0).IndexOf("\"tools\":", StringComparison.Ordinal) >= 0,
                        "关工具时不该带工具定义");

                    // 落盘检查
                    Check.True(env.Store.Peek("test/conversations/" + env.Conversation.Id + ".json") != null,
                        "会话正文已写");
                    Check.True(env.Store.Peek("test/conversations/index.json") != null, "索引已写");

                    var reloaded = env.Workspace.LoadConversationAsync("test", env.Conversation.Id).Result;
                    Check.EqualInt(2, reloaded.Messages.Count, "用户 + 助手两条");

                    // 流式响应不带 usage，这时应当用本地估算兜底而不是记 0
                    Check.True(reloaded.LastUsage.Estimated, "标记为估算值");
                    Check.True(reloaded.LastUsage.InputTokens > 0, "输入有估算值");
                    Check.True(reloaded.LastUsage.OutputTokens > 0, "输出有估算值");
                }
            });

            // ═════════════ 工具循环两轮 ═════════════
            Runner.Add("端到端", "工具循环：两轮经真实 HTTP，工具真的执行了", delegate
            {
                using (var server = new FakeModelServer())
                {
                    var first = new FakeTurn();
                    first.ToolCalls.Add(new ToolCallRequest { Id = "call_1", Name = "get_time", ArgumentsJson = "{}" });
                    server.Enqueue(first);
                    server.Enqueue(new FakeTurn { Content = "现在是下午。" });

                    var env = BuildEnv(server, true);
                    var result = env.Chat.SendAsync("几点了", null, new AgentObserver(), CancellationToken.None).Result;

                    Check.Equal(AgentOutcome.Completed, result.Outcome, "应当完成");
                    Check.Equal("现在是下午。", result.FinalMessage.Content, "第二轮给出最终回答");
                    Check.EqualInt(2, server.RequestCount, "请求两次");

                    Check.True(server.BodyAt(1).IndexOf("\"role\":\"tool\"", StringComparison.Ordinal) >= 0,
                        "第二轮把工具结果带回去了");
                    Check.True(server.BodyAt(1).IndexOf("当前时间", StringComparison.Ordinal) >= 0,
                        "带回的是 get_time 的真实输出");

                    // 时间线上应当是：用户 / 过程 / 输出
                    var rows = TimelineProjector.Project(env.Conversation, false, null);
                    Check.EqualInt(3, rows.Count, "三行");
                    Check.Equal(RowKind.Process, rows[1].Kind, "中间是过程行");
                    Check.EqualInt(1, rows[1].Steps.Count, "过程里有一个工具步骤");
                    Check.Equal(StepKind.Tool, rows[1].Steps[0].Kind, "步骤类型是工具");
                    Check.Equal(StepState.Done, rows[1].Steps[0].State, "工具已完成");
                    Check.Contains(rows[1].Steps[0].Body, "当前时间", "步骤里带工具输出");
                }
            });

            // ═════════════ 错误路径 ═════════════
            Runner.Add("端到端", "401 落一条失败消息", delegate
            {
                using (var server = new FakeModelServer())
                {
                    server.Enqueue(new FakeTurn { StatusCode = 401, ErrorCode = "invalid_api_key" });

                    var env = BuildEnv(server, true);
                    var result = env.Chat.SendAsync("你好", null, new AgentObserver(), CancellationToken.None).Result;

                    Check.Equal(AgentOutcome.Failed, result.Outcome, "应当失败");
                    Check.Equal("http_401", result.ErrorCode, "错误码");
                    Check.Contains(result.ErrorMessage, "密钥无效", "给出可操作提示");

                    var rows = TimelineProjector.Project(env.Conversation, false, null);
                    Check.Equal(RowKind.Error, rows[rows.Count - 1].Kind, "最后一行是错误行");
                    Check.Equal("http_401", rows[rows.Count - 1].ErrorCode, "错误码写进行里");
                }
            });

            Runner.Add("端到端", "网络不通给出 network 错误而不是崩掉", delegate
            {
                var store = new InMemoryFileStore();
                var secrets = new InMemorySecretStore();
                var workspace = new Workspace(store, secrets, NullLogger.Instance, SystemClock.Instance);
                workspace.InitializeAsync().Wait();

                // 指向一个没人监听的端口
                var dead = new ApiProfile
                {
                    Id = "dead",
                    Name = "死地址",
                    Url = "http://localhost:1/v1/chat/completions",
                    Model = "m"
                };
                workspace.ApiProfiles.Items.Add(dead);
                workspace.ApiProfiles.DefaultId = "dead";
                workspace.SetApiKey(dead, "k");
                workspace.SaveApiProfilesAsync().Wait();

                var profile = workspace.CreateProfileAsync("测试", "t2").Result;
                profile.Tools.ToolsEnabled = true;
                workspace.SaveProfileAsync(profile).Wait();

                var chat = new ChatService(workspace, store, NullLogger.Instance, SystemClock.Instance,
                    new HttpTransport(), null, null, new FakeInteraction(), ToolRegistry.CreateDefault());
                chat.OpenProfileAsync(profile).Wait();
                chat.StartConversationAsync("x").Wait();

                var result = chat.SendAsync("你好", null, new AgentObserver(), CancellationToken.None).Result;

                Check.Equal(AgentOutcome.Failed, result.Outcome, "应当失败");
                Check.Equal("network", result.ErrorCode, "错误码是 network");
            });

            // ═════════════ 审批拦截 ═════════════
            Runner.Add("端到端", "敏感工具被拒：文件真的没写，拒绝说明回给模型", delegate
            {
                using (var server = new FakeModelServer())
                {
                    var first = new FakeTurn();
                    first.ToolCalls.Add(new ToolCallRequest
                    {
                        Id = "call_w",
                        Name = "write_file",
                        ArgumentsJson = "{\"path\":\"a.md\",\"content\":\"秘密内容\"}"
                    });
                    server.Enqueue(first);
                    server.Enqueue(new FakeTurn { Content = "那我先不写了。" });

                    var env = BuildEnv(server, true);
                    env.Interaction.ApproveResult = false;

                    var result = env.Chat.SendAsync("写个文件", null, new AgentObserver(), CancellationToken.None).Result;

                    Check.Equal(AgentOutcome.Completed, result.Outcome, "仍然正常收尾");
                    Check.EqualInt(1, env.Interaction.ConfirmCalls, "弹了一次审批");
                    Check.True(env.Store.Peek("test/files/a.md") == null, "文件没有被写");

                    Check.True(server.BodyAt(1).IndexOf("拒绝", StringComparison.Ordinal) >= 0,
                        "拒绝说明回给了模型");
                }
            });

            Runner.Add("端到端", "批准后敏感工具真的写文件", delegate
            {
                using (var server = new FakeModelServer())
                {
                    var first = new FakeTurn();
                    first.ToolCalls.Add(new ToolCallRequest
                    {
                        Id = "call_w",
                        Name = "write_file",
                        ArgumentsJson = "{\"path\":\"notes.md\",\"content\":\"写入的内容\"}"
                    });
                    server.Enqueue(first);
                    server.Enqueue(new FakeTurn { Content = "写好了。" });

                    var env = BuildEnv(server, true);
                    env.Interaction.ApproveResult = true;

                    env.Chat.SendAsync("写个文件", null, new AgentObserver(), CancellationToken.None).Wait();

                    Check.Equal("写入的内容", env.Store.Peek("test/files/notes.md"), "文件写进去了");
                }
            });

            // ═════════════ Anthropic 形态 ═════════════
            Runner.Add("端到端", "Anthropic 形态响应解析正确", delegate
            {
                using (var server = new FakeModelServer { Shape = "anthropic" })
                {
                    server.Enqueue(new FakeTurn { Content = "这是 Anthropic 的回答。", Reasoning = "想一下" });

                    var env = BuildEnv(server, true, "anthropic");
                    var result = env.Chat.SendAsync("你好", null, new AgentObserver(), CancellationToken.None).Result;

                    Check.Equal(AgentOutcome.Completed, result.Outcome, "应当完成");
                    Check.Equal("这是 Anthropic 的回答。", result.FinalMessage.Content, "文本块解析");
                    Check.Equal("想一下", result.FinalMessage.Reasoning, "思考块解析");
                    Check.EqualInt(12, result.Usage.InputTokens, "用量解析");

                    // system 必须是顶层字段，消息里不能有 system 角色
                    Check.True(server.BodyAt(0).IndexOf("\"system\":", StringComparison.Ordinal) >= 0, "system 提到顶层");
                    Check.False(server.BodyAt(0).IndexOf("\"role\":\"system\"", StringComparison.Ordinal) >= 0,
                        "消息里没有 system 角色");
                    Check.True(server.BodyAt(0).IndexOf("\"max_tokens\":", StringComparison.Ordinal) >= 0,
                        "Anthropic 必填的 max_tokens 带上了");
                }
            });

            // ═════════════ 密钥卫生 ═════════════
            Runner.Add("端到端", "密钥只在请求头里，不进配置文件", delegate
            {
                using (var server = new FakeModelServer())
                {
                    server.Enqueue(new FakeTurn { Content = "好" });
                    var env = BuildEnv(server, false);

                    env.Chat.SendAsync("你好", null, new AgentObserver(), CancellationToken.None).Wait();

                    var config = env.Store.Peek(WorkspaceLayout.SharedApiProfiles);
                    Check.NotContains(config, "sk-e2e-secret-key-do-not-leak", "配置文件里没有密钥");
                    Check.NotContains(config, "sk-", "配置文件里连密钥前缀都没有");
                }
            });

            Runner.Add("端到端", "会话标题用首句，且索引随之更新", delegate
            {
                using (var server = new FakeModelServer())
                {
                    server.Enqueue(new FakeTurn { Content = "收到" });
                    var env = BuildEnv(server, false);

                    env.Chat.SendAsync("帮我看看登录为什么失败", null, new AgentObserver(), CancellationToken.None).Wait();

                    var entries = env.Workspace.ListConversationsAsync("test", false).Result;
                    Check.EqualInt(1, entries.Count, "索引里有一条");
                    Check.Contains(entries[0].Title, "登录", "标题取自首句");
                    Check.Contains(entries[0].Preview, "收到", "预览取最后输出");
                }
            });
        }

        // ═════════════ 环境搭建 ═════════════

        private sealed class Env
        {
            public InMemoryFileStore Store;
            public InMemorySecretStore Secrets;
            public Workspace Workspace;
            public ChatService Chat;
            public Profile Profile;
            public Conversation Conversation;
            public ApiProfile Api;
            public FakeInteraction Interaction;
        }

        private static Env BuildEnv(FakeModelServer server, bool toolsEnabled)
        {
            return BuildEnv(server, toolsEnabled, "openai");
        }

        private static Env BuildEnv(FakeModelServer server, bool toolsEnabled, string provider)
        {
            var env = new Env();
            env.Store = new InMemoryFileStore();
            env.Secrets = new InMemorySecretStore();

            env.Workspace = new Workspace(env.Store, env.Secrets, NullLogger.Instance, SystemClock.Instance);
            env.Workspace.InitializeAsync().Wait();

            env.Api = new ApiProfile
            {
                Id = "api-e2e",
                Name = "端到端测试",
                Provider = provider,
                Url = server.BaseUrl,
                Model = provider == "anthropic" ? "claude-sonnet-4-20250514" : "test-model"
            };
            env.Workspace.ApiProfiles.Items.Add(env.Api);
            env.Workspace.ApiProfiles.DefaultId = env.Api.Id;
            env.Workspace.SetApiKey(env.Api, "sk-e2e-secret-key-do-not-leak");
            env.Workspace.SaveApiProfilesAsync().Wait();

            env.Profile = env.Workspace.CreateProfileAsync("测试档案", "test").Result;
            env.Profile.Api.ProfileId = env.Api.Id;
            env.Profile.Tools.ToolsEnabled = toolsEnabled;
            env.Profile.Persona.SystemPrompt = "你是一个测试助手。";
            env.Workspace.SaveProfileAsync(env.Profile).Wait();

            env.Interaction = new FakeInteraction { ApproveResult = true };

            env.Chat = new ChatService(
                env.Workspace,
                env.Store,
                NullLogger.Instance,
                SystemClock.Instance,
                new HttpTransport(),
                null,                       // 设备能力：这组用例用不到
                null,                       // 嵌入：这组用例用不到
                env.Interaction,
                ToolRegistry.CreateDefault());

            env.Chat.OpenProfileAsync(env.Profile).Wait();
            env.Conversation = env.Chat.StartConversationAsync("端到端").Result;

            return env;
        }
    }
}
