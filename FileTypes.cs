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

        /// <summary>Office 文档扩展名（小写，含点）。</summary>
        public static readonly HashSet<string> Office = new(StringComparer.OrdinalIgnoreCase)
        {
            ".docx", ".xlsx", ".xls", ".pptx"
        };

        /// <summary>PDF 扩展名。</summary>
        public const string Pdf = ".pdf";

        /// <summary>所有支持的扩展名（含 PDF 与 Office）。</summary>
        public static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase)
        {
            ".md", ".markdown", ".mkd", ".mdown",
            ".pdf", ".docx", ".xlsx", ".xls", ".pptx"
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

        /// <summary>打开文件对话框的统一过滤器。</summary>
        public const string OpenFilter =
            "支持的文件(*.md;*.markdown;*.mkd;*.mdown;*.pdf;*.xlsx;*.xls;*.pptx;*.docx)|*.md;*.markdown;*.mkd;*.mdown;*.pdf;*.xlsx;*.xls;*.pptx;*.docx|" +
            "Markdown 文件 (*.md;*.markdown;*.mkd;*.mdown)|*.md;*.markdown;*.mkd;*.mdown|" +
            "PDF 文件 (*.pdf)|*.pdf|" +
            "Excel 文件 (*.xlsx;*.xls)|*.xlsx;*.xls|" +
            "PowerPoint 文件 (*.pptx)|*.pptx|" +
            "Word 文件 (*.docx)|*.docx|" +
            "所有文件(*.*)|*.*";
    }
}
