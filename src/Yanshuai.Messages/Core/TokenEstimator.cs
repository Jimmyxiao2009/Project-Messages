// Core/TokenEstimator.cs —— Token 估算
//
// 旧项目用 "中文×1.5 + 其他×0.25，再跟 词数×1.3 取最大" 的拍脑袋公式，问题是
// 它把 CJK 扩展区、全角标点、emoji 都算漏了，长中文会话会低估到触发 400 错误。
// 这里按 Unicode 区块分类计权，并显式统计 emoji 与全角标点。

using System;
using System.Globalization;

namespace Yanshuai.Messages.Core
{
    public static class TokenEstimator
    {
        /// <summary>估算一个字符串的 token 数。宁可略高估，也不要低估（低估会导致请求被拒）。</summary>
        public static int Estimate(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;

            double cjk = 0;        // 汉字 / 假名 / 韩文：约 1 token 1~1.5 字
            double emoji = 0;      // emoji：一个符号常占 2~4 token
            double fullWidth = 0;  // 全角标点
            double latin = 0;      // 拉丁字母数字
            double other = 0;

            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];

                if (char.IsHighSurrogate(ch) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    emoji += 1;
                    i++;
                    continue;
                }

                if (IsCjk(ch)) { cjk += 1; continue; }
                if (IsFullWidthPunctuation(ch)) { fullWidth += 1; continue; }
                if (char.IsLetterOrDigit(ch)) { latin += 1; continue; }
                if (char.IsWhiteSpace(ch)) { latin += 0.25; continue; }
                other += 1;
            }

            double tokens = cjk * 1.35
                          + emoji * 2.5
                          + fullWidth * 1.1
                          + latin * 0.28
                          + other * 0.5;

            int result = (int)Math.Ceiling(tokens);
            return result < 1 ? 1 : result;
        }

        /// <summary>
        /// 估算一条消息的开销：正文 + 推理内容 + 角色/分隔符固定开销。
        /// 固定开销取 4，是 OpenAI 与 Anthropic 常见封装的量级。
        /// </summary>
        public static int EstimateMessage(string content, string reasoning)
        {
            return Estimate(content) + Estimate(reasoning) + 4;
        }

        private static bool IsCjk(char ch)
        {
            return (ch >= 0x4E00 && ch <= 0x9FFF)
                || (ch >= 0x3400 && ch <= 0x4DBF)
                || (ch >= 0xF900 && ch <= 0xFAFF)
                || (ch >= 0x3000 && ch <= 0x303F && ch != 0x3000)   // CJK 标点（不含全角空格）
                || (ch >= 0x3040 && ch <= 0x30FF)                   // 假名
                || (ch >= 0xAC00 && ch <= 0xD7AF)                   // 韩文
                || (ch >= 0xFF00 && ch <= 0xFF60);                  // 全角形式
        }

        private static bool IsFullWidthPunctuation(char ch)
        {
            return (ch >= 0xFF01 && ch <= 0xFF0F)
                || (ch >= 0xFF1A && ch <= 0xFF20)
                || (ch >= 0xFF3B && ch <= 0xFF40)
                || (ch >= 0xFF5B && ch <= 0xFF65)
                || (ch >= 0x2000 && ch <= 0x206F)
                || (ch >= 0x2010 && ch <= 0x2027);
        }
    }

    /// <summary>
    /// 已知模型的上下文窗口。命中不了就用默认值；档案里显式填了 MaxContextTokens 时优先用它。
    /// 这张表只用子串匹配，所以 "deepseek-reasoner-2024xx" 这类带后缀的型号也能命中。
    /// </summary>
    public static class ModelContextLimits
    {
        public const int DefaultLimit = 32000;

        private static readonly string[][] Table = new string[][]
        {
            new[] { "gpt-4o-mini",            "128000" },
            new[] { "gpt-4o",                 "128000" },
            new[] { "gpt-4.1",                "1047576" },
            new[] { "gpt-4-turbo",            "128000" },
            new[] { "gpt-3.5",                "16385" },
            new[] { "o1",                     "200000" },
            new[] { "o3",                     "200000" },
            new[] { "o4-mini",                "200000" },
            new[] { "claude-3-5-sonnet",      "200000" },
            new[] { "claude-3-5-haiku",       "200000" },
            new[] { "claude-3-7-sonnet",      "200000" },
            new[] { "claude-sonnet-4",        "200000" },
            new[] { "claude-opus-4",          "200000" },
            new[] { "claude-haiku-4",         "200000" },
            new[] { "deepseek-chat",          "128000" },
            new[] { "deepseek-reasoner",      "128000" },
            new[] { "deepseek-r1",            "64000" },
            new[] { "deepseek-v3",            "64000" },
            new[] { "qwen3",                  "131072" },
            new[] { "qwen2.5",                "131072" },
            new[] { "qwen-max",               "32768" },
            new[] { "glm-4",                  "128000" },
            new[] { "moonshot",               "131072" },
            new[] { "kimi",                   "131072" },
            new[] { "llama-3.3-70b",          "131072" },
            new[] { "llama-3.1",              "131072" },
            new[] { "gemini-2.5",             "1048576" },
            new[] { "gemini-2.0",             "1048576" },
            new[] { "gemini-1.5",             "1048576" },
            new[] { "grok-3",                 "131072" },
            new[] { "mistral-large",          "131072" },
            new[] { "yi-large",               "32768" }
        };

        public static int GetLimit(string modelName)
        {
            if (string.IsNullOrEmpty(modelName)) return DefaultLimit;
            var name = modelName.ToLowerInvariant();
            foreach (var row in Table)
            {
                if (name.IndexOf(row[0], StringComparison.Ordinal) >= 0)
                {
                    int v;
                    if (int.TryParse(row[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return v;
                }
            }
            return DefaultLimit;
        }

        /// <summary>档案里显式配了 MaxContextTokens 就用它，否则查表。</summary>
        public static int GetEffectiveLimit(string modelName, int configured)
        {
            return configured > 0 ? configured : GetLimit(modelName);
        }
    }
}
