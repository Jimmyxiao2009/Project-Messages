// Platform/UwpPdfRenderer.cs —— PDF 渲染（UWP 实现）
//
// 关键发现：**UWP 自带 PDF 渲染，不用自己写解析器**。
// Windows.Data.Pdf.PdfDocument 从 Windows 8.1 / WP 8.1 起就在，W10M 上可用：
//   PdfDocument.LoadFromFileAsync(file)
//     → doc.PageCount
//     → doc.GetPage(i).RenderToStreamAsync(stream, options)
//     → BitmapDecoder 读回来 → BitmapEncoder 按需降采样并重编码 JPEG
//
// 两个必须做对的细节：
//   1) 渲染选项里 BackgroundColor 要设成白色。默认透明底在模型侧常被当成黑色，
//      白底黑字的文档会变成"黑底黑字"什么都看不见。
//   2) 尺寸按 DPI 算，再交给 ImageCompressor 判断要不要缩 —— 不要直接把原始像素
//      扔给编码器，扫描件一页可能有 4000×5000。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Yanshuai.Messages.Core;

namespace Yanshuai.Messages.Platform
{
    public sealed class UwpPdfRenderer : IPdfRenderer
    {
        private readonly UwpFileStore _store;

        public UwpPdfRenderer(UwpFileStore store)
        {
            _store = store;
        }

        public bool IsAvailable
        {
            get
            {
                try
                {
                    return Windows.Foundation.Metadata.ApiInformation
                        .IsTypePresent("Windows.Data.Pdf.PdfDocument");
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        // ═════════════════════════════════════════════════════════════════
        // 渲染成图
        // ═════════════════════════════════════════════════════════════════
        public async Task<PdfRenderResult> RenderAsync(string relativePath, int maxPages, CancellationToken ct)
        {
            var result = new PdfRenderResult();

            if (!IsAvailable)
            {
                result.Error = "当前系统不支持 PDF 渲染。";
                return result;
            }

            if (maxPages <= 0) maxPages = ImageCompressor.DefaultPdfPageLimit;

            StorageFile file;
            try
            {
                file = await _store.GetFileAsync(relativePath).ConfigureAwait(false);
                if (file == null)
                {
                    result.Error = "找不到这个 PDF 文件。";
                    return result;
                }
            }
            catch (Exception)
            {
                result.Error = "无法打开这个 PDF 文件。";
                return result;
            }

            PdfDocument document;
            try
            {
                document = await PdfDocument.LoadFromFileAsync(file).AsTask(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                result.Error = "这个 PDF 打不开，可能已加密或损坏。";
                return result;
            }

            result.PageCount = (int)document.PageCount;
            if (result.PageCount == 0)
            {
                result.Error = "这个 PDF 没有页面。";
                return result;
            }

            int toRender = Math.Min(maxPages, result.PageCount);
            if (result.PageCount > toRender)
            {
                result.Warnings.Add("PDF 共 " + result.PageCount + " 页，只送前 " + toRender + " 页。");
            }

            for (int i = 0; i < toRender; i++)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var page = document.GetPage((uint)i);

                    var options = new PdfPageRenderOptions
                    {
                        // 白底：透明底会被模型当成黑色
                        BackgroundColor = Windows.UI.Colors.White,
                        DestinationWidth = (uint)Math.Max(1, page.Size.Width * ImageCompressor.PdfRenderDpi / 72.0)
                    };

                    using (var stream = new InMemoryRandomAccessStream())
                    {
                        await page.RenderToStreamAsync(stream, options).AsTask(ct).ConfigureAwait(false);
                        stream.Seek(0);

                        var image = await EncodeAsync(stream, i + 1, ct).ConfigureAwait(false);
                        if (image != null && !string.IsNullOrEmpty(image.Base64))
                        {
                            result.Pages.Add(image);
                            result.RenderedCount++;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    result.Warnings.Add("第 " + (i + 1) + " 页渲染失败，已跳过。");
                }
            }

            result.Ok = result.RenderedCount > 0;
            if (!result.Ok && string.IsNullOrEmpty(result.Error))
            {
                result.Error = "没有一页能渲染出来。";
            }
            return result;
        }

        /// <summary>把渲染出来的流解码、按需降采样、重编码成 JPEG，并返回 base64。</summary>
        private static async Task<PdfPageImage> EncodeAsync(IRandomAccessStream source, int pageNumber, CancellationToken ct)
        {
            var decoder = await BitmapDecoder.CreateAsync(source).AsTask(ct).ConfigureAwait(false);

            uint width = decoder.PixelWidth;
            uint height = decoder.PixelHeight;

            // 先用尺寸估一个体积（按 JPEG 约 0.25 字节/像素的经验值），
            // 让 ImageCompressor 决定缩放比例与质量
            long estimated = (long)(width * height * 0.25);
            var plan = ImageCompressor.Plan((int)width, (int)height, estimated);

            uint targetWidth = (uint)Math.Max(1, plan.TargetWidth);
            uint targetHeight = (uint)Math.Max(1, plan.TargetHeight);

            var transform = new BitmapTransform
            {
                ScaledWidth = targetWidth,
                ScaledHeight = targetHeight,
                InterpolationMode = BitmapInterpolationMode.Fant
            };

            var pixelData = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage).AsTask(ct).ConfigureAwait(false);

            using (var output = new InMemoryRandomAccessStream())
            {
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output).AsTask(ct).ConfigureAwait(false);
                encoder.SetPixelData(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    targetWidth,
                    targetHeight,
                    decoder.DpiX > 0 ? decoder.DpiX : 96,
                    decoder.DpiY > 0 ? decoder.DpiY : 96,
                    pixelData.DetachPixelData());

                // JPEG 质量：0–1 映射到 0–100
                encoder.BitmapTransform.ScaledWidth = targetWidth;
                encoder.BitmapTransform.ScaledHeight = targetHeight;
                try
                {
                    encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
                }
                catch (Exception) { }

                await encoder.FlushAsync().AsTask(ct).ConfigureAwait(false);
                output.Seek(0);

                var bytes = await ReadAllBytesAsync(output).ConfigureAwait(false);

                return new PdfPageImage
                {
                    PageNumber = pageNumber,
                    MimeType = "image/jpeg",
                    Base64 = Convert.ToBase64String(bytes),
                    ByteSize = bytes.Length,
                    Width = (int)targetWidth,
                    Height = (int)targetHeight
                };
            }
        }

        private static async Task<byte[]> ReadAllBytesAsync(IRandomAccessStream stream)
        {
            var size = (uint)stream.Size;
            using (var reader = new DataReader(stream.GetInputStreamAt(0)))
            {
                await reader.LoadAsync(size).AsTask().ConfigureAwait(false);
                var bytes = new byte[size];
                reader.ReadBytes(bytes);
                return bytes;
            }
        }

        // ═════════════════════════════════════════════════════════════════
        // 抽文字
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// 尝试抽 PDF 的纯文本。
        ///
        /// 说明：UWP 的 PdfDocument **不提供文本抽取 API**（只能渲染）。
        /// 所以这里的策略是"抽不到就返回空"，由调用方回退到渲染成图。
        /// 真正要做端上文本抽取需要自己解 xref + Flate 内容流，成本高且只覆盖简单 PDF，
        /// 已列入下个版本；当前对文本型 PDF 的最优路径是走 provider 的原生文档输入。
        /// </summary>
        public Task<string> ExtractTextAsync(string relativePath, int maxChars, CancellationToken ct)
        {
            return Task.FromResult(string.Empty);
        }
    }
}
