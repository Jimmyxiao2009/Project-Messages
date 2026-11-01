// Core/Models/GlobalSettings.cs —— 跨档案的全局设置
//
// 只放"不属于任何档案"的东西。档案自己的配置在 profile.json 里。
// 联网搜索的 ApiKey 不进这个文件，走 ISecretStore。

using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Yanshuai.Messages.Core
{
    public sealed class SearchSettings
    {
        /// <summary>tavily | searxng | bing | duckduckgo</summary>
        /// 默认 bing：DuckDuckGo 的 HTML 端点在境内连不通，之前默认 DDG 等于默认不可用。
        [JsonProperty("provider")] public string Provider { get; set; } = "bing";

        [JsonProperty("baseUrl")] public string BaseUrl { get; set; } = "";
        [JsonProperty("maxResults")] public int MaxResults { get; set; } = 5;

        /// <summary>每条结果的正文截断字数：2000 / 8000 / 0（不限）。</summary>
        [JsonProperty("depth")] public int Depth { get; set; } = 2000;

        [JsonProperty("userAgent")] public string UserAgent { get; set; } = "";

        /// <summary>
        /// 搜索服务的密钥。**不落盘**（与模型 API Key 一样进 ISecretStore），
        /// 由工作区在装载设置时填进来。旧项目把搜索 Key 明文写在 search_apis.json 里。
        /// </summary>
        [JsonIgnore] public string ApiKey { get; set; }

        /// <summary>是否允许 fetch_page 抓取任意网页（默认关闭，防误抓与 SSRF）。</summary>
        [JsonProperty("allowFetchPage")] public bool AllowFetchPage { get; set; }

        [JsonProperty("timeoutSeconds")] public int TimeoutSeconds { get; set; } = 30;
    }

    /// <summary>指向某个 Provider 下的某个模型。两项都非空才算"已指定"。</summary>
    public sealed class ModelRef
    {
        [JsonProperty("apiProfileId")] public string ApiProfileId { get; set; } = "";
        [JsonProperty("model")] public string Model { get; set; } = "";

        [JsonIgnore]
        public bool IsSet { get { return !string.IsNullOrWhiteSpace(ApiProfileId) && !string.IsNullOrWhiteSpace(Model); } }

        public void Clear() { ApiProfileId = ""; Model = ""; }
    }

    public static class SpeechEngines
    {
        public const string System = "system";
        public const string Model = "model";
    }

    public sealed class GlobalSettings
    {
        [JsonProperty("version")] public int Version { get; set; } = 1;

        /// <summary>主题：dark | light | system</summary>
        [JsonProperty("theme")] public string Theme { get; set; } = "dark";

        /// <summary>跟随系统强调色。</summary>
        [JsonProperty("followSystemAccent")] public bool FollowSystemAccent { get; set; } = true;

        /// <summary>不跟随时使用的手动强调色 #RRGGBB。</summary>
        [JsonProperty("customAccent")] public string CustomAccent { get; set; } = "#0078D7";

        /// <summary>send = 回车发送；newline = 回车换行。</summary>
        [JsonProperty("enterBehavior")] public string EnterBehavior { get; set; } = "send";

        /// <summary>last | new | list</summary>
        [JsonProperty("startupBehavior")] public string StartupBehavior { get; set; } = "last";

        [JsonProperty("replySound")] public bool ReplySound { get; set; }

        /// <summary>时间线一次加载多少条历史。</summary>
        [JsonProperty("historyPageSize")] public int HistoryPageSize { get; set; } = 20;

        /// <summary>是否记住"上次打开的档案与会话"。</summary>
        [JsonProperty("lastProfileId")] public string LastProfileId { get; set; } = "";
        [JsonProperty("lastConversationId")] public string LastConversationId { get; set; } = "";

        [JsonProperty("search")] public SearchSettings Search { get; set; }

        /// <summary>全局记忆开关。档案里另有自己的开关。</summary>
        [JsonProperty("globalMemoryEnabled")] public bool GlobalMemoryEnabled { get; set; }

        [JsonProperty("favoriteModels")] public List<string> FavoriteModels { get; set; } = new List<string>();
        [JsonProperty("collapsedProviders")] public List<string> CollapsedProviders { get; set; } = new List<string>();
        [JsonProperty("regexRules")] public List<RegexTransform> RegexRules { get; set; } = new List<RegexTransform>();
        [JsonProperty("bodyFontSize")] public int BodyFontSize { get; set; } = 15;
        [JsonProperty("lineHeight")] public int LineHeight { get; set; } = 22;
        [JsonProperty("translationLanguage")] public string TranslationLanguage { get; set; } = "zh-CN";
        [JsonProperty("speechRate")] public double SpeechRate { get; set; } = 1;
        [JsonProperty("speechVolume")] public double SpeechVolume { get; set; } = 1;
        [JsonProperty("speechVoice")] public string SpeechVoice { get; set; } = "";

        /// <summary>
        /// 副手模型：起标题、翻译、回复建议、压缩、记忆整理。未指定时依次退回
        /// 档案 / Provider 上旧的 subModel，再退回对话模型。
        /// </summary>
        [JsonProperty("assistantModel")] public ModelRef AssistantModel { get; set; } = new ModelRef();

        /// <summary>第一轮回复后用副手模型起一个标题（否则用第一句话截断）。</summary>
        [JsonProperty("aiTitles")] public bool AiTitles { get; set; } = true;

        /// <summary>每轮回复后用副手模型生成几条可点的回复建议。</summary>
        [JsonProperty("replySuggestions")] public bool ReplySuggestions { get; set; } = true;

        /// <summary>朗读引擎：system = 系统语音；model = 朗读模型（OpenAI 兼容 /audio/speech）。</summary>
        [JsonProperty("speechEngine")] public string SpeechEngine { get; set; } = SpeechEngines.System;
        [JsonProperty("speechModel")] public ModelRef SpeechModel { get; set; } = new ModelRef();
        [JsonProperty("speechModelVoice")] public string SpeechModelVoice { get; set; } = "alloy";
        [JsonProperty("attachmentMaxPages")] public int AttachmentMaxPages { get; set; } = 5;
        [JsonProperty("defaultAppearance")] public AppearanceOptions DefaultAppearance { get; set; } = new AppearanceOptions();

        [JsonProperty("onboardingComplete")] public bool OnboardingComplete { get; set; }
        [JsonProperty("authorizedFolders")] public List<AuthorizedFolder> AuthorizedFolders { get; set; } = new List<AuthorizedFolder>();
        [JsonProperty("lastSeenCrash")] public long LastSeenCrash { get; set; }
        public GlobalSettings()
        {
            Search = new SearchSettings();
        }

        public void Repair()
        {
            if (FavoriteModels == null) FavoriteModels = new List<string>();
            if (CollapsedProviders == null) CollapsedProviders = new List<string>();
            if (RegexRules == null) RegexRules = new List<RegexTransform>();
            if (DefaultAppearance == null) DefaultAppearance = new AppearanceOptions();
            if (AuthorizedFolders == null) AuthorizedFolders = new List<AuthorizedFolder>();
            if (Search == null) Search = new SearchSettings();
            if (AssistantModel == null) AssistantModel = new ModelRef();
            if (SpeechModel == null) SpeechModel = new ModelRef();
            if (string.IsNullOrEmpty(SpeechEngine)) SpeechEngine = SpeechEngines.System;
            if (SpeechModelVoice == null) SpeechModelVoice = "";
            if (Search.MaxResults <= 0) Search.MaxResults = 5;
            if (Search.TimeoutSeconds <= 0) Search.TimeoutSeconds = 30;
            if (HistoryPageSize <= 0) HistoryPageSize = 20;
            if (string.IsNullOrEmpty(Theme)) Theme = "dark";
            if (string.IsNullOrEmpty(EnterBehavior)) EnterBehavior = "send";
            if (string.IsNullOrEmpty(StartupBehavior)) StartupBehavior = "last";
        }
    }
}
