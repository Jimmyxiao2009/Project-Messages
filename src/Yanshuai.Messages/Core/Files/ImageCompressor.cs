// Core/Files/ImageCompressor.cs —— 图片压缩
//
// 为什么必须有这一步：手机拍的扫描件单页可能 3~5 MB，直接 base64 送模型
// 既慢又可能超限（多数视觉模型对单图有 5~20MB 硬上限，而且 token 按尺寸算）。
// 压缩目标是"长边不超过视觉模型的有效分辨率 + 体积可控"。
//
// 这里是纯逻辑（只算参数、不碰图像 API），真正的编解码在 Platform 层用
// BitmapDecoder / BitmapEncoder 做。分开的好处：参数策略可以单测。

using System;

namespace Yanshuai.Messages.Core
{
    public sealed class ImageResizePlan
    {
        public int SourceWidth { get; set; }
        public int SourceHeight { get; set; }
        public int TargetWidth { get; set; }
        public int TargetHeight { get; set; }
        public bool NeedsResize { get; set; }
        /// <summary>0–1。超过阈值时下调，用于"体积还是太大"的第二轮压缩。</summary>
        public double Quality { get; set; } = 0.75;
        public string Reason { get; set; } = "";
    }

    public static class ImageCompressor
    {
        /// <summary>
        /// 长边上限。取 1568 是有依据的：主流视觉模型（Claude / GPT-4o 系）
        /// 会把超过约 1568px 的图缩到该量级再编码，再大只是白占带宽。
        /// </summary>
        public const int MaxLongEdge = 1568;

        /// <summary>单张图片的目标体积上限（字节）。</summary>
        public const long TargetBytes = 400L * 1024;

        /// <summary>低于这个体积就不折腾了，原样发。</summary>
        public const long SkipBelowBytes = 220L * 1024;

        /// <summary>压缩质量下限（再低就明显糊了，宁可让用户看到"太大"的提示）。</summary>
        public const double MinQuality = 0.45;

        /// <summary>
        /// 规划一次压缩。不读图像数据，只按尺寸与体积给方案。
        /// </summary>
        public static ImageResizePlan Plan(int width, int height, long byteSize)
        {
            var plan = new ImageResizePlan
            {
                SourceWidth = width,
                SourceHeight = height,
                TargetWidth = width,
                TargetHeight = height,
                Quality = 0.75
            };

            if (width <= 0 || height <= 0)
            {
                plan.NeedsResize = false;
                plan.Reason = "尺寸无效，跳过压缩";
                return plan;
            }

            bool tooBig = byteSize > TargetBytes;
            int longEdge = Math.Max(width, height);
            bool tooLarge = longEdge > MaxLongEdge;

            if (!tooBig && !tooLarge)
            {
                plan.NeedsResize = false;
                plan.Reason = "尺寸与体积都在范围内，原样发送";
                return plan;
            }

            plan.NeedsResize = true;

            if (tooLarge)
            {
                double scale = (double)MaxLongEdge / longEdge;
                plan.TargetWidth = Math.Max(1, (int)Math.Round(width * scale));
                plan.TargetHeight = Math.Max(1, (int)Math.Round(height * scale));
                plan.Reason = "长边 " + longEdge + " 超过 " + MaxLongEdge + "，缩到 " + plan.TargetWidth + "×" + plan.TargetHeight;
            }
            else
            {
                plan.Reason = "体积 " + (byteSize / 1024) + "KB 超过目标，仅降质量";
            }

            // 体积超得越多，质量压得越狠（但有下限）
            if (tooBig)
            {
                double ratio = (double)byteSize / TargetBytes;
                if (ratio > 6) plan.Quality = MinQuality;
                else if (ratio > 3) plan.Quality = 0.55;
                else if (ratio > 1.5) plan.Quality = 0.65;
                else plan.Quality = 0.72;
            }

            return plan;
        }

        /// <summary>
        /// 压缩后如果还是太大，第二轮：继续降质量。
        /// 返回 null 表示"已经到底了，别再试"。
        /// </summary>
        public static ImageResizePlan NextAttempt(ImageResizePlan previous, long actualBytes)
        {
            if (previous == null) return null;
            if (actualBytes <= TargetBytes) return null;
            if (previous.Quality <= MinQuality + 0.001) return null;

            var next = new ImageResizePlan
            {
                SourceWidth = previous.TargetWidth,
                SourceHeight = previous.TargetHeight,
                TargetWidth = previous.TargetWidth,
                TargetHeight = previous.TargetHeight,
                NeedsResize = true,
                Quality = Math.Max(MinQuality, previous.Quality - 0.12),
                Reason = "仍然偏大，质量降到 " + Math.Max(MinQuality, previous.Quality - 0.12).ToString("0.00")
            };
            return next;
        }

        /// <summary>按字节数给一句人话，用于日志与提示。</summary>
        public static string Describe(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024) + " KB";
            return (bytes / 1024.0 / 1024.0).ToString("0.##") + " MB";
        }

        /// <summary>
        /// PDF 渲染的默认参数：每英寸多少像素。
        /// 手机屏上阅读足够，同时避免渲染出来的位图过大。
        /// </summary>
        public const float PdfRenderDpi = 144;

        /// <summary>单次最多处理多少页 PDF（可配）。</summary>
        public const int DefaultPdfPageLimit = 5;

        /// <summary>PDF 页面渲染选项：白底（透明底会被模型当成黑色）。</summary>
        public static bool PdfTransparentBackground { get { return false; } }
    }
}
