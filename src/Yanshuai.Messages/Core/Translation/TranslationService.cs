// Core/Translation/TranslationService.cs —— 消息翻译
//
// 翻译就是一次普通补全调用，所以直接复用 provider 层，不引入第二套 HTTP 路径。
//
// 几个刻意的决定：
//   1) **不改原文**。译文缓存在 Message.Translations 上叠加显示，
//      用户随时能切回原文；也避免了"翻错了原文就没了"。
//   2) **按语言缓存**。来回切语言不重复花钱。
//   3) **代码块原样保留**。翻译代码会毁掉它 —— 提示词里明确要求，
//      译后还要校验代码块数量，对不上就退回原文，宁可少翻不可翻坏。

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Yanshuai.Messages.Core
{
    public sealed class TranslationResult
    {
        public bool Ok { get; set; }
        public string Text { get; set; } = "";
        public string ErrorCode { get; set; } = "";
        public string ErrorMessage { get; set; } = "";
        public bool FromCache { get; set; }
    }

    public sealed class TranslationService
    {
        private readonly IHttpTransport _http;
        private readonly ILogger _log;

        private static readonly Regex CodeFence = new Regex("```", RegexOptions.Compiled);

        public TranslationService(IHttpTransport http, ILogger log)
        {
            _http = http;
            _log = log ?? NullLogger.Instance;
        }

        /// <summary>
        /// 翻译一条消息。已经有目标语言的译文时直接返回缓存。
        /// </summary>
        public async Task<TranslationResult> TranslateMessageAsync(
            Message message,
            string targetLanguage,
            ApiProfile api,
            string apiKey,
            CancellationToken ct)
        {
            if (message == null) return Fail("no_message", "没有可翻译的消息。");
            if (string.IsNullOrWhiteSpace(targetLanguage)) return Fail("no_language", "没有指定目标语言。");

            var cached = message.GetTranslation(targetLanguage);
            if (!string.IsNullOrEmpty(cached))
            {
                return new TranslationResult { Ok = true, Text = cached, FromCache = true };
            }

            var source = message.Content;
            if (string.IsNullOrWhiteSpace(source))
            {
                return Fail("empty_content", "这条消息没有内容。");
            }

            var result = await TranslateTextAsync(source, targetLanguage, api, apiKey, ct).ConfigureAwait(false);
            if (result.Ok && !string.IsNullOrEmpty(result.Text))
            {
                message.SetTranslation(targetLanguage, result.Text);
            }
            return result;
        }

        public async Task<TranslationResult> TranslateTextAsync(
            string text,
            string targetLanguage,
            ApiProfile api,
            string apiKey,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(text)) return Fail("empty_content", "没有可翻译的内容。");
            if (api == null) return Fail("no_api", "没有可用的模型。");
            if (api.RequiresApiKey && string.IsNullOrEmpty(apiKey)) return Fail("no_key", "没有可用的密钥。");
            if (string.IsNullOrWhiteSpace(api.Model)) return Fail("no_api", "没有可用的模型。");

            var provider = ProviderFactory.Create(api, _http, _log);

            var request = new ChatRequest
            {
                Api = api,
                ApiKey = apiKey,
                Temperature = 0.2,
                Stream = false,
                Turns = new List<ChatTurn>
                {
                    ChatTurn.System(BuildSystemPrompt(targetLanguage)),
                    ChatTurn.User(text)
                }
            };

            ChatResult completion;
            try
            {
                completion = await provider.CompleteAsync(request, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Log(LogLevel.Warning, "translate", "翻译调用失败：" + ex.GetType().Name);
                return Fail("network", "翻译请求失败，请检查网络。");
            }

            if (completion == null || !completion.Ok)
            {
                var code = completion != null ? completion.ErrorCode : "empty";
                var msg = completion != null ? completion.ErrorMessage : "模型没有返回内容。";
                return Fail(string.IsNullOrEmpty(code) ? "failed" : code, msg);
            }

            var translated = Cleanup(completion.Content);

            // 代码块数量对不上 → 模型改动了代码，退回原文更安全
            if (CodeFence.Matches(text).Count != CodeFence.Matches(translated).Count)
            {
                _log.Log(LogLevel.Warning, "translate", "译文代码块数量与原文不符，放弃这次翻译结果");
                return Fail("code_mismatch", "译文改动了代码块，已放弃（避免翻坏代码）。");
            }

            if (string.IsNullOrWhiteSpace(translated))
            {
                return Fail("empty", "模型返回了空译文。");
            }

            return new TranslationResult { Ok = true, Text = translated };
        }

        private static string BuildSystemPrompt(string targetLanguage)
        {
            var sb = new StringBuilder();
            sb.Append("你是一个翻译引擎。把用户发来的内容翻译成").Append(LanguageName(targetLanguage)).Append("。\n");
            sb.Append("规则：\n");
            sb.Append("1. 只输出译文本身，不要任何解释、前言、引号或 Markdown 包装。\n");
            sb.Append("2. 保留原有的换行与段落结构。\n");
            sb.Append("3. 代码块、行内代码、URL、文件路径、变量名、命令**原样保留，不要翻译**。\n");
            sb.Append("4. 专有名词、产品名、模型名保持原文；必要时在括号里给译名。\n");
            sb.Append("5. 语气与原文一致（原文随意就随意，原文正式就正式）。\n");
            sb.Append("6. 如果原文已经是目标语言，原样返回。");
            return sb.ToString();
        }

        /// <summary>去掉模型爱加的外层包装。</summary>
        private static string Cleanup(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            var result = text.Trim();

            // 整体被 ``` 包住 → 剥掉
            if (result.StartsWith("```", StringComparison.Ordinal) && result.EndsWith("```", StringComparison.Ordinal))
            {
                int firstNewline = result.IndexOf('\n');
                if (firstNewline > 0)
                {
                    var inner = result.Substring(firstNewline + 1);
                    int lastFence = inner.LastIndexOf("```", StringComparison.Ordinal);
                    if (lastFence >= 0) inner = inner.Substring(0, lastFence);
                    var candidate = inner.Trim();
                    // 只在剥完后看起来不是"本来就是代码"时才采纳
                    if (candidate.Length > 0) result = candidate;
                }
            }

            // 常见前言
            var prefixes = new string[]
            {
                "译文：", "译文:", "翻译：", "翻译:", "以下是译文：", "以下是译文:",
                "Translation:", "Translated text:"
            };
            foreach (var prefix in prefixes)
            {
                if (result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    result = result.Substring(prefix.Length).Trim();
                    break;
                }
            }

            return result;
        }

        public static string LanguageName(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return "中文";

            var lower = tag.ToLowerInvariant();
            if (lower.StartsWith("zh", StringComparison.Ordinal)) return "简体中文";
            if (lower.StartsWith("en", StringComparison.Ordinal)) return "英语";
            if (lower.StartsWith("ja", StringComparison.Ordinal)) return "日语";
            if (lower.StartsWith("ko", StringComparison.Ordinal)) return "韩语";
            if (lower.StartsWith("fr", StringComparison.Ordinal)) return "法语";
            if (lower.StartsWith("de", StringComparison.Ordinal)) return "德语";
            if (lower.StartsWith("es", StringComparison.Ordinal)) return "西班牙语";
            if (lower.StartsWith("ru", StringComparison.Ordinal)) return "俄语";
            if (lower.StartsWith("pt", StringComparison.Ordinal)) return "葡萄牙语";
            if (lower.StartsWith("it", StringComparison.Ordinal)) return "意大利语";
            if (lower.StartsWith("ar", StringComparison.Ordinal)) return "阿拉伯语";
            if (lower.StartsWith("th", StringComparison.Ordinal)) return "泰语";
            if (lower.StartsWith("vi", StringComparison.Ordinal)) return "越南语";

            return tag;
        }

        /// <summary>界面上给用户选的语言（按常用度排）。</summary>
        public static readonly string[] CommonLanguages = new string[]
        {
            "zh-CN", "en", "ja", "ko", "fr", "de", "es", "ru", "pt", "it"
        };

        private static TranslationResult Fail(string code, string message)
        {
            return new TranslationResult { Ok = false, ErrorCode = code, ErrorMessage = message };
        }
    }
}
