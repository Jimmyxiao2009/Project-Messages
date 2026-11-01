// Core/Agent/AgentRunner.cs —— Agent 主循环
//
// 两条路径：
//   A. 不开工具 → 真流式（逐块回调给 UI，定稿后落一条最终消息）
//   B. 开工具   → 工具循环（非流式，因为要拿完整的 tool_calls），
//                 但每轮的思考与工具步骤都立刻回调，UI 边跑边显示，
//                 不像旧项目那样等全部结束再"打字机回放"。
//
// 循环的终止条件写全了，别只靠"轮次用尽"：
//   1) 本轮没有 tool_calls           → 正常收尾
//   2) 轮次用尽                      → 收尾并明确告知用户
//   3) token 预算用尽                → 收尾并告知
//   4) 用户取消                      → 保留已产出内容
//   5) provider 返回错误             → 落一条失败消息（时间线上是错误行）
//
// 取消时**不丢弃已产出内容**：流式路径会把已收到的文本落盘，工具路径会补一条
// "本轮已被打断"的系统提示，用户可以接着发消息继续。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Yanshuai.Messages.Core
{
    public sealed class AgentObserver
    {
        /// <summary>整体状态变化（运行中/等待批准/正在停止…）。</summary>
        public Action<RunState, string> StateChanged { get; set; }
        /// <summary>一个新的步骤出现（思考或工具）。</summary>
        public Action<StepRecord> StepStarted { get; set; }
        /// <summary>已有步骤被更新（工具跑完、失败）。</summary>
        public Action<StepRecord> StepUpdated { get; set; }
        /// <summary>流式增量。第一个参数是正文增量，第二个是推理增量。</summary>
        public Action<string, string> AssistantDelta { get; set; }
        /// <summary>落了一条消息（用户/助手/工具）。UI 可以据此刷新时间线。</summary>
        public Action<Message> MessageAppended { get; set; }
        /// <summary>给用户看的一次性提示（压缩、裁剪、打断…）。</summary>
        public Action<string> Notice { get; set; }
    }

    public sealed class AgentRunRequest
    {
        public Profile Profile { get; set; }
        public ApiProfile Api { get; set; }
        public string ApiKey { get; set; } = "";
        public Conversation Conversation { get; set; }

        /// <summary>调用方准备好的提示词上下文（含记忆块、知识库、注入库）。</summary>
        public PromptContext PromptContext { get; set; }

        public ToolContext Tools { get; set; }
        public ToolRegistry Registry { get; set; }
        /// <summary>本次可用工具（已按档案开关过滤）。空表示走纯流式。</summary>
        public List<ITool> AvailableTools { get; set; }

        public PermissionService Permissions { get; set; }
        public IUserInteraction Interaction { get; set; }
        public IChatProvider Provider { get; set; }
        public AgentObserver Observer { get; set; }

        /// <summary>子代理嵌套深度，顶层为 0。</summary>
        public int SubagentDepth { get; set; }

        /// <summary>本轮用户输入（用于记忆检索与相关性打分）。</summary>
        public string UserInput { get; set; } = "";

        /// <summary>整轮累计 token 上限，0 表示不限。</summary>
        public int MaxTotalTokens { get; set; }

        public AgentRunRequest()
        {
            AvailableTools = new List<ITool>();
        }
    }

    public enum AgentOutcome
    {
        Completed = 0,
        Cancelled = 1,
        Failed = 2
    }

    public sealed class AgentRunResult
    {
        public AgentOutcome Outcome { get; set; }
        public string ErrorCode { get; set; } = "";
        public string ErrorMessage { get; set; } = "";
        /// <summary>落库的最终助手消息（被取消且没有产出时为 null）。</summary>
        public Message FinalMessage { get; set; }
        public int Turns { get; set; }
        /// <summary>这一轮里实际执行过的工具调用次数（含失败与被拒的）。</summary>
        public int ToolCalls { get; set; }
        public UsageInfo Usage { get; set; }
        /// <summary>本轮新增的消息条数，供调用方判断要不要保存。</summary>
        public int AppendedMessages { get; set; }

        public AgentRunResult()
        {
            Usage = new UsageInfo();
        }
    }

    public sealed class AgentRunner
    {
        private readonly ILogger _log;

        public AgentRunner(ILogger log)
        {
            _log = log;
        }

        public async Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken ct)
        {
            if (request == null) throw new ArgumentNullException("request");
            if (request.Profile == null) throw new ArgumentException("缺少档案");
            if (request.Conversation == null) throw new ArgumentException("缺少会话");
            if (request.Provider == null) throw new ArgumentException("缺少 provider");

            request.Profile.Repair();
            var observer = request.Observer ?? new AgentObserver();
            _observer = observer;

            // 调用方忘了给上下文时不至于直接空引用
            if (request.PromptContext == null)
            {
                request.PromptContext = new PromptContext
                {
                    Profile = request.Profile,
                    Conversation = request.Conversation,
                    Api = request.Api
                };
            }
            if (request.PromptContext.Profile == null) request.PromptContext.Profile = request.Profile;
            if (request.PromptContext.Conversation == null) request.PromptContext.Conversation = request.Conversation;

            // 权限档位**不在这里复位**：对话页的快捷权限设置要能在会话中途改档。
            // 复位时机在 ChatService.OpenProfileAsync / OpenConversationAsync（按档案默认），
            // 那里才是"每次进入会话"的语义。

            bool useTools = request.AvailableTools != null && request.AvailableTools.Count > 0 &&
                request.Api.ResolveMetadata(request.Api.Model).Tools != false;

            try
            {
                return useTools
                    ? await RunToolLoopAsync(request, ct).ConfigureAwait(false)
                    : await RunStreamingAsync(request, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return await HandleCancellationAsync(request).ConfigureAwait(false);
            }
        }

        private AgentObserver _observer;

        // ═════════════════════════════════════════════════════════════════
        // A. 纯流式（不开工具）
        // ═════════════════════════════════════════════════════════════════
        private async Task<AgentRunResult> RunStreamingAsync(AgentRunRequest request, CancellationToken ct)
        {
            var result = new AgentRunResult();
            var observer = request.Observer ?? new AgentObserver();

            observer.StateChanged?.Invoke(RunState.Thinking, "正在生成");

            var assembled = PromptAssembler.Build(request.PromptContext);
            bool stream = request.Profile.Generation.StreamOutput;
            var chat = BuildChatRequest(request, assembled, null, stream);

            var accumulator = new StreamAccumulator();
            Action<StreamChunk> onChunk = delegate (StreamChunk chunk)
            {
                if (chunk == null) return;

                if (!string.IsNullOrEmpty(chunk.ContentDelta))
                {
                    accumulator.AppendContent(chunk.ContentDelta);
                    observer.AssistantDelta?.Invoke(chunk.ContentDelta, null);
                }
                if (!string.IsNullOrEmpty(chunk.ReasoningDelta))
                {
                    accumulator.AppendReasoning(chunk.ReasoningDelta);
                    observer.AssistantDelta?.Invoke(null, chunk.ReasoningDelta);
                }
            };
            var response = stream
                ? await request.Provider.StreamAsync(chat, onChunk, ct).ConfigureAwait(false)
                : await request.Provider.CompleteAsync(chat, ct).ConfigureAwait(false);

            AddUsage(result.Usage, response.Usage);

            if (!response.Ok)
            {
                return AppendFailure(request, result, response.ErrorCode, response.ErrorMessage);
            }

            var final = new Message
            {
                Role = MessageRole.Assistant,
                Content = response.Content,
                Reasoning = response.Reasoning,
                IsFinal = true,
                TokensIn = response.Usage != null ? response.Usage.InputTokens : 0,
                TokensOut = response.Usage != null ? response.Usage.OutputTokens : 0
            };
            Append(request, result, final);

            observer.StateChanged?.Invoke(RunState.Done, "");
            result.Outcome = AgentOutcome.Completed;
            result.FinalMessage = final;
            return result;
        }

        // ═════════════════════════════════════════════════════════════════
        // B. 工具循环
        // ═════════════════════════════════════════════════════════════════
        private async Task<AgentRunResult> RunToolLoopAsync(AgentRunRequest request, CancellationToken ct)
        {
            var result = new AgentRunResult();
            var observer = request.Observer ?? new AgentObserver();
            var profile = request.Profile;
            var conversation = request.Conversation;

            int maxTurns = profile.Tools.MaxToolTurns;
            if (maxTurns <= 0) maxTurns = Limits.DefaultMaxToolTurns;

            var schemas = request.Registry != null
                ? request.Registry.Schemas(request.AvailableTools)
                : new List<ToolSchema>();

            for (int turn = 1; turn <= maxTurns; turn++)
            {
                ct.ThrowIfCancellationRequested();

                result.Turns = turn;
                observer.StateChanged?.Invoke(RunState.Thinking,
                    "第 " + turn.ToString(CultureInfo.InvariantCulture) + " 轮");

                var assembled = PromptAssembler.Build(request.PromptContext);

                // 工具轮也必须能流式。模型完全可以在"带着工具"的情况下直接回答，
                // 那时用户依然该看到打字机效果；tool_calls 增量由 provider 的
                // StreamAsync 自己攒（OpenAiChatProvider 的 FlushToolCalls）。
                // 曾经这里写死非流式，等"工具默认常开"之后，每一轮对话都走这条
                // 非流式分支 —— 用户感知就是"完全没有流式了，每句都要等整段到齐"。
                bool stream = request.Profile.Generation.StreamOutput;
                var chat = BuildChatRequest(request, assembled, schemas, stream);

                ChatResult response;
                if (stream)
                {
                    var accumulator = new StreamAccumulator();
                    Action<StreamChunk> onChunk = delegate (StreamChunk chunk)
                    {
                        if (chunk == null) return;
                        if (!string.IsNullOrEmpty(chunk.ContentDelta))
                        {
                            accumulator.AppendContent(chunk.ContentDelta);
                            observer.AssistantDelta?.Invoke(chunk.ContentDelta, null);
                        }
                        if (!string.IsNullOrEmpty(chunk.ReasoningDelta))
                        {
                            accumulator.AppendReasoning(chunk.ReasoningDelta);
                            observer.AssistantDelta?.Invoke(null, chunk.ReasoningDelta);
                        }
                    };
                    response = await request.Provider.StreamAsync(chat, onChunk, ct).ConfigureAwait(false);
                }
                else
                {
                    response = await request.Provider.CompleteAsync(chat, ct).ConfigureAwait(false);
                }
                AddUsage(result.Usage, response.Usage);

                if (!response.Ok)
                {
                    return AppendFailure(request, result, response.ErrorCode, response.ErrorMessage);
                }

                // 没有工具调用 → 这一轮的话就是最终回答
                if (!response.HasToolCalls)
                {
                    var final = new Message
                    {
                        Role = MessageRole.Assistant,
                        Content = response.Content,
                        Reasoning = response.Reasoning,
                        IsFinal = true,
                        TokensIn = response.Usage != null ? response.Usage.InputTokens : 0,
                        TokensOut = response.Usage != null ? response.Usage.OutputTokens : 0
                    };
                    Append(request, result, final);

                    observer.StateChanged?.Invoke(RunState.Done, "");
                    result.Outcome = AgentOutcome.Completed;
                    result.FinalMessage = final;
                    return result;
                }

                // 落一条"过程"助手消息（含 tool_calls），时间线会把它变成过程轨道
                var assistantMessage = new Message
                {
                    Role = MessageRole.Assistant,
                    Content = response.Content,
                    Reasoning = response.Reasoning,
                    IsFinal = false,
                    TokensIn = response.Usage != null ? response.Usage.InputTokens : 0,
                    TokensOut = response.Usage != null ? response.Usage.OutputTokens : 0
                };
                foreach (var call in response.ToolCalls)
                {
                    assistantMessage.ToolCalls.Add(new ToolCallRecord
                    {
                        Id = call.Id,
                        Name = call.Name,
                        ArgumentsJson = string.IsNullOrEmpty(call.ArgumentsJson) ? "{}" : call.ArgumentsJson
                    });
                }
                Append(request, result, assistantMessage);

                // 立刻把步骤推给 UI
                if (!string.IsNullOrEmpty(response.Reasoning))
                {
                    observer.StepStarted?.Invoke(new StepRecord
                    {
                        Kind = StepKind.Reasoning,
                        State = StepState.Done,
                        Title = "思考",
                        Body = response.Reasoning,
                        // 与 TimelineProjector 从消息推导出的那一步同一个 key，供去重
                        CallId = assistantMessage.Id + ":reasoning"
                    });
                }

                var steps = new List<StepRecord>();
                foreach (var call in response.ToolCalls)
                {
                    var step = new StepRecord
                    {
                        Kind = StepKind.Tool,
                        State = StepState.Running,
                        Title = TimelineProjector.ToolDisplayName(call.Name),
                        Subtitle = TimelineProjector.SummarizeArguments(call.ArgumentsJson),
                        CallId = call.Id
                    };
                    steps.Add(step);
                    observer.StepStarted?.Invoke(step);
                }

                observer.StateChanged?.Invoke(RunState.ToolRunning,
                    "正在执行 " + response.ToolCalls.Count.ToString(CultureInfo.InvariantCulture) + " 个工具");

                result.ToolCalls += response.ToolCalls.Count;

                // 同一轮的多个工具并行；轮与轮之间严格串行
                var executions = new List<Task<ToolExecution>>();
                foreach (var call in response.ToolCalls)
                {
                    executions.Add(ExecuteToolAsync(call, request, ct));
                }
                var outcomes = await Task.WhenAll(executions).ConfigureAwait(false);

                for (int i = 0; i < outcomes.Length; i++)
                {
                    var outcome = outcomes[i];
                    var call = response.ToolCalls[i];

                    var text = TextUtil.Truncate(outcome.Text ?? "", Limits.ToolOutputChars);

                    conversation.Messages.Add(new Message
                    {
                        Role = MessageRole.Tool,
                        ToolCallId = call.Id,
                        Content = text,
                        Failed = !outcome.Ok
                    });

                    if (i < steps.Count)
                    {
                        steps[i].State = outcome.Ok ? StepState.Done : StepState.Failed;
                        steps[i].Body = text;
                        observer.StepUpdated?.Invoke(steps[i]);
                    }

                    // 记到对应的 ToolCallRecord，供投影时展示摘要
                    RecordToolPreview(assistantMessage, call.Id, text, outcome.Ok);

                    // 图片结果：追加一条带图片的用户消息，让视觉模型能看到
                    if (!string.IsNullOrEmpty(outcome.ImageBase64))
                    {
                        var imageMessage = new Message { Role = MessageRole.User, Content = "（这是上一步读取的图片）" };
                        imageMessage.Attachments.Add(new Attachment
                        {
                            Kind = "image",
                            Name = outcome.ImageName ?? "image.png",
                            MimeType = outcome.ImageMime ?? "image/png",
                            ImageBase64 = outcome.ImageBase64
                        });
                        conversation.Messages.Add(imageMessage);
                    }
                }

                if (request.MaxTotalTokens > 0 && result.Usage.Total > request.MaxTotalTokens)
                {
                    AppendNotice(conversation, "本轮累计用量已超过设定上限，先停在这里。");
                    var stopMessage = new Message
                    {
                        Role = MessageRole.Assistant,
                        IsFinal = true,
                        Content = "这次的用量已经超过设定上限，我先停下。把任务拆小一点再继续会更稳。"
                    };
                    Append(request, result, stopMessage);
                    observer.StateChanged?.Invoke(RunState.Done, "");
                    result.Outcome = AgentOutcome.Completed;
                    result.FinalMessage = stopMessage;
                    return result;
                }

                conversation.Touch();
            }

            // 轮次用尽
            var exhausted = new Message
            {
                Role = MessageRole.Assistant,
                IsFinal = true,
                Content = "已经连续调用了 " + maxTurns.ToString(CultureInfo.InvariantCulture) +
                          " 轮工具，先停在这里。可以让我接着做，或者把任务拆成几步。"
            };
            Append(request, result, exhausted);
            observer.StateChanged?.Invoke(RunState.Done, "");
            result.Outcome = AgentOutcome.Completed;
            result.FinalMessage = exhausted;
            return result;
        }

        // ═════════════════════════════════════════════════════════════════
        // 工具执行
        // ═════════════════════════════════════════════════════════════════
        private sealed class ToolExecution
        {
            public bool Ok;
            public string Text = "";
            public string ImageBase64;
            public string ImageMime;
            public string ImageName;
        }

        private async Task<ToolExecution> ExecuteToolAsync(
            ToolCallRequest call, AgentRunRequest request, CancellationToken ct)
        {
            if (call == null) return new ToolExecution { Ok = false, Text = "错误的工具调用。" };

            var tool = request.Registry != null ? request.Registry.Find(call.Name) : null;
            if (tool == null)
            {
                return new ToolExecution { Ok = false, Text = "错误：没有名为 " + call.Name + " 的工具。" };
            }

            // 参数容错：小模型经常给出被截断的 JSON
            var args = call.ArgumentsJson;
            if (string.IsNullOrEmpty(args) || !ToolArguments.IsValidJson(args))
            {
                args = ToolArguments.RepairTruncated(args);
            }

            // 权限：敏感工具需要批准（非敏感直接放行）
            if (request.Permissions != null)
            {
                try
                {
                    var key = PermissionService.BuildKey(call.Name, args);
                    var permissionRequest = PermissionService.BuildRequest(call.Name, key, args);
                    var denial = await request.Permissions
                        .CheckAsync(call.Name, key, permissionRequest, request.Interaction, ct)
                        .ConfigureAwait(false);
                    if (denial != null) return new ToolExecution { Ok = false, Text = denial };
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    if (_log != null) _log.Log(LogLevel.Warning, "agent", "权限检查异常: " + call.Name);
                    return new ToolExecution { Ok = false, Text = "错误：权限检查失败。" };
                }
            }

            try
            {
                var outcome = await tool.InvokeAsync(args, request.Tools, ct).ConfigureAwait(false);
                var text = outcome.Content ?? "";

                // 图片结果：从内容里解出 mime 与 base64
                if (text.StartsWith(ReadImageTool.Marker, StringComparison.Ordinal))
                {
                    var payload = text.Substring(ReadImageTool.Marker.Length);
                    int colon = payload.IndexOf(':');
                    if (colon > 0)
                    {
                        return new ToolExecution
                        {
                            Ok = true,
                            Text = "已读取图片并随下一条消息附上。",
                            ImageMime = payload.Substring(0, colon),
                            ImageBase64 = payload.Substring(colon + 1),
                            ImageName = "image"
                        };
                    }
                    return new ToolExecution { Ok = false, Text = "错误：图片数据格式不对。" };
                }

                return new ToolExecution { Ok = outcome.Ok, Text = text };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                if (_log != null) _log.Log(LogLevel.Error, "agent", "工具异常: " + call.Name);
                return new ToolExecution { Ok = false, Text = "错误：工具执行失败。" };
            }
        }

        private static void RecordToolPreview(Message assistantMessage, string callId, string text, bool ok)
        {
            if (assistantMessage == null || assistantMessage.ToolCalls == null) return;
            foreach (var record in assistantMessage.ToolCalls)
            {
                if (!string.Equals(record.Id, callId, StringComparison.Ordinal)) continue;
                record.ResultPreview = TextUtil.Truncate(WebText.OneLine(text, 200), 200);
                record.Ok = ok;
                return;
            }
        }

        // ═════════════════════════════════════════════════════════════════
        // 收尾与辅助
        // ═════════════════════════════════════════════════════════════════
        private AgentRunResult AppendFailure(AgentRunRequest request, AgentRunResult result, string code, string message)
        {
            var observer = request.Observer ?? new AgentObserver();
            var text = string.IsNullOrEmpty(message) ? "请求失败。" : message;

            var failure = new Message
            {
                Role = MessageRole.Assistant,
                Content = text,
                IsFinal = true,
                Failed = true,
                ErrorCode = code ?? ""
            };
            Append(request, result, failure);

            observer.StateChanged?.Invoke(RunState.Failed, text);
            result.Outcome = AgentOutcome.Failed;
            result.ErrorCode = code ?? "";
            result.ErrorMessage = text;
            result.FinalMessage = failure;

            if (_log != null) _log.Log(LogLevel.Warning, "agent", "调用失败 code=" + (code ?? ""));
            return result;
        }

        private async Task<AgentRunResult> HandleCancellationAsync(AgentRunRequest request)
        {
            var result = new AgentRunResult { Outcome = AgentOutcome.Cancelled };
            var observer = request.Observer ?? new AgentObserver();
            var conversation = request.Conversation;

            var hasPartial = false;
            if (conversation != null && conversation.Messages != null && conversation.Messages.Count > 0)
            {
                for (int i = conversation.Messages.Count - 1; i >= 0; i--)
                {
                    var message = conversation.Messages[i];
                    if (message.Role == MessageRole.User) break;
                    if (message.Role == MessageRole.Assistant && message.IsFinal && !string.IsNullOrEmpty(message.Content))
                    {
                        hasPartial = true;
                        break;
                    }
                }
            }

            if (!hasPartial)
            {
                AppendNotice(conversation, "本轮已被打断。可以直接发送新消息继续。");
            }

            observer.StateChanged?.Invoke(RunState.Stopped, "已停止");
            await Task.CompletedTask.ConfigureAwait(false);
            return result;
        }

        private static void AppendNotice(Conversation conversation, string text)
        {
            if (conversation == null) return;
            conversation.Messages.Add(new Message { Role = MessageRole.System, Content = text });
        }

        private void Append(AgentRunRequest request, AgentRunResult result, Message message)
        {
            request.Conversation.Messages.Add(message);
            request.Conversation.Touch();
            result.AppendedMessages++;

            var observer = request.Observer ?? new AgentObserver();
            observer.MessageAppended?.Invoke(message);
        }

        private static ChatRequest BuildChatRequest(
            AgentRunRequest request, AssembledPrompt assembled, List<ToolSchema> tools, bool stream)
        {
            var api = request.Api;
            var profile = request.Profile;

            var chat = new ChatRequest
            {
                Api = api,
                ApiKey = request.ApiKey,
                Turns = assembled.Turns,
                Tools = tools ?? new List<ToolSchema>(),
                Stream = stream,
                VisionEnabled = ResolveVision(profile, api)
            };

            chat.ReasoningEffort = profile.Api.ReasoningLevel ?? "";
            chat.Temperature = profile.Api.Temperature;
            chat.TopP = profile.Api.TopP;
            chat.MaxTokens = profile.Api.MaxTokens;
            int outputLimit = api.ResolveMetadata(api.Model).MaxOutputTokens;
            if (outputLimit > 0 && chat.MaxTokens.HasValue && chat.MaxTokens.Value > outputLimit) chat.MaxTokens = outputLimit;

            if (profile.Api.CustomBody != null)
            {
                foreach (var kv in profile.Api.CustomBody)
                {
                    if (!string.IsNullOrEmpty(kv.Key)) chat.ExtraBody[kv.Key] = kv.Value;
                }
            }

            return chat;
        }

        private static bool ResolveVision(Profile profile, ApiProfile api)
        {
            return ModelSelection.ResolveVision(profile, api);
        }

        private static void AddUsage(UsageInfo target, UsageInfo source)
        {
            if (source == null) return;
            target.InputTokens += source.InputTokens;
            target.OutputTokens += source.OutputTokens;
            target.CachedTokens += source.CachedTokens;
        }
    }
}
