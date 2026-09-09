// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Linq;

namespace SeeMe
{
    /// <summary>
    /// 导出格式单一来源：ExportDialog 格式网格、保存文件过滤器、pandoc 扩展名推断与白名单共用这一张表。
    /// 新增导出格式只改这里。
    /// </summary>
    public static class SupportedExportFormats
    {
        /// <summary>一个导出格式：pandoc 格式名 / 默认扩展名 / 展示名 / 保存过滤器 / 是否在对话框网格展示。</summary>
        public sealed record Entry(string Format, string Ext, string Label, string Filter, bool ShowInDialog = true);

        /// <summary>全部支持的导出格式（含 rst：后端可用，但不在对话框网格展示）。</summary>
        public static readonly Entry[] All =
        {
            new("docx",     ".docx", "Word 文档",        "Word 文档 (*.docx)|*.docx"),
            new("pdf",      ".pdf",  "PDF 文档",         "PDF 文档 (*.pdf)|*.pdf"),
            new("latex",    ".tex",  "LaTeX",            "LaTeX 源文件 (*.tex)|*.tex"),
            new("html",     ".html", "HTML 网页",        "HTML 网页 (*.html)|*.html"),
            new("epub",     ".epub", "电子书",           "电子书 (*.epub)|*.epub"),
            new("markdown", ".md",   "Markdown",         "Markdown 文件 (*.md)|*.md"),
            new("rst",      ".rst",  "reStructuredText", "reStructuredText (*.rst)|*.rst", ShowInDialog: false),
        };

        /// <summary>对话框网格展示的格式（顺序即展示顺序）。</summary>
        public static IReadOnlyList<Entry> DialogFormats => All.Where(e => e.ShowInDialog).ToList();

        /// <summary>
        /// 格式名 → 规范条目（兼容别名 tex/md/markdown/html 等，大小写不敏感）；未知返回 null。
        /// </summary>
        public static Entry? FromFormat(string? format) => (format ?? "").ToLowerInvariant() switch
        {
            "docx" => All[0],
            "pdf" => All[1],
            "latex" or "tex" => All[2],
            "html" or "htm" => All[3],
            "epub" => All[4],
            "markdown" or "md" => All[5],
            "rst" => All[6],
            _ => null,
        };

        /// <summary>扩展名（带或不带点）→ 规范条目；未知返回 null。</summary>
        public static Entry? FromExt(string? ext)
        {
            var e = (ext ?? "").TrimStart('.').ToLowerInvariant();
            return All.FirstOrDefault(x => x.Ext.TrimStart('.') == e);
        }

        /// <summary>白名单校验（防 format 注入额外 pandoc 参数）。</summary>
        public static bool IsAllowed(string? format) => FromFormat(format) != null;
    }
}
