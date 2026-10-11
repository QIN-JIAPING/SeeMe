// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;

namespace SeeMe
{
    /// <summary>
    /// 文件准入判定：**唯一的"这个文件能不能打开"判据**（扩展名受支持 + 文件头与扩展名一致）。
    ///
    /// <para><b>为什么要有这个类</b>：判据原先只写在 <c>MainWindow.FilterSupportedFiles</c> 里，
    /// 而它**只被拖拽与 WebView 拖放两条路径调用** —— 主打开路径
    /// <c>OpenFileInternal</c>（文件对话框 / 最近文件 / 命令行 / 文件关联 / 单实例接力）
    /// 既不查 <see cref="FileTypes.IsSupported"/> 也不查魔数，
    /// 即<b>伪造扩展名的文件可以直接被打开</b>。这是仓库长期记录的已知缺口，
    /// 2026-10-11 下沉到这里，让所有入口共用同一条判据。</para>
    ///
    /// <para><b>为什么是 public</b>：项目无 <c>InternalsVisibleTo</c>，测试程序集只能访问 public 成员；
    /// 这条判据必须有测试覆盖（与 <see cref="MagicBytes"/> / <see cref="FileTypes"/> / <see cref="Limits"/> 一致）。</para>
    /// </summary>
    public static class FileAdmission
    {
        /// <summary>准入结论。</summary>
        public enum Verdict
        {
            /// <summary>放行。</summary>
            Accept,

            /// <summary>扩展名不在 <see cref="FileTypes.All"/> 内 —— 本程序根本不渲染这种文件。</summary>
            Unsupported,

            /// <summary>
            /// 扩展名受支持，但**无法确认**内容与之相符（文件头不符，或文件太小读不出文件头）。
            ///
            /// <para>刻意与 <see cref="Unsupported"/> 区分：这一类**不硬拒**。
            /// 用户手上可能有"扩展名写错但内容正常"的文件，硬拒会把他挡在门外；
            /// 由调用方决定是询问用户还是直接跳过（拖拽路径直接跳过，主打开路径弹确认）。</para>
            /// </summary>
            ContentMismatch,
        }

        /// <summary>
        /// 无固定文件头的纯文本格式：内容无从校验，只看扩展名即可放行。
        ///
        /// <para><b>必须与 <see cref="MagicBytes.MatchesExt"/> 的兜底分支保持一致</b> ——
        /// 那个 switch 对这些扩展名走 <c>_ =&gt; true</c>，即"纯文本跳过校验"。
        /// 两处不同步会让语义漂移，故 <c>FileAdmissionTests</c> 有一条守卫用例
        /// 断言这里每个扩展名在 <see cref="MagicBytes"/> 下确实恒为 true。</para>
        /// </summary>
        public static readonly HashSet<string> HeaderlessExts = new(StringComparer.OrdinalIgnoreCase)
        {
            ".md", ".markdown", ".mkd", ".mdown", ".csv"
        };

        /// <summary>
        /// 判定文件是否可打开。
        ///
        /// <para>读取量固定为 12 字节（<b>不读全文</b>）：WEBP 的 "WEBP" 标记在偏移 8-11，
        /// 8 字节不够判定会漏检。</para>
        /// </summary>
        public static Verdict Check(string? path)
        {
            if (string.IsNullOrEmpty(path)) return Verdict.Unsupported;

            string ext;
            try
            {
                if (!File.Exists(path)) return Verdict.Unsupported;
                ext = Path.GetExtension(path);
            }
            catch { return Verdict.Unsupported; }

            if (!FileTypes.IsSupported(ext)) return Verdict.Unsupported;

            // 纯文本格式无固定文件头，FileStream 也不必开 —— 空 .md 是合法文件
            if (HeaderlessExts.Contains(ext)) return Verdict.Accept;

            var header = new byte[12];
            int read;
            try
            {
                // FileShare.ReadWrite：文档可能正被别的程序占用，读文件头不该因此失败
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                read = fs.Read(header, 0, 12);
            }
            catch { return Verdict.Unsupported; }   // 打不开 = 当成不支持

            // 太小，读不出可判定的文件头 —— 无法确认，交给调用方决定
            // （拖拽路径会跳过，主打开路径会询问用户）
            if (read < 4) return Verdict.ContentMismatch;

            return MagicBytes.MatchesExt(ext, header) ? Verdict.Accept : Verdict.ContentMismatch;
        }

        /// <summary>是否可无条件放行（拖拽等"静默过滤"场景用：不询问用户，不符即剔除）。</summary>
        public static bool IsAcceptable(string? path) => Check(path) == Verdict.Accept;
    }
}
