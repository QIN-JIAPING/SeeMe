// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

namespace SeeMe
{
    /// <summary>
    /// 防御性资源上限（DoS 防护）集中定义，供运行时与测试统一引用，避免 magic number 散落漂移。
    /// 边界测试直接以本类常量 ±1 构造用例，杜绝 off-by-one 与常量不同步。
    /// </summary>
    public static class Limits
    {
        /// <summary>内联图片大小上限（超限不内联，防 OOM）。</summary>
        public const long MaxInlineImageBytes = 5 * 1024 * 1024;

        /// <summary>内容搜索 / 自定义 CSS 单文件大小上限（超限跳过）。</summary>
        public const long MaxSearchBytes = 256 * 1024;

        /// <summary>内联编辑文件大小上限（超限建议用外部编辑器）。</summary>
        public const long MaxEditBytes = 5 * 1024 * 1024;

        /// <summary>Excel 单表最大渲染行数。</summary>
        public const int MaxExcelRows = 5000;

        /// <summary>Excel 最大渲染工作表数。</summary>
        public const int MaxExcelSheets = 50;

        /// <summary>PPT 最大渲染幻灯片数。</summary>
        public const int MaxPptSlides = 200;

        /// <summary>
        /// 独立图片查看的单边像素上限（宽或高任一超限即拒绝）。
        ///
        /// <para><b>为什么必须设这道闸</b>：图片解码后的内存 ≈ 宽 × 高 × 4 字节（RGBA）。
        /// WebView2 虽然会做流式解码，但一张 20000×20000 的 PNG 解码后仍是约 1.6 GB，
        /// 足以让渲染进程直接 OOM 崩溃 —— 那是用户无法理解的"打开一张图就崩了"。</para>
        ///
        /// <para>上限取 8000：8000×8000×4 ≈ 256 MB，是单张图能被接受的量级。
        /// 注意 PNG/JPEG/GIF/BMP/WEBP 的文件头都带尺寸，**读文件头即可判定，无需完整解码**。</para>
        /// </summary>
        public const int MaxImagePixels = 8000;

        /// <summary>独立图片查看的单文件大小上限（超限拒绝，避免从磁盘读入超大文件本身就把内存顶穿）。</summary>
        public const long MaxImageFileBytes = 64L * 1024 * 1024;

        /// <summary>
        /// 代码块注入行号的行数上限（超限该块不注入行号，仍正常显示与复制）。
        ///
        /// <para><b>为什么必须有这道闸</b>：行号实现是按行切分后逐行包 <c>&lt;span class="seeme-ln"&gt;</c>，
        /// 一个 10 万行的代码块会产生 10 万个额外 DOM 节点，页面加载与滚动会明显卡顿甚至卡死。
        /// 行号是阅读辅助而非必需功能 —— 超限时**静默降级**（不注入行号）比拒绝渲染合理。</para>
        ///
        /// <para>上限取 3000：3000 行代码块的节点增量尚在 Chromium 可流畅处理的量级，
        /// 且正常文档里极少有超过 3000 行的单个代码块（超大代码应作为独立文件打开）。</para>
        /// </summary>
        public const int MaxCodeLinesWithNumbers = 3000;

        /// <summary>
        /// 页面「复制代码」单次可写入剪贴板的字符数上限（超限拒绝并提示）。
        ///
        /// <para>页面侧回传的是 <c>textContent</c>，一个误点「复制整块」的大型代码块
        /// 可能带上百万字符：既会长时间占用剪贴板，也会让宿主侧多留一份字符串副本。
        /// 阅读器场景下没人真需要一次复制 1MB 代码，直接拒绝比默默卡一下更好。</para>
        /// </summary>
        public const int MaxClipboardChars = 1024 * 1024;

        /// <summary>
        /// 图表导出单次回传的字符数上限（PNG 的 data URL 或 SVG 文本）。
        ///
        /// <para>data URL 是 base64，比原始字节膨胀约 1.33 倍；一个 2x 像素比的复杂 ECharts
        /// 图大约几 MB。上限取 20MB 字符：既容得下高分辨率导出，又能在页面渲染异常
        /// （例如返回了一个巨大空白画布）时及时拒绝，而不是把内存写爆。</para>
        /// </summary>
        public const int MaxChartExportChars = 20 * 1024 * 1024;

        /// <summary>
        /// Office 容器（docx/xlsx/pptx = zip）解压后总大小上限（防 zip 炸弹 OOM）。
        /// 按中央目录声明的 entry.Length 累计预检，超限拒绝解析。
        /// </summary>
        public const long MaxDecompressedBytes = 256L * 1024 * 1024;
    }
}
