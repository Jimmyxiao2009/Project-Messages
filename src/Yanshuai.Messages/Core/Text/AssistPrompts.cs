// Core/Text/AssistPrompts.cs —— 副手模型的小任务：起标题、回复建议
//
// 提示词构造与输出解析都是纯函数。副手模型往往是便宜的小模型，
// 输出格式经常不听话（加编号、加引号、包代码块、多一句客套），
// 解析这一侧要宽进严出，宁可少给一条建议也不要把"好的，以下是建议："显示给用户。

using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Yanshuai.Messages.Core
{
    public static class AssistPrompts
    {
        public const int SuggestionCount = 3;
        public const int SuggestionMaxChars = 40;
        public const int TitleMaxChars = 20;

        private const int RecentMessages = 6;
        private const int MessageChars = 600;

        // ───────────── 标题 ─────────────

        public static string BuildTitlePrompt(IList<Message> messages)
        {
            var sb = new StringBuilder();
            sb.Append("给下面这段对话起一个简短的标题。\n");
            sb.Append("要求：不超过 12 个字（英文不超过 6 个词），概括主题，不要标点、引号、书名号和表情，");
            sb.Append("用对话所用的语言，只输出标题本身。\n\n");
            AppendTranscript(sb, messages, 4);
            return sb.ToString();
        }

        public static string CleanTitle(string output)
        {
            if (string.IsNullOrWhiteSpace(output)) return "";
            var line = FirstMeaningfulLine(output);
            line = StripLabel(line, new[] { "标题：", "标题:", "Title:", "title:" });
            // 引号和句末标点会互相包住（如「《标题》。」），一起剥
            line = line.Trim().Trim('"', '\'', '“', '”', '‘', '’', '《', '》', '「', '」', '『', '』', '*', '#', '`', ' ',
                '。', '.', '！', '!', '？', '?', '，', ',', '；', ';', '：', ':');
            if (line.Length > TitleMaxChars) line = line.Substring(0, TitleMaxChars);
            return line.Trim();
        }

        // ───────────── 回复建议 ─────────────

        /// <summary>
        /// 站在用户角度，给出接下来最可能想说的几句话。
        /// 要求 JSON 数组，便于解析；解析器也接受按行列出的格式。
        /// </summary>
        public static string BuildSuggestionPrompt(IList<Message> messages, string assistantName, string userName)
        {
            var sb = new StringBuilder();
            sb.Append("你在帮用户想下一句要发什么。根据下面的对话，站在");
            sb.Append(string.IsNullOrWhiteSpace(userName) ? "用户" : "用户（" + userName.Trim() + "）");
            sb.Append("的角度，写 ").Append(SuggestionCount).Append(" 条接下来最可能发给");
            sb.Append(string.IsNullOrWhiteSpace(assistantName) ? "对方" : assistantName.Trim());
            sb.Append("的回复。\n");
            sb.Append("要求：\n");
            sb.Append("1. 每条不超过 20 个字（英文不超过 12 个词），口语、具体，彼此方向不同（例如追问细节、换个角度、推进下一步）。\n");
            sb.Append("2. 用对话所用的语言；如果对方在演绎角色，就用符合情境的说法。\n");
            sb.Append("3. 不要编号、不要解释，只输出一个 JSON 字符串数组，例如 [\"…\",\"…\",\"…\"]。\n\n");
            AppendTranscript(sb, messages, RecentMessages);
            return sb.ToString();
        }

        public static List<string> ParseSuggestions(string output)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(output)) return result;

            var text = output.Trim();
            int open = text.IndexOf('[');
            int close = text.LastIndexOf(']');
            if (open >= 0 && close > open)
            {
                try
                {
                    var array = JArray.Parse(text.Substring(open, close - open + 1));
                    foreach (var token in array)
                    {
                        if (token.Type == JTokenType.String) Add(result, (string)token);
                    }
                }
                catch (Exception)
                {
                    result.Clear();
                }
            }

            if (result.Count == 0)
            {
                foreach (var raw in text.Replace("\r", "").Split('\n'))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("```", StringComparison.Ordinal)) continue;
                    if (line.EndsWith("：", StringComparison.Ordinal) || line.EndsWith(":", StringComparison.Ordinal)) continue;
                    Add(result, StripListMarker(line));
                }
            }

            if (result.Count > SuggestionCount) result.RemoveRange(SuggestionCount, result.Count - SuggestionCount);
            return result;
        }

        private static void Add(List<string> list, string candidate)
        {
            if (candidate == null) return;
            var s = candidate.Trim().Trim('"', '“', '”', '「', '」', ',', '，').Trim();
            if (s.Length == 0 || s.Length > SuggestionMaxChars) return;
            foreach (var existing in list) if (string.Equals(existing, s, StringComparison.OrdinalIgnoreCase)) return;
            list.Add(s);
        }

        private static string StripListMarker(string line)
        {
            int i = 0;
            while (i < line.Length && (char.IsDigit(line[i]) || line[i] == '.' || line[i] == '、' || line[i] == ')' ||
                   line[i] == '）' || line[i] == '-' || line[i] == '*' || line[i] == '•' || line[i] == ' ')) i++;
            return i > 0 && i < line.Length ? line.Substring(i) : line;
        }

        // ───────────── 共用 ─────────────

        private static void AppendTranscript(StringBuilder sb, IList<Message> messages, int count)
        {
            sb.Append("对话：\n");
            if (messages == null) return;
            var picked = new List<Message>();
            for (int i = messages.Count - 1; i >= 0 && picked.Count < count; i--)
            {
                var m = messages[i];
                if (m == null || string.IsNullOrWhiteSpace(m.Content)) continue;
                if (m.Role != MessageRole.User && m.Role != MessageRole.Assistant) continue;
                picked.Insert(0, m);
            }
            foreach (var m in picked)
            {
                sb.Append(m.Role == MessageRole.User ? "用户：" : "对方：");
                sb.Append(TextUtil.Truncate(m.Content.Trim(), MessageChars)).Append('\n');
            }
        }

        private static string FirstMeaningfulLine(string text)
        {
            foreach (var raw in text.Replace("\r", "").Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("```", StringComparison.Ordinal)) continue;
                return line;
            }
            return "";
        }

        private static string StripLabel(string line, string[] labels)
        {
            foreach (var label in labels)
                if (line.StartsWith(label, StringComparison.OrdinalIgnoreCase)) return line.Substring(label.Length);
            return line;
        }
    }
}
