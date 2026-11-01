// Core/Files/DocumentExtractor.cs —— 文档解析统一入口
//
// 按扩展名分派到具体的解析器，产出统一的 ExtractedDocument。
// 纯逻辑部分（Office / 文本）在 Core 里；PDF 渲染依赖平台 API，通过 IPdfRenderer 注入。
//
// 设计取舍：PDF 优先抽文字，抽不出东西（扫描件）才渲染成图。
// 因为文字进上下文最省 token，而且能被引用；渲染成图只在必要的时候做。

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Yanshuai.Messages.Core
{
    /// <summary>PDF 渲染出的一页图片。</summary>
    public sealed class PdfPageImage
    {
        public int PageNumber { get; set; }
        public string MimeType { get; set; } = "image/jpeg";
        public string Base64 { get; set; } = "";
        public long ByteSize { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    public sealed class PdfRenderResult
    {
        public bool Ok { get; set; }
        public string Error { get; set; } = "";
        public int PageCount { get; set; }
        public int RenderedCount { get; set; }
        public List<PdfPageImage> Pages { get; private set; }
        public List<string> Warnings { get; private set; }

        public PdfRenderResult()
        {
            Pages = new List<PdfPageImage>();
            Warnings = new List<string>();
        }
    }

    /// <summary>PDF 渲染能力。由平台层实现（UWP 用 Windows.Data.Pdf）。</summary>
    public interface IPdfRenderer
    {
        bool IsAvailable { get; }

        /// <summary>
        /// 把 PDF 的前若干页渲染成压缩后的 JPEG。
        /// maxPages 为 0 表示用默认值。
        /// </summary>
        Task<PdfRenderResult> RenderAsync(string relativePath, int maxPages, CancellationToken ct);

        /// <summary>尝试抽取 PDF 的纯文本（扫描件会返回空）。失败返回空串。</summary>
        Task<string> ExtractTextAsync(string relativePath, int maxChars, CancellationToken ct);
    }

    /// <summary>没有 PDF 能力时的兜底实现（桌面测试 / 平台不支持）。</summary>
    public sealed class NullPdfRenderer : IPdfRenderer
    {
        public static readonly NullPdfRenderer Instance = new NullPdfRenderer();

        public bool IsAvailable { get { return false; } }

        public Task<PdfRenderResult> RenderAsync(string relativePath, int maxPages, CancellationToken ct)
        {
            var result = new PdfRenderResult { Ok = false, Error = "当前平台不支持 PDF 渲染。" };
            return Task.FromResult(result);
        }

        public Task<string> ExtractTextAsync(string relativePath, int maxChars, CancellationToken ct)
        {
            return Task.FromResult(string.Empty);
        }
    }

    /// <summary>解析结果（可能同时含文字与图片）。</summary>
    public sealed class DocumentResult
    {
        public bool Ok { get; set; }
        public string Kind { get; set; } = "";
        /// <summary>进上下文的文字。</summary>
        public string Text { get; set; } = "";
        /// <summary>需要走视觉通道的图片（PDF 扫描件）。</summary>
        public List<PdfPageImage> Images { get; private set; }
        public List<string> Warnings { get; private set; }
        public int PageCount { get; set; }
        public bool Truncated { get; set; }

        public DocumentResult()
        {
            Images = new List<PdfPageImage>();
            Warnings = new List<string>();
        }

        public int CharCount { get { return Text == null ? 0 : Text.Length; } }
    }

    public sealed class DocumentExtractor
    {
        public const int MaxTextChars = 120000;
        public const int MaxPdfTextChars = 60000;

        private readonly IFileStore _store;
        private readonly IPdfRenderer _pdf;

        public DocumentExtractor(IFileStore store, IPdfRenderer pdf)
        {
            _store = store;
            _pdf = pdf ?? NullPdfRenderer.Instance;
        }

        /// <summary>支持解析的扩展名（供 UI 的文件选择器过滤）。</summary>
        public static readonly string[] SupportedExtensions = new string[]
        {
            ".docx", ".xlsx", ".pptx", ".pdf",
            ".txt", ".md", ".json", ".csv", ".tsv", ".xml", ".yaml", ".yml",
            ".cs", ".py", ".js", ".ts", ".html", ".css", ".sql", ".log"
        };

        public static bool IsSupported(string fileName)
        {
            var extension = WorkspaceLayout.Extension(fileName ?? "");
            foreach (var candidate in SupportedExtensions)
            {
                if (string.Equals(candidate, extension, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public static bool IsPdf(string fileName)
        {
            return string.Equals(WorkspaceLayout.Extension(fileName ?? ""), ".pdf", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>按路径解析。relativePath 是档案内的相对路径。</summary>
        public async Task<DocumentResult> ExtractAsync(
            string relativePath, string fileName, int maxPdfPages, CancellationToken ct)
        {
            var result = new DocumentResult();
            if (string.IsNullOrEmpty(relativePath))
            {
                result.Warnings.Add("文件路径为空。");
                return result;
            }

            var extension = WorkspaceLayout.Extension(fileName ?? relativePath);

            // ── PDF：先文字，抽不到再渲染 ──
            if (string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase))
            {
                result.Kind = "pdf";

                if (_pdf != null && _pdf.IsAvailable)
                {
                    var text = await _pdf.ExtractTextAsync(relativePath, MaxPdfTextChars, ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(text) && text.Trim().Length > 40)
                    {
                        result.Text = TextUtil.Truncate(text, MaxPdfTextChars);
                        result.Ok = true;
                        result.Warnings.Add("这是文本型 PDF，已直接提取文字。");
                        return result;
                    }

                    // 扫描件：渲染成图
                    var rendered = await _pdf.RenderAsync(relativePath, maxPdfPages, ct).ConfigureAwait(false);
                    result.PageCount = rendered.PageCount;
                    result.Images.AddRange(rendered.Pages);
                    result.Warnings.AddRange(rendered.Warnings);

                    if (rendered.RenderedCount > 0)
                    {
                        result.Ok = true;
                        result.Text = "（这是一个扫描件 PDF，已把前 " + rendered.RenderedCount + " 页作为图片附上。）";
                        return result;
                    }

                    result.Ok = false;
                    result.Warnings.Add(rendered.Ok ? "PDF 渲染不出内容。" : rendered.Error);
                    return result;
                }

                result.Warnings.Add("当前平台不支持 PDF 渲染，无法读取这个文件。");
                return result;
            }

            // ── Office ──
            if (OoxmlReader.IsOfficeExtension(extension))
            {
                byte[] bytes;
                try
                {
                    bytes = await _store.ReadBytesAsync(relativePath).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    result.Warnings.Add("读取文件失败。");
                    return result;
                }

                var extracted = OoxmlReader.Extract(bytes, fileName);
                result.Kind = extracted.Kind;
                result.Ok = extracted.Ok;
                result.Text = extracted.Text;
                result.PageCount = extracted.PageCount;
                result.Truncated = extracted.Truncated;
                result.Warnings.AddRange(extracted.Warnings);

                if (result.CharCount > MaxTextChars)
                {
                    result.Text = TextUtil.Truncate(result.Text, MaxTextChars);
                    result.Truncated = true;
                }
                return result;
            }

            // ── 纯文本 ──
            try
            {
                var text = await _store.ReadTextAsync(relativePath).ConfigureAwait(false);
                result.Kind = "text";
                result.Ok = true;

                if (text != null && text.Length > MaxTextChars)
                {
                    text = TextUtil.Truncate(text, MaxTextChars);
                    result.Truncated = true;
                }
                result.Text = text ?? "";

                if (string.IsNullOrEmpty(result.Text)) result.Warnings.Add("文件是空的。");
                return result;
            }
            catch (Exception)
            {
                result.Warnings.Add("无法读取为文本（可能是二进制文件）。");
                return result;
            }
        }

        /// <summary>
        /// 把解析结果拼成进上下文的一段文字。图片部分由调用方另行处理（走视觉通道）。
        /// </summary>
        public static string FormatForContext(string fileName, DocumentResult document)
        {
            if (document == null) return "";

            var sb = new System.Text.StringBuilder();
            sb.Append("【附件：").Append(fileName);

            if (document.PageCount > 1)
            {
                sb.Append("，共 ").Append(document.PageCount).Append(" 页/表");
            }
            if (document.Truncated)
            {
                sb.Append("，内容过长已截断");
            }
            sb.Append("】\n");

            if (!string.IsNullOrEmpty(document.Text))
            {
                sb.Append(document.Text);
            }

            foreach (var warning in document.Warnings)
            {
                sb.Append("\n（提示：").Append(warning).Append("）");
            }

            return sb.ToString();
        }
    }
}
