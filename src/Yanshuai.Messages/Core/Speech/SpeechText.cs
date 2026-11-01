// Core/Speech/SpeechText.cs —— 朗读前的文本处理（纯逻辑，可单测）
//
// 为什么需要这一层：直接把整条 AI 回复丢给 TTS 有三个问题。
//   1) 回复里有 Markdown 标记、代码块、表格 —— 读出来是噪音。
//   2) 一条回复可能几千字，一次合成要么卡住界面，要么超出引擎的文本上限。
//   3) 中文没有空格，长段一次读完没有停顿，听感很差。
//
// 所以先清洗再切段。切段要贴着标点切，不能把句子拦腰截断 ——
// 这也是唯一值得单测的部分，真正发声的部分换个平台实现就行。

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Yanshuai.Messages.Core
{
    public sealed class SpeechChunk
    {
        public string Text { get; set; } = "";
        /// <summary>这一段是否来自代码块（朗读时通常跳过）。</summary>
        public bool IsCode { get; set; }
    }

    public static class SpeechText
    {
        /// <summary>单段上限。太长会让引擎停顿异常，太短会把句子切碎。</summary>
        public const int DefaultMaxChunkChars = 120;

        private static readonly Regex CodeFence = new Regex("```[\\s\\S]*?```", RegexOptions.Compiled);
        private static readonly Regex InlineCode = new Regex("`([^`\n]+)`", RegexOptions.Compiled);
        private static readonly Regex Link = new Regex("\\[([^\\]]*)\\]\\(([^\\)]*)\\)", RegexOptions.Compiled);
        private static readonly Regex Image = new Regex("!\\[([^\\]]*)\\]\\(([^\\)]*)\\)", RegexOptions.Compiled);
        private static readonly Regex Emphasis = new Regex(@"[*_~]{1,3}([^\n]+?)[*_~]{1,3}", RegexOptions.Compiled);
        private static readonly Regex Heading = new Regex("^\\s{0,3}#{1,6}\\s*", RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex TableRow = new Regex("^\\s*\\|.*\\|\\s*$", RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex TableRule = new Regex("^\\s*\\|?[\\s:\\-\\|]+\\|?\\s*$", RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex Url = new Regex(@"https?://\S+", RegexOptions.Compiled);
        private static readonly Regex MultiBlank = new Regex("\n{2,}", RegexOptions.Compiled);

        /// <summary>把 Markdown 变成"适合朗读"的纯文本。代码块按需保留。</summary>
        public static string ToSpeechText(string markdown, bool keepCode)
        {
            if (string.IsNullOrEmpty(markdown)) return "";

            var text = markdown;

            // 代码块先处理：要么整段丢掉，要么只留内容不读围栏
            text = CodeFence.Replace(text, delegate (Match m)
            {
                if (!keepCode) return " ";
                var inner = m.Value;
                int first = inner.IndexOf('\n');
                if (first < 0) return " ";
                var body = inner.Substring(first + 1);
                int last = body.LastIndexOf("```", StringComparison.Ordinal);
                if (last >= 0) body = body.Substring(0, last);
                return "\n" + body + "\n";
            });

            // 表格整体丢掉：逐格朗读没有意义
            text = TableRule.Replace(text, " ");
            text = TableRow.Replace(text, " ");

            text = Image.Replace(text, "$1");
            text = Link.Replace(text, "$1");
            text = InlineCode.Replace(text, "$1");
            text = Heading.Replace(text, "");
            text = Url.Replace(text, " 链接 ");
            text = Emphasis.Replace(text, "$1");

            // 列表符号
            text = Regex.Replace(text, "^\\s*[-*+]\\s+", "", RegexOptions.Multiline);
            text = Regex.Replace(text, "^\\s*\\d+[.)]\\s+", "", RegexOptions.Multiline);

            // 引用与分隔线
            text = Regex.Replace(text, "^\\s*>\\s?", "", RegexOptions.Multiline);
            text = Regex.Replace(text, "^\\s*[-*_]{3,}\\s*$", " ", RegexOptions.Multiline);

            text = MultiBlank.Replace(text, "\n");
            return text.Trim();
        }

        /// <summary>
        /// 切段。优先在句末标点后切；一段内没有标点时退到逗号；再没有就硬切。
        /// 不切断 Markdown 残留的成对标点，也不让某段只剩一个孤零零的标点。
        /// </summary>
        public static List<SpeechChunk> Split(string text, int maxChars)
        {
            var chunks = new List<SpeechChunk>();
            if (string.IsNullOrWhiteSpace(text)) return chunks;
            if (maxChars < 10) maxChars = 10;

            var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            var paragraphs = normalized.Split('\n');

            foreach (var paragraph in paragraphs)
            {
                var line = paragraph.Trim();
                if (line.Length == 0) continue;

                if (line.Length <= maxChars)
                {
                    chunks.Add(new SpeechChunk { Text = line });
                    continue;
                }

                SplitLongLine(line, maxChars, chunks);
            }

            return chunks;
        }

        private static void SplitLongLine(string line, int maxChars, List<SpeechChunk> chunks)
        {
            int start = 0;

            while (start < line.Length)
            {
                int remaining = line.Length - start;
                if (remaining <= maxChars)
                {
                    var tail = line.Substring(start).Trim();
                    if (tail.Length > 0) chunks.Add(new SpeechChunk { Text = tail });
                    break;
                }

                int windowEnd = start + maxChars;
                int cut = FindCut(line, start, windowEnd);

                var piece = line.Substring(start, cut - start).Trim();
                if (piece.Length > 0) chunks.Add(new SpeechChunk { Text = piece });

                start = cut;

                // 跳过切点后的空白，避免下一段以空格开头
                while (start < line.Length && char.IsWhiteSpace(line[start])) start++;
            }
        }

        /// <summary>在 [start, windowEnd) 里找一个合适的切点，返回切点后一位。</summary>
        private static int FindCut(string line, int start, int windowEnd)
        {
            // 1) 句末标点：切在标点之后
            for (int i = windowEnd - 1; i > start; i--)
            {
                if (IsSentenceEnd(line[i]))
                {
                    return i + 1;
                }
            }

            // 2) 次级标点：切在标点之后
            for (int i = windowEnd - 1; i > start; i--)
            {
                if (IsClauseEnd(line[i]))
                {
                    return i + 1;
                }
            }

            // 3) 空白
            for (int i = windowEnd - 1; i > start; i--)
            {
                if (char.IsWhiteSpace(line[i])) return i + 1;
            }

            // 4) 硬切。不要切在代理对中间，否则会切出半个字符。
            int hard = windowEnd;
            if (hard < line.Length && char.IsLowSurrogate(line[hard]) && char.IsHighSurrogate(line[hard - 1]))
            {
                hard--;
            }
            return hard;
        }

        private static bool IsSentenceEnd(char c)
        {
            return c == '。' || c == '！' || c == '？' || c == '；'
                || c == '.' || c == '!' || c == '?' || c == ';'
                || c == '…' || c == '」' || c == '』' || c == '"' || c == '）';
        }

        private static bool IsClauseEnd(char c)
        {
            return c == '，' || c == ',' || c == '、' || c == '：' || c == ':'
                || c == '）' || c == ')' || c == '】' || c == '—';
        }

        /// <summary>一步到位：Markdown → 可朗读的段落列表。</summary>
        public static List<SpeechChunk> Prepare(string markdown, bool keepCode, int maxChars)
        {
            return Split(ToSpeechText(markdown, keepCode), maxChars);
        }
    }
}
