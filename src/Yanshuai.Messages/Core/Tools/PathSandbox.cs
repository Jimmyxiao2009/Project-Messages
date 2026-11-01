// Core/Tools/PathSandbox.cs —— 路径安全与网页文本提取
//
// 安全边界只有一条：**工具只能读写当前档案文件夹下的文件**。
// 旧项目用 KnownFolders 直接放开了相册/音乐/视频/文档与整个 SD 卡，
// 一旦模型被提示注入带偏就能翻遍用户设备。这里从根上收紧：
//   1) 拒绝任何绝对路径与盘符；
//   2) 拒绝任何 ".."（不做"归一化后再判断"，直接拒）；
//   3) 拒绝含非法文件名字符的段；
//   4) 结果一律拼在 <档案>/files/ 之下。
// 需要访问外部目录时走单独的"授权目录"入口，不从这里绕。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Yanshuai.Messages.Core
{
    public static class PathSandbox
    {
        /// <summary>把模型给的路径归一化成档案内的相对路径。非法返回 null。</summary>
        public static string NormalizeRelative(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";

            var p = raw.Trim().Replace('\\', '/');

            // 盘符
            if (p.Length >= 2 && p[1] == ':') p = p.Substring(2);

            // 前置斜杠（绝对路径）
            p = p.TrimStart('/');

            // 模型经常带上 files 前缀，容忍一下
            if (p.StartsWith("files/", StringComparison.OrdinalIgnoreCase)) p = p.Substring(6);

            var segments = p.Split('/');
            var stack = new List<string>();
            var invalid = System.IO.Path.GetInvalidFileNameChars();

            foreach (var segment in segments)
            {
                if (segment.Length == 0 || segment == ".") continue;
                if (segment == "..") return null;                       // 任何上跳都拒绝
                if (segment.IndexOfAny(invalid) >= 0) return null;
                if (segment.Length > 128) return null;
                stack.Add(segment);
            }

            return string.Join("/", stack.ToArray());
        }

        /// <summary>归一化并拼成档案内的完整相对路径（含 files 前缀）。非法返回 null。</summary>
        public static string ResolveInFiles(ToolContext context, string raw)
        {
            var relative = NormalizeRelative(raw);
            if (relative == null) return null;

            var root = context.FilesRoot;
            if (string.IsNullOrEmpty(relative)) return root;
            return root + "/" + relative;
        }

        /// <summary>把路径里明显越界的情况转成给模型看的说明。</summary>
        public static string RejectReason(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "路径为空。";
            if (raw.IndexOf("..", StringComparison.Ordinal) >= 0)
            {
                return "不允许使用 .. 访问上级目录。请只使用档案目录内的相对路径。";
            }
            return "路径不合法（不能是绝对路径，也不能包含特殊字符）：" + TextUtil.Truncate(raw, 80);
        }
    }

    public static class WebText
    {
        private static readonly Regex ScriptStyle = new Regex(
            @"<(script|style)[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex Tag = new Regex(@"<[^>]+>", RegexOptions.Compiled);

        private static readonly Regex Blank = new Regex(@"\n{3,}", RegexOptions.Compiled);

        /// <summary>极简 HTML → 文本。不追求完整，只要够模型读。</summary>
        public static string HtmlToText(string html)
        {
            if (string.IsNullOrEmpty(html)) return "";
            var s = ScriptStyle.Replace(html, " ");
            s = Regex.Replace(s, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"</(p|div|li|h[1-6]|tr)>", "\n", RegexOptions.IgnoreCase);
            s = Tag.Replace(s, " ");
            s = DecodeEntities(s);
            s = Regex.Replace(s, @"[ \t]{2,}", " ");
            s = Blank.Replace(s, "\n\n");
            return s.Trim();
        }

        public static string DecodeEntities(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var s = text
                .Replace("&nbsp;", " ")
                .Replace("&amp;", "&")
                .Replace("&lt;", "<")
                .Replace("&gt;", ">")
                .Replace("&quot;", "\"")
                .Replace("&#39;", "'")
                .Replace("&apos;", "'")
                .Replace("&mdash;", "—")
                .Replace("&ndash;", "–");

            // 数字实体
            s = Regex.Replace(s, @"&#(\d{1,6});", delegate (Match m)
            {
                int code;
                if (int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out code)
                    && code > 0 && code <= 0x10FFFF)
                {
                    try { return char.ConvertFromUtf32(code); }
                    catch (Exception) { return m.Value; }
                }
                return m.Value;
            });
            return s;
        }

        /// <summary>压缩成单行预览，给工具摘要用。</summary>
        public static string OneLine(string text, int max)
        {
            var flat = Regex.Replace(text ?? "", @"\s+", " ");
            return TextUtil.Truncate(flat.Trim(), max);
        }
    }
}
