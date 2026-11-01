// Core/Text.cs —— 文本工具：slug、哈希、截断、相似度、关键词切分
//
// 集中放这里，避免像旧项目那样每个文件各写一份 SafeFileName / Truncate。
// 注意：UWP 的 .NET Standard 2.0 面里没有 string.Contains(string, StringComparison)，
// 一律用 IndexOf(..., StringComparison) >= 0。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Yanshuai.Messages.Core
{
    public static class TextUtil
    {
        public static bool Contains(string haystack, string needle, StringComparison cmp)
        {
            if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle)) return false;
            return haystack.IndexOf(needle, cmp) >= 0;
        }

        public static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            if (max <= 0) return string.Empty;
            if (s.Length <= max) return s;
            return s.Substring(0, max) + "…";
        }

        /// <summary>压掉连续空白，用于比较与去重。</summary>
        public static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length);
            bool lastSpace = false;
            foreach (var ch in s)
            {
                bool isSpace = char.IsWhiteSpace(ch);
                if (isSpace)
                {
                    if (!lastSpace) sb.Append(' ');
                    lastSpace = true;
                }
                else
                {
                    sb.Append(char.ToLowerInvariant(ch));
                    lastSpace = false;
                }
            }
            return sb.ToString().Trim();
        }

        /// <summary>把名字变成安全的文件夹名。空则回退 "profile"。</summary>
        public static string Slug(string name)
        {
            if (string.IsNullOrEmpty(name)) return "profile";
            var sb = new StringBuilder(name.Length);
            foreach (var ch in name.Trim())
            {
                if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
                else if (ch == '-' || ch == '_') sb.Append(ch);
                else if (ch == ' ' || ch == '.' || ch == '/' || ch == '\\') sb.Append('-');
                // 其余字符（含中文标点）丢弃
            }
            var slug = sb.ToString().Trim('-');
            // 允许中文：上面 char.IsLetterOrDigit 对中文返回 true，所以中文名会保留
            if (slug.Length == 0) slug = "profile";
            if (slug.Length > 48) slug = slug.Substring(0, 48);
            return slug;
        }

        public static string Sha256Hex(string text)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? string.Empty));
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        public static string Sha256Hex(byte[] data)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(data ?? new byte[0]);
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        public static string Base64Url(byte[] data)
        {
            return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
    }

    /// <summary>
    /// BM25 检索。用于没有嵌入向量时的关键词召回，也是向量召回失败后的降级路径。
    /// 参数是经典取值 K1=1.2 / B=0.75，不做花活。
    /// </summary>
    public sealed class Bm25Index
    {
        private const double K1 = 1.2;
        private const double B = 0.75;

        private readonly List<string> _docIds = new List<string>();
        private readonly List<Dictionary<string, int>> _docTerms = new List<Dictionary<string, int>>();
        private readonly List<int> _docLengths = new List<int>();
        private readonly Dictionary<string, int> _docFreq = new Dictionary<string, int>(StringComparer.Ordinal);
        private double _avgLength = 1.0;

        public int Count { get { return _docIds.Count; } }

        public void Add(string docId, string text)
        {
            var terms = Tokenize(text);
            var tf = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var t in terms)
            {
                int c;
                tf.TryGetValue(t, out c);
                tf[t] = c + 1;
            }

            _docIds.Add(docId);
            _docTerms.Add(tf);
            _docLengths.Add(terms.Count);

            foreach (var t in tf.Keys)
            {
                int c;
                _docFreq.TryGetValue(t, out c);
                _docFreq[t] = c + 1;
            }

            RecalcAverage();
        }

        public void Clear()
        {
            _docIds.Clear();
            _docTerms.Clear();
            _docLengths.Clear();
            _docFreq.Clear();
            _avgLength = 1.0;
        }

        private void RecalcAverage()
        {
            if (_docLengths.Count == 0) { _avgLength = 1.0; return; }
            long total = 0;
            foreach (var l in _docLengths) total += l;
            _avgLength = Math.Max(1.0, (double)total / _docLengths.Count);
        }

        /// <summary>返回 (docId, score) 降序结果，score > 0 才算命中。</summary>
        public List<KeyValuePair<string, double>> Search(string query, int topK)
        {
            var result = new List<KeyValuePair<string, double>>();
            if (_docIds.Count == 0 || string.IsNullOrEmpty(query)) return result;

            var qTerms = Tokenize(query).Distinct().ToList();
            if (qTerms.Count == 0) return result;

            int n = _docIds.Count;
            var scores = new double[n];

            foreach (var term in qTerms)
            {
                int df;
                if (!_docFreq.TryGetValue(term, out df) || df == 0) continue;
                double idf = Math.Log((n - df + 0.5) / (df + 0.5) + 1.0);

                for (int i = 0; i < n; i++)
                {
                    int tf;
                    if (!_docTerms[i].TryGetValue(term, out tf) || tf == 0) continue;
                    double dl = Math.Max(1, _docLengths[i]);
                    double denom = tf + K1 * (1.0 - B + B * dl / _avgLength);
                    scores[i] += idf * (tf * (K1 + 1.0)) / denom;
                }
            }

            for (int i = 0; i < n; i++)
            {
                if (scores[i] > 0) result.Add(new KeyValuePair<string, double>(_docIds[i], scores[i]));
            }

            result.Sort(delegate (KeyValuePair<string, double> a, KeyValuePair<string, double> b)
            {
                return b.Value.CompareTo(a.Value);
            });

            if (topK > 0 && result.Count > topK) result = result.GetRange(0, topK);
            return result;
        }

        /// <summary>
        /// 分词：中文按单字 + 双字组合切开，英文数字按单词。中英混排时两边都能召回。
        /// 不引入词典，因为手机上不值得为分词带一份词典。
        /// </summary>
        public static List<string> Tokenize(string text)
        {
            var tokens = new List<string>();
            if (string.IsNullOrEmpty(text)) return tokens;

            var word = new StringBuilder();
            var cjk = new StringBuilder();

            Action flushWord = delegate ()
            {
                if (word.Length > 0)
                {
                    tokens.Add(word.ToString().ToLowerInvariant());
                    word.Clear();
                }
            };
            Action flushCjk = delegate ()
            {
                if (cjk.Length == 0) return;
                var s = cjk.ToString();
                for (int i = 0; i < s.Length; i++)
                {
                    tokens.Add(s[i].ToString());
                    if (i + 1 < s.Length) tokens.Add(s.Substring(i, 2));
                }
                cjk.Clear();
            };

            foreach (var ch in text)
            {
                if (IsCjk(ch))
                {
                    flushWord();
                    cjk.Append(ch);
                }
                else if (char.IsLetterOrDigit(ch))
                {
                    flushCjk();
                    word.Append(ch);
                }
                else
                {
                    flushWord();
                    flushCjk();
                }
            }
            flushWord();
            flushCjk();
            return tokens;
        }

        private static bool IsCjk(char ch)
        {
            return (ch >= 0x4E00 && ch <= 0x9FFF)      // 基本区
                || (ch >= 0x3400 && ch <= 0x4DBF)      // 扩展 A
                || (ch >= 0xF900 && ch <= 0xFAFF)      // 兼容表意
                || (ch >= 0x3040 && ch <= 0x30FF);     // 假名
        }
    }
}
