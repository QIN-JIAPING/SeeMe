// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;

namespace SeeMe
{
    /// <summary>受支持的文件类型统一定义，消除散落各处的扩展名判断。</summary>
    public static class FileTypes
    {
        /// <summary>Markdown 扩展名（小写，含点）。</summary>
        public static readonly HashSet<string> Markdown = new(StringComparer.OrdinalIgnoreCase)
        {
            ".md", ".markdown", ".mkd", ".mdown"
        };

        /// <summary>Office 文档扩展名（小写，含点）。含 anydoc-wasm 覆盖的 12 种格式及宏文档变体 docm/xlsm。</summary>
        public static readonly HashSet<string> Office = new(StringComparer.OrdinalIgnoreCase)
        {
            ".docx", ".doc", ".xlsx", ".xls", ".pptx", ".ppt",
            ".rtf", ".odt", ".ods", ".odp", ".epub", ".csv",
            ".docm", ".xlsm"
        };

        /// <summary>PDF 扩展名。</summary>
        public const string Pdf = ".pdf";

        /// <summary>
        /// 图片扩展名（独立查看，非文档内嵌图）。
        ///
        /// <para><b>刻意不含 <c>.svg</c></b>：SVG 是 XML 文档，可内嵌 <c>&lt;script&gt;</c>
        /// 且能通过 <c>&lt;use href&gt;</c> 引外部资源，XSS 面与 SeeMe 已花大力气封堵的
        /// HTML 注入是同一类。为本格式打开豁免不值得 —— 有待明确需求再单独评估。</para>
        ///
        /// <para>也不含 <c>.svgz</c>（gzip 压缩的 SVG，同样风险且需解压）。</para>
        /// </summary>
        public static readonly HashSet<string> Image = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico", ".tif", ".tiff"
        };

        /// <summary>所有支持的扩展名（含 PDF、Office 与图片）。</summary>
        public static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase)
        {
            ".md", ".markdown", ".mkd", ".mdown",
            ".pdf", ".docx", ".doc", ".docm", ".xlsx", ".xls", ".xlsm", ".pptx", ".ppt",
            ".rtf", ".odt", ".ods", ".odp", ".epub", ".csv",
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico", ".tif", ".tiff"
        };

        /// <summary>是否为受支持的文件类型。</summary>
        public static bool IsSupported(string? ext) =>
            !string.IsNullOrEmpty(ext) && All.Contains(ext!);

        /// <summary>是否为 Markdown 文件。</summary>
        public static bool IsMarkdown(string? ext) =>
            !string.IsNullOrEmpty(ext) && Markdown.Contains(ext!);

        /// <summary>是否为 Office 文档。</summary>
        public static bool IsOffice(string? ext) =>
            !string.IsNullOrEmpty(ext) && Office.Contains(ext!);

        /// <summary>是否为 PDF。</summary>
        public static bool IsPdf(string? ext) =>
        !string.IsNullOrEmpty(ext) && ext!.Equals(Pdf, StringComparison.OrdinalIgnoreCase);

        /// <summary>是否为图片文件。</summary>
        public static bool IsImage(string? ext) =>
            !string.IsNullOrEmpty(ext) && Image.Contains(ext!);

        /// <summary>打开文件对话框的统一过滤器。</summary>
        public const string OpenFilter =
            "支持的文件(*.md;*.markdown;*.mkd;*.mdown;*.pdf;*.docx;*.doc;*.docm;*.xlsx;*.xls;*.xlsm;*.pptx;*.ppt;*.rtf;*.odt;*.ods;*.odp;*.epub;*.csv;*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp;*.ico;*.tif;*.tiff)|*.md;*.markdown;*.mkd;*.mdown;*.pdf;*.docx;*.doc;*.docm;*.xlsx;*.xls;*.xlsm;*.pptx;*.ppt;*.rtf;*.odt;*.ods;*.odp;*.epub;*.csv;*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp;*.ico;*.tif;*.tiff|" +
            "Markdown 文件 (*.md;*.markdown;*.mkd;*.mdown)|*.md;*.markdown;*.mkd;*.mdown|" +
            "PDF 文件 (*.pdf)|*.pdf|" +
            "Word 文件 (*.docx;*.doc;*.docm;*.rtf;*.odt)|*.docx;*.doc;*.docm;*.rtf;*.odt|" +
            "Excel 文件 (*.xlsx;*.xls;*.xlsm;*.ods;*.csv)|*.xlsx;*.xls;*.xlsm;*.ods;*.csv|" +
            "PowerPoint 文件 (*.pptx;*.ppt;*.odp)|*.pptx;*.ppt;*.odp|" +
            "电子书 (*.epub)|*.epub|" +
            "图片 (*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp;*.ico;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp;*.ico;*.tif;*.tiff|" +
            "所有文件(*.*)|*.*";
    }
}
