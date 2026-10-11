// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Linq;
using System.Text;

namespace SeeMe
{
    /// <summary>
    /// 编辑页粘贴图片时「页面回传的 data URL → 磁盘字节 + 扩展名」的纯函数解码。
    ///
    /// <para><b>为什么单独成类</b>：与 <see cref="ChartExportCodec"/> 同样的理由 ——
    /// 这是链路的输入闸门（"什么样的回传会被当成图片写盘"），必须可被单元测试直接覆盖。
    /// 项目无 <c>InternalsVisibleTo</c>，放 <c>public</c> 独立类是唯一做法。</para>
    ///
    /// <para><b>🔴 安全口径必须与 <see cref="FileTypes.Image"/> 一致：不接受 SVG。</b>
    /// 粘贴是最容易被忽视的注入入口 —— 从浏览器复制一张图，剪贴板里可能是
    /// <c>data:image/svg+xml;base64,...</c>，而 SVG 可内嵌 <c>&lt;script&gt;</c>。
    /// 一旦落盘并被 Markdown 以 <c>![](assets/x.svg)</c> 引用，就等于把可执行内容
    /// 引进了渲染页。本项目已为 <c>.svg</c> 单独立过决策守卫，这里同样是白名单：
    /// <b>只认下面这张表里的 MIME，其余一律拒绝</b>，不做"猜扩展名"的宽容处理。</para>
    /// </summary>
    public static class PastedImageCodec
    {
        /// <summary>允许落盘的 MIME → 扩展名白名单（小写比较）。</summary>
        private static readonly (string Mime, string Ext)[] Allowed =
        {
            ("image/png", ".png"),
            ("image/jpeg", ".jpg"),
            ("image/jpg", ".jpg"),
            ("image/gif", ".gif"),
            ("image/webp", ".webp"),
            ("image/bmp", ".bmp"),
        };

        private const string DataPrefix = "data:";
        private const string Base64Suffix = ";base64";

        /// <summary>
        /// 解析 data URL。返回（字节, 扩展名）；无法识别或超限返回 (null, "")。
        /// </summary>
        /// <param name="dataUrl">形如 <c>data:image/png;base64,iVBORw0...</c>。</param>
        /// <param name="maxBytes">解码后允许的最大字节数。</param>
        public static (byte[]? Bytes, string Ext) Decode(string? dataUrl, long maxBytes)
        {
            if (string.IsNullOrEmpty(dataUrl)) return (null, "");

            // 头部与数据以第一个逗号分隔；base64 字母表不含逗号，故这个切分是安全的。
            var comma = dataUrl!.IndexOf(',');
            if (comma <= 0) return (null, "");

            var header = dataUrl.Substring(0, comma);
            var body = dataUrl.Substring(comma + 1);

            if (!header.StartsWith(DataPrefix, StringComparison.OrdinalIgnoreCase)) return (null, "");
            if (!header.EndsWith(Base64Suffix, StringComparison.OrdinalIgnoreCase)) return (null, "");

            var mime = header.Substring(DataPrefix.Length, header.Length - DataPrefix.Length - Base64Suffix.Length);
            var ext = Allowed
                .Where(a => string.Equals(a.Mime, mime, StringComparison.OrdinalIgnoreCase))
                .Select(a => a.Ext)
                .FirstOrDefault();
            if (string.IsNullOrEmpty(ext)) return (null, "");

            try
            {
                // FromBase64String 对长度非 4 倍数 / 含非法字符抛 FormatException
                var bytes = Convert.FromBase64String(body);
                if (bytes.Length == 0 || bytes.Length > maxBytes) return (null, "");
                return (bytes, ext);
            }
            catch (FormatException) { return (null, ""); }
        }

        /// <summary>
        /// 内容与声明类型是否一致（复用 <see cref="MagicBytes.MatchesExt"/>）。
        ///
        /// <para><b>为什么 MIME 白名单还不够</b>：data URL 里的 <c>image/png</c> 只是**页面自称**的类型，
        /// 载荷本身可以是任意字节。不校验就等于让页面决定落盘文件的真实内容 ——
        /// 这正是仓库既有 <see cref="MagicBytes"/>（"防扩展名伪造"）要挡的事；
        /// 粘贴图片是新增的落盘入口，必须用同一把尺子。</para>
        /// </summary>
        public static bool ContentMatchesExt(string ext, byte[] bytes)
            => bytes != null && bytes.Length > 0 && MagicBytes.MatchesExt(ext, bytes);

        /// <summary>
        /// 生成落盘文件名：以来源名（剪贴板常给 <c>image.png</c>）为前缀 + 时间戳去重。
        ///
        /// <para><b>🔴 名字来自不可信来源，必须净化</b>：它经 postMessage 从渲染页回传，
        /// 可能含路径分隔符、<c>..</c>、Windows 保留设备名或控制字符。
        /// 只保留字母/数字/连字符/下划线与非 ASCII（中文文件名要能保留），其余一律丢弃 ——
        /// 否则粘贴一次就可能把文件写到 <c>assets\</c> 之外。</para>
        ///
        /// <para><b>为什么放在这里而不是 MainWindow</b>：它是纯函数，且属于本类的输入闸门。
        /// 项目无 <c>InternalsVisibleTo</c>，放在 MainWindow 里当私有方法就**无法被测试覆盖**
        /// （2026-10-11 安全审查时正因此只能拿反射去够它，而本机只有 .NET Framework 宿主、
        /// 反射不了 net8 程序集）。与 <see cref="ChartExportCodec"/> 从 MainWindow 抽出的理由相同。</para>
        ///
        /// <para><b>时间戳不是装饰</b>：Windows 保留设备名（<c>CON</c>/<c>NUL</c>/<c>COM1</c>…）
        /// 只在"名字主体恰好等于设备名"时生效；追加 <c>-&lt;时间戳&gt;</c> 后，
        /// 即使来源名就是 <c>CON.png</c>，产物 <c>CON-20261011-210000.png</c> 也不再命中设备名。</para>
        /// </summary>
        public static string BuildFileName(string? rawName, string ext)
        {
            var stem = string.IsNullOrWhiteSpace(rawName) ? "pasted" : rawName!;
            try { stem = Path.GetFileNameWithoutExtension(stem); } catch { stem = "pasted"; }

            var sb = new StringBuilder(stem.Length);
            foreach (var ch in stem)
            {
                if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch > 127) sb.Append(ch);
            }
            var cleaned = sb.ToString().Trim();
            if (cleaned.Length == 0) cleaned = "pasted";
            if (cleaned.Length > 40) cleaned = cleaned.Substring(0, 40);

            return cleaned + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ext;
        }
    }
}
