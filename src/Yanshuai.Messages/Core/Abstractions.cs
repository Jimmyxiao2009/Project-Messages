// Core/Abstractions.cs
//
// 平台无关的最小接口集。Core 里任何代码都不得直接引用 Windows.*。
//
// 为什么这么做：旧项目把 ApplicationData.Current / FileIO / CoreApplication.MainView.Dispatcher
// 直接写进业务逻辑，导致三件事同时发生——逻辑无法在桌面跑测试、无法换宿主、
// 而且工具循环里到处是 "回到 UI 线程" 的胶水代码。这里用接口把它挡住，
// 平台层（Platform\*.cs）负责实现，桌面测试工程提供内存实现。

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Yanshuai.Messages.Core
{
    /// <summary>工作区文件读写。路径一律用相对于工作区根的正斜杠路径。</summary>
    public interface IFileStore
    {
        Task<bool> ExistsAsync(string relativePath);
        Task<string> ReadTextAsync(string relativePath);
        Task WriteTextAsync(string relativePath, string content);

        /// <summary>读二进制（图片、角色卡原图）。文本文件也能读，按 UTF-8 编码返回。</summary>
        Task<byte[]> ReadBytesAsync(string relativePath);

        /// <summary>写二进制。</summary>
        Task WriteBytesAsync(string relativePath, byte[] data);

        Task DeleteAsync(string relativePath);
        Task<IReadOnlyList<string>> ListFilesAsync(string relativeDir, bool recursive);
        Task<IReadOnlyList<string>> ListDirectoriesAsync(string relativeDir);
        Task EnsureDirectoryAsync(string relativeDir);
        Task MoveAsync(string fromRelative, string toRelative);
        string GetAbsolutePath(string relativePath);
    }

    /// <summary>全局标量设置。只放"跨档案"的东西；档案自己的配置在 profile.json 里。</summary>
    public interface ISettingsStore
    {
        string GetString(string key, string fallback);
        void SetString(string key, string value);
        bool GetBool(string key, bool fallback);
        void SetBool(string key, bool value);
        int GetInt(string key, int fallback);
        void SetInt(string key, int value);
        double GetDouble(string key, double fallback);
        void SetDouble(string key, double value);
        void Remove(string key);
    }

    /// <summary>API Key 等凭据。UWP 走 PasswordVault，测试走内存字典。</summary>
    public interface ISecretStore
    {
        string Get(string key);
        void Set(string key, string value);
        void Remove(string key);
    }

    public enum LogLevel
    {
        Debug = 0,
        Info = 1,
        Warning = 2,
        Error = 3
    }

    /// <summary>
    /// 脱敏日志。实现方负责落盘与轮换；调用方只给"已经不含密钥"的文本。
    /// 约定：绝不记录 API Key、Token、邮箱、密码、请求/响应正文、URL 查询参数。
    /// </summary>
    public interface ILogger
    {
        void Log(LogLevel level, string category, string message);
    }

    public interface IClock
    {
        DateTimeOffset UtcNow { get; }
    }

    public sealed class SystemClock : IClock
    {
        public static readonly SystemClock Instance = new SystemClock();
        public DateTimeOffset UtcNow { get { return DateTimeOffset.UtcNow; } }
    }

    // ─────────────────────────────────────────────────────────────────────
    // HTTP
    // ─────────────────────────────────────────────────────────────────────

    public enum HttpMethodKind
    {
        Get = 0,
        Post = 1,
        Patch = 2,
        Delete = 3
    }

    public sealed class HttpRequestSpec
    {
        public HttpMethodKind Method { get; set; }
        public string Url { get; set; }
        public Dictionary<string, string> Headers { get; private set; }
        public string Body { get; set; }
        public int TimeoutSeconds { get; set; }

        /// <summary>响应是二进制（如音频）。成功时内容放在 HttpResponseSpec.BodyBytes，Body 为空。</summary>
        public bool ExpectBinary { get; set; }

        public HttpRequestSpec()
        {
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            TimeoutSeconds = 180;
        }

        public HttpRequestSpec AddHeader(string name, string value)
        {
            if (!string.IsNullOrEmpty(name)) Headers[name] = value ?? string.Empty;
            return this;
        }
    }

    public sealed class HttpResponseSpec
    {
        public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public int StatusCode { get; set; }
        public string Body { get; set; }
        public string ContentType { get; set; }

        /// <summary>仅在请求 ExpectBinary 且响应成功时填充。</summary>
        public byte[] BodyBytes { get; set; }

        public bool IsSuccess
        {
            get { return StatusCode >= 200 && StatusCode < 300; }
        }
    }

    public interface IHttpTransport
    {
        /// <summary>一次性读取完整响应体。</summary>
        Task<HttpResponseSpec> SendAsync(HttpRequestSpec request, CancellationToken ct);

        /// <summary>
        /// 逐行读取响应体（用于 SSE 流式）。onLine 返回 false 表示调用方要求提前终止。
        /// 传输层不得缓存整段响应，必须边读边回调，否则流式就失去意义。
        /// </summary>
        Task StreamLinesAsync(HttpRequestSpec request, Func<string, bool> onLine, CancellationToken ct);
    }

    // ─────────────────────────────────────────────────────────────────────
    // 嵌入（RAG）
    // ─────────────────────────────────────────────────────────────────────

    public interface IEmbedder
    {
        bool IsAvailable { get; }
        int Dimensions { get; }
        Task<float[]> EmbedAsync(string text, CancellationToken ct);
    }

    // ─────────────────────────────────────────────────────────────────────
    // 设备能力（日历 / 联系人 / 拨号 / 短信 / 启动应用 / 媒体键 / 读图）
    // ─────────────────────────────────────────────────────────────────────

    public interface IDeviceBridge
    {
        string DeviceFamily { get; }

        Task<IReadOnlyList<string>> ListCalendarAsync(int max, CancellationToken ct);
        Task<string> CreateCalendarEventAsync(string title, DateTimeOffset start, int minutes, string notes, CancellationToken ct);
        Task<IReadOnlyList<string>> SearchContactsAsync(string query, int max, CancellationToken ct);
        Task<string> LaunchUriAsync(string uri, CancellationToken ct);
        Task<string> MediaControlAsync(string action, CancellationToken ct);
        Task<string> ReadImageAsBase64Async(string path, CancellationToken ct);
    }

    // ─────────────────────────────────────────────────────────────────────
    // 与用户交互（审批 / 追问）——由 UI 层实现
    // ─────────────────────────────────────────────────────────────────────

    public sealed class ToolPermissionRequest
    {
        public string ToolName { get; set; }
        /// <summary>权限键，形如 "write_file:notes.md"。命中已授权集合时不会再问。</summary>
        public string PermissionKey { get; set; }
        public string Title { get; set; }
        public string Detail { get; set; }
        public List<KeyValuePair<string, string>> Fields { get; private set; }

        public ToolPermissionRequest()
        {
            Fields = new List<KeyValuePair<string, string>>();
        }
    }

    public sealed class AskQuestion
    {
        public string InputType { get; set; } = "text";
        public string Id { get; set; }
        public string Text { get; set; }
        public bool AllowMultiple { get; set; }
        public bool AllowFreeText { get; set; }
        public List<string> Options { get; private set; }

        public AskQuestion()
        {
            AllowFreeText = true;
            Options = new List<string>();
        }
    }

    public sealed class AskAnswer
    {
        public string Id { get; set; }
        public List<string> Values { get; private set; }

        public AskAnswer()
        {
            Values = new List<string>();
        }

        public string Joined
        {
            get { return Values.Count == 0 ? string.Empty : string.Join(", ", Values); }
        }
    }

    public interface IUserInteraction
    {
        /// <summary>请求用户批准一个敏感操作。返回 false 表示拒绝。</summary>
        Task<bool> ConfirmPermissionAsync(ToolPermissionRequest request, CancellationToken ct);

        /// <summary>向用户提几个问题。返回的答案按 Id 对应。</summary>
        Task<IReadOnlyList<AskAnswer>> AskAsync(IReadOnlyList<AskQuestion> questions, CancellationToken ct);
    }

    /// <summary>没有 UI 时的兜底实现：一律拒绝审批、追问返回空。桌面测试用。</summary>
    public sealed class DenyAllInteraction : IUserInteraction
    {
        public static readonly DenyAllInteraction Instance = new DenyAllInteraction();

        public Task<bool> ConfirmPermissionAsync(ToolPermissionRequest request, CancellationToken ct)
        {
            return Task.FromResult(false);
        }

        public Task<IReadOnlyList<AskAnswer>> AskAsync(IReadOnlyList<AskQuestion> questions, CancellationToken ct)
        {
            IReadOnlyList<AskAnswer> empty = new List<AskAnswer>();
            return Task.FromResult(empty);
        }
    }

    /// <summary>空日志实现。</summary>
    public sealed class NullLogger : ILogger
    {
        public static readonly NullLogger Instance = new NullLogger();
        public void Log(LogLevel level, string category, string message) { }
    }

    // ─────────────────────────────────────────────────────────────────────
    // 语音合成（TTS）
    // ─────────────────────────────────────────────────────────────────────

    public sealed class SpeechRequest
    {
        public string Text { get; set; } = "";
        /// <summary>语速倍率，1.0 为正常。</summary>
        public double Rate { get; set; } = 1.0;
        /// <summary>音量 0–1。</summary>
        public double Volume { get; set; } = 1.0;
        /// <summary>语言标签，如 "zh-CN"。留空用系统默认。</summary>
        public string Language { get; set; } = "";
        /// <summary>音色名，留空用系统默认。</summary>
        public string Voice { get; set; } = "";
    }

    /// <summary>
    /// 语音合成。Core 只负责把文本切段与排队，真正发声由平台实现。
    /// 抽象出来是因为桌面测试环境没有语音引擎，而"切段"这件事本身是纯逻辑、值得单测。
    /// </summary>
    public interface ISpeechSynthesizer
    {
        bool IsAvailable { get; }

        /// <summary>当前设备可用的音色名。</summary>
        Task<IReadOnlyList<string>> ListVoicesAsync(CancellationToken ct);

        /// <summary>朗读一段文本，播完（或被取消）后返回。</summary>
        Task SpeakAsync(SpeechRequest request, CancellationToken ct);

        /// <summary>停止当前朗读。</summary>
        Task StopAsync();
    }

    /// <summary>没有语音引擎时的实现：可用性为假，朗读直接返回。</summary>
    public sealed class NullSpeechSynthesizer : ISpeechSynthesizer
    {
        public static readonly NullSpeechSynthesizer Instance = new NullSpeechSynthesizer();

        public bool IsAvailable { get { return false; } }

        public Task<IReadOnlyList<string>> ListVoicesAsync(CancellationToken ct)
        {
            IReadOnlyList<string> empty = new List<string>();
            return Task.FromResult(empty);
        }

        public Task SpeakAsync(SpeechRequest request, CancellationToken ct) { return Task.CompletedTask; }

        public Task StopAsync() { return Task.CompletedTask; }
    }
}
