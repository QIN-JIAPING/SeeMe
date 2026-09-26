// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

using System;
using System.Text;

namespace SeeMe
{
    /// <summary>
    /// 图表导出载荷的纯函数编解码 —— 从 <see cref="MainWindow"/> 分离出来以便单元测试。
    ///
    /// <para><b>为什么单独成类</b>：这段逻辑是「页面回传字符串 → 磁盘字节 + 扩展名 + 保存过滤器」的
    /// 纯转换，没有任何 WPF/WebView 依赖，却原本埋在 MainWindow 分部类里（private static，
    /// 测试既够不到也无法在不构造窗口的前提下调用）。项目无 <c>InternalsVisibleTo</c>，
    /// 放 public 独立类是唯一能让它被测试覆盖的做法。</para>
    ///
    /// <para><b>安全相关</b>：这里决定「什么样的回传会被当成有效图片」，是导出链路的输入闸门。
    /// 必须严格校验前缀（只认 <c>data:image/png;base64,</c>），不能把任意 data: URL
    /// （例如 <c>data:text/html,...</c>）当图片写盘 —— 那等于让页面决定落盘文件的类型。</para>
    /// </summary>
    public static class ChartExportCodec
    {
        /// <summary>PNG 的 data URL 前缀（不含 base64 体）。</summary>
        public const string PngDataUrlPrefix = "data:image/png;base64,";

        /// <summary>
        /// 把页面回传的 payload 还原成字节。
        /// <paramref name="fmt"/> 为 <c>svg</c> 时按纯文本处理，否则要求 PNG data URL。
        /// 返回 (字节, 扩展名, 保存过滤器)；无法识别返回 (null, "", "")。
        /// </summary>
        public static (byte[]? Bytes, string Ext, string Filter) Decode(string? data, string? fmt)
        {
            if (string.IsNullOrEmpty(data)) return (null, "", "");

            if (string.Equals(fmt, "svg", StringComparison.OrdinalIgnoreCase))
            {
                // SVG 走文本而非 base64：落盘后用户可直接用编辑器打开修改。
                // 编码用无 BOM 的 UTF-8 —— SVG 规范推荐无 BOM，带 BOM 会让部分解析器
                // 把 BOM 当作内容前导字符而渲染异常。
                return (new UTF8Encoding(false).GetBytes(data), ".svg", "SVG 矢量图 (*.svg)|*.svg");
            }

            // 只认 PNG 前缀：不能放宽成「任意 data:」——那会让页面能决定落盘文件的类型
            if (!data.StartsWith(PngDataUrlPrefix, StringComparison.OrdinalIgnoreCase))
                return (null, "", "");
            try
            {
                var b64 = data.Substring(PngDataUrlPrefix.Length);
                // Convert.FromBase64String 会在长度非 4 倍数或含非法字符时抛 FormatException
                var bytes = Convert.FromBase64String(b64);
                if (bytes.Length == 0) return (null, "", "");
                return (bytes, ".png", "PNG 图片 (*.png)|*.png");
            }
            catch (FormatException) { return (null, "", ""); }
        }
    }
}
