// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Markdig;

namespace SeeMe
{
    /// <summary>主题管理服务契约。实例可被 MainWindow 在 Loaded 时手动创建（手写工厂）。</summary>
    public interface IThemeManager
    {
        string Current { get; }
        string Light { get; }
        string Dark { get; }
        bool WelcomeDismissed { get; }
        event Action? Changed;
        void Initialize();
        void Toggle(Window window);
        void ApplyToWindow(Window window, string theme);
        void SetWelcomeDismissed(bool dismissed);

        /// <summary>主题色切换（indigo / blue / green）。</summary>
        void ApplyAccent(string accent);

        /// <summary>动画效果开关（false 时主题切换直接赋值，不做过渡动画）。</summary>
        bool AnimationsEnabled { get; set; }
    }

    /// <summary>文档转换器服务契约。Office / PDF 转 HTML。</summary>
    public interface IFileConverter
    {
        Task<string> ExcelToHtml(string filePath);
        Task<string> PptToHtml(string filePath);
        Task<string> PdfToHtml(string filePath);
        Task<string> DocxToHtml(string filePath);
        string FormatSizeBytes(long bytes);
    }

    /// <summary>Markdown / 文档渲染服务契约。</summary>
    public interface IRenderService
    {
        MarkdownPipeline Pipeline { get; }
        IThemeManager? ThemeManager { get; set; }
        IFileConverter? Converter { get; set; }
        IHighlightStore? HighlightStore { get; set; }
        string ProcessRelativePaths(string html, string? baseDir);
        string SanitizeLinkHrefs(string html);
        string WrapTables(string html);
        string UpgradePreBlocks(string html);
        /// <summary>代码块逐行包裹 .seeme-line，为 CSS 行号与行级复制提供钩子（超限块静默降级）。</summary>
        string AddCodeLineNumbers(string html);
        string? StripYamlFrontMatter(string md, out string? frontMatterBlock);
        string BuildFrontMatterCard(string? block);
        string BuildTocCard(string md);
        List<TocItem> BuildTocItems(string renderedHtml);
        string StripBom(string s);
        bool IsDarkTheme(IThemeManager theme);
        string Render(string bodyHtml, PanelState state, string frontMatterCard, FrameworkElement resourceElement, Func<string>? logErr = null, string? customCss = null);
        string BuildOfficePage(string bodyHtml, PanelState state, FrameworkElement resourceElement);
        string BuildWelcomePage(FrameworkElement resourceElement);
    }

    /// <summary>文件历史服务契约。</summary>
    public interface IFileHistory
    {
        IReadOnlyList<string> Entries { get; }
        event Action? Changed;
        void Add(string path);
        void Remove(string path);
        void Clear();
    }

    /// <summary>书签存储契约。</summary>
    public interface IBookmarkStore
    {
        event Action? Changed;
        bool Contains(string path);
        bool Toggle(string path);
    }

    /// <summary>高亮标注存储契约：按文件路径持久化选区高亮（重启后仍保留）。</summary>
    public interface IHighlightStore
    {
        IReadOnlyList<HighlightItem> Items { get; }
        event Action? Changed;
        IReadOnlyList<HighlightItem> ForFile(string path);
        void Add(HighlightItem item);
        void Remove(string id);
        void ClearFile(string path);
    }

    /// <summary>用户笔记存储契约：用户自由输入的笔记，按文件路径持久化（与标注解耦）。</summary>
    public interface INoteStore
    {
        IReadOnlyList<NoteItem> Items { get; }
        event Action? Changed;
        IReadOnlyList<NoteItem> ForFile(string path);
        NoteItem Add(string file, string content);
        void Update(string id, string content);
        void Remove(string id);
        void ClearFile(string path);
    }
}
