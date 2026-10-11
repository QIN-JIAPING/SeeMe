// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SeeMe
{
    /// <summary>
    /// 把「逐屏滚动截取的多张 PNG」按各自的滚动偏移拼成一张整篇长图。
    ///
    /// <para><b>为什么单独成类</b>：与 <see cref="ChartExportCodec"/> 同样的理由 ——
    /// 拼接是纯几何+像素运算，不含 WebView 依赖，独立出来才能真正被测到；
    /// 截屏那段（滚动、等待渲染、捕获）留在 MainWindow 里。</para>
    ///
    /// <para><b>🔴 DPI 缩放是这里最容易错的地方</b>：<c>CapturePreviewAsync</c> 返回的是
    /// <b>物理像素</b>图像（125% 缩放下 1000 CSS px 宽会拿到 1250 px），
    /// 而滚动偏移是 <b>CSS 像素</b>。两者单位不同，直接混用会让长图每接一屏就错位一截。
    /// 这里统一换算到帧的像素坐标系后再绘制。</para>
    /// </summary>
    public static class LongImageExporter
    {
        /// <summary>拼接结果：成功给 <see cref="Bytes"/>，失败给 <see cref="Error"/> 的可读原因。</summary>
        public sealed record Result(byte[]? Bytes, string? Error)
        {
            public bool Ok => Bytes != null;
        }

        // ──────────────── 截屏前的规划与预检 ────────────────
        //
        // 🔴 这几个 API 存在的唯一理由：**把上限判断提到截屏之前**。
        //    长图的屏数由页面报告的 scrollHeight 决定（不可信输入），若先截后判，
        //    超大文档会先滚几百屏、把 PNG 全堆进内存，才发现超限 —— 表现为「点导出就卡死」。
        //    （2026-10-11 安全审查发现：原先只有 Stitch 阶段的上限。）

        /// <summary>
        /// 逐屏滚动偏移（CSS 像素）。最后一屏对齐到「整篇高度 − 视口高度」，
        /// 保证文档末尾不被截断（会与上一屏重叠，拼接时由后一帧覆盖）。
        /// </summary>
        public static List<int> SegmentOffsets(int canvasHeightCss, int viewportHeightCss)
        {
            var offsets = new List<int>();
            if (canvasHeightCss <= 0 || viewportHeightCss <= 0) return offsets;

            var lastOffset = Math.Max(0, canvasHeightCss - viewportHeightCss);
            for (var y = 0; y < canvasHeightCss; y += viewportHeightCss)
            {
                var offset = Math.Min(y, lastOffset);
                if (offsets.Count == 0 || offsets[^1] != offset) offsets.Add(offset);
                if (offset >= lastOffset) break;
            }
            return offsets;
        }

        /// <summary>
        /// 截屏前预检：返回 <c>null</c> 表示可以继续，否则返回给用户看的原因。
        ///
        /// <para><b>必须在开始逐屏截取之前调用。</b>这里用 CSS 像素（截屏前拿不到设备像素比），
        /// 因此是**保守的**下界判断；设备像素级的真正上限仍由 <see cref="Stitch"/> 把关 ——
        /// 两层各司其职：这里挡住「屏数爆炸」，那里挡住「位图过大」。</para>
        /// </summary>
        /// <param name="viewportWidthCss">视口宽（CSS 像素）。</param>
        /// <param name="canvasHeightCss">整篇高（CSS 像素）。</param>
        /// <param name="segmentCount">将要点亮的屏数（见 <see cref="SegmentOffsets"/>）。</param>
        public static string? Preflight(int viewportWidthCss, int canvasHeightCss, int segmentCount)
        {
            if (viewportWidthCss <= 0 || canvasHeightCss <= 0)
                return "页面尺寸无效";

            if (canvasHeightCss > Limits.MaxLongImageHeight)
                return $"文档过长（约 {canvasHeightCss} 像素高），超出长图上限 {Limits.MaxLongImageHeight} 像素，请改用 PDF 导出";

            if (segmentCount > Limits.MaxLongImageSegments)
                return $"需要截取 {segmentCount} 屏，超出上限 {Limits.MaxLongImageSegments} 屏，请改用 PDF 导出";

            if ((long)viewportWidthCss * canvasHeightCss > Limits.MaxLongImagePixels)
                return $"长图过大（{viewportWidthCss}×{canvasHeightCss} 像素），请改用 PDF 导出";

            return null;
        }

        /// <summary>
        /// 拼接长图。
        /// </summary>
        /// <param name="frames">按滚动顺序截取的 PNG 帧。</param>
        /// <param name="offsetsCss">每帧对应的滚动偏移（CSS 像素，纵向）。</param>
        /// <param name="canvasWidthCss">视口宽度（CSS 像素）。</param>
        /// <param name="canvasHeightCss">整篇高度（CSS 像素）。</param>
        public static Result Stitch(
            IReadOnlyList<byte[]> frames,
            IReadOnlyList<int> offsetsCss,
            int canvasWidthCss,
            int canvasHeightCss)
        {
            if (frames == null || offsetsCss == null || frames.Count == 0 || frames.Count != offsetsCss.Count)
                return new Result(null, "没有可拼接的画面");
            if (canvasWidthCss <= 0 || canvasHeightCss <= 0)
                return new Result(null, "页面尺寸无效");

            // 先全部解码并**保持引用**：RenderTargetBitmap.Render 之前位图不能被回收，
            // 若在循环里用局部变量持有，GC 一跑就渲染成空白。
            var decoded = new List<(BitmapSource Bitmap, int Offset)>(frames.Count);
            for (var i = 0; i < frames.Count; i++)
            {
                var bmp = DecodePng(frames[i]);
                if (bmp != null) decoded.Add((bmp, offsetsCss[i]));
            }
            if (decoded.Count == 0) return new Result(null, "截取的画面无法解码");

            // 设备像素比 = 帧宽 / 视口 CSS 宽。取第一帧为准（同一 WebView 的各帧必然一致）。
            var frameWidth = decoded[0].Bitmap.PixelWidth;
            if (frameWidth <= 0) return new Result(null, "截取的画面尺寸无效");
            var scale = frameWidth / (double)canvasWidthCss;

            // 画布高度：既要覆盖整篇高度，也要保证最后一帧完整落在画布内（避免四舍五入截掉末行）。
            var frameHeight = decoded[0].Bitmap.PixelHeight;
            var canvasHeightPx = (int)Math.Ceiling(canvasHeightCss * scale);
            foreach (var (_, offset) in decoded)
                canvasHeightPx = Math.Max(canvasHeightPx, (int)Math.Round(offset * scale) + frameHeight);

            if (canvasHeightPx > Limits.MaxLongImageHeight)
                return new Result(null, $"文档过长（约 {canvasHeightPx} 像素高），超出长图上限 {Limits.MaxLongImageHeight} 像素");
            if ((long)frameWidth * canvasHeightPx > Limits.MaxLongImagePixels)
                return new Result(null, $"长图过大（{frameWidth}×{canvasHeightPx} 像素），请改用 PDF 导出");

            try
            {
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    foreach (var (bmp, offset) in decoded)
                    {
                        // 后一帧覆盖前一帧的重叠区：重叠处内容相同，视觉上无缝
                        dc.DrawImage(bmp, new Rect(0, Math.Round(offset * scale), bmp.PixelWidth, bmp.PixelHeight));
                    }
                }

                var rtb = new RenderTargetBitmap(frameWidth, canvasHeightPx, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(visual);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(rtb));
                using var ms = new MemoryStream();
                encoder.Save(ms);
                return new Result(ms.ToArray(), null);
            }
            catch (Exception ex)
            {
                SeeMeLog.Error("LongImageExporter.Stitch", ex);
                return new Result(null, "拼接失败：" + ex.Message);
            }
        }

        /// <summary>解码 PNG 帧；失败返回 null（单帧失败不该让整张长图导出失败）。</summary>
        private static BitmapSource? DecodePng(byte[] png)
        {
            try
            {
                using var ms = new MemoryStream(png);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                // CacheOption.OnLoad 必须：否则解码被推迟到流关闭之后，拿到的是一张空图
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();   // 冻结后跨线程/跨渲染阶段都安全
                return bmp;
            }
            catch { return null; }
        }
    }
}
