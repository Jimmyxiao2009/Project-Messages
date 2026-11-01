// Core/ChatService.cs —— UI 的编排入口
//
// UI 只调这里。它负责把"发一条消息"变成完整流程：
//   落用户消息 → 压缩上下文（如需）→ 组提示词（人设/世界书/知识库/记忆）
//   → 跑 Agent → 落盘会话 → 更新索引 → 触发记忆抽取
//
// 这样一来 UI 不需要知道 provider、工具、记忆、压缩的存在 —— 旧项目最痛的
// 就是这些全糊在 MainPage 的 20 个分部文件里，改任何一处都要动 UI。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Yanshuai.Messages.Core
{
    public sealed class FollowUpResult
    {
        public string ConversationId { get; set; } = "";
        /// <summary>新的 AI 标题；没改时为 null。</summary>
        public string Title { get; set; }
        public List<string> Suggestions { get; set; } = new List<string>();
    }

    public sealed class ChatService
    {
        private readonly Workspace _workspace;
        private readonly IFileStore _store;
        private readonly ILogger _log;
        private readonly IClock _clock;
        private readonly IHttpTransport _http;
        private readonly IDeviceBridge _device;
        private readonly IEmbedder _embedder;
        private readonly IUserInteraction _interaction;
        private readonly AgentRunner _runner;

        public ChatService(
            Workspace workspace,
            IFileStore store,
            ILogger log,
            IClock clock,
            IHttpTransport http,
            IDeviceBridge device,
            IEmbedder embedder,
            IUserInteraction interaction,
            ToolRegistry registry)
        {
            _workspace = workspace;
            _store = store;
            _log = log;
            _clock = clock ?? SystemClock.Instance;
            _http = http;
            _device = device;
            _embedder = embedder;
            _interaction = interaction ?? DenyAllInteraction.Instance;
            _runner = new AgentRunner(log);
            Registry = registry ?? ToolRegistry.CreateDefault();
            Compressor = new ContextCompressor(log);
        }

        public ToolRegistry Registry { get; private set; }
        public Func<Profile, IFileStore> ToolStoreFactory { get; set; }
        public ContextCompressor Compressor { get; private set; }

        public Profile Profile { get; private set; }
        public Conversation Conversation { get; private set; }
        public ApiProfile Api { get; private set; }
        public MemoryService Memory { get; private set; }
        public RunState State { get; private set; }
        public bool IsDraft { get; private set; }

        public string ProfileSlug { get { return Profile != null ? Profile.Slug : ""; } }

        /// <summary>脚本运行时（桌面注入桩，真机注入 PyUwp）。为空则 run_python 不可用。</summary>
        public IScriptRuntime ScriptRuntime { get; set; }

        /// <summary>脚本可调用的宿主能力。切换档案时由组合根替换。</summary>
        public IScriptHostBridge ScriptHost { get; set; }

        /// <summary>
        /// 本次会话的权限档位（对话页快捷权限设置改这里）。不落盘，
        /// 打开档案/会话时按档案的 FullTrust 默认值复位。
        /// </summary>
        public ToolPermissionLevel PermissionLevel
        {
            get { return _workspace.Permissions != null ? _workspace.Permissions.Level : ToolPermissionLevel.WorkspaceWrite; }
            set { if (_workspace.Permissions != null) _workspace.Permissions.Level = value; }
        }

        /// <summary>按档案默认值复位权限档位。进入档案/会话时调用。</summary>
        private void ResetPermissionLevel()
        {
            if (_workspace.Permissions == null) return;
            _workspace.Permissions.Level = (Profile != null && Profile.Tools != null && Profile.Tools.FullTrust)
                ? ToolPermissionLevel.FullAccess
                : ToolPermissionLevel.WorkspaceWrite;
        }

        /// <summary>会话级用量日志。为空则不记录。</summary>
        public TokenTechLog TechLog { get; set; }

        /// <summary>当前会话已进行的用户轮次，供用量日志编号。</summary>
        public int TurnCounter { get; private set; }

        /// <summary>会话是否处于"用户需要处理"的状态（列表上用）。</summary>
        public bool NeedsAttention { get; set; }

        // ═════════════════════════════════════════════════════════════════
        // 打开档案与会话
        // ═════════════════════════════════════════════════════════════════
        public async Task<bool> OpenProfileAsync(Profile profile)
        {
            if (profile == null) return false;
            profile.Repair();

            Profile = profile;
            Api = _workspace.ApiProfiles.Resolve(profile.Api.ProfileId);

            Memory = new MemoryService(_store, _log, _clock, _embedder);
            await Memory.LoadAsync(profile.Slug).ConfigureAwait(false);
            if (profile.Memory.UseGlobal) await Memory.LoadGlobalAsync(true).ConfigureAwait(false);

            ResetPermissionLevel();
            State = RunState.Idle;
            return true;
        }

        public async Task<bool> OpenConversationAsync(Conversation conversation)
        {
            if (conversation == null || Profile == null) return false;
            conversation.Repair();
            IsDraft = !await _store.ExistsAsync(WorkspaceLayout.ConversationFile(Profile.Slug, conversation.Id)).ConfigureAwait(false);
            Conversation = conversation;
            ResetPermissionLevel();
            State = RunState.Idle;

            // 轮次编号接上历史：日志里的 turn 要和用户看到的"第几轮"对得上，
            // 从 0 重新数会让一个开了很久的会话每轮都显示 #1。
            // 用已有的用户消息条数当起点，比把计数器持久化简单也不容易失真。
            int userTurns = 0;
            foreach (var message in conversation.Messages)
            {
                if (message.Role == MessageRole.User) userTurns++;
            }
            TurnCounter = userTurns;

            await Task.CompletedTask.ConfigureAwait(false);
            return true;
        }

        /// <summary>新建会话。首句（人设里的 firstMessage）作为第一条助手消息落进去。</summary>
        public async Task<Conversation> StartConversationAsync(string title, bool persist = true)
        {
            if (Profile == null) return null;

            var conversation = _workspace.CreateConversation(Profile, title);

            var firstMessage = Profile.Persona != null ? Profile.Persona.FirstMessage : null;
            if (!string.IsNullOrWhiteSpace(firstMessage))
            {
                conversation.Messages.Add(new Message
                {
                    Role = MessageRole.Assistant,
                    Content = firstMessage.Trim(),
                    IsFinal = true
                });
            }

            Conversation = conversation;
            IsDraft = !persist;
            if (persist) await _workspace.SaveConversationAsync(Profile.Slug, conversation).ConfigureAwait(false);
            return conversation;
        }

        // ═════════════════════════════════════════════════════════════════
        // 发送
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// 发一条消息并跑完整轮。attachments 里的图片会按需读成 base64 交给视觉模型。
        /// </summary>
        public async Task<AgentRunResult> SendAsync(
            string text,
            IList<Attachment> attachments,
            AgentObserver observer,
            CancellationToken ct)
        {
            var result = new AgentRunResult();

            if (Profile == null) return FailLocal(result, "还没有选择档案。");
            if (Conversation == null) return FailLocal(result, "还没有打开会话。");

            // 用量日志的计时起点放在所有前置校验之后：
            // 校验失败的轮次不该被算进"这一轮花了多久"
            var startedAt = _clock.UtcNow;
            var compressed = false;
            var savedTokens = 0;
            TurnCounter++;

            var effectiveApi = ModelSelection.Resolve(_workspace.ApiProfiles, Profile, Conversation);
            if (effectiveApi == null) return FailLocal(result, "选中的 Provider 不存在。请重新选择 Provider 与模型。");
            Api = _workspace.ApiProfiles.Find(effectiveApi.Id);
            var apiKey = _workspace.GetApiKey(Api);
            if (Api.RequiresApiKey && string.IsNullOrEmpty(apiKey)) return FailLocal(result, "这个 Provider 还没有填密钥。");
            if (string.IsNullOrWhiteSpace(effectiveApi.Model)) return FailLocal(result, "请先获取模型列表并选择一个模型。");
            if (attachments != null && effectiveApi.ResolveMetadata(effectiveApi.Model).Vision == false)
                foreach (var attachment in attachments) if (attachment.IsImage) return FailLocal(result, "当前模型不支持图片，请切换到支持图片的模型。");

            // 1) 文本附件的内容拼进用户消息（图片走视觉通道，见 LoadImageAttachmentsAsync）
            var body = new StringBuilder(text ?? string.Empty);
            if (attachments != null)
            {
                foreach (var attachment in attachments)
                {
                    if (!string.Equals(attachment.Kind, "text", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.IsNullOrEmpty(attachment.Path)) continue;

                    try
                    {
                        var content = await _store.ReadTextAsync(attachment.Path).ConfigureAwait(false);
                        if (string.IsNullOrEmpty(content)) continue;

                        body.Append("\n\n【附件：").Append(attachment.Name).Append("】\n");
                        body.Append(TextUtil.Truncate(content, Limits.ReadFileChars));
                    }
                    catch (Exception)
                    {
                        // 附件读不出来就跳过，不打断这一轮
                    }
                }
            }

            // 2) 落用户消息
            var userMessage = new Message
            {
                Role = MessageRole.User,
                Content = body.ToString(),
                Attachments = new List<Attachment>()
            };
            if (attachments != null)
            {
                foreach (var attachment in attachments) userMessage.Attachments.Add(attachment);
            }
            Conversation.Messages.Add(userMessage);
            IsDraft = false;
            await _workspace.SaveConversationAsync(Profile.Slug, Conversation).ConfigureAwait(false);
            observer?.MessageAppended?.Invoke(userMessage);

            if (Conversation.Messages.Count <= 1 || IsUntitled(Conversation.Title))
            {
                Conversation.Title = SuggestTitle(text);
                Conversation.TitleSource = "auto";
            }
            // 旧建议对应的是上一条回复，发出新消息后就作废
            Conversation.Suggestions = new List<string>();
            Conversation.SuggestionsFor = "";

            // 2) 图片按需装载（不落盘 base64）
            await LoadImageAttachmentsAsync(userMessage, ct).ConfigureAwait(false);

            // 3) 压缩上下文（如果要）
            var systemPromptPreview = BuildSystemPromptPreview();
            if (Profile.Memory.AutoSummarize &&
                ContextCompressor.ShouldCompact(Conversation,
                    systemPromptPreview,
                    ModelContextLimits.GetEffectiveLimit(effectiveApi.Model, effectiveApi.MaxContextTokens)))
            {
                observer?.StateChanged?.Invoke(RunState.Thinking, "正在压缩上下文");
                var compact = await CompactAsync(ct).ConfigureAwait(false);
                if (compact.Compacted)
                {
                    compressed = true;
                    savedTokens = Math.Max(0, compact.TokensBefore - compact.TokensAfter);
                    observer?.Notice?.Invoke("已把早前对话压缩成摘要（" + compact.CompressedCount.ToString(CultureInfo.InvariantCulture) + " 条）。");
                }
            }

            // 4) 组上下文（传这一轮的请求副本，别传存储档案 —— 见方法上的说明）
            var promptContext = await BuildPromptContextAsync(userMessage, ct, effectiveApi).ConfigureAwait(false);

            var tools = Registry.Resolve(Profile);
            var toolContext = BuildToolContext(promptContext, 0);

            var request = new AgentRunRequest
            {
                Profile = Profile,
                Api = effectiveApi,
                ApiKey = apiKey,
                Conversation = Conversation,
                PromptContext = promptContext,
                Tools = toolContext,
                Registry = Registry,
                AvailableTools = tools,
                Permissions = _workspace.Permissions,
                Interaction = _interaction,
                Provider = ProviderFactory.Create(effectiveApi, _http, _log),
                Observer = observer,
                SubagentDepth = 0,
                UserInput = text
            };

            State = RunState.Thinking;
            result = await _runner.RunAsync(request, ct).ConfigureAwait(false);
            State = result.Outcome == AgentOutcome.Cancelled ? RunState.Stopped
                  : result.Outcome == AgentOutcome.Failed ? RunState.Failed
                  : RunState.Done;

            // 5) 落盘
            // 服务端没报用量时（流式响应很常见）用本地估算兜底，
            // 否则列表上的 token 统计在主路径上永远是 0。
            if (result.Usage.Total == 0 && result.FinalMessage != null)
            {
                EstimateUsage(result);
            }

            Conversation.TotalInputTokens += result.Usage.InputTokens;
            Conversation.TotalOutputTokens += result.Usage.OutputTokens;
            Conversation.LastUsage = result.Usage;

            await _workspace.SaveConversationAsync(Profile.Slug, Conversation).ConfigureAwait(false);
            await _workspace.MarkConversationStateAsync(Profile.Slug, Conversation.Id, NeedsAttention, false,
                result.Outcome == AgentOutcome.Failed).ConfigureAwait(false);

            // 5.5) 用量日志。写失败不能影响对话本身，所以 TokenTechLog 内部自己吞异常。
            await RecordTechLogAsync(result, startedAt, effectiveApi, compressed, savedTokens, ct).ConfigureAwait(false);

            // 6) 记忆抽取（按轮数节流）
            if (Profile.Memory.Enabled && result.Outcome == AgentOutcome.Completed)
            {
                var ignored = MaybeExtractMemoryAsync(ct);
            }

            return result;
        }

        private static AgentRunResult FailLocal(AgentRunResult result, string message)
        {
            result.Outcome = AgentOutcome.Failed;
            result.ErrorCode = "local";
            result.ErrorMessage = message;
            return result;
        }

        /// <summary>
        /// 记一条会话用量。只记元数据，不记提示词正文与密钥 ——
        /// 这份文件是要能直接导出给用户看的。
        /// </summary>
        private async Task RecordTechLogAsync(
            AgentRunResult result,
            DateTimeOffset startedAt,
            ApiProfile api,
            bool compressed,
            int savedTokens,
            CancellationToken ct)
        {
            if (TechLog == null || Conversation == null || Profile == null) return;

            var entry = new TechLogEntry
            {
                At = startedAt,
                Turn = TurnCounter,
                Model = api != null ? api.Model : "",
                Provider = api != null ? api.Provider : "",
                Host = HostOf(api),
                InputTokens = result.Usage != null ? result.Usage.InputTokens : 0,
                OutputTokens = result.Usage != null ? result.Usage.OutputTokens : 0,
                CachedTokens = result.Usage != null ? result.Usage.CachedTokens : 0,
                Estimated = result.Usage != null && result.Usage.Estimated,
                DurationMs = (long)Math.Max(0, (_clock.UtcNow - startedAt).TotalMilliseconds),
                ModelCalls = result.Turns,
                ToolCalls = result.ToolCalls,
                Compressed = compressed,
                SavedTokens = savedTokens,
                Error = result.Outcome == AgentOutcome.Failed ? result.ErrorCode : "",
                // 有工具时走的是非流式路径（需要完整 tool_calls），没有才流式
                Stream = result.ToolCalls == 0
            };

            try
            {
                await TechLog.AppendAsync(Profile.Slug, Conversation.Id, entry, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Log(LogLevel.Warning, "techlog", "记录用量失败：" + ex.GetType().Name);
            }
        }

        /// <summary>
        /// 只取主机名。完整 URL 可能带 query 里的临时 token，
        /// 而这份日志是要能导出给用户、甚至贴到公开 issue 里的。
        /// </summary>
        private static string HostOf(ApiProfile api)
        {
            if (api == null || string.IsNullOrEmpty(api.Url)) return "";

            try
            {
                var uri = new Uri(api.Url);
                return uri.Host;
            }
            catch (Exception)
            {
                return "";
            }
        }

        /// <summary>
        /// 本地估算用量。刻意偏保守（宁可略高估），并且只覆盖最近的对话窗口，
        /// 因为再早的内容本来就会被压缩掉。估算结果会带 Estimated 标记。
        /// </summary>
        private void EstimateUsage(AgentRunResult result)
        {
            int input = TokenEstimator.Estimate(BuildSystemPromptPreview()) + TokenEstimator.Estimate(Conversation.Summary);

            var messages = Conversation.Messages;
            int start = Math.Max(0, messages.Count - 20);
            for (int i = start; i < messages.Count; i++)
            {
                var message = messages[i];
                if (message == result.FinalMessage) continue;      // 输出的部分单独算
                input += TokenEstimator.EstimateMessage(message.Content, message.Reasoning);
            }

            var final = result.FinalMessage;
            int output = TokenEstimator.EstimateMessage(final.Content, final.Reasoning);

            result.Usage.InputTokens = input;
            result.Usage.OutputTokens = output;
            result.Usage.Estimated = true;

            if (_log != null)
            {
                _log.Log(LogLevel.Info, "usage", "服务端未报用量，已本地估算");
            }
        }

        private static bool IsUntitled(string title)
        {
            return string.IsNullOrWhiteSpace(title) || title == "新对话";
        }

        /// <summary>用第一句用户消息当标题。够用，且不需要额外请求。</summary>
        public static string SuggestTitle(string text)
        {
            var flat = WebText.OneLine(text ?? "", 30);
            if (string.IsNullOrEmpty(flat)) return "新对话";
            return flat;
        }

        // ═════════════════════════════════════════════════════════════════
        // 上下文组装
        // ═════════════════════════════════════════════════════════════════

        public async Task<AssembledPrompt> PreviewPromptAsync(Profile profile, Conversation conversation, CancellationToken ct)
        {
            var preview = new ChatService(_workspace, _store, _log, _clock, _http, _device, _embedder, _interaction, Registry);
            await preview.OpenProfileAsync(profile).ConfigureAwait(false);
            await preview.OpenConversationAsync(conversation ?? _workspace.CreateConversation(profile, "预览")).ConfigureAwait(false);
            // 用会话实际会用的那个请求副本，预览才和真正发出去的一致
            var resolvedApi = ModelSelection.Resolve(_workspace.ApiProfiles, profile, preview.Conversation);
            var context = await preview.BuildPromptContextAsync(null, ct, resolvedApi).ConfigureAwait(false);
            return PromptAssembler.Build(context);
        }

        /// <summary>
        /// 组上下文。resolvedApi 是"这一轮真正要发出去的请求副本"
        /// （ModelSelection.Resolve 的结果：会话级模型覆盖 + 元数据都已套上）。
        /// 必须传它，不能退回 Api —— Api 是存储里的原始 Provider 档案，
        /// 模型名可能不是会话正在用的那个，视觉能力也可能是旧的。
        /// 传错过一次，症状是模型级判定的"支持图片"到不了装配层，图片被静默丢掉。
        /// </summary>
        private async Task<PromptContext> BuildPromptContextAsync(Message userMessage, CancellationToken ct, ApiProfile resolvedApi = null)
        {
            var context = new PromptContext
            {
                Profile = Profile,
                Api = resolvedApi ?? Api,
                Conversation = Conversation,
                Library = _workspace.Library,
                User = _workspace.ResolveUserPersona(Profile),
                ToolsEnabled = Profile.Tools.ToolsEnabled,
                RegexRules = _workspace.Settings.RegexRules,
                NowLocal = _clock.UtcNow.ToLocalTime(),
                TimeZone = TimeZoneInfo.Local.DisplayName,
                DeviceInfo = _device != null ? _device.DeviceFamily : "",
                Locale = "zh-CN"
            };

            foreach (var tool in Registry.Resolve(Profile)) context.ToolNames.Add(tool.Name);

            // 知识库：档案 files 根目录下的文本文件（不递归，跳过二进制与特殊文件）
            context.Knowledge = await LoadKnowledgeAsync(ct).ConfigureAwait(false);

            // 记忆块
            if (Profile.Memory.Enabled && Memory != null)
            {
                var input = userMessage != null ? userMessage.Content : "";
                context.MemoryBlock = await Memory
                    .BuildContextBlockAsync(input, 15, Profile.Memory.UseGlobal, ct)
                    .ConfigureAwait(false);
            }

            return context;
        }

        private async Task<List<KnowledgeText>> LoadKnowledgeAsync(CancellationToken ct)
        {
            var result = new List<KnowledgeText>();
            if (string.IsNullOrEmpty(ProfileSlug)) return result;

            IReadOnlyList<string> files;
            try
            {
                files = await _store.ListFilesAsync(WorkspaceLayout.FilesDir(ProfileSlug), false).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return result;
            }

            int used = 0;
            var allowed = new string[] { ".md", ".txt", ".json", ".cs", ".py", ".js", ".ts", ".html", ".csv", ".yaml", ".yml", ".xml" };

            foreach (var file in files)
            {
                if (used >= PromptAssembler.KnowledgeTotalChars) break;
                ct.ThrowIfCancellationRequested();

                var extension = WorkspaceLayout.Extension(file);
                bool ok = false;
                foreach (var candidate in allowed)
                {
                    if (string.Equals(extension, candidate, StringComparison.OrdinalIgnoreCase)) { ok = true; break; }
                }
                if (!ok) continue;

                try
                {
                    var text = await _store.ReadTextAsync(file).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(text)) continue;

                    var body = TextUtil.Truncate(text, PromptAssembler.KnowledgePerFileChars);
                    result.Add(new KnowledgeText { Name = WorkspaceLayout.FileName(file), Content = body });
                    used += body.Length;
                }
                catch (Exception) { }
            }

            return result;
        }

        private ToolContext BuildToolContext(PromptContext promptContext, int depth)
        {
            var context = new ToolContext
            {
                Profile = Profile,
                Api = Api,
                ProfileSlug = ProfileSlug,
                Store = ToolStoreFactory != null ? ToolStoreFactory(Profile) : _store,
                Http = _http,
                Log = _log,
                Clock = _clock,
                Device = _device,
                Embedder = _embedder,
                Search = _workspace.Settings.Search,
                Memory = Memory,
                McpServers = _workspace.ResolveMcp(Profile),
                Skills = _workspace.ResolveSkills(Profile),

                // 脚本执行：运行时与宿主桥由组合根注入，开关跟着档案走。
                // 工具层还要再查一次 ScriptEnabled —— 两道门都过才真的执行，
                // 因为 Resolve() 只影响工具清单，改档案后清单可能还没刷新。
                ScriptRuntime = ScriptRuntime,
                ScriptHost = ScriptHost ?? NoScriptHostBridge.Instance,
                Interaction = _interaction,
                ScriptEnabled = Profile != null && Profile.Tools.ScriptEnabled
            };

            context.RunSubagentAsync = delegate (string task, CancellationToken subCt)
            {
                return RunSubagentAsync(task, depth + 1, promptContext, subCt);
            };

            return context;
        }

        /// <summary>
        /// 子代理：独立的一条会话，独立的系统提示词，工具集去掉 spawn_subagent 防止无限递归。
        /// </summary>
        private async Task<string> RunSubagentAsync(
            string task, int depth, PromptContext parent, CancellationToken ct)
        {
            if (depth > Limits.MaxSubagentDepth)
            {
                return "已经到子代理的最大嵌套深度（" + Limits.MaxSubagentDepth.ToString(CultureInfo.InvariantCulture) + "），不能再派了。";
            }

            var childProfile = Profile;
            var api = Api;
            if (api == null) return "没有可用的模型。";
            var apiKey = _workspace.GetApiKey(api);
            if (string.IsNullOrEmpty(apiKey)) return "没有可用的密钥。";

            var childConversation = new Conversation { ProfileId = Profile.Id, Title = "子代理" };
            childConversation.Messages.Add(new Message { Role = MessageRole.User, Content = task });

            var childContext = new PromptContext
            {
                Profile = childProfile,
                Api = api,
                Conversation = childConversation,
                Library = _workspace.Library,
                User = parent != null ? parent.User : null,
                ToolsEnabled = Profile.Tools.ToolsEnabled,
                RegexRules = _workspace.Settings.RegexRules,
                NowLocal = _clock.UtcNow.ToLocalTime(),
                DeviceInfo = parent != null ? parent.DeviceInfo : "",
                Locale = "zh-CN"
            };

            var childTools = new List<ITool>();
            foreach (var tool in Registry.Resolve(childProfile))
            {
                if (tool.Name == "spawn_subagent") continue;    // 不再嵌套
                childTools.Add(tool);
            }
            childContext.ToolNames.Clear();
            foreach (var tool in childTools) childContext.ToolNames.Add(tool.Name);

            // 子代理只保留最近一小段父上下文摘要
            if (parent != null && parent.Conversation != null && !string.IsNullOrEmpty(parent.Conversation.Summary))
            {
                childContext.RecentChatsBlock = "【父对话摘要】\n" + TextUtil.Truncate(parent.Conversation.Summary, 1200);
            }

            var request = new AgentRunRequest
            {
                Profile = childProfile,
                Api = api,
                ApiKey = apiKey,
                Conversation = childConversation,
                PromptContext = childContext,
                Tools = BuildToolContext(childContext, depth),
                Registry = Registry,
                AvailableTools = childTools,
                Permissions = _workspace.Permissions,
                Interaction = _interaction,
                Provider = ProviderFactory.Create(api, _http, _log),
                Observer = null,
                SubagentDepth = depth,
                UserInput = task
            };

            var result = await _runner.RunAsync(request, ct).ConfigureAwait(false);
            if (result.FinalMessage != null && !string.IsNullOrEmpty(result.FinalMessage.Content))
            {
                return result.FinalMessage.Content;
            }
            return string.IsNullOrEmpty(result.ErrorMessage) ? "子代理没有产出结论。" : result.ErrorMessage;
        }

        public int EstimateContextTokens()
        {
            if (Conversation == null || Profile == null) return 0;
            return ContextCompressor.EstimateConversationTokens(Conversation, BuildSystemPromptPreview(),
                Math.Max(0, Conversation.SummarizedUpTo + 1));
        }

        private string BuildSystemPromptPreview()
        {
            // 只为了估 token，用最省的方式拼一个近似值
            var builder = new StringBuilder();
            if (Profile.Persona != null)
            {
                builder.Append(Profile.Persona.SystemPrompt);
                builder.Append(Profile.Persona.Description);
                builder.Append(Profile.Persona.Personality);
            }

            return builder.ToString();
        }

        // ═════════════════════════════════════════════════════════════════
        // 压缩与记忆抽取
        // ═════════════════════════════════════════════════════════════════

        public async Task<CompactResult> PreviewCompactAsync(Profile profile, Conversation conversation, CancellationToken ct)
        {
            if (profile == null || conversation == null) return new CompactResult { Reason = "没有会话" };
            var preview = new ChatService(_workspace, _store, _log, _clock, _http, _device, _embedder, _interaction, Registry);
            await preview.OpenProfileAsync(profile).ConfigureAwait(false);
            var copy = JsonBridge.Clone(conversation);
            await preview.OpenConversationAsync(copy).ConfigureAwait(false);
            var effectiveApi = ModelSelection.Resolve(_workspace.ApiProfiles, profile, copy);
            if (effectiveApi == null) return new CompactResult { Reason = "没有配置模型" };
            return await preview.Compressor.CompactAsync(copy, preview.BuildSystemPromptPreview(),
                ModelContextLimits.GetEffectiveLimit(effectiveApi.Model, effectiveApi.MaxContextTokens),
                delegate (string prompt, CancellationToken token) { return preview.CompleteTextAsync(prompt, token); }, ct).ConfigureAwait(false);
        }

        public async Task<CompactResult> CompactAsync(CancellationToken ct)
        {
            if (Conversation == null || Api == null)
            {
                return new CompactResult { Reason = "没有会话或模型" };
            }

            var effectiveApi = ModelSelection.Resolve(_workspace.ApiProfiles, Profile, Conversation);
            if (effectiveApi == null) return new CompactResult { Reason = "选中的 Provider 不存在" };
            var limit = ModelContextLimits.GetEffectiveLimit(effectiveApi.Model, effectiveApi.MaxContextTokens);

            Func<string, CancellationToken, Task<string>> summarizer = delegate (string prompt, CancellationToken innerCt)
            {
                return CompleteTextAsync(prompt, innerCt);
            };

            var result = await Compressor
                .CompactAsync(Conversation, BuildSystemPromptPreview(), limit, summarizer, ct)
                .ConfigureAwait(false);

            if (result.Compacted)
            {
                await _workspace.SaveConversationAsync(ProfileSlug, Conversation).ConfigureAwait(false);
            }
            return result;
        }

        // ═════════════════════════════════════════════════════════════════
        // 副手模型：起标题、翻译、回复建议、压缩、记忆整理
        // ═════════════════════════════════════════════════════════════════

        /// <summary>当前会话该用的副手模型（请求用副本）。没有可用 Provider 时为 null。</summary>
        public ApiProfile ResolveAssistantApi()
        {
            return ModelSelection.ResolveAssistant(_workspace.ApiProfiles, _workspace.Settings.AssistantModel, Profile, Conversation);
        }

        private Task<string> CompleteTextAsync(string prompt, CancellationToken ct)
        {
            return CompleteTextAsync(prompt, 800, ct);
        }

        private Task<string> CompleteTextAsync(string prompt, int maxTokens, CancellationToken ct)
        {
            return CompleteTextAsync(prompt, maxTokens, 0, ct);
        }

        private async Task<string> CompleteTextAsync(string prompt, int maxTokens, double temperature, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(prompt)) return null;
            var api = ResolveAssistantApi();
            if (api == null || string.IsNullOrWhiteSpace(api.Model)) return null;
            var apiKey = _workspace.GetApiKey(api);
            if (api.RequiresApiKey && string.IsNullOrEmpty(apiKey)) return null;

            var provider = ProviderFactory.Create(api, _http, _log);
            int outputLimit = api.ResolveMetadata(api.Model).MaxOutputTokens;
            if (outputLimit > 0) maxTokens = Math.Min(maxTokens, outputLimit);
            var request = new ChatRequest { Api = api, ApiKey = apiKey, MaxTokens = maxTokens, Temperature = temperature };
            request.Turns.Add(ChatTurn.System("你是一个精确的文本处理助手，只输出被要求的内容，不要加解释。"));
            request.Turns.Add(ChatTurn.User(prompt));

            try
            {
                var result = await provider.CompleteAsync(request, ct).ConfigureAwait(false);
                return result.Ok ? result.Content : null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 一轮回复完成后的副手任务：必要时起 AI 标题、生成回复建议，然后落盘。
        /// 不放进 SendAsync：它们要多花一两次请求，不该让"正在生成"多转几秒。
        /// 调用方要保证它和下一次 SendAsync 不并发（两边都会保存同一个会话文件）。
        /// </summary>
        public async Task<FollowUpResult> RunFollowUpsAsync(CancellationToken ct)
        {
            var outcome = new FollowUpResult();
            var conversation = Conversation;
            if (Profile == null || conversation == null || conversation.Messages.Count == 0) return outcome;
            // 用户可能在等待期间切到别的会话；结果仍然属于、也只保存到发起时的那条会话
            var slug = Profile.Slug;
            outcome.ConversationId = conversation.Id;
            var last = conversation.Messages[conversation.Messages.Count - 1];
            if (last.Role != MessageRole.Assistant || string.IsNullOrWhiteSpace(last.Content)) return outcome;

            var settings = _workspace.Settings;
            bool changed = false;

            if (settings.AiTitles && conversation.TitleSource == "auto")
            {
                var title = AssistPrompts.CleanTitle(
                    await CompleteTextAsync(AssistPrompts.BuildTitlePrompt(conversation.Messages), 60, ct).ConfigureAwait(false));
                // 没拿到也要改掉 auto，免得之后每轮都再试一次
                conversation.TitleSource = "ai";
                if (!string.IsNullOrEmpty(title))
                {
                    conversation.Title = title;
                    outcome.Title = title;
                }
                changed = true;
            }

            if (settings.ReplySuggestions)
            {
                var persona = _workspace.ResolveUserPersona(Profile);
                var prompt = AssistPrompts.BuildSuggestionPrompt(conversation.Messages, Profile.Name, persona != null ? persona.Name : "");
                var suggestions = AssistPrompts.ParseSuggestions(await CompleteTextAsync(prompt, 200, 0.7, ct).ConfigureAwait(false));
                if (suggestions.Count > 0)
                {
                    conversation.Suggestions = suggestions;
                    conversation.SuggestionsFor = last.Id;
                    outcome.Suggestions = suggestions;
                    changed = true;
                }
            }

            if (changed)
            {
                await _workspace.SaveConversationAsync(slug, conversation).ConfigureAwait(false);
            }
            return outcome;
        }

        /// <summary>
        /// 到点就抽一次记忆。用轮数取模控制频率，避免每轮都花一次副模型调用。
        /// </summary>
        public async Task<int> MaybeExtractMemoryAsync(CancellationToken ct)
        {
            if (Profile == null || Conversation == null || Memory == null) return 0;
            if (!Profile.Memory.Enabled) return 0;

            int interval = Profile.Memory.MinMessagesForSummary;
            if (interval <= 0) interval = 10;

            int userTurns = 0;
            foreach (var message in Conversation.Messages)
            {
                if (message.Role == MessageRole.User) userTurns++;
            }
            if (userTurns == 0 || userTurns % interval != 0) return 0;

            return await ExtractMemoryAsync(ct).ConfigureAwait(false);
        }

        public async Task<int> ExtractMemoryAsync(CancellationToken ct)
        {
            if (Memory == null || Conversation == null) return 0;

            // 取最近若干条
            var recent = new List<Message>();
            int start = Math.Max(0, Conversation.Messages.Count - 12);
            for (int i = start; i < Conversation.Messages.Count; i++) recent.Add(Conversation.Messages[i]);
            if (recent.Count == 0) return 0;

            var prompt = MemoryService.BuildExtractionPrompt(recent, 5);
            var output = await CompleteTextAsync(prompt, 400, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(output)) return 0;

            var parsed = MemoryService.ParseExtraction(output, Conversation.Id);
            if (parsed.Count == 0) return 0;

            int added = 0;
            foreach (var item in parsed)
            {
                var scope = Profile.Memory.UseGlobal ? MemoryScope.Global : MemoryScope.Profile;
                var stored = Memory.Add(item.Text, item.Category, item.Importance, Conversation.Id, scope);
                if (stored != null) added++;
            }

            if (added > 0)
            {
                await Memory.SaveAsync().ConfigureAwait(false);
                if (Profile.Memory.UseGlobal) await Memory.SaveGlobalAsync().ConfigureAwait(false);
            }
            return added;
        }

        // ═════════════════════════════════════════════════════════════════
        // 附件
        // ═════════════════════════════════════════════════════════════════

        /// <summary>把图片附件读成 base64（只读一次，读完缓存在内存里，不落盘）。</summary>
        private async Task LoadImageAttachmentsAsync(Message message, CancellationToken ct)
        {
            if (message == null || message.Attachments == null) return;
            if (_device == null) return;

            foreach (var attachment in message.Attachments)
            {
                if (!attachment.IsImage) continue;
                if (!string.IsNullOrEmpty(attachment.ImageBase64)) continue;
                if (string.IsNullOrEmpty(attachment.Path)) continue;

                try
                {
                    var base64 = await _device.ReadImageAsBase64Async(attachment.Path, ct).ConfigureAwait(false);
                    attachment.ImageBase64 = base64;
                }
                catch (Exception) { }
            }
        }
    }
}
