// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace SeeMe
{
    public partial class MainWindow
    {
        private void OnPanelFocusL(object sender, MouseButtonEventArgs e)
        {
            if (_app.Left == null) return;
            _activePanel = _app.Left;
            UpdateActivePanelVisual();
            StatusText.Text = "已选中左栏";
        }


        private void OnPanelFocusR(object sender, MouseButtonEventArgs e)
        {
            if (_app.Right == null) return;
            _activePanel = _app.Right;
            UpdateActivePanelVisual();
            StatusText.Text = "已选中右栏";
        }


        private void UpdateActivePanelVisual()
        {
            if (_app.Left == null) return;
            // 用 SetResourceReference（等价 XAML DynamicResource）设置标题栏背景：
            // 动态解析资源键，ThemeManager 无论原地动画 brush 实例还是用新实例替换字典条目，
            // 标题栏都会自动跟随，不存在"本地值指向陈旧 brush 实例"的失效路径。
            foreach (var state in new[] { _app.Left, _app.Right })
            {
                if (state == null) continue;
                var key = (state == _activePanel && _app.IsSplitMode)
                    ? "TitleBarActiveBackgroundBrush"
                    : "TitleBarBackgroundBrush";
                state.TitleBar.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, key);
            }
        }


        private void OnToggleSplit(object sender, RoutedEventArgs e)
        {
            SetSplitMode(!_app.IsSplitMode);
        }


        private void SetSplitMode(bool split)
        {
            _app.IsSplitMode = split;

            if (split)
            {
                SplitGrid.ColumnDefinitions[1].Width = new GridLength(5);
                SplitGrid.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
                SplitterControl.Visibility = Visibility.Visible;
                RightPanel.Visibility = Visibility.Visible;
                // 右栏为空时同样显示欢迎提示卡片（尊重"不再显示"开关）
                if (string.IsNullOrEmpty(_app.Right?.CurrentFile))
                    ShowWelcome(_app.Right!);
            }
            else
            {
                SplitGrid.ColumnDefinitions[1].Width = new GridLength(0);
                SplitGrid.ColumnDefinitions[2].Width = new GridLength(0);
                SplitterControl.Visibility = Visibility.Collapsed;
                RightPanel.Visibility = Visibility.Collapsed;
            }
            UpdateActivePanelVisual();
            UpdateWindowTitle();
            // 分栏状态变化后同步右栏占位（右栏无文件 → 不占空间，避免灰色空区域）
            UpdateSplitAutoHide();
            // 分栏状态变化可能改变面板宽度，刷新标题栏按钮折叠状态
            Dispatcher.BeginInvoke(new Action(UpdateTitleBarOverflow));
        }

        /// <summary>
        /// 响应式：面板宽度 < 阈值时把标题栏三个图标按钮折叠进 ⋯ 溢出按钮（点击弹 ContextMenu），
        /// 窄屏标题栏只剩「文件名 + ⋯」，彻底不拥挤。阈值按单栏/分栏区分：分栏时每块面板实际宽度减半。
        /// </summary>
        private void UpdateTitleBarOverflow()
        {
            try
            {
                const double narrowThreshold = 480;
                var leftNarrow = WebViewL.ActualWidth > 0 && WebViewL.ActualWidth < narrowThreshold;
                var rightNarrow = WebViewR.ActualWidth > 0 && WebViewR.ActualWidth < narrowThreshold;
                TitleBarActionsL.Visibility = leftNarrow ? Visibility.Collapsed : Visibility.Visible;
                TitleBarOverflowL.Visibility = leftNarrow ? Visibility.Visible : Visibility.Collapsed;
                TitleBarActionsR.Visibility = rightNarrow ? Visibility.Collapsed : Visibility.Visible;
                TitleBarOverflowR.Visibility = rightNarrow ? Visibility.Visible : Visibility.Collapsed;
            }
            catch { }
        }

        /// <summary>按当前文件类型控制「编辑」按钮显隐：md/txt/html/css/js/json 与 docx 可编辑，其余隐藏。</summary>
        private void UpdateEditButtonVisibility()
        {
            try
            {
                EditBtnL.Visibility = CanEditFile(_app.Left?.CurrentFile) ? Visibility.Visible : Visibility.Collapsed;
                EditBtnR.Visibility = CanEditFile(_app.Right?.CurrentFile) ? Visibility.Visible : Visibility.Collapsed;
            }
            catch { }
        }

        /// <summary>左标题栏 ⋯ 溢出菜单（窄面板时替代图标按钮）。</summary>
        private void OnTitleBarOverflowL(object sender, RoutedEventArgs e)
        {
            ShowTitleBarOverflowMenu(TitleBarOverflowL,
                (_, _) => OnToggleAnnL(sender, e),
                (_, _) => OnToggleEditL(sender, e),
                (_, _) => OnExportL(sender, e),
                (_, _) => OnOpenL(sender, e),
                (_, _) => OnToggleSplit(sender, e));
        }

        /// <summary>右标题栏 ⋯ 溢出菜单（窄面板时替代图标按钮）。</summary>
        private void OnTitleBarOverflowR(object sender, RoutedEventArgs e)
        {
            ShowTitleBarOverflowMenu(TitleBarOverflowR,
                (_, _) => OnToggleAnnR(sender, e),
                (_, _) => OnToggleEditR(sender, e),
                (_, _) => OnExportR(sender, e),
                (_, _) => OnOpenR(sender, e),
                (_, _) => OnToggleSplit(sender, e));
        }

        /// <summary>
        /// 弹出标题栏 ⋯ 溢出菜单（高亮笔/编辑/导出/打开/分栏）。Button.Click 事件内同步打开 ContextMenu
        /// 会因按钮持有鼠标捕获而立即关闭，故用 Dispatcher 延迟到捕获释放后再打开。
        /// </summary>
        private void ShowTitleBarOverflowMenu(Button anchor, params RoutedEventHandler[] handlers)
        {
            var menu = new ContextMenu();
            string[] labels = { "高亮笔", "编辑 (Ctrl+S)", "导出 (pandoc)", "打开文件", "切换分栏 (Ctrl+T)" };
            for (int i = 0; i < labels.Length && i < handlers.Length; i++)
                menu.Items.Add(BuildOverflowItem(labels[i], handlers[i]));
            menu.PlacementTarget = anchor;
            menu.Placement = PlacementMode.Bottom;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                menu.IsOpen = true;
                menu.Closed += (_, _) => menu.Items.Clear();
            }));
        }

        /// <summary>构建 ⋯ 溢出菜单项（主题样式 MenuItem + 事件包装）。</summary>
        private MenuItem BuildOverflowItem(string header, RoutedEventHandler handler)
        {
            var item = new MenuItem
            {
                Header = header,
                Style = (Style)FindResource("ThemedMenuItem")
            };
            item.Click += handler;
            return item;
        }


        private void UpdateWindowTitle()
        {
            var l = !string.IsNullOrEmpty(_app.Left?.CurrentFile)
                ? Path.GetFileName(_app.Left.CurrentFile) : "未选择";
            if (_app.IsSplitMode)
            {
                var r = !string.IsNullOrEmpty(_app.Right?.CurrentFile)
                    ? Path.GetFileName(_app.Right.CurrentFile) : "未选择";
                Title = $"SeeMe - {l} | {r}";
            }
            else
            {
                Title = $"SeeMe - {l}";
            }
        }


        private void UpdateInfoPanel(PanelState state)
        {
            try
            {
                if (state.CurrentFile != null && File.Exists(state.CurrentFile))
                {
                    InfoFileName.Text = Path.GetFileName(state.CurrentFile);
                    InfoFilePath.Text = Path.GetDirectoryName(state.CurrentFile) ?? "";
                    var fi = new FileInfo(state.CurrentFile);
                    InfoFileSize.Text = _converter.FormatSizeBytes(fi.Length);
                    InfoModified.Text = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm");
                }
                else
                {
                    InfoFileName.Text = "未打开文件";
                    InfoFilePath.Text = "未打开文件";
                    InfoFileSize.Text = "—";
                    InfoModified.Text = "—";
                }
                RefreshBacklinks(state);
                RefreshStats(state);
            }
            catch (Exception ex) { LogErr("UpdateInfoPanel: " + ex.Message); }
        }

        /// <summary>
        /// 刷新信息面板「文件统计」卡（字数/行数/页数）。
        /// Markdown 读原文本；PDF 用 anydoc 提取的文本层（无文本层显示 —，页数取 PDF.js 上报或扫描文件）；
        /// Office 用 anydoc 提取的 Markdown（.xls / FileConverter 回退无源文本显示 —）。
        /// 后台线程读取，避免大文件卡 UI；md 超 2MB 只提示不统计。
        /// </summary>
        private async void RefreshStats(PanelState state)
        {
            try
            {
                var path = state.CurrentFile;
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    SetStats("—", "—", "—");
                    return;
                }
                var ext = Path.GetExtension(path);

                // ── Markdown ──
                if (FileTypes.IsMarkdown(ext))
                {
                    var stats = await Task.Run(() =>
                    {
                        var fi = new FileInfo(path);
                        if (fi.Length > 2L * 1024 * 1024) return (-1, -1); // 超限仅提示
                        var text = ReadTextAuto(path);
                        var (cjk, words) = CountTextStats(text);
                        var lines = text.Split('\n').Length;
                        return (cjk + words, lines);
                    });
                    if (stats.Item1 < 0) { SetStats(">2MB", "—", "—"); return; }
                    // 页数估算：约 45 行/页（对齐草图：86 行 ≈ 2 页）
                    var pages = Math.Max(1, (int)Math.Ceiling(stats.Item2 / 45.0));
                    SetStats(stats.Item1.ToString("N0"), stats.Item2.ToString("N0"), pages.ToString("N0"));
                    return;
                }

                // ── PDF（anydoc 提取的文本层统计；无文本层 → 仅显示页数）──
                if (FileTypes.IsPdf(ext))
                {
                    string text = state.PdfText ?? "";
                    bool scanned = false, unknown = false;
                    if (string.IsNullOrEmpty(text))
                    {
                        if (PdfTextCache.TryRead(path, out var t, out var hasText))
                        {
                            if (hasText) text = t ?? "";
                            else scanned = true;
                        }
                        else unknown = true;
                    }
                    if (unknown) { SetStats("—", "—", "—"); return; } // 尚未提取
                    if (scanned)
                    {
                        SetStats("—", "—", CountPdfPages(path) > 0 ? CountPdfPages(path).ToString("N0") : "—");
                        return;
                    }
                    var pdfStats = await Task.Run(() =>
                    {
                        var (cjk, words) = CountTextStats(text);
                        var lines = text.Split('\n').Length;
                        var pg = CountPdfPages(path);
                        return (cjk + words, lines, pg);
                    });
                    SetStats(pdfStats.Item1.ToString("N0"), pdfStats.Item2.ToString("N0"),
                             pdfStats.Item3 > 0 ? pdfStats.Item3.ToString("N0") : "—");
                    return;
                }

                // ── Office（anydoc 提取的 Markdown）──
                var office = await Task.Run(() =>
                {
                    var md = state.DocxMarkdown;
                    if (string.IsNullOrEmpty(md)) return (-1, -1, -1);
                    var (cjk, words) = CountTextStats(md);
                    var lines = md.Split('\n').Length;
                    var pages = Math.Max(1, (int)Math.Ceiling(lines / 45.0));
                    return (cjk + words, lines, pages);
                });
                if (office.Item1 < 0) { SetStats("—", "—", "—"); return; }
                SetStats(office.Item1.ToString("N0"), office.Item2.ToString("N0"), office.Item3.ToString("N0"));
            }
            catch (Exception ex) { LogErr("RefreshStats: " + ex.Message); }
        }

        /// <summary>粗略统计 PDF 页数：扫描文件字节中的 "/Type /Page"（排除 "/Type /Pages"）。
        /// 对象流压缩的 PDF 可能低估，仅作估算（文本视图下无其他页数来源）。</summary>
        private static int CountPdfPages(string path)
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                int count = 0;
                for (int i = 0; i + 11 < bytes.Length; i++)
                {
                    if (bytes[i] == (byte)'/' && bytes[i + 1] == (byte)'T' && bytes[i + 2] == (byte)'y'
                        && bytes[i + 3] == (byte)'p' && bytes[i + 4] == (byte)'e' && bytes[i + 5] == (byte)' '
                        && bytes[i + 6] == (byte)'/' && bytes[i + 7] == (byte)'P' && bytes[i + 8] == (byte)'a'
                        && bytes[i + 9] == (byte)'g' && bytes[i + 10] == (byte)'e' && bytes[i + 11] != (byte)'s')
                        count++;
                }
                return count;
            }
            catch { return 0; }
        }

        /// <summary>写「文件统计」卡三个数值（跨线程安全）。</summary>
        private void SetStats(string words, string lines, string pages)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => SetStats(words, lines, pages)));
                return;
            }
            try
            {
                if (StatWords != null) StatWords.Text = words;
                if (StatLines != null) StatLines.Text = lines;
                if (StatPages != null) StatPages.Text = pages;
            }
            catch (Exception ex) { LogErr("SetStats: " + ex.Message); }
        }

        /// <summary>
        /// 反向链接：扫描历史记录中其他 Markdown 文档，找出内容里以相对/绝对路径引用当前文件的，
        /// 显示在信息面板。后台线程执行避免阻塞 UI；对超大历史文件限量读取。
        /// </summary>
        private async void RefreshBacklinks(PanelState state)
        {
            try
            {
                var current = state.CurrentFile;
                if (string.IsNullOrEmpty(current) || !File.Exists(current))
                {
                    BacklinkList.ItemsSource = null;
                    BacklinkHint.Text = "暂无反向链接";
                    return;
                }

                // 当前文件名（无扩展名）与文件名本身作为匹配目标
                var nameNoExt = Path.GetFileNameWithoutExtension(current);
                var nameFull = Path.GetFileName(current);
                var backlinks = await Task.Run(() =>
                {
                    var results = new System.Collections.Generic.List<string>();
                    foreach (var p in _app.History.Entries)
                    {
                        if (string.IsNullOrEmpty(p) || !File.Exists(p)) continue;
                        var ext = Path.GetExtension(p).ToLowerInvariant();
                        if (!FileTypes.IsMarkdown(ext)) continue;
                        if (Path.GetFullPath(p).Equals(Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase))
                            continue;
                        try
                        {
                            // 限量读取前 1MB，避免扫描超大文件卡顿
                            var fi = new FileInfo(p);
                            if (fi.Length > 1024 * 1024) continue;
                            var content = File.ReadAllText(p);
                            var baseDir = Path.GetDirectoryName(current) ?? "";
                            var rel = Path.GetFileName(current);
                            if (content.Contains(nameNoExt, StringComparison.OrdinalIgnoreCase)
                                || content.Contains(nameFull, StringComparison.OrdinalIgnoreCase)
                                || (!string.IsNullOrEmpty(baseDir) && content.Contains(baseDir, StringComparison.OrdinalIgnoreCase)))
                            {
                                results.Add(p);
                            }
                        }
                        catch { }
                    }
                    return results;
                });

                if (backlinks.Count == 0)
                {
                    BacklinkList.ItemsSource = null;
                    BacklinkHint.Text = "没有文档引用当前文件";
                    return;
                }

                BacklinkList.ItemsSource = backlinks;
                BacklinkHint.Text = $"找到 {backlinks.Count} 个引用";
            }
            catch (Exception ex) { LogErr("RefreshBacklinks: " + ex.Message); }
        }

        private void OnBacklinkClick(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (BacklinkList.SelectedItem is string path && File.Exists(path))
                    OpenFileInternal(_activePanel ?? _app.Left, path);
            }
            catch (Exception ex) { LogErr("OnBacklinkClick: " + ex.Message); }
        }


        /// <summary>信息面板是否被「窗口过窄」自动隐藏（用于恢复，不覆盖手动操作）。</summary>
        private bool _infoPanelAutoHidden;

        /// <summary>
        /// 设置信息面板可见性（统一处理 WebView 渲染闪避 + 列宽 + 箭头图标）。
        /// 箭头语义：面板可见时点它收起（面板在右 → 箭头朝右 IconChevronRight）；
        /// 隐藏时点它展开（箭头朝左 IconChevronLeft），消除「左箭头却隐藏右面板」的违和。
        /// silent=true 时（窗口自动收展路径）不覆盖状态栏文字。
        /// </summary>
        private void SetInfoPanelVisibility(bool visible, bool silent = false)
        {
            if (visible)
            {
                InfoPanel.Visibility = Visibility.Visible;
                // 信息面板与笔记面板互斥显示
                if (NotesPanel != null) NotesPanel.Visibility = Visibility.Collapsed;
                if (NotesToggleBtn != null) NotesToggleBtn.Background = Brushes.Transparent;
                if (InfoPanelToggleIcon != null)
                    InfoPanelToggleIcon.Data = (Geometry)FindResource("IconChevronRight");
                InfoPanelToggleBtn.ToolTip = "隐藏信息面板";
                if (!silent) StatusText.Text = "信息面板已显示";
            }
            else
            {
                InfoPanel.Visibility = Visibility.Collapsed;
                if (InfoPanelToggleIcon != null)
                    InfoPanelToggleIcon.Data = (Geometry)FindResource("IconChevronLeft");
                InfoPanelToggleBtn.ToolTip = "显示信息面板";
                if (!silent) StatusText.Text = "信息面板已隐藏，点击箭头可恢复";
            }
            // 统一列宽：任一右面板可见 → 190，都隐藏 → 0（主内容区自动扩展占满，不留白）
            // 注意：不再隐藏/恢复 WebView（Visibility 切换会重建 WebView2 渲染表面，
            // 大文档恢复时灰屏几十秒）；WebView 全程可见，跟随列宽增量 resize。
            UpdateRightPanelColWidth();
            // 双栏自适应：右面板全关 + 右栏无文件 → 收掉右栏占位（避免大块灰色空区域）
            UpdateSplitAutoHide();
        }

        /// <summary>
        /// 双栏右栏占位自适应：右栏无文件时永远不占空间（无论面板是否打开、用户是否切过双栏），
        /// 消除"右侧大块灰色空区域"；右栏有文件时恢复 IsSplitMode 双栏显示。
        /// </summary>
        private void UpdateSplitAutoHide()
        {
            if (SplitGrid == null || SplitGrid.ColumnDefinitions.Count < 3) return;
            var rightHasFile = !string.IsNullOrEmpty(_app.Right?.CurrentFile);
            var showRight = _app.IsSplitMode && rightHasFile;
            if (SplitterControl != null)
                SplitterControl.Visibility = showRight ? Visibility.Visible : Visibility.Collapsed;
            if (RightPanel != null)
                RightPanel.Visibility = showRight ? Visibility.Visible : Visibility.Collapsed;
            SplitGrid.ColumnDefinitions[1].Width = new GridLength(showRight ? 5 : 0);
            SplitGrid.ColumnDefinitions[2].Width = new GridLength(
                showRight ? 1 : 0, showRight ? GridUnitType.Star : GridUnitType.Pixel);
        }

        /// <summary>
        /// 统一右侧列宽：信息面板或笔记面板任一可见 → 190；都隐藏 → 0，
        /// 主内容区（* 列）自动扩展占满剩余宽度，不留空白。
        /// 带 240ms 平滑过渡动画（受「动画效果」设置控制），列宽从 190↔0 渐变无跳动；
        /// WebView 全程可见，跟随列宽增量 resize（不做 Visibility 切换，避免渲染表面重建灰屏）。
        /// </summary>
        private void UpdateRightPanelColWidth()
        {
            if (InfoPanelCol == null) return;
            var anyVisible = InfoPanel.Visibility == Visibility.Visible
                || (NotesPanel != null && NotesPanel.Visibility == Visibility.Visible);
            var animate = AppSettings.Get(AppSettings.AnimationsKey, true);
            RightPanelCol.Apply(InfoPanelCol, anyVisible, animate);
        }

        private void OnToggleInfoPanel(object sender, RoutedEventArgs e)
        {
            // 手动操作：取消「自动隐藏」标记，避免窗口回宽后与用户意图冲突
            _infoPanelAutoHidden = false;
            SetInfoPanelVisibility(InfoPanel.Visibility != Visibility.Visible);
        }

        /// <summary>响应式：窗口过窄时自动隐藏右面板；回宽且此前是自动隐藏时自动恢复。</summary>
        private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
        {
            try
            {
                if (ActualWidth < 900)
                {
                    // 仅自动收：面板当前可见才折叠，绝不强制已隐藏的面板
                    if (InfoPanel.Visibility == Visibility.Visible)
                    {
                        _infoPanelAutoHidden = true;
                        SetInfoPanelVisibility(false, silent: true);
                    }
                }
                else if (_infoPanelAutoHidden && InfoPanel.Visibility != Visibility.Visible)
                {
                    // 窗口回宽且面板是被自动隐藏的 → 自动恢复（用户手动关闭的保持关闭）
                    _infoPanelAutoHidden = false;
                    SetInfoPanelVisibility(true, silent: true);
                }
                // 窗口尺寸变化会改变内容区宽度，刷新标题栏按钮折叠状态
                Dispatcher.BeginInvoke(new Action(UpdateTitleBarOverflow));
            }
            catch (Exception ex) { LogErr("Window size changed: " + ex.Message); }
        }

        /// <summary>切换左侧栏 文件/大纲 Tab（选中项以背景高亮区分，无下划线）。</summary>
        private void OnTabFiles(object sender, RoutedEventArgs e)
        {
            FilesPanel.Visibility = Visibility.Visible;
            OutlinePanel.Visibility = Visibility.Collapsed;
            TabFilesBtn.Background = FindResource("ItemSelectedBrush") as System.Windows.Media.Brush;
            TabFilesBtn.FontWeight = FontWeights.SemiBold;
            TabOutlineBtn.Background = System.Windows.Media.Brushes.Transparent;
            TabOutlineBtn.FontWeight = FontWeights.Normal;
        }

        private void OnTabOutline(object sender, RoutedEventArgs e)
        {
            FilesPanel.Visibility = Visibility.Collapsed;
            OutlinePanel.Visibility = Visibility.Visible;
            TabFilesBtn.Background = System.Windows.Media.Brushes.Transparent;
            TabFilesBtn.FontWeight = FontWeights.Normal;
            TabOutlineBtn.Background = FindResource("ItemSelectedBrush") as System.Windows.Media.Brush;
            TabOutlineBtn.FontWeight = FontWeights.SemiBold;
        }

        /// <summary>临时禁用/恢复两个 WebView 的渲染（不再使用：Visibility 切换会重建 WebView2 渲染表面，大文档恢复灰屏）。</summary>

    }
}
