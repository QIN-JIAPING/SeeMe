// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;

namespace SeeMe
{
    /// <summary>
    /// MainWindow 分部类：命令面板（CommandPaletteWindow）用的对外入口。
    ///
    /// 设计约定
    /// ────────
    /// MainWindow 的业务方法一律 private；命令面板是独立 Window，只能访问 public 成员，
    /// 因此本文件集中承载「面向命令面板的公开入口」，把这类转发壳从 MainWindow.xaml.cs
    /// 主体中外移，避免与窗口自身逻辑混杂。
    ///
    /// 新增命令面板动作时：
    ///   1. 在这里加一个 public 入口方法（命名见下）；
    ///   2. 在 CommandPaletteWindow.BuildCommands() 注册 Mk(...) 项。
    ///
    /// 命名约定：转发壳用「动作名 + Public」时表示内部同名方法为 private 且签名不同
    /// （如 ToggleThemePublic 对应 OnThemeToggle(sender, e) 事件处理器）；
    /// 若内部方法本就 public 或无参歧义，则直接沿用动作名（如 ToggleSplit、RefreshActivePanel）。
    /// </summary>
    public partial class MainWindow
    {
        // ══════════════════════════════════════════════════════════
        //  面板定位：命令面板所有"作用于当前面板"动作的统一入口
        // ══════════════════════════════════════════════════════════

        /// <summary>当前激活面板；未激活（如收起分栏后）回退左栏——命令面板统一入口。</summary>
        public PanelState? ActiveOrLeft => _activePanel ?? _app.Left;

        // ══════════════════════════════════════════════════════════
        //  文件
        // ══════════════════════════════════════════════════════════

        /// <summary>在当前面板打开文件选择对话框。</summary>
        public void ShowOpenForForActive()
        {
            if (_activePanel != null) ShowOpenFor(_activePanel);
            else if (_app.Left != null) ShowOpenFor(_app.Left);
        }

        /// <summary>用系统默认程序打开当前文件。</summary>
        public void OpenActiveWithDefaultAppPublic() => OpenActiveWithDefaultApp();

        /// <summary>把指定文件投递到当前激活面板。</summary>
        public void OpenFileIntoActive(string path)
        {
            var t = ActiveOrLeft;
            if (t != null) OpenFileInto(t, path);
        }

        /// <summary>把文件投递到指定面板（命令面板「打开到右栏」等用）。</summary>
        public void OpenFileInto(PanelState t, string path)
        {
            if (t != null && !string.IsNullOrEmpty(path) && File.Exists(path))
                OpenFileInternal(t, path);
        }

        /// <summary>强制重新加载当前文档（绕过缓存与 mtime 判定）。</summary>
        public void RefreshActivePanel()
        {
            var t = ActiveOrLeft;
            if (t != null && !string.IsNullOrEmpty(t.CurrentFile))
                _ = ReloadFileAsync(t, true, t.ResetCts());
        }

        /// <summary>最近文件条目（供命令面板「最近」分组，最多取用 10 条）。</summary>
        public IReadOnlyList<string> GetHistoryEntries()
            => _app?.History.Entries ?? Array.Empty<string>();

        // ══════════════════════════════════════════════════════════
        //  视图
        // ══════════════════════════════════════════════════════════

        /// <summary>切换明暗主题（转发到主题按钮的事件处理器）。</summary>
        public void ToggleThemePublic() => OnThemeToggle(this, new RoutedEventArgs());

        /// <summary>切换双栏 / 取消分栏。</summary>
        public void ToggleSplit() => SetSplitMode(!_app.IsSplitMode);

        /// <summary>切换全屏演示 / 专注阅读模式。</summary>
        public void TogglePresentationPublic() => TogglePresentation();

        /// <summary>切换信息面板显隐。</summary>
        public void ToggleInfoPanel() => OnToggleInfoPanel(this, new RoutedEventArgs());

        /// <summary>显示笔记面板。</summary>
        public void ToggleNotesPanel() => SetNotesPanelVisibility(true);

        /// <summary>切换命令面板显隐（WebView 内 Ctrl+Shift+P 经 palette-toggle 消息桥接到此）。</summary>
        public void ToggleCommandPalette() => TogglePaletteWindow();

        /// <summary>切换侧边栏 Tab（0=大纲 1=标注 2=全部标注 …）。</summary>
        public void ActivateSideTabPublic(int idx) => ActivateSideTab(idx);

        /// <summary>切换浮动大纲显隐（文档上方可拖动的浮层，随滚动高亮跟随）。</summary>
        public void ToggleOutlineFloatPublic() => SetOutlineFloatVisible(!_outlineFloatOn);

        // ══════════════════════════════════════════════════════════
        //  查找与缩放
        // ══════════════════════════════════════════════════════════

        /// <summary>在指定面板打开页内查找。</summary>
        public void ShowFindFor(PanelState t)
        {
            if (t != null) ShowFind(t);
        }

        /// <summary>把当前面板缩放到指定倍数。</summary>
        public void SetZoomActive(double scale)
        {
            var t = ActiveOrLeft;
            if (t != null) SetZoom(t, scale);
        }

        /// <summary>在当前面板缩放基础上增减（钳制 0.5–2.5，与宿主快捷键同一区间）。</summary>
        public void SetZoomActiveDelta(double delta)
        {
            var t = ActiveOrLeft;
            if (t != null) SetZoom(t, Math.Clamp(t.FontScale + delta, 0.5, 2.5));
        }

        // ══════════════════════════════════════════════════════════
        //  标注与编辑
        // ══════════════════════════════════════════════════════════

        /// <summary>切换当前面板的高亮笔开关。</summary>
        public void ToggleHighlightPen()
        {
            var t = ActiveOrLeft;
            if (t != null) ToggleAnnMode(t);
        }

        /// <summary>切换编辑 / 预览模式（仅当当前文件可编辑）。</summary>
        public void ToggleEditModePublic()
        {
            var t = ActiveOrLeft;
            if (t != null && CanEditFile(t.CurrentFile)) ToggleEditMode(t);
        }

        // ══════════════════════════════════════════════════════════
        //  导出与打印
        // ══════════════════════════════════════════════════════════

        /// <summary>打印当前文档。</summary>
        public void PrintActivePanelPublic() => PrintActivePanel();

        /// <summary>把当前预览导出为 PNG 图片（转发到导出按钮的事件处理器）。</summary>
        public void ExportActiveToImage() => OnExportImage(this, new RoutedEventArgs());

        /// <summary>打开导出对话框（Markdown / Word 等）。</summary>
        public void ShowExportDialogForActive()
        {
            var t = ActiveOrLeft;
            if (t != null) ShowExportDialog(t);
        }

        // ══════════════════════════════════════════════════════════
        //  诊断
        // ══════════════════════════════════════════════════════════

        /// <summary>命令面板捕获异常时的日志出口。</summary>
        public void LogErrPublic(string msg) => LogErr(msg);
    }
}
