// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SeeMe
{
    /// <summary>
    /// 独立命令面板窗口（不挂在主窗内，避免遮住当前阅读区）。
    /// 由 MainWindow 通过 Show()/Hide() 控制开合，Topmost 永远置顶，可拖动。
    /// </summary>
    public partial class CommandPaletteWindow : Window
    {
        private readonly MainWindow _owner;
        private readonly List<PaletteItem> _all = new();

        private sealed class PaletteItem
        {
            public string Group { get; set; } = "";
            public string Title { get; set; } = "";
            public string Hint { get; set; } = "";
            public Action? Action { get; set; }
        }

        public CommandPaletteWindow(MainWindow owner)
        {
            _owner = owner;
            InitializeComponent();
            // 独立窗口必须自己合入主题字典——DynamicResource 找不到资源会抛 XamlParseException
            owner.ApplyCurrentThemeToWindow(this);
            // 设 Owner 让 z-order 稳定（避免独立 Window 与主窗兄弟比较导致被左栏 Tab 区视觉遮挡）
            try { Owner = owner; } catch { /* 跨线程等极端情况下 Owner 设置可能抛——吞掉回退到无 Owner */ }
            // 独立窗口样式：真透明（圆角外的 12px 边距圈透空，CardShadow 落在透明区）+ 置顶、不在任务栏占位
            AllowsTransparency = true;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            // 拖动
            DragBar.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ChangedButton == MouseButton.Left) DragMove();
            };
            // × 关闭
            CloseBtn.Click += (_, _) => Hide();
            // 主窗 Deactivate / 状态变化时强制把自己顶回最前——Topmost 在 WebView2 子窗口之上仍可能被抢
            owner.Deactivated += (_, _) =>
            {
                if (IsVisible)
                {
                    Topmost = true;
                    Activate();
                }
            };
        }

        public void Toggle()
        {
            if (IsVisible) Hide();
            else ShowPalette();
        }

        private void ShowPalette()
        {
            // 居中靠上于 Owner 主窗（不是屏幕 WorkArea——沙箱 hook 下 SystemParameters.WorkArea 可能错位）
            Width = 720; Height = 540;
            var owner = Owner ?? _owner;
            if (owner != null && owner.WindowState != WindowState.Minimized && owner.ActualWidth > 0)
            {
                // 以 Owner 客户区中心为锚点（屏幕坐标）
                var ownerLeft = owner.Left + (owner.ActualWidth - Width) / 2;
                var ownerTop = owner.Top + Math.Max(60, (owner.ActualHeight - Height) / 3);
                // 兜底：算出屏幕外则回退到 WorkArea 居中
                var workArea = SystemParameters.WorkArea;
                if (ownerLeft < workArea.Left || ownerLeft + Width > workArea.Right)
                    ownerLeft = workArea.Left + (workArea.Width - Width) / 2;
                if (ownerTop < workArea.Top || ownerTop + Height > workArea.Bottom)
                    ownerTop = workArea.Top + 60;
                Left = ownerLeft;
                Top = ownerTop;
            }
            else
            {
                var workArea = SystemParameters.WorkArea;
                Left = workArea.Left + (workArea.Width - Width) / 2;
                Top = workArea.Top + 60;
            }
            BuildCommands();
            ApplyFilter();
            Show();
            // 设了 Owner，独立 Window 与主窗共享 owner 链 → z-order 稳定；
            // 但仍需每次 Show 后强制 Topmost + Activate，避免被主窗子控件（WebView2 等）覆盖
            Topmost = true;
            Activate();
            SearchBox.Focus();
            SearchBox.Text = "";
            SearchBox.Focus();
        }

        private void BuildCommands()
        {
            _all.Clear();
            var o = _owner;
            var p = o.ActiveOrLeft;
            // ── 文件 ──
            _all.Add(Mk("文件", "打开文件…", "Ctrl+O", () => o.ShowOpenForForActive()));
            _all.Add(Mk("文件", "新建窗口", "Ctrl+N", () => { if (Application.Current is App app) app.CreateWindow(); }));
            _all.Add(Mk("文件", "在默认程序中打开当前文件", "Enter", () => o.OpenActiveWithDefaultAppPublic()));
            _all.Add(Mk("文件", "刷新当前文档", "F5", () => o.RefreshActivePanel()));
            // ── 视图 ──
            _all.Add(Mk("视图", "切换明暗主题", "Ctrl+Shift+D", () => o.ToggleThemePublic()));
            _all.Add(Mk("视图", "双栏 / 取消分栏", "Ctrl+T", () => o.ToggleSplit()));
            _all.Add(Mk("视图", "全屏演示 / 专注阅读", "F11", () => o.TogglePresentationPublic()));
            _all.Add(Mk("视图", "页内查找", "Ctrl+F", () => { var t = o.ActiveOrLeft; if (t != null) o.ShowFindFor(t); }));
            _all.Add(Mk("视图", "缩放至 100%", "Ctrl+0", () => o.SetZoomActive(1.0)));
            _all.Add(Mk("视图", "放大", "Ctrl+=", () => o.SetZoomActiveDelta(0.1)));
            _all.Add(Mk("视图", "缩小", "Ctrl+-", () => o.SetZoomActiveDelta(-0.1)));
            _all.Add(Mk("视图", "显示 / 隐藏信息面板", "", () => o.ToggleInfoPanel()));
            _all.Add(Mk("视图", "显示 / 隐藏笔记面板", "", () => o.ToggleNotesPanel()));
            _all.Add(Mk("视图", "浮动大纲（随滚动跟随）", "", () => o.ToggleOutlineFloatPublic()));
            // ── 标注 / 编辑 ──
            _all.Add(Mk("标注", "高亮笔开关（当前面板）", "", () => o.ToggleHighlightPen()));
            _all.Add(Mk("标注", "查看全部标注（左侧 Tab）", "", () => o.ActivateSideTabPublic(2)));
            _all.Add(Mk("编辑", "切换编辑 / 预览模式", "Ctrl+S", () => o.ToggleEditModePublic()));
            // ── 文档 ──
            _all.Add(Mk("文档", "打印当前文档", "Ctrl+P", () => o.PrintActivePanelPublic()));
            _all.Add(Mk("文档", "导出当前预览为图片 PNG", "", () => o.ExportActiveToImage()));
            if (p?.CurrentFile != null)
                _all.Add(Mk("文档", "导出为 Markdown / Word…", "", () => o.ShowExportDialogForActive()));
            // ── 最近文件（最多 10）──
            try
            {
                foreach (var path in o.GetHistoryEntries().Take(10))
                {
                    if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
                    var q = path;
                    _all.Add(Mk("最近", Path.GetFileName(path), Path.GetDirectoryName(path) ?? "", () => o.OpenFileIntoActive(q)));
                }
            }
            catch { }
        }

        private PaletteItem Mk(string group, string title, string hint, Action action)
            => new PaletteItem { Group = group, Title = title, Hint = hint, Action = action };

        private void ApplyFilter()
        {
            var q = (SearchBox.Text ?? "").Trim();
            IEnumerable<PaletteItem> items = _all;
            if (q.Length > 0)
            {
                var f = q.ToLowerInvariant();
                items = items.Where(i =>
                        i.Title.ToLowerInvariant().Contains(f)
                        || i.Hint.ToLowerInvariant().Contains(f)
                        || i.Group.Contains(f, StringComparison.OrdinalIgnoreCase));
            }
            var list = items.ToList();
            CmdList.ItemsSource = null;
            CmdList.ItemsSource = list;
            EmptyHint.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (list.Count > 0) CmdList.SelectedIndex = 0;
        }

        private void OnFilter(object sender, TextChangedEventArgs e)
        {
            Hint.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
            ApplyFilter();
        }

        private void OnBoxKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) Hide();
            else if (e.Key == Key.Down && CmdList.Items.Count > 0)
            {
                CmdList.SelectedIndex = Math.Min(CmdList.Items.Count - 1, CmdList.SelectedIndex + 1);
                CmdList.ScrollIntoView(CmdList.SelectedItem);
            }
            else if (e.Key == Key.Up && CmdList.Items.Count > 0)
            {
                CmdList.SelectedIndex = Math.Max(0, CmdList.SelectedIndex - 1);
                CmdList.ScrollIntoView(CmdList.SelectedItem);
            }
            else if (e.Key == Key.Enter)
            {
                ExecSelected();
            }
        }

        private void OnListKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) Hide();
            else if (e.Key == Key.Enter) ExecSelected();
        }

        private void OnListDblClick(object sender, MouseButtonEventArgs e)
        {
            ExecSelected();
        }

        private void ExecSelected()
        {
            if (CmdList.SelectedItem is PaletteItem it && it.Action != null)
            {
                Hide();
                try { it.Action(); }
                catch (Exception ex) { _owner.LogErrPublic("Palette exec: " + ex.Message); }
            }
        }
    }
}