// tests/CoreTests/AgentTests.cs —— Agent 主循环测试
//
// 这是整个逻辑层最值得测的部分：轮次控制、终止条件、取消、权限拦截、
// 图片回传。全部用假的 provider 驱动，不发一个真实请求。

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Yanshuai.Messages.Core;

namespace Yanshuai.Messages.Tests
{
    public sealed class FakeChatProvider : IChatProvider
    {
        public readonly Queue<ChatResult> Responses = new Queue<ChatResult>();
        public readonly List<ChatRequest> Requests = new List<ChatRequest>();
        public List<StreamChunk> StreamChunks;
        public ChatResult StreamResult;
        public bool ThrowCancel;

        public Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            if (ThrowCancel) throw new OperationCanceledException();
            if (Responses.Count == 0) return Task.FromResult(ChatResult.Fail("empty", "测试没有更多预设响应"));
            return Task.FromResult(Responses.Dequeue());
        }

        public Task<ChatResult> StreamAsync(ChatRequest request, Action<StreamChunk> onChunk, CancellationToken ct)
        {
            Requests.Add(request);
            if (ThrowCancel) throw new OperationCanceledException();

            var text = "";
            if (StreamChunks != null)
            {
                foreach (var chunk in StreamChunks)
                {
                    ct.ThrowIfCancellationRequested();
                    if (onChunk != null) onChunk(chunk);
                    text += chunk.ContentDelta ?? "";
                }
            }

            if (StreamResult != null) return Task.FromResult(StreamResult);
            return Task.FromResult(new ChatResult { Ok = true, Content = text });
        }
    }

    /// <summary>回显参数，用来验证工具结果确实回填到了会话里。</summary>
    public sealed class EchoTool : ToolBase
    {
        public int Calls;

        public override string Name { get { return "echo"; } }
        public override string Description { get { return "回显"; } }
        public override ToolGroup Group { get { return ToolGroup.Agent; } }

        protected override void DescribeParams(ToolSchema schema)
        {
            schema.Param("text", "string", "要回显的内容", false);
        }

        public override Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext context, CancellationToken ct)
        {
            Calls++;
            var text = ToolArguments.GetString(argumentsJson, "text", "");
            return Task.FromResult(ToolResult.Success("回显：" + text));
        }
    }

    /// <summary>返回图片标记的工具，验证图片消息的插入。</summary>
    public sealed class ImageTool : ToolBase
    {
        public override string Name { get { return "fake_image"; } }
        public override string Description { get { return "假图片工具"; } }
        public override ToolGroup Group { get { return ToolGroup.Files; } }
        public override bool IsSensitive { get { return false; } }

        protected override void DescribeParams(ToolSchema schema) { }

        public override Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext context, CancellationToken ct)
        {
            return Task.FromResult(ToolResult.Success(ReadImageTool.Marker + "image/png:QUJD"));
        }
    }

    public static class AgentTests
    {
        public static void Register()
        {
            Runner.Add("Agent", "两轮收尾：过程落库 + 最终输出", delegate
            {
                var provider = new FakeChatProvider();
                provider.Responses.Enqueue(ToolCall("c1", "echo", "{\"text\":\"hi\"}"));
                provider.Responses.Enqueue(new ChatResult { Ok = true, Content = "最终答案" });

                var echo = new EchoTool();
                var observer = new AgentObserver();
                var stepsStarted = new List<StepRecord>();
                var stepsUpdated = new List<StepRecord>();
                observer.StepStarted = delegate (StepRecord s) { stepsStarted.Add(s); };
                observer.StepUpdated = delegate (StepRecord s) { stepsUpdated.Add(s); };

                var request = MakeRequest(provider, new List<ITool> { echo }, observer);
                var result = new AgentRunner(NullLogger.Instance).RunAsync(request, CancellationToken.None).Result;

                Check.Equal(AgentOutcome.Completed, result.Outcome, "应当完成");
                Check.EqualInt(2, result.Turns, "跑了两轮");
                Check.Equal("最终答案", result.FinalMessage.Content, "最终内容");

                // 用户 + 过程助手 + 工具结果 + 最终助手
                Check.EqualInt(4, request.Conversation.Messages.Count, "消息条数");
                Check.Equal(MessageRole.Assistant, request.Conversation.Messages[1].Role, "第二条是助手");
                Check.False(request.Conversation.Messages[1].IsFinal, "过程消息不是最终");
                Check.EqualInt(1, request.Conversation.Messages[1].ToolCalls.Count, "带一个工具调用");
                Check.Equal(MessageRole.Tool, request.Conversation.Messages[2].Role, "第三条是工具结果");
                Check.Contains(request.Conversation.Messages[2].Content, "回显：hi", "工具结果回填");
                Check.True(request.Conversation.Messages[3].IsFinal, "最后一条是最终输出");

                Check.EqualInt(1, echo.Calls, "工具被调用一次");
                Check.EqualInt(1, stepsStarted.Count, "UI 收到一个工具步骤");
                Check.EqualInt(1, stepsUpdated.Count, "UI 收到一次步骤更新");
                Check.Equal(StepState.Done, stepsUpdated[0].State, "步骤状态为完成");
            });

            Runner.Add("Agent", "工具结果的摘要写回调用记录", delegate
            {
                var provider = new FakeChatProvider();
                provider.Responses.Enqueue(ToolCall("c1", "echo", "{\"text\":\"摘要\"}"));
                provider.Responses.Enqueue(new ChatResult { Ok = true, Content = "完" });

                var request = MakeRequest(provider, new List<ITool> { new EchoTool() }, new AgentObserver());
                new AgentRunner(NullLogger.Instance).RunAsync(request, CancellationToken.None).Wait();

                var record = request.Conversation.Messages[1].ToolCalls[0];
                Check.Contains(record.ResultPreview, "回显", "预览已写入");
                Check.True(record.Ok, "标记为成功");
            });

            Runner.Add("Agent", "轮次用尽时明确告知用户", delegate
            {
                var provider = new FakeChatProvider();
                for (int i = 0; i < 5; i++)
                {
                    provider.Responses.Enqueue(ToolCall("c" + i, "echo", "{\"text\":\"x\"}"));
                }

                var request = MakeRequest(provider, new List<ITool> { new EchoTool() }, new AgentObserver());
                request.Profile.Tools.MaxToolTurns = 2;

                var result = new AgentRunner(NullLogger.Instance).RunAsync(request, CancellationToken.None).Result;

                Check.Equal(AgentOutcome.Completed, result.Outcome, "仍然算完成");
                Check.EqualInt(2, result.Turns, "只跑了两轮");
                Check.Contains(result.FinalMessage.Content, "连续调用了 2 轮", "告知轮次上限");
            });

            Runner.Add("Agent", "provider 报错时落一条失败消息", delegate
            {
                var provider = new FakeChatProvider();
                provider.Responses.Enqueue(ChatResult.Fail("http_401", "密钥无效"));

                var request = MakeRequest(provider, new List<ITool> { new EchoTool() }, new AgentObserver());
                var result = new AgentRunner(NullLogger.Instance).RunAsync(request, CancellationToken.None).Result;

                Check.Equal(AgentOutcome.Failed, result.Outcome, "应当失败");
                Check.Equal("http_401", result.ErrorCode, "错误码");
                Check.True(result.FinalMessage.Failed, "消息标记为失败");
                Check.Contains(result.FinalMessage.Content, "密钥无效", "带上可读说明");
            });

            Runner.Add("Agent", "取消时保留已产出并补一条打断提示", delegate
            {
                var provider = new FakeChatProvider { ThrowCancel = true };
                var request = MakeRequest(provider, new List<ITool> { new EchoTool() }, new AgentObserver());

                var result = new AgentRunner(NullLogger.Instance).RunAsync(request, CancellationToken.None).Result;

                Check.Equal(AgentOutcome.Cancelled, result.Outcome, "应当取消");
                var last = request.Conversation.Messages[request.Conversation.Messages.Count - 1];
                Check.Equal(MessageRole.System, last.Role, "补系统提示");
                Check.Contains(last.Content, "打断", "提示内容");
            });

            Runner.Add("Agent", "不开工具时走流式并落盘最终消息", delegate
            {
                var provider = new FakeChatProvider
                {
                    StreamChunks = new List<StreamChunk>
                    {
                        new StreamChunk { ContentDelta = "你" },
                        new StreamChunk { ContentDelta = "好" }
                    }
                };

                var deltas = new List<string>();
                var observer = new AgentObserver();
                observer.AssistantDelta = delegate (string content, string reasoning)
                {
                    if (content != null) deltas.Add(content);
                };

                var request = MakeRequest(provider, new List<ITool>(), observer);
                var result = new AgentRunner(NullLogger.Instance).RunAsync(request, CancellationToken.None).Result;

                Check.Equal(AgentOutcome.Completed, result.Outcome, "完成");
                Check.Equal("你好", result.FinalMessage.Content, "拼接内容");
                Check.EqualInt(2, deltas.Count, "两次增量回调");
                Check.True(provider.Requests[0].Stream, "请求走流式");
            });

            Runner.Add("Agent", "未知工具返回可读错误而不炸", delegate
            {
                var provider = new FakeChatProvider();
                provider.Responses.Enqueue(ToolCall("c1", "no_such_tool", "{}"));
                provider.Responses.Enqueue(new ChatResult { Ok = true, Content = "好" });

                var request = MakeRequest(provider, new List<ITool> { new EchoTool() }, new AgentObserver());
                new AgentRunner(NullLogger.Instance).RunAsync(request, CancellationToken.None).Wait();

                var toolMessage = request.Conversation.Messages[2];
                Check.Equal(MessageRole.Tool, toolMessage.Role, "仍然是工具消息");
                Check.True(toolMessage.Failed, "标记失败");
                Check.Contains(toolMessage.Content, "没有名为", "说明里带工具名");
            });

            Runner.Add("Agent", "敏感工具被拒绝时把拒绝说明回给模型", delegate
            {
                var provider = new FakeChatProvider();
                provider.Responses.Enqueue(ToolCall("c1", "write_file", "{\"path\":\"a.md\",\"content\":\"x\"}"));
                provider.Responses.Enqueue(new ChatResult { Ok = true, Content = "那我换个方式" });

                var ui = new FakeInteraction { ApproveResult = false };
                var request = MakeRequest(provider, new List<ITool> { new FileWriteTool() }, new AgentObserver());
                request.Interaction = ui;

                new AgentRunner(NullLogger.Instance).RunAsync(request, CancellationToken.None).Wait();

                Check.EqualInt(1, ui.ConfirmCalls, "弹了一次审批");
                var toolMessage = request.Conversation.Messages[2];
                Check.True(toolMessage.Failed, "工具结果标记失败");
                Check.Contains(toolMessage.Content, "拒绝", "回给模型的是拒绝说明");
            });

            Runner.Add("Agent", "批准后敏感工具真正执行", delegate
            {
                var provider = new FakeChatProvider();
                provider.Responses.Enqueue(ToolCall("c1", "write_file", "{\"path\":\"a.md\",\"content\":\"写入内容\"}"));
                provider.Responses.Enqueue(new ChatResult { Ok = true, Content = "写好了" });

                var store = new InMemoryFileStore();
                var ui = new FakeInteraction { ApproveResult = true };
                var request = MakeRequest(provider, new List<ITool> { new FileWriteTool() }, new AgentObserver());
                request.Interaction = ui;
                request.Tools.Store = store;

                new AgentRunner(NullLogger.Instance).RunAsync(request, CancellationToken.None).Wait();

                Check.EqualInt(1, ui.ConfirmCalls, "弹了一次");
                Check.Equal("写入内容", store.Peek("test/files/a.md"), "文件真的写了");
                Check.False(request.Conversation.Messages[2].Failed, "工具成功");
            });

            Runner.Add("Agent", "图片结果转成一条带附件的用户消息", delegate
            {
                var provider = new FakeChatProvider();
                provider.Responses.Enqueue(ToolCall("c1", "fake_image", "{}"));
                provider.Responses.Enqueue(new ChatResult { Ok = true, Content = "看到了" });

                var request = MakeRequest(provider, new List<ITool> { new ImageTool() }, new AgentObserver());
                new AgentRunner(NullLogger.Instance).RunAsync(request, CancellationToken.None).Wait();

                var imageMessage = request.Conversation.Messages[3];
                Check.Equal(MessageRole.User, imageMessage.Role, "图片走用户消息");
                Check.EqualInt(1, imageMessage.Attachments.Count, "带一个附件");
                Check.Equal("image/png", imageMessage.Attachments[0].MimeType, "MIME 正确");
                Check.Equal("QUJD", imageMessage.Attachments[0].ImageBase64, "base64 内容");
            });

            Runner.Add("Agent", "工具入参被截断时自动修补", delegate
            {
                var provider = new FakeChatProvider();
                provider.Responses.Enqueue(ToolCall("c1", "echo", "{\"text\":\"没闭合"));
                provider.Responses.Enqueue(new ChatResult { Ok = true, Content = "ok" });

                var echo = new EchoTool();
                var request = MakeRequest(provider, new List<ITool> { echo }, new AgentObserver());
                new AgentRunner(NullLogger.Instance).RunAsync(request, CancellationToken.None).Wait();

                Check.EqualInt(1, echo.Calls, "仍然执行了");
                Check.False(request.Conversation.Messages[2].Failed, "没被判失败");
            });

            Runner.Add("Agent", "每轮都重建提示词（上下文变化会体现）", delegate
            {
                var provider = new FakeChatProvider();
                provider.Responses.Enqueue(ToolCall("c1", "echo", "{}"));
                provider.Responses.Enqueue(new ChatResult { Ok = true, Content = "ok" });

                var request = MakeRequest(provider, new List<ITool> { new EchoTool() }, new AgentObserver());
                new AgentRunner(NullLogger.Instance).RunAsync(request, CancellationToken.None).Wait();

                Check.EqualInt(2, provider.Requests.Count, "请求两次");
                int first = provider.Requests[0].Turns.Count;
                int second = provider.Requests[1].Turns.Count;
                Check.True(second > first, "第二轮把工具结果带进了上下文");
                Check.EqualInt(1, provider.Requests[0].Tools.Count, "第一轮带工具定义");

                // 工具结果必须紧跟 tool_calls，中间不能插东西
                var turns = provider.Requests[1].Turns;
                for (int i = 0; i < turns.Count; i++)
                {
                    if (turns[i].Role != "assistant") continue;
                    if (turns[i].ToolCalls == null || turns[i].ToolCalls.Count == 0) continue;
                    Check.Equal("tool", turns[i + 1].Role, "工具结果紧跟其后");
                }
            });
        }

        // ── 辅助 ─────────────────────────────────────────────────────────

        private static ChatResult ToolCall(string id, string name, string argumentsJson)
        {
            var result = new ChatResult { Ok = true, Content = "" };
            result.ToolCalls.Add(new ToolCallRequest { Id = id, Name = name, ArgumentsJson = argumentsJson });
            return result;
        }

        private static AgentRunRequest MakeRequest(
            FakeChatProvider provider, List<ITool> tools, AgentObserver observer)
        {
            var profile = new Profile { Name = "测试档案", Slug = "test" };
            var conversation = new Conversation { ProfileId = profile.Id };
            conversation.Messages.Add(new Message { Role = MessageRole.User, Content = "你好" });

            var registry = new ToolRegistry();
            foreach (var tool in tools) registry.Register(tool);

            var permissions = new PermissionService(new InMemoryFileStore(), NullLogger.Instance);
            permissions.LoadAsync().Wait();

            return new AgentRunRequest
            {
                Profile = profile,
                Api = new ApiProfile { Id = "api1", Name = "测试", Model = "test-model" },
                ApiKey = "k",
                Conversation = conversation,
                PromptContext = new PromptContext { Profile = profile, Conversation = conversation },
                Tools = new ToolContext
                {
                    Profile = profile,
                    ProfileSlug = "test",
                    Store = new InMemoryFileStore(),
                    Clock = SystemClock.Instance,
                    Search = new SearchSettings(),
                    Log = NullLogger.Instance
                },
                Registry = registry,
                AvailableTools = tools,
                Provider = provider,
                Interaction = new FakeInteraction(),
                Permissions = permissions,
                Observer = observer
            };
        }
    }
}
