// tests/CoreTests/Feature4Tests.cs —— 图片压缩策略 + 文档解析入口
//
// 压缩策略是纯逻辑（只算参数不碰图像 API），所以能在这里完整验证。
// 真正的编解码在 Platform 层，靠真机验证。

using System;
using System.Threading;
using System.Threading.Tasks;
using Yanshuai.Messages.Core;

namespace Yanshuai.Messages.Tests
{
    /// <summary>假 PDF 渲染器：用来验证 DocumentExtractor 的分派与降级逻辑。</summary>
    public sealed class FakePdfRenderer : IPdfRenderer
    {
        public bool Available = true;
        public string TextToReturn = "";
        public int PageCountToReturn = 3;
        public int RenderCountToReturn = 3;
        public bool FailRender;

        public bool IsAvailable { get { return Available; } }

        public Task<PdfRenderResult> RenderAsync(string relativePath, int maxPages, CancellationToken ct)
        {
            var result = new PdfRenderResult { Ok = !FailRender, PageCount = PageCountToReturn };

            if (FailRender)
            {
                result.Error = "渲染失败（测试）";
                return Task.FromResult(result);
            }

            int limit = maxPages <= 0 ? ImageCompressor.DefaultPdfPageLimit : maxPages;
            int count = Math.Min(limit, RenderCountToReturn);

            for (int i = 0; i < count; i++)
            {
                result.Pages.Add(new PdfPageImage
                {
                    PageNumber = i + 1,
                    MimeType = "image/jpeg",
                    Base64 = "QUJD",
                    ByteSize = 1024,
                    Width = 1000,
                    Height = 1400
                });
            }
            result.RenderedCount = count;

            if (PageCountToReturn > count)
            {
                result.Warnings.Add("PDF 共 " + PageCountToReturn + " 页，只送前 " + count + " 页。");
            }
            return Task.FromResult(result);
        }

        public Task<string> ExtractTextAsync(string relativePath, int maxChars, CancellationToken ct)
        {
            return Task.FromResult(TextToReturn ?? "");
        }
    }

    public static class Feature4Tests
    {
        public static void Register()
        {
            RegisterCompression();
            RegisterExtractor();
        }

        // ═════════════ 图片压缩策略 ═════════════
        private static void RegisterCompression()
        {
            Runner.Add("图片压缩", "小图小体积不折腾", delegate
            {
                var plan = ImageCompressor.Plan(800, 600, 100 * 1024);

                Check.False(plan.NeedsResize, "不需要处理");
                Check.EqualInt(800, plan.TargetWidth, "宽度不变");
                Check.Contains(plan.Reason, "范围内", "说明原因");
            });

            Runner.Add("图片压缩", "超大尺寸按长边缩放", delegate
            {
                // 4000×3000 的扫描件
                var plan = ImageCompressor.Plan(4000, 3000, 3 * 1024 * 1024);

                Check.True(plan.NeedsResize, "需要缩放");
                Check.EqualInt(ImageCompressor.MaxLongEdge, plan.TargetWidth, "长边压到上限");
                Check.True(plan.TargetHeight < ImageCompressor.MaxLongEdge, "短边等比缩小");

                // 宽高比必须保持
                double sourceRatio = 4000.0 / 3000.0;
                double targetRatio = (double)plan.TargetWidth / plan.TargetHeight;
                Check.InRange(sourceRatio - 0.02, sourceRatio + 0.02, targetRatio, "宽高比保持");
            });

            Runner.Add("图片压缩", "竖图同样按长边处理", delegate
            {
                var plan = ImageCompressor.Plan(1200, 3000, 2 * 1024 * 1024);
                Check.EqualInt(ImageCompressor.MaxLongEdge, plan.TargetHeight, "长边是高");
                Check.True(plan.TargetWidth < plan.TargetHeight, "仍然是竖图");
            });

            Runner.Add("图片压缩", "尺寸够小但体积超标时只降质量", delegate
            {
                var plan = ImageCompressor.Plan(1000, 800, 2 * 1024 * 1024);

                Check.True(plan.NeedsResize, "需要处理");
                Check.EqualInt(1000, plan.TargetWidth, "尺寸不变");
                Check.EqualInt(800, plan.TargetHeight, "尺寸不变");
                Check.True(plan.Quality < 0.75, "质量下调");
                Check.Contains(plan.Reason, "体积", "说明是体积问题");
            });

            Runner.Add("图片压缩", "体积超得越多质量压得越狠但有下限", delegate
            {
                var mild = ImageCompressor.Plan(1000, 800, 500 * 1024);
                var severe = ImageCompressor.Plan(1000, 800, 8 * 1024 * 1024);

                Check.True(severe.Quality <= mild.Quality, "超得多压得狠");
                Check.True(severe.Quality >= ImageCompressor.MinQuality, "不低于下限");
            });

            Runner.Add("图片压缩", "第二轮继续压到下限就停", delegate
            {
                // 用中等超标的情况：首次质量不会直接落到下限
                var plan = ImageCompressor.Plan(1000, 800, 900 * 1024);
                Check.True(plan.Quality > ImageCompressor.MinQuality, "首次没有直接压到下限");

                var next = ImageCompressor.NextAttempt(plan, 600 * 1024);
                Check.True(next != null, "还能再压");
                Check.True(next.Quality < plan.Quality, "质量继续降");

                // 已经到下限
                var atFloor = new ImageResizePlan { Quality = ImageCompressor.MinQuality };
                Check.True(ImageCompressor.NextAttempt(atFloor, 900 * 1024) == null, "到底就停");

                // 已经够小
                Check.True(ImageCompressor.NextAttempt(plan, 100 * 1024) == null, "达标就停");

                // 极端超标：首次就直接到底，没有第二轮
                var extreme = ImageCompressor.Plan(1000, 800, 8 * 1024 * 1024);
                Check.Equal(ImageCompressor.MinQuality, extreme.Quality, "极端情况首次到底");
                Check.True(ImageCompressor.NextAttempt(extreme, 900 * 1024) == null, "到底后不再尝试");
            });

            Runner.Add("图片压缩", "体积文案可读", delegate
            {
                Check.Equal("500 B", ImageCompressor.Describe(500), "字节");
                Check.Equal("2 KB", ImageCompressor.Describe(2048), "千字节");
                Check.Equal("1.5 MB", ImageCompressor.Describe(1572864), "兆字节");
            });

            Runner.Add("图片压缩", "无效尺寸不炸", delegate
            {
                var plan = ImageCompressor.Plan(0, 0, 1024);
                Check.False(plan.NeedsResize, "跳过");
            });
        }

        // ═════════════ 解析入口分派 ═════════════
        private static void RegisterExtractor()
        {
            Runner.Add("解析入口", "文本型 PDF 走文字通道，不渲染图片", delegate
            {
                var store = new InMemoryFileStore();
                var pdf = new FakePdfRenderer { TextToReturn = new string('字', 200) };
                var extractor = new DocumentExtractor(store, pdf);

                var result = extractor.ExtractAsync("files/doc.pdf", "doc.pdf", 5, CancellationToken.None).Result;

                Check.True(result.Ok, "成功");
                Check.Equal("pdf", result.Kind, "类型");
                Check.EqualInt(0, result.Images.Count, "文本型不产生图片");
                Check.Contains(result.Text, "字", "有文字");
                Check.Contains(result.Warnings[0], "文本型", "说明走的是文字通道");
            });

            Runner.Add("解析入口", "扫描件 PDF 回退到渲染图片", delegate
            {
                var store = new InMemoryFileStore();
                var pdf = new FakePdfRenderer { TextToReturn = "", PageCountToReturn = 3 };
                var extractor = new DocumentExtractor(store, pdf);

                var result = extractor.ExtractAsync("files/scan.pdf", "scan.pdf", 5, CancellationToken.None).Result;

                Check.True(result.Ok, "成功");
                Check.EqualInt(3, result.Images.Count, "三页图片");
                Check.EqualInt(3, result.PageCount, "总页数");
                Check.Contains(result.Text, "扫描件", "说明是扫描件");
            });

            Runner.Add("解析入口", "PDF 页数超过上限时截断并提示", delegate
            {
                var store = new InMemoryFileStore();
                var pdf = new FakePdfRenderer { TextToReturn = "", PageCountToReturn = 20, RenderCountToReturn = 20 };
                var extractor = new DocumentExtractor(store, pdf);

                var result = extractor.ExtractAsync("files/big.pdf", "big.pdf", 5, CancellationToken.None).Result;

                Check.EqualInt(5, result.Images.Count, "只渲染 5 页");
                Check.EqualInt(20, result.PageCount, "总页数如实报告");
                Check.True(ContainsWarning(result.Warnings, "只送前 5 页"), "提示已截断");
            });

            Runner.Add("解析入口", "平台不支持 PDF 时给出明确提示", delegate
            {
                var store = new InMemoryFileStore();
                var extractor = new DocumentExtractor(store, NullPdfRenderer.Instance);

                var result = extractor.ExtractAsync("files/a.pdf", "a.pdf", 5, CancellationToken.None).Result;

                Check.False(result.Ok, "失败");
                Check.True(ContainsWarning(result.Warnings, "不支持"), "说明不支持");
            });

            Runner.Add("解析入口", "Office 文件走 OoxmlReader", delegate
            {
                var store = new InMemoryFileStore();
                var extractor = new DocumentExtractor(store, NullPdfRenderer.Instance);

                // 放一个真的最小 docx 进去
                var docx = BuildMinimalDocx();
                store.WriteBytesAsync("files/a.docx", docx).Wait();

                var result = extractor.ExtractAsync("files/a.docx", "a.docx", 5, CancellationToken.None).Result;

                Check.True(result.Ok, "成功");
                Check.Equal("docx", result.Kind, "类型");
                Check.Contains(result.Text, "你好文档", "内容");
            });

            Runner.Add("解析入口", "纯文本直接读", delegate
            {
                var store = new InMemoryFileStore();
                store.WriteTextAsync("files/notes.md", "# 标题\n正文内容").Wait();

                var extractor = new DocumentExtractor(store, NullPdfRenderer.Instance);
                var result = extractor.ExtractAsync("files/notes.md", "notes.md", 5, CancellationToken.None).Result;

                Check.True(result.Ok, "成功");
                Check.Equal("text", result.Kind, "类型");
                Check.Contains(result.Text, "正文内容", "内容");
            });

            Runner.Add("解析入口", "支持列表与 PDF 判定", delegate
            {
                Check.True(DocumentExtractor.IsSupported("a.docx"), "docx");
                Check.True(DocumentExtractor.IsSupported("a.pdf"), "pdf");
                Check.True(DocumentExtractor.IsSupported("a.md"), "md");
                Check.False(DocumentExtractor.IsSupported("a.exe"), "exe 不支持");
                Check.True(DocumentExtractor.IsPdf("a.PDF"), "大写扩展名也认");
                Check.False(DocumentExtractor.IsPdf("a.docx"), "docx 不是 pdf");
            });

            Runner.Add("解析入口", "上下文格式化包含页数与截断提示", delegate
            {
                var document = new DocumentResult
                {
                    Ok = true,
                    Text = "正文",
                    PageCount = 8,
                    Truncated = true
                };
                document.Warnings.Add("内容很长");

                var text = DocumentExtractor.FormatForContext("报告.docx", document);

                Check.Contains(text, "报告.docx", "文件名");
                Check.Contains(text, "8 页", "页数");
                Check.Contains(text, "已截断", "截断提示");
                Check.Contains(text, "正文", "正文");
                Check.Contains(text, "内容很长", "警告");
            });
        }

        private static bool ContainsWarning(System.Collections.Generic.List<string> warnings, string needle)
        {
            foreach (var warning in warnings)
            {
                if (warning != null && warning.IndexOf(needle, StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        private static byte[] BuildMinimalDocx()
        {
            using (var stream = new System.IO.MemoryStream())
            {
                using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, true))
                {
                    var entry = zip.CreateEntry("word/document.xml");
                    using (var writer = new System.IO.StreamWriter(entry.Open()))
                    {
                        writer.Write("<?xml version=\"1.0\"?><w:document " +
                            "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
                            "<w:body><w:p><w:r><w:t>你好文档</w:t></w:r></w:p></w:body></w:document>");
                    }
                }
                return stream.ToArray();
            }
        }
    }
}
