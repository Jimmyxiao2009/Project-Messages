// Core/Models/Conversations.cs —— 会话、消息、步骤、附件、用量
//
// 与旧项目的关键差异：
//   1) 一个会话一个文件（旧项目把所有会话塞进一个巨大 JSON，每次改动整份重写）。
//   2) 步骤（StepRecord）显式落盘。旧项目靠 ThinkSteps 内联在气泡上、且与 UI 状态耦合，
//      换个界面就读不出来。这里步骤是数据，不是视图状态。
//   3) 推理内容与正文分开存，DeepSeek-R1 这类模型多轮工具调用时不能丢 reasoning。

using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Yanshuai.Messages.Core
{
    public sealed class Attachment
    {
        /// <summary>text | image</summary>
        [JsonProperty("kind")] public string Kind { get; set; } = "text";
        [JsonProperty("name")] public string Name { get; set; } = "";
        /// <summary>相对档案文件夹的路径，例如 files/notes.md。不内联 base64。</summary>
        [JsonProperty("path")] public string Path { get; set; } = "";
        [JsonProperty("mime")] public string MimeType { get; set; } = "";
        [JsonProperty("size")] public long SizeBytes { get; set; }

        /// <summary>
        /// 运行时装载的图片内容。**不落盘**（JsonIgnore）——
        /// 旧项目把 base64 内联进消息 JSON，一条带图消息能让整个数据文件膨胀几十兆，
        /// 每次保存都要整份重写。这里图片只存路径，需要发请求时才按需读进来。
        /// </summary>
        [JsonIgnore] public string ImageBase64 { get; set; }

        [JsonIgnore] public bool IsImage { get { return string.Equals(Kind, "image", StringComparison.OrdinalIgnoreCase); } }
    }

    public sealed class ToolCallRecord
    {
        [JsonProperty("id")] public string Id { get; set; } = "";
        [JsonProperty("name")] public string Name { get; set; } = "";
        [JsonProperty("args")] public string ArgumentsJson { get; set; } = "";
        /// <summary>结果预览（截断过），供重建轨迹用；完整结果在对应的 tool 消息里。</summary>
        [JsonProperty("preview")] public string ResultPreview { get; set; } = "";
        [JsonProperty("ok")] public bool Ok { get; set; } = true;
    }

    /// <summary>过程轨道上的一个步骤。这是"数据"，不是 UI 状态。</summary>
    public sealed class StepRecord
    {
        [JsonProperty("kind")] public StepKind Kind { get; set; }
        [JsonProperty("state")] public StepState State { get; set; }
        /// <summary>步骤标题：工具名，或思考的短标签。</summary>
        [JsonProperty("title")] public string Title { get; set; } = "";
        /// <summary>副标题：参数摘要、文件大小、耗时等。</summary>
        [JsonProperty("subtitle")] public string Subtitle { get; set; } = "";
        /// <summary>展开后的正文（思考全文 / 工具输出）。</summary>
        [JsonProperty("body")] public string Body { get; set; } = "";
        [JsonProperty("startedAtUtc")] public DateTimeOffset StartedAtUtc { get; set; } = DateTimeOffset.UtcNow;
        [JsonProperty("durationMs")] public long DurationMs { get; set; }
        /// <summary>命令类工具的退出码，非命令类为 null。</summary>
        [JsonProperty("exitCode")] public int? ExitCode { get; set; }

        /// <summary>
        /// 关联 id：工具步骤填 tool_call 的 id，思考步骤填 "消息id:reasoning"。
        /// 实时步骤和从消息推导出的步骤靠它去重 —— 不填就会整段重复显示。
        /// </summary>
        [JsonIgnore] public string CallId { get; set; } = "";

        [JsonIgnore]
        public bool IsRunning { get { return State == StepState.Running; } }
    }

    /// <summary>
    /// 一条消息。
    ///
    /// 注意：过程步骤**不落在这里**。它们由 TimelineProjector 从消息序列推导
    /// （assistant 的 reasoning / toolCalls + 对应 tool 消息的结果）。
    /// 旧项目把步骤同时存在"气泡视图状态"和"模型数据"两边，两边一不一致就出现
    /// 「轨道和正文对不上」，而且换界面就读不出来。单一数据源就没有这个问题。
    /// </summary>
    public sealed class Message
    {
        [JsonProperty("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
        [JsonProperty("role")] public MessageRole Role { get; set; }
        [JsonProperty("content")] public string Content { get; set; } = "";

        /// <summary>思维链原文。工具循环的每一轮都要带上，否则 DeepSeek-R1 这类模型多轮会断推理。</summary>
        [JsonProperty("reasoning")] public string Reasoning { get; set; } = "";

        /// <summary>assistant 这一轮发起的工具调用。</summary>
        [JsonProperty("toolCalls")] public List<ToolCallRecord> ToolCalls { get; set; }

        /// <summary>role=tool 时，这条结果对应哪个调用。</summary>
        [JsonProperty("toolCallId")] public string ToolCallId { get; set; } = "";

        [JsonProperty("attachments")] public List<Attachment> Attachments { get; set; }

        [JsonProperty("createdAtUtc")] public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>是否属于一轮的最终输出。只有最后一条 assistant 才是。</summary>
        [JsonProperty("isFinal")] public bool IsFinal { get; set; }

        [JsonProperty("failed")] public bool Failed { get; set; }
        [JsonProperty("errorCode")] public string ErrorCode { get; set; } = "";

        [JsonProperty("tokensIn")] public int TokensIn { get; set; }
        [JsonProperty("tokensOut")] public int TokensOut { get; set; }

        /// <summary>
        /// 分支锚点。同一个"位置"的多条候选消息共享一个 NodeId。
        /// 普通消息的 NodeId 等于自己的 Id；只有真正产生过分支（重生成/编辑）的消息
        /// 才会在 Conversation.Variants 里出现一条以它为键的候选列表。
        /// </summary>
        [JsonProperty("nodeId")] public string NodeId { get; set; } = "";

        /// <summary>这条消息当时用的是哪个模型。模型被删/改名后历史仍能正确显示。</summary>
        [JsonProperty("modelName")] public string ModelName { get; set; } = "";

        /// <summary>
        /// 译文缓存，键是语言标签（如 "zh-CN"）。
        ///
        /// 存在消息上而不是"当前显示语言"这种全局状态上：翻译是一次模型调用，
        /// 会花钱也会花时间；用户来回切语言时不该重复翻译。
        /// 原文永远保留在 Content 里，译文只是叠加显示，不做覆盖。
        /// </summary>
        [JsonProperty("translations")] public Dictionary<string, string> Translations { get; set; }

        /// <summary>这条消息当时用的 provider 显示名（同上，用于历史回看）。</summary>
        [JsonProperty("providerName")] public string ProviderName { get; set; } = "";

        public Message()
        {
            ToolCalls = new List<ToolCallRecord>();
            Attachments = new List<Attachment>();
        }

        [JsonIgnore]
        public bool IsUser { get { return Role == MessageRole.User; } }

        [JsonIgnore]
        public bool HasToolCalls { get { return ToolCalls != null && ToolCalls.Count > 0; } }

        /// <summary>保证 NodeId 有值。反序列化老数据后调用。</summary>
        public void EnsureNodeId()
        {
            if (string.IsNullOrEmpty(NodeId)) NodeId = Id;
        }

        /// <summary>取译文；没有就返回 null。</summary>
        public string GetTranslation(string language)
        {
            if (string.IsNullOrEmpty(language) || Translations == null) return null;
            string value;
            return Translations.TryGetValue(language, out value) ? value : null;
        }

        public void SetTranslation(string language, string text)
        {
            if (string.IsNullOrEmpty(language)) return;
            if (Translations == null) Translations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Translations[language] = text ?? "";
        }

        public Message Clone()
        {
            return JsonBridge.Clone(this);
        }
    }

    public sealed class UsageInfo
    {
        [JsonProperty("input")] public int InputTokens { get; set; }
        [JsonProperty("output")] public int OutputTokens { get; set; }
        [JsonProperty("cached")] public int CachedTokens { get; set; }

        /// <summary>
        /// 这些数字是本地估算的，不是服务端报的。
        /// 流式响应默认不返回 usage（OpenAI 要显式要 stream_options 才给，
        /// 而很多中转网关不认这个字段），所以这时候用本地估算兜底，
        /// 并在界面上标一个"约"，而不是显示 0。
        /// </summary>
        [JsonProperty("estimated")] public bool Estimated { get; set; }

        [JsonIgnore] public int Total { get { return InputTokens + OutputTokens; } }
    }

    public sealed class Conversation
    {
        [JsonProperty("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
        [JsonProperty("profileId")] public string ProfileId { get; set; } = "";
        [JsonProperty("title")] public string Title { get; set; } = "新对话";

        /// <summary>
        /// 标题来源：auto = 第一句话截断（可被 AI 标题替换）；ai = 副手模型起的；
        /// 空 = 用户起的或旧数据，一律不自动改。
        /// </summary>
        [JsonProperty("titleSource")] public string TitleSource { get; set; } = "";

        /// <summary>副手模型给的回复建议，只对 SuggestionsFor 这条助手消息有效。</summary>
        [JsonProperty("suggestions")] public List<string> Suggestions { get; set; }
        [JsonProperty("suggestionsFor")] public string SuggestionsFor { get; set; } = "";

        /// <summary>最后一条是助手消息且建议正是为它生成的，才返回；否则空表。</summary>
        public List<string> CurrentSuggestions()
        {
            if (Suggestions == null || Suggestions.Count == 0 || Messages == null || Messages.Count == 0) return new List<string>();
            var last = Messages[Messages.Count - 1];
            if (last.Role != MessageRole.Assistant || last.Id != SuggestionsFor) return new List<string>();
            return new List<string>(Suggestions);
        }
        [JsonProperty("createdAtUtc")] public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
        [JsonProperty("updatedAtUtc")] public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
        [JsonProperty("archived")] public bool Archived { get; set; }
        [JsonProperty("pinned")] public bool Pinned { get; set; }

        [JsonProperty("messages")] public List<Message> Messages { get; set; }

        /// <summary>已写入的上下文摘要（替换式，不堆叠）。</summary>
        [JsonProperty("summary")] public string Summary { get; set; } = "";
        /// <summary>摘要已经覆盖到第几条消息（含）。</summary>
        [JsonProperty("summarizedUpTo")] public int SummarizedUpTo { get; set; } = -1;
        [JsonProperty("lastCompactAtUtc")] public DateTimeOffset? LastCompactAtUtc { get; set; }

        [JsonProperty("totalIn")] public long TotalInputTokens { get; set; }
        [JsonProperty("totalOut")] public long TotalOutputTokens { get; set; }
        [JsonProperty("lastUsage")] public UsageInfo LastUsage { get; set; }

        /// <summary>null 表示跟随档案外观。</summary>
        [JsonProperty("appearance")] public AppearanceOptions AppearanceOverride { get; set; }

        /// <summary>
        /// 会话级模型覆盖（"这一条会话临时换个模型"）。
        /// 留空表示跟随档案默认。这样用户可以随时切换而不用改档案，
        /// 也不会在同一个会话里把上下文切碎。
        /// </summary>
        [JsonProperty("apiProfileId")] public string ModelApiProfileId { get; set; } = "";
        [JsonProperty("modelOverride")] public string ModelOverride { get; set; } = "";

        /// <summary>会话级人设覆盖（档案允许时生效）。</summary>
        [JsonProperty("systemPromptOverride")] public string SystemPromptOverride { get; set; } = "";

        /// <summary>
        /// 分支候选池。键是 NodeId，值是该位置的所有候选（**含当前选中的那条**）。
        ///
        /// 为什么这样设计而不是"每个位置一个节点对象"：Messages 仍然是"当前活跃路径"，
        /// 时间线投影、压缩、提示词装配全都不需要改；只有在用户真的重生成或编辑过的地方，
        /// 才会在 Variants 里多出一份候选。没产生分支的会话，这个字典就是空的。
        /// </summary>
        [JsonProperty("variants")] public Dictionary<string, List<Message>> Variants { get; set; }

        /// <summary>群聊成员（多角色）。为空表示单角色会话。</summary>
        [JsonProperty("members")] public List<GroupMember> Members { get; set; }

        /// <summary>群聊回复模式：round_robin（轮流）/ mention（点名）/ free（自由）。</summary>
        [JsonProperty("groupReplyMode")] public string GroupReplyMode { get; set; } = "round_robin";

        public Conversation()
        {
            Messages = new List<Message>();
            LastUsage = new UsageInfo();
            Variants = new Dictionary<string, List<Message>>(StringComparer.Ordinal);
            Members = new List<GroupMember>();
        }

        public void Repair()
        {
            if (Messages == null) Messages = new List<Message>();
            if (LastUsage == null) LastUsage = new UsageInfo();
            if (Variants == null) Variants = new Dictionary<string, List<Message>>(StringComparer.Ordinal);
            if (Members == null) Members = new List<GroupMember>();
            if (Suggestions == null) Suggestions = new List<string>();
            if (TitleSource == null) TitleSource = "";
            if (SuggestionsFor == null) SuggestionsFor = "";

            foreach (var m in Messages)
            {
                if (m.ToolCalls == null) m.ToolCalls = new List<ToolCallRecord>();
                if (m.Attachments == null) m.Attachments = new List<Attachment>();
                if (m.Translations == null) m.Translations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                m.EnsureNodeId();
            }

            // 候选池里的消息同样要补齐，否则切分支时会空引用
            foreach (var pair in Variants)
            {
                if (pair.Value == null) continue;
                foreach (var m in pair.Value)
                {
                    if (m == null) continue;
                    if (m.ToolCalls == null) m.ToolCalls = new List<ToolCallRecord>();
                    if (m.Attachments == null) m.Attachments = new List<Attachment>();
                    if (m.Translations == null) m.Translations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (string.IsNullOrEmpty(m.NodeId)) m.NodeId = pair.Key;
                }
            }

            foreach (var member in Members) member.Repair();
        }

        // ═════════════════════════════════════════════════════════════════
        // 分支操作
        // ═════════════════════════════════════════════════════════════════

        /// <summary>追加一条消息（走 Messages，不产生分支）。</summary>
        public void Append(Message message)
        {
            if (message == null) return;
            message.EnsureNodeId();
            Messages.Add(message);
        }

        /// <summary>
        /// 在指定位置产生一个新候选并选中它（"重新生成"与"编辑"都走这里）。
        /// 返回该候选在候选池中的下标；失败返回 -1。
        /// </summary>
        public int AddVariant(int index, Message candidate)
        {
            if (candidate == null) return -1;
            if (index < 0 || index >= Messages.Count) return -1;

            var current = Messages[index];
            var nodeId = current.NodeId;
            if (string.IsNullOrEmpty(nodeId)) nodeId = current.Id;
            current.NodeId = nodeId;

            List<Message> pool;
            if (!Variants.TryGetValue(nodeId, out pool))
            {
                // 第一次产生分支：把原消息作为第 0 个候选放进池里
                pool = new List<Message> { current };
                Variants[nodeId] = pool;
            }

            candidate.NodeId = nodeId;
            pool.Add(candidate);
            if (index <= SummarizedUpTo) { Summary = ""; SummarizedUpTo = -1; }
            Messages[index] = candidate;
            Touch();
            return pool.Count - 1;
        }

        /// <summary>某个位置有几条候选。没分支过返回 1。</summary>
        public int VariantCount(int index)
        {
            if (index < 0 || index >= Messages.Count) return 0;

            var nodeId = Messages[index].NodeId;
            if (string.IsNullOrEmpty(nodeId)) return 1;

            List<Message> pool;
            if (!Variants.TryGetValue(nodeId, out pool) || pool == null || pool.Count == 0) return 1;
            return pool.Count;
        }

        /// <summary>当前选中第几条候选（从 0 开始）。没分支过返回 0。</summary>
        public int VariantIndex(int index)
        {
            if (index < 0 || index >= Messages.Count) return 0;

            var current = Messages[index];
            var nodeId = current.NodeId;
            if (string.IsNullOrEmpty(nodeId)) return 0;

            List<Message> pool;
            if (!Variants.TryGetValue(nodeId, out pool) || pool == null) return 0;

            for (int i = 0; i < pool.Count; i++)
            {
                if (string.Equals(pool[i].Id, current.Id, StringComparison.Ordinal)) return i;
            }
            return 0;
        }

        /// <summary>切到某个位置的另一条候选。返回是否成功。</summary>
        public bool SelectVariant(int index, int variantIndex)
        {
            if (index < 0 || index >= Messages.Count) return false;

            var nodeId = Messages[index].NodeId;
            if (string.IsNullOrEmpty(nodeId)) return false;

            List<Message> pool;
            if (!Variants.TryGetValue(nodeId, out pool) || pool == null) return false;
            if (variantIndex < 0 || variantIndex >= pool.Count) return false;

            if (index <= SummarizedUpTo) { Summary = ""; SummarizedUpTo = -1; }
            Messages[index] = pool[variantIndex];
            Touch();
            return true;
        }

        /// <summary>
        /// 从指定位置分叉出一条新会话（复制到该位置为止，含）。
        /// 附件路径沿用原文件（只读引用，不做物理拷贝），这样分叉不会让存储翻倍。
        /// </summary>
        public Conversation ForkAt(int index, string title)
        {
            var fork = new Conversation
            {
                ProfileId = ProfileId,
                Title = title,
                Summary = Summary,
                SummarizedUpTo = SummarizedUpTo,
                AppearanceOverride = AppearanceOverride,
                ModelApiProfileId = ModelApiProfileId,
                ModelOverride = ModelOverride,
                SystemPromptOverride = SystemPromptOverride,
                GroupReplyMode = GroupReplyMode
            };

            int last = index < 0 ? Messages.Count - 1 : Math.Min(index, Messages.Count - 1);
            for (int i = 0; i <= last; i++)
            {
                var copy = Messages[i].Clone();
                copy.Id = Guid.NewGuid().ToString("N");
                copy.NodeId = copy.Id;          // 分叉后重新开始，不继承候选池
                fork.Messages.Add(copy);
            }

            if (fork.SummarizedUpTo > last) { fork.Summary = ""; fork.SummarizedUpTo = -1; }
            foreach (var member in Members)
            {
                fork.Members.Add(new GroupMember
                {
                    ProfileId = member.ProfileId,
                    Name = member.Name,
                    Avatar = member.Avatar,
                    Enabled = member.Enabled,
                    Greeting = member.Greeting,
                    Order = member.Order
                });
            }

            return fork;
        }

        /// <summary>生成不重名的分叉标题：标题(1)、标题(2)…</summary>
        public static string ForkTitle(string baseTitle, IReadOnlyList<string> existingTitles)
        {
            var root = string.IsNullOrWhiteSpace(baseTitle) ? "对话" : baseTitle.Trim();

            if (!Contains(existingTitles, root)) return root;

            for (int i = 1; i < 1000; i++)
            {
                var candidate = root + "(" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
                if (!Contains(existingTitles, candidate)) return candidate;
            }
            return root + "(" + Guid.NewGuid().ToString("N").Substring(0, 4) + ")";
        }

        private static bool Contains(IReadOnlyList<string> list, string value)
        {
            if (list == null) return false;
            foreach (var item in list)
            {
                if (string.Equals(item, value, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        public void Touch()
        {
            UpdatedAtUtc = DateTimeOffset.UtcNow;
        }

        public Message LastUserMessage()
        {
            for (int i = Messages.Count - 1; i >= 0; i--)
            {
                if (Messages[i].Role == MessageRole.User) return Messages[i];
            }
            return null;
        }

        /// <summary>取最后一条最终输出，用于列表预览。</summary>
        public Message LastOutput()
        {
            for (int i = Messages.Count - 1; i >= 0; i--)
            {
                var m = Messages[i];
                if (m.Role == MessageRole.Assistant && m.IsFinal && !string.IsNullOrEmpty(m.Content)) return m;
            }
            return null;
        }

        public string Preview()
        {
            var m = LastOutput();
            if (m != null) return TextUtil.Truncate(TextUtil.Normalize(m.Content), 60);
            var u = LastUserMessage();
            if (u != null) return TextUtil.Truncate(TextUtil.Normalize(u.Content), 60);
            return "";
        }
    }

    /// <summary>会话索引项。列表页只读这个文件，不打开每个会话。</summary>
    public sealed class ConversationIndexEntry
    {
        [JsonProperty("id")] public string Id { get; set; } = "";
        [JsonProperty("profileId")] public string ProfileId { get; set; } = "";
        [JsonProperty("title")] public string Title { get; set; } = "";
        [JsonProperty("preview")] public string Preview { get; set; } = "";
        [JsonProperty("updatedAtUtc")] public DateTimeOffset UpdatedAtUtc { get; set; }
        [JsonProperty("createdAtUtc")] public DateTimeOffset CreatedAtUtc { get; set; }
        [JsonProperty("archived")] public bool Archived { get; set; }
        [JsonProperty("pinned")] public bool Pinned { get; set; }
        [JsonProperty("tokens")] public long Tokens { get; set; }
        [JsonProperty("needsAttention")] public bool NeedsAttention { get; set; }
        [JsonProperty("running")] public bool Running { get; set; }
        [JsonProperty("failed")] public bool Failed { get; set; }
    }

    public sealed class ConversationIndex
    {
        [JsonProperty("entries")] public List<ConversationIndexEntry> Entries { get; set; }

        public ConversationIndex()
        {
            Entries = new List<ConversationIndexEntry>();
        }

        public ConversationIndexEntry Find(string id)
        {
            foreach (var e in Entries)
            {
                if (e.Id == id) return e;
            }
            return null;
        }

        public void Upsert(ConversationIndexEntry entry)
        {
            var existing = Find(entry.Id);
            if (existing == null)
            {
                Entries.Add(entry);
                return;
            }
            existing.ProfileId = entry.ProfileId;
            existing.Title = entry.Title;
            existing.Preview = entry.Preview;
            existing.UpdatedAtUtc = entry.UpdatedAtUtc;
            existing.Archived = entry.Archived;
            existing.Pinned = entry.Pinned;
            existing.Tokens = entry.Tokens;
            existing.NeedsAttention = entry.NeedsAttention;
            existing.Running = entry.Running;
            existing.Failed = entry.Failed;
        }

        public bool Remove(string id)
        {
            for (int i = 0; i < Entries.Count; i++)
            {
                if (Entries[i].Id == id) { Entries.RemoveAt(i); return true; }
            }
            return false;
        }

        /// <summary>排序：置顶优先，然后按更新时间倒序。</summary>
        public List<ConversationIndexEntry> Sorted(bool includeArchived)
        {
            var list = new List<ConversationIndexEntry>();
            foreach (var e in Entries)
            {
                if (!includeArchived && e.Archived) continue;
                list.Add(e);
            }
            list.Sort(delegate (ConversationIndexEntry a, ConversationIndexEntry b)
            {
                if (a.Pinned != b.Pinned) return a.Pinned ? -1 : 1;
                return b.UpdatedAtUtc.CompareTo(a.UpdatedAtUtc);
            });
            return list;
        }
    }

    /// <summary>时间线投影出来的"行"。UI 直接消费这个，不再自己拼。</summary>
    public sealed class TimelineRow
    {
        public RowKind Kind { get; set; }
        /// <summary>稳定键，用于折叠状态持久化。</summary>
        public string Key { get; set; }
        public MessageIdRef Source { get; set; }

        // User / Output
        public string Text { get; set; }
        public List<Attachment> Attachments { get; set; }
        public bool IsFinal { get; set; }
        public bool Failed { get; set; }
        public string ErrorCode { get; set; }

        // Process
        public List<StepRecord> Steps { get; set; }
        public StepState State { get; set; }
        public string Summary { get; set; }
        public bool Collapsed { get; set; }

        public DateTimeOffset Time { get; set; }
        public int TokensIn { get; set; }
        public int TokensOut { get; set; }

        public TimelineRow()
        {
            Attachments = new List<Attachment>();
            Steps = new List<StepRecord>();
        }
    }

    public sealed class MessageIdRef
    {
        public string MessageId { get; set; }
        public int Index { get; set; }
    }
}
