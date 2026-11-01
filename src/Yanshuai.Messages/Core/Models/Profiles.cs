// Core/Models/Profiles.cs —— 档案与人设、模型档案、生成参数
//
// 字段形状对齐设计稿 §3.4（也就是对照 rikkahub 的 Assistant 模型）。
// 落盘用 Newtonsoft，属性名全部显式标注，改名不会悄悄破坏已有数据。

using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Yanshuai.Messages.Core
{
    /// <summary>
    /// 模型接入配置（一个"API 档案"）。API Key 不在这里 —— 它进 ISecretStore，
    /// 键是 "api." + Id。旧项目把 Key 明文写在 JSON 里，这次不再犯。
    /// </summary>
    public sealed class ApiProfile
    {
        [JsonProperty("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
        [JsonProperty("name")] public string Name { get; set; } = "新配置";

        /// <summary>openai（含所有 OpenAI 兼容端点）或 anthropic（原生 Messages API）。</summary>
        [JsonProperty("provider")] public string Provider { get; set; } = "openai";

        [JsonProperty("url")] public string Url { get; set; } = "";
        [JsonProperty("modelsUrl")] public string ModelsUrl { get; set; } = "";
        [JsonProperty("requiresApiKey")] public bool RequiresApiKey { get; set; } = true;
        [JsonProperty("model")] public string Model { get; set; } = "";
        [JsonProperty("subModel")] public string SubModel { get; set; } = "";
        [JsonProperty("vision")] public bool VisionEnabled { get; set; }

        /// <summary>0 表示按模型名查内置窗口表。</summary>
        [JsonProperty("maxContextTokens")] public int MaxContextTokens { get; set; }

        [JsonProperty("customHeaders")] public Dictionary<string, string> CustomHeaders { get; set; }
        [JsonProperty("customBody")] public Dictionary<string, object> CustomBody { get; set; }

        [JsonProperty("cachedModels")] public List<string> CachedModels { get; set; }
        [JsonProperty("manualModels")] public List<string> ManualModels { get; set; }
        [JsonProperty("modelOverrides")] public Dictionary<string, ModelMetadata> ModelOverrides { get; set; }

        /// <summary>
        /// 每个模型的元数据缓存（窗口 / 模态 / 推理 / 价格档）。
        /// 来源有三层，优先级：手动改过的（Source=Manual） > 服务端返回的（Server） > 内置表推断的（BuiltIn）。
        /// 存下来是为了：拉过一次之后离线也能显示窗口与能力，不用每次重拉。
        /// </summary>
        [JsonProperty("modelMetadata")] public Dictionary<string, ModelMetadata> ModelMetadataCache { get; set; }

        [JsonProperty("modelsFetchedAtUtc")] public DateTimeOffset? ModelsFetchedAtUtc { get; set; }
        [JsonProperty("modelsFetchError")] public string ModelsFetchError { get; set; } = "";

        public ApiProfile()
        {
            CustomHeaders = new Dictionary<string, string>();
            CustomBody = new Dictionary<string, object>();
            CachedModels = new List<string>();
            ManualModels = new List<string>();
            ModelOverrides = new Dictionary<string, ModelMetadata>(StringComparer.Ordinal);
            ModelMetadataCache = new Dictionary<string, ModelMetadata>(StringComparer.Ordinal);
        }

        /// <summary>取某个模型的元数据：缓存优先，其次内置表，最后返回"全未知"。</summary>
        public ModelMetadata ResolveMetadata(string modelId)
        {
            var result = ModelMetadataTable.Resolve(modelId);
            ModelMetadata cached;
            if (!string.IsNullOrEmpty(modelId) && ModelMetadataCache != null &&
                ModelMetadataCache.TryGetValue(modelId, out cached) && cached != null) result.ApplyOverride(cached);
            if (!string.IsNullOrEmpty(modelId) && ModelOverrides != null &&
                ModelOverrides.TryGetValue(modelId, out cached) && cached != null) result.ApplyOverride(cached);
            return result;
        }

        public void ApplyModelList(ModelListResult list, DateTimeOffset fetchedAt)
        {
            if (list == null || !list.Ok) throw new InvalidOperationException("不能保存未完成的模型列表。");
            if (ModelOverrides == null) ModelOverrides = new Dictionary<string, ModelMetadata>();
            if (ManualModels == null) ManualModels = new List<string>();
            if (ModelMetadataCache != null) foreach (var pair in ModelMetadataCache) {
                if (pair.Value == null || pair.Value.Source != MetadataSource.Manual) continue;
                if (!ModelOverrides.ContainsKey(pair.Key)) ModelOverrides[pair.Key] = pair.Value.Clone();
                if (!ManualModels.Contains(pair.Key)) ManualModels.Add(pair.Key);
            }
            CachedModels = list.Models;
            ModelMetadataCache = new Dictionary<string, ModelMetadata>(StringComparer.Ordinal);
            foreach (var entry in list.Entries) ModelMetadataCache[entry.ModelId] = entry.Clone();
            ModelsFetchedAtUtc = fetchedAt;
            if (string.IsNullOrWhiteSpace(Model) && CachedModels.Count > 0) Model = CachedModels[0];
        }

        [JsonIgnore]
        public bool IsAnthropic
        {
            get { return string.Equals(Provider, "anthropic", StringComparison.OrdinalIgnoreCase); }
        }

        [JsonIgnore]
        public bool IsGemini
        {
            get
            {
                return string.Equals(Provider, "gemini", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Provider, "google", StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>凭据存储键。</summary>
        [JsonIgnore]
        public string SecretKey
        {
            get { return "api." + Id; }
        }

        /// <summary>缺 URL 时按 provider 补默认值，避免用户新建后第一次调用直接 400。</summary>
        [JsonIgnore]
        public string EffectiveUrl
        {
            get
            {
                if (!string.IsNullOrEmpty(Url)) return IsGemini ? Url : ProviderEndpoints.ChatUrl(Url, IsAnthropic);
                if (IsGemini) return GeminiChatProvider.DefaultBase;
                return IsAnthropic
                    ? "https://api.anthropic.com/v1/messages"
                    : "https://api.openai.com/v1/chat/completions";
            }
        }

        public ApiProfile Clone()
        {
            return JsonBridge.Clone(this);
        }
    }

    /// <summary>人设。字段与 SillyTavern 角色卡 v2/v3 一一对应，导入时直接落位。</summary>
    public sealed class Persona
    {
        [JsonProperty("name")] public string Name { get; set; } = "新档案";
        [JsonProperty("systemPrompt")] public string SystemPrompt { get; set; } = "";
        [JsonProperty("description")] public string Description { get; set; } = "";
        [JsonProperty("personality")] public string Personality { get; set; } = "";
        [JsonProperty("scenario")] public string Scenario { get; set; } = "";
        [JsonProperty("firstMessage")] public string FirstMessage { get; set; } = "";
        [JsonProperty("exampleDialogue")] public string ExampleDialogue { get; set; } = "";
        [JsonProperty("postHistoryInstructions")] public string PostHistoryInstructions { get; set; } = "";
        [JsonProperty("creator")] public string Creator { get; set; } = "";
        [JsonProperty("creatorNotes")] public string CreatorNotes { get; set; } = "";
        [JsonProperty("characterVersion")] public string CharacterVersion { get; set; } = "";

        [JsonProperty("tags")] public List<string> Tags { get; set; }

        /// <summary>导入时的卡规范版本（2 或 3），0 表示手建的。</summary>
        [JsonProperty("cardSpecVersion")] public int CardSpecVersion { get; set; }

        /// <summary>卡片里未知的扩展字段原样保留，导出时能还回去，不丢数据。</summary>
        [JsonProperty("extensions")] public Dictionary<string, object> Extensions { get; set; }

        public Persona()
        {
            Tags = new List<string>();
            Extensions = new Dictionary<string, object>();
        }
    }

    public sealed class PresetMessage
    {
        [JsonProperty("role")] public string Role { get; set; } = "user";
        [JsonProperty("content")] public string Content { get; set; } = "";
    }

    /// <summary>档案侧的模型与请求参数覆盖。留空（null）表示"用上游默认"。</summary>
    public sealed class ProfileApiOptions
    {
        [JsonProperty("profileId")] public string ProfileId { get; set; } = "";
        [JsonProperty("model")] public string Model { get; set; } = "";
        [JsonProperty("provider")] public string Provider { get; set; } = "";
        [JsonProperty("temperature")] public double? Temperature { get; set; }
        [JsonProperty("topP")] public double? TopP { get; set; }
        [JsonProperty("maxTokens")] public int? MaxTokens { get; set; }
        [JsonProperty("reasoningLevel")] public string ReasoningLevel { get; set; } = "auto";
        /// <summary>每次请求带多少条最近消息，0 = 不限。</summary>
        [JsonProperty("contextMessageSize")] public int ContextMessageSize { get; set; }
        [JsonProperty("visionEnabled")] public bool? VisionEnabled { get; set; }
        [JsonProperty("maxContextTokens")] public int MaxContextTokens { get; set; }
        [JsonProperty("subModel")] public string SubModel { get; set; } = "";
        [JsonProperty("customHeaders")] public Dictionary<string, string> CustomHeaders { get; set; }
        [JsonProperty("customBody")] public Dictionary<string, object> CustomBody { get; set; }

        public ProfileApiOptions()
        {
            CustomHeaders = new Dictionary<string, string>();
            CustomBody = new Dictionary<string, object>();
        }
    }

    public sealed class GenerationOptions
    {
        [JsonProperty("streamOutput")] public bool StreamOutput { get; set; } = true;

        /// <summary>包裹每条用户消息；必须含 {{message}}，否则用户输入会丢失。</summary>
        [JsonProperty("messageTemplate")] public string MessageTemplate { get; set; } = "{{message}}";

        [JsonProperty("presetMessages")] public List<PresetMessage> PresetMessages { get; set; }

        public GenerationOptions()
        {
            PresetMessages = new List<PresetMessage>();
        }
    }

    public sealed class ToolOptions
    {
        /// <summary>
        /// 总开关。关掉就完全不调用工具，走纯流式对话。
        /// 单靠 Enabled 列表表达不了"一个都不用"（空列表的语义是"全用"），
        /// 所以必须有一个显式开关。
        /// </summary>
        [JsonProperty("toolsEnabled")] public bool ToolsEnabled { get; set; } = true;

        /// <summary>启用哪些工具。为空表示"全部"（前提是 ToolsEnabled 为真）。</summary>
        [JsonProperty("enabled")] public List<string> Enabled { get; set; }
        [JsonProperty("mcpServers")] public List<string> McpServers { get; set; }
        [JsonProperty("skills")] public List<string> Skills { get; set; }
        [JsonProperty("useAllSkills")] public bool UseAllSkills { get; set; } = true;
        /// <summary>跳过所有敏感操作审批。默认关，且每次进入会话时复位。</summary>
        [JsonProperty("fullTrust")] public bool FullTrust { get; set; }
        [JsonProperty("maxToolTurns")] public int MaxToolTurns { get; set; } = Limits.DefaultMaxToolTurns;

        /// <summary>
        /// 允许执行脚本（run_python）。**默认关**。
        /// 会执行代码的工具不该在新档案里默认打开 —— 用户得先知道自己在开什么。
        /// </summary>
        [JsonProperty("scriptEnabled")] public bool ScriptEnabled { get; set; }

        public ToolOptions()
        {
            Enabled = new List<string>();
            McpServers = new List<string>();
            Skills = new List<string>();
        }
    }

    public sealed class PromptOptions
    {
        [JsonProperty("injectionIds")] public List<string> InjectionIds { get; set; }
        [JsonProperty("lorebookIds")] public List<string> LorebookIds { get; set; }
        [JsonProperty("allowConversationOverride")] public bool AllowConversationOverride { get; set; }

        public PromptOptions()
        {
            InjectionIds = new List<string>();
            LorebookIds = new List<string>();
        }
    }

    public sealed class MemoryOptions
    {
        [JsonProperty("enabled")] public bool Enabled { get; set; } = true;
        [JsonProperty("useGlobal")] public bool UseGlobal { get; set; }
        [JsonProperty("recentChatsReference")] public bool RecentChatsReference { get; set; }
        [JsonProperty("timeReminder")] public bool TimeReminder { get; set; }
        [JsonProperty("autoSummarize")] public bool AutoSummarize { get; set; } = true;
        [JsonProperty("minMessagesForSummary")] public int MinMessagesForSummary { get; set; } = 10;
        [JsonProperty("maxMemories")] public int MaxMemories { get; set; } = 100;
        [JsonProperty("ragEnabled")] public bool RagEnabled { get; set; } = true;
        [JsonProperty("ragTopK")] public int RagTopK { get; set; } = 5;
        [JsonProperty("ragMinScore")] public double RagMinScore { get; set; } = 0.3;
    }

    public sealed class AppearanceOptions
    {
        /// <summary>none | solid | image</summary>
        [JsonProperty("backgroundType")] public string BackgroundType { get; set; } = "none";
        /// <summary>solid 时是 #RRGGBB；image 时是图片路径（不是 base64，避免 JSON 膨胀）。</summary>
        [JsonProperty("backgroundValue")] public string BackgroundValue { get; set; } = "";
        [JsonProperty("backgroundOpacity")] public int BackgroundOpacity { get; set; } = 12;
        [JsonProperty("userBubbleColor")] public string UserBubbleColor { get; set; } = "";
        [JsonProperty("aiBubbleColor")] public string AiBubbleColor { get; set; } = "";
        [JsonProperty("quoteColor")] public string QuoteColor { get; set; } = "";
        [JsonProperty("bracketColor")] public string BracketColor { get; set; } = "";
    }

    public sealed class AffinityState
    {
        [JsonProperty("enabled")] public bool Enabled { get; set; }
        [JsonProperty("value")] public int Value { get; set; } = 50;
        [JsonProperty("trend")] public string Trend { get; set; } = "stable";

        public void Apply(int delta)
        {
            int before = Value;
            int next = before + delta;
            if (next < 0) next = 0;
            if (next > 100) next = 100;
            Value = next;
            Trend = next > before ? "up" : (next < before ? "down" : "stable");
        }
    }

    /// <summary>一个档案 = 工作区里的一个文件夹。</summary>
    public sealed class Profile
    {
        [JsonProperty("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
        [JsonProperty("name")] public string Name { get; set; } = "新档案";
        /// <summary>文件夹名，创建后不再改（重命名只改 name）。</summary>
        [JsonProperty("slug")] public string Slug { get; set; } = "";
        [JsonProperty("avatar")] public string Avatar { get; set; } = "";
        [JsonProperty("createdAtUtc")] public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

        [JsonProperty("persona")] public Persona Persona { get; set; }
        [JsonProperty("userPersonaId")] public string UserPersonaId { get; set; } = "";

        [JsonProperty("api")] public ProfileApiOptions Api { get; set; }
        [JsonProperty("generation")] public GenerationOptions Generation { get; set; }
        [JsonProperty("tools")] public ToolOptions Tools { get; set; }
        [JsonProperty("prompts")] public PromptOptions Prompts { get; set; }
        [JsonProperty("memory")] public MemoryOptions Memory { get; set; }
        [JsonProperty("appearance")] public AppearanceOptions Appearance { get; set; }
        [JsonProperty("affinity")] public AffinityState Affinity { get; set; }

        public Profile()
        {
            Persona = new Persona();
            Api = new ProfileApiOptions();
            Generation = new GenerationOptions();
            Tools = new ToolOptions();
            Prompts = new PromptOptions();
            Memory = new MemoryOptions();
            Appearance = new AppearanceOptions();
            Affinity = new AffinityState();
        }

        /// <summary>反序列化后补齐 null 子对象，避免升级/手改配置导致的空引用。</summary>
        public void Repair()
        {
            if (Persona == null) Persona = new Persona();
            if (Api == null) Api = new ProfileApiOptions();
            if (Generation == null) Generation = new GenerationOptions();
            if (Tools == null) Tools = new ToolOptions();
            if (Prompts == null) Prompts = new PromptOptions();
            if (Memory == null) Memory = new MemoryOptions();
            if (Appearance == null) Appearance = new AppearanceOptions();
            if (Affinity == null) Affinity = new AffinityState();

            if (Api.CustomHeaders == null) Api.CustomHeaders = new Dictionary<string, string>();
            if (Api.CustomBody == null) Api.CustomBody = new Dictionary<string, object>();
            if (Generation.PresetMessages == null) Generation.PresetMessages = new List<PresetMessage>();
            if (Tools.Enabled == null) Tools.Enabled = new List<string>();
            if (Tools.McpServers == null) Tools.McpServers = new List<string>();
            if (Tools.Skills == null) Tools.Skills = new List<string>();
            if (Prompts.InjectionIds == null) Prompts.InjectionIds = new List<string>();
            if (Prompts.LorebookIds == null) Prompts.LorebookIds = new List<string>();
            if (Persona.Tags == null) Persona.Tags = new List<string>();
            if (Persona.Extensions == null) Persona.Extensions = new Dictionary<string, object>();
            if (Tools.MaxToolTurns <= 0) Tools.MaxToolTurns = Limits.DefaultMaxToolTurns;
            if (string.IsNullOrEmpty(Slug)) Slug = TextUtil.Slug(Name);
            if (Persona.Name != Name && !string.IsNullOrEmpty(Persona.Name)) { /* 保留各自语义 */ }

            // 消息模板缺占位符会让用户输入凭空消失，这里兜底
            if (string.IsNullOrEmpty(Generation.MessageTemplate) ||
                !TextUtil.Contains(Generation.MessageTemplate, "{{message}}", StringComparison.Ordinal))
            {
                Generation.MessageTemplate = "{{message}}";
            }
        }
    }

    /// <summary>用户人设（"我是谁"）。可与多个档案关联。</summary>
    public sealed class UserPersona
    {
        [JsonProperty("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
        [JsonProperty("name")] public string Name { get; set; } = "";
        [JsonProperty("description")] public string Description { get; set; } = "";
        [JsonProperty("avatar")] public string Avatar { get; set; } = "";
    }
}
