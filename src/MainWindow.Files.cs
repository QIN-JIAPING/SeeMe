// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

using System;
using Microsoft.Win32;
using System.Windows.Controls;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace SeeMe
{
    public partial class MainWindow
    {
        private void ShowOpenFor(PanelState state)
        {
            var dlg = new OpenFileDialog
            {
                Filter = FileTypes.OpenFilter,
                Title = "选择文件"
            };
            if (dlg.ShowDialog(this) == true) OpenFileInternal(state, dlg.FileName);
        }


        private async void OpenFileInternal(PanelState state, string path)
        {
            try
            {
                // 编辑态切换文件：先把未落盘的改动保存到原文件，再打开新文件（自动保存计时未到也不丢字）
                if (state.EditMode) await FlushPendingEditAsync(state);
                // 离开当前文件前落盘阅读进度。
                // 注：2026-09-18 之前这里只有一行注释、没有实现（实际只是把 LastScrollY 清零），
                // 即"滚动位置记忆"从未跨会话生效。本次补齐。
                SaveReadProgress(state);
                var fullPath = Path.GetFullPath(path);
                state.CurrentFile = fullPath;
                state.FileDir = Path.GetDirectoryName(fullPath);
                state.TitleText.Text = Path.GetFileName(fullPath);
                state.TitleText.ToolTip = Path.GetDirectoryName(fullPath) ?? "";
                state.EditMode = false; // 切换文件即退出编辑态（回到预览）
                // 进度清零后立刻用存储里的历史值回填 —— 这就是"继续阅读"：
                // 下次打开同一文件时，渲染页会用 LastScrollY 还原滚动位置。
                state.LastScrollY = 0;
                state.LastReadPercent = 0;
                state.CurrentHeadingId = "";
                var resume = _progress?.ForFile(fullPath);
                if (resume != null)
                {
                    state.LastScrollY = resume.MaxScrollY;
                    state.LastReadPercent = resume.Percent;
                }
                UpdateEditButtonVisibility();
                UpdateInfoPanel(state);
                // 取消之前的加载，避免异步竞争
                _ = ReloadFileAsync(state, true, state.ResetCts());
                SetupWatcher(state);
                UpdateWindowTitle();
                _app.History.Add(fullPath);
                StatusText.Text = "已打开: " + Path.GetFileName(fullPath) + "（实时刷新中）";
                LogInfo("Opened: " + Path.GetFileName(fullPath));
                // 右栏文件变化后同步双栏占位（打开到右栏 → 双栏恢复；关闭 → 收折）
                UpdateSplitAutoHide();
            }
            catch (Exception ex)
            {
                StatusText.Text = "打开失败: " + ex.Message;
                LogErr("Open file: " + ex);
            }
        }

        /// <summary>
        /// 把当前面板的阅读进度落盘（切换文件 / 关闭面板时调用）。
        ///
        /// <para><b>节流</b>：本方法只在"离开文件"这一刻调用，不在 scroll 回调里调 ——
        /// scroll 经 rAF 节流后仍有约 60 次/秒，逐次写盘毫无意义且伤 SSD。
        /// 运行中的进度只更新内存（<c>PanelState.LastReadPercent</c>）。</para>
        /// </summary>
        private void SaveReadProgress(PanelState state)
        {
            try
            {
                if (_progress == null) return;
                if (string.IsNullOrEmpty(state.CurrentFile)) return;
                // 没读到过任何进度时不写（避免为每个只瞥一眼的文件建条目）
                if (state.LastScrollY <= 0 && state.LastReadPercent <= 0) return;
                _progress.Record(state.CurrentFile, state.LastReadPercent, state.LastScrollY);
            }
            catch (Exception ex) { LogErr("SaveReadProgress: " + ex.Message); }
        }

        /// <summary>刷新最近文件列表（受搜索过滤影响；用虚拟化集合承载）。</summary>
        private void RefreshRecentFilesList()
        {
            // 线程守门：本方法是**应用级单例** History/Bookmarks/Progress 的 Changed 回调，
            // 谁在后台线程改一次集合就会直接写 UI 控件 → 跨线程访问异常。
            // 目前所有调用链都在 UI 线程（实测），但那是「调用方恰好守规矩」，不是本方法的保证。
            // 显式 CheckAccess 把它变成结构性保证（范例：MainWindow.Panels.cs 的 SetStats）。
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(RefreshRecentFilesList));
                return;
            }
            try
            {
                var source = _app.History.Entries;
                IEnumerable<string> filtered;
                if (!string.IsNullOrWhiteSpace(_searchFilter))
                {
                    var f = _searchFilter.Trim().ToLowerInvariant();
                    // 内容搜索模式：输入以 ">" 开头时，扫描历史文件内容（限量 256KB/文件，后台线程）
                    if (f.StartsWith(">"))
                    {
                        var keyword = f.Substring(1).Trim();
                        RefreshByContent(keyword);
                        return;
                    }
                    filtered = source.Where(p =>
                    {
                        var name = Path.GetFileName(p).ToLowerInvariant();
                        var dir = (Path.GetDirectoryName(p) ?? "").ToLowerInvariant();
                        return name.Contains(f) || dir.Contains(f);
                    });
                }
                else
                {
                    filtered = source;
                }
                var list = filtered.ToList();
                // 书签置顶（保持原相对顺序）
                var bookmarked = list.Where(p => _bookmarks.Contains(p)).ToList();
                var rest = list.Where(p => !_bookmarks.Contains(p)).ToList();
                var ordered = bookmarked.Concat(rest).ToList();
                RecentFilesList.ItemsSource = null;
                RecentFilesList.ItemsSource = ordered.Select(p =>
                {
                    try
                    {
                        var name = Path.GetFileName(p);
                        // 显示名缩短（新建 Microsoft Word 文档 → 新建 Word 文档），tooltip 仍显示全名
                        var shortName = ShortenNewFileName(name);
                        var prog = _progress?.ForFile(p);
                        // 只在"读了一部分"时显示进度条：0% 无意义，100% 已读完不必再提示
                        var showProg = prog != null && prog.Percent > 0.5 && prog.Percent < 99.5;
                        const double TrackWidth = 60.0;
                        return new RecentItem
                        {
                            Display = (_bookmarks.Contains(p) ? "📌 " : "") + shortName,
                            Full = name,
                            ProgressText = showProg ? $"读到 {(int)Math.Round(prog!.Percent)}%" : "",
                            ProgressWidth = showProg ? Math.Max(2, TrackWidth * prog!.Percent / 100.0) : 0,
                            HasProgress = showProg
                        };
                    }
                    catch { return new RecentItem { Display = p, Full = p }; }
                }).ToList();
                // Tag 必须与显示顺序一致（书签置顶改变了索引），否则 GetSelectedRecentPath 取错文件
                RecentFilesList.Tag = ordered;
            }
            catch (Exception ex) { LogErr("Refresh list: " + ex.Message); }
        }

        /// <summary>全文检索索引（Lucene）：惰性初始化，索引目录 %LOCALAPPDATA%\SeeMe\lucene。</summary>
        private SearchIndexService? _searchIndex;

        /// <summary>
        /// 内容搜索（搜索框输入 ">关键词"）：走 Lucene 全文索引（秒级），替代旧线性扫描。
        /// 首次搜索自动建索引（限量 256KB/文件），文件变化自动重建；命中即列出。
        /// </summary>
        private async void RefreshByContent(string keyword)
        {
            try
            {
                if (string.IsNullOrEmpty(keyword)) { RefreshRecentFilesList(); return; }
                _searchIndex ??= new SearchIndexService(
                    Path.Combine(StoragePaths.Root, "lucene"));
                var kw = keyword.ToLowerInvariant();
                // pdfStats: [0]=扫描版 PDF（无文本层） [1]=未提取文本的 PDF
                var pdfStats = new int[2];
                var hits = await Task.Run(() =>
                {
                    var files = _app.History.Entries
                        .Where(p => !string.IsNullOrEmpty(p) && File.Exists(p))
                        .ToList();
                    foreach (var p in files)
                    {
                        if (!FileTypes.IsPdf(Path.GetExtension(p))) continue;
                        if (PdfTextCache.TryRead(p, out var pdfText, out var hasText))
                        {
                            if (!hasText) pdfStats[0]++;
                        }
                        else pdfStats[1]++;
                    }
                    string? PdfTextProvider(string p)
                        => PdfTextCache.TryRead(p, out var t, out var has) && has ? t : null;
                    return _searchIndex!.Search(files, PdfTextProvider, kw, out _).ToList();
                });

                var bookmarked = hits.Where(p => _bookmarks.Contains(p)).ToList();
                var rest = hits.Where(p => !_bookmarks.Contains(p)).ToList();
                var ordered = bookmarked.Concat(rest).ToList();
                RecentFilesList.ItemsSource = null;
                RecentFilesList.ItemsSource = ordered.Select(p =>
                {
                    try { return (bookmarked.Contains(p) ? "📌 " : "🔍 ") + Path.GetFileName(p); }
                    catch { return p; }
                }).ToList();
                // Tag 必须与显示顺序一致，否则 GetSelectedRecentPath 按索引取路径会错位
                RecentFilesList.Tag = ordered;
                var notes = new List<string>();
                if (pdfStats[1] > 0) notes.Add($"{pdfStats[1]} 个 PDF 未提取（打开一次后可搜索）");
                if (pdfStats[0] > 0) notes.Add($"{pdfStats[0]} 个扫描版 PDF 无文本层");
                var pdfNote = notes.Count > 0 ? "；" + string.Join("；", notes) : "";
                StatusText.Text = (hits.Count > 0 ? $"内容搜索：找到 {hits.Count} 个文件" : "内容搜索：无匹配") + pdfNote;
            }
            catch (Exception ex) { LogErr("RefreshByContent: " + ex.Message); }
        }


        private System.Windows.Threading.DispatcherTimer? _searchDebounce;

        private void OnSearchFilter(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            try
            {
                _searchFilter = (SearchBox.Text ?? "").Trim();
                // 空输入时显示占位提示
                if (SearchHint != null)
                    SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text)
                        ? Visibility.Visible : Visibility.Collapsed;
                // 防抖：停顿 250ms 后才刷新列表，避免每键全量扫描历史
                if (_searchDebounce == null)
                {
                    _searchDebounce = new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(250)
                    };
                    _searchDebounce.Tick += (_, _) =>
                    {
                        _searchDebounce.Stop();
                        // 刷新失败不该中断后续输入（防抖计时器是一次性的，抛出去会让列表再也不刷新），
                        // 但也不能像以前那样完全静默 —— 落一条日志，便于定位「搜索框没反应」类问题。
                        try { RefreshRecentFilesList(); }
                        catch (Exception ex) { LogErr("Search debounce refresh: " + ex.Message); }
                    };
                }
                _searchDebounce.Stop();
                _searchDebounce.Start();
            }
            catch (Exception ex) { LogErr("OnSearchFilter: " + ex.Message); }
        }

        /// <summary>Windows 新建文件默认名过长（新建 Microsoft Word 文档…），显示时缩短为通用称呼，hover 显示全名。</summary>
        private static string ShortenNewFileName(string name)
        {
            if (name.StartsWith("新建 Microsoft Word 文档", StringComparison.Ordinal))
                return "新建 Word 文档" + Path.GetExtension(name);
            if (name.StartsWith("新建 Microsoft Excel 工作表", StringComparison.Ordinal))
                return "新建 Excel 表格" + Path.GetExtension(name);
            if (name.StartsWith("新建 Microsoft PowerPoint 演示文稿", StringComparison.Ordinal))
                return "新建 PPT 演示" + Path.GetExtension(name);
            return name;
        }

        /// <summary>
        /// 最近文件列表项：Display=显示名（可缩短），Full=完整文件名（tooltip）。
        /// 后三项为阅读进度展示（见 <see cref="ReadProgressStore"/>）：
        /// 无进度时 <see cref="ProgressText"/> 为空、<see cref="ProgressWidth"/> 为 0，模板据此隐藏进度条。
        /// </summary>
        private sealed class RecentItem
        {
            public string Display { get; set; } = "";
            public string Full { get; set; } = "";

            /// <summary>进度文字，如 "读到 63%"；无进度记录时为空串。</summary>
            public string ProgressText { get; set; } = "";

            /// <summary>
            /// 进度条填充宽度（像素）。WPF 的 ProgressBar 宽度绑定需要具体数值，
            /// 这里按固定轨道宽 60px 折算好传进来，避免在 XAML 里做百分比换算。
            /// </summary>
            public double ProgressWidth { get; set; }

            /// <summary>是否有进度（XAML 用它切换进度行可见性）。</summary>
            public bool HasProgress { get; set; }
        }


        private void OnRecentFileClick(object sender, MouseButtonEventArgs e)
        {
            if (RecentFilesList.SelectedItem == null) return;
            var tagList = RecentFilesList.Tag as List<string>;
            if (tagList == null) return;
            var idx = RecentFilesList.SelectedIndex;
            if (idx < 0 || idx >= tagList.Count) return;
            var path = tagList[idx];
            if (!File.Exists(path))
            {
                _app.History.Remove(path);
                StatusText.Text = "文件不存在，已从历史移除: " + Path.GetFileName(path);
                RefreshRecentFilesList();
                return;
            }

            PanelState target;
            if (!_app.IsSplitMode)
            {
                target = _app.Left;
            }
            else if (string.IsNullOrEmpty(_app.Left.CurrentFile))
            {
                target = _app.Left;
            }
            else if (string.IsNullOrEmpty(_app.Right.CurrentFile))
            {
                target = _app.Right;
            }
            else
            {
                // ActiveOrLeft 声明为 PanelState?（命令面板统一入口，可能无活动面板），
                // 但此处必然非空（_app.Left 恒非空）。用局部变量收窄类型而非 ! 压制，
                // 让编译器与运行时保护保持一致。
                target = ActiveOrLeft ?? _app.Left;
            }

            _activePanel = target;
            UpdateActivePanelVisual();
            OpenFileInternal(target, path);
        }


        private void OnRecentFileRightClick(object sender, MouseButtonEventArgs e)
        {
            var dep = e.OriginalSource as DependencyObject;
            var item = FindAncestor<ListBoxItem>(dep);
            if (item != null) item.IsSelected = true;
        }


        private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T t) return t;
                current = System.Windows.Media.VisualTreeHelper.GetParent(current);
            }
            return null;
        }


        /// <summary>折叠/展开最近文件列表（标题右侧箭头 ▾/▸）。</summary>
        private bool _recentCollapsed;

        private void OnToggleRecentCollapse(object sender, RoutedEventArgs e)
        {
            _recentCollapsed = !_recentCollapsed;
            RecentFilesList.Visibility = _recentCollapsed ? Visibility.Collapsed : Visibility.Visible;
            // 草图语义：展开时「▸ 折叠」、折叠时「▾ 展开」（替换原 X）
            RecentCollapseIcon.Data = (Geometry)FindResource(
                _recentCollapsed ? "IconChevronDown" : "IconChevronRight");
            RecentCollapseLabel.Text = _recentCollapsed ? "展开" : "折叠";
            RecentCollapseBtn.ToolTip = _recentCollapsed ? "展开最近文件" : "折叠最近文件";
        }

        /// <summary>右键菜单：清空全部历史（带二次确认，防止误触）。</summary>
        private void OnCtxClearHistory(object sender, RoutedEventArgs e)
        {
            if (!ShowClearHistoryDialog()) return;
            _app.History.Clear();
            RefreshRecentFilesList();
            StatusText.Text = "最近文件已全部清空";
        }

        /// <summary>清空历史确认弹窗（对齐草图：标题 + 正文 + 取消/清空按钮，清空为红色）。</summary>
        private bool ShowClearHistoryDialog()
        {
            var result = false;
            var win = new Window
            {
                Title = "清空最近文件？",
                Width = 380, Height = 210,
                Background = (Brush)FindResource("CardBackgroundBrush")
            };

            var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(20, 18, 20, 14) };
            var title = new System.Windows.Controls.TextBlock
            {
                Text = "清空最近文件？",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextPrimaryBrush"),
                Margin = new Thickness(0, 0, 0, 10)
            };
            var msg = new System.Windows.Controls.TextBlock
            {
                Text = "将删除全部历史记录，此操作不可撤销。",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)FindResource("TextBodyBrush"),
                Margin = new Thickness(0, 0, 0, 18)
            };
            panel.Children.Add(title);
            panel.Children.Add(msg);

            var row = new System.Windows.Controls.StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var cancelBtn = new System.Windows.Controls.Button
            {
                Content = "取消",
                Style = (Style)FindResource("ChipBtn"),
                FontSize = 12,
                Padding = new Thickness(14, 6, 14, 6),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = Cursors.Hand
            };
            cancelBtn.Click += (_, _) => win.Close();
            var clearBtn = new System.Windows.Controls.Button
            {
                Content = "清空",
                Style = (Style)FindResource("DangerBtn"),
                FontSize = 12,
                Padding = new Thickness(14, 6, 14, 6),
                Cursor = Cursors.Hand
            };
            clearBtn.Click += (_, _) => { result = true; win.Close(); };
            row.Children.Add(cancelBtn);
            row.Children.Add(clearBtn);
            panel.Children.Add(row);

            ShowThemedDialog(win, w => w.Content = panel);
            return result;
        }


        private string? GetSelectedRecentPath()
        {
            var tagList = RecentFilesList.Tag as List<string>;
            if (tagList == null) return null;
            var idx = RecentFilesList.SelectedIndex;
            if (idx < 0 || idx >= tagList.Count) return null;
            return tagList[idx];
        }


        private void OpenRecentInto(PanelState target)
        {
            var path = GetSelectedRecentPath();
            if (string.IsNullOrEmpty(path)) return;
            if (!File.Exists(path))
            {
                _app.History.Remove(path);
                StatusText.Text = "文件不存在，已从历史移除: " + Path.GetFileName(path);
                RefreshRecentFilesList();
                return;
            }
            _activePanel = target;
            UpdateActivePanelVisual();
            OpenFileInternal(target, path);
        }


        private void OnCtxOpenInLeft(object sender, RoutedEventArgs e) => OpenRecentInto(_app.Left);

        private void OnCtxOpenInRight(object sender, RoutedEventArgs e)
        {
            EnsureRightPanel();
            OpenRecentInto(_app.Right);
        }


        private void OnCtxRevealInExplorer(object sender, RoutedEventArgs e)
        {
            var path = GetSelectedRecentPath();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                StatusText.Text = "文件不存在";
                return;
            }
            try
            {
                var expPsi = new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = true };
                // 用 ArgumentList 逐参数传递，避免路径含 & | 等特殊字符被错误解析
                expPsi.ArgumentList.Add("/select," + path);
                System.Diagnostics.Process.Start(expPsi);
            }
            catch (Exception ex)
            {
                StatusText.Text = "无法打开资源管理器 " + ex.Message;
            }
        }


        private void OnCtxRemove(object sender, RoutedEventArgs e)
        {
            var path = GetSelectedRecentPath();
            if (string.IsNullOrEmpty(path)) return;
            _app.History.Remove(path);
            StatusText.Text = "已从历史移除: " + Path.GetFileName(path);
            RefreshRecentFilesList();
        }


        private void OnCtxToggleBookmark(object sender, RoutedEventArgs e)
        {
            var path = GetSelectedRecentPath();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            var added = _bookmarks.Toggle(path);
            StatusText.Text = added ? "已添加书签: " + Path.GetFileName(path) : "已移除书签: " + Path.GetFileName(path);
            // Toggle 内部触发 Changed → RefreshRecentFilesList（书签变化自动刷新列表）
            if (!added) RefreshRecentFilesList();
        }

        /// <summary>
        /// 批量导出：多选 Markdown 文件 → 循环调用 pandoc 导出为 Word（逐文件报告结果）。
        /// 依赖 pandoc；未安装时提示跳转安装引导。
        /// </summary>
        private async void OnBatchExport(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_pandoc.FindPandoc() == null)
                {
                    ShowPandocMissingDialog();
                    return;
                }
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = "Markdown 文件 (*.md;*.markdown;*.mkd;*.mdown)|*.md;*.markdown;*.mkd;*.mdown|所有文件 (*.*)|*.*",
                    Title = "选择要批量导出的 Markdown 文件（可多选）",
                    Multiselect = true
                };
                if (dlg.ShowDialog(this) != true || dlg.FileNames.Length == 0) return;

                var files = dlg.FileNames.Where(f => FileTypes.IsMarkdown(Path.GetExtension(f))).ToList();
                if (files.Count == 0) { StatusText.Text = "未选择有效的 Markdown 文件"; return; }

                StatusText.Text = $"正在导出 {files.Count} 个文件…";
                int ok = 0, fail = 0;
                var failures = new System.Text.StringBuilder();
                foreach (var f in files)
                {
                    var (success, message) = await _pandoc.ExportAsync(f, format: "docx");
                    if (success) ok++;
                    else { fail++; failures.AppendLine(Path.GetFileName(f) + ": " + message); }
                }
                StatusText.Text = $"批量导出完成：成功 {ok}，失败 {fail}";
                if (fail > 0)
                {
                    System.Windows.MessageBox.Show("导出失败：" + Environment.NewLine + failures,
                        "批量导出结果", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else
                {
                    System.Windows.MessageBox.Show($"已成功导出 {ok} 个文件（Word 格式，与原文件同目录 _exported.docx）",
                        "批量导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex) { LogErr("OnBatchExport: " + ex.Message); StatusText.Text = "批量导出异常: " + ex.Message; }
        }


        private void OnCtxOpen(object sender, RoutedEventArgs e)
        {
            OnRecentFileClick(RecentFilesList, null!);
        }


        private void OnCtxCopyPath(object sender, RoutedEventArgs e)
        {
            var path = GetSelectedRecentPath();
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                System.Windows.Clipboard.SetText(path);
                StatusText.Text = "已复制路径 " + path;
            }
            catch (Exception ex)
            {
                StatusText.Text = "复制失败: " + ex.Message;
            }
        }


        private void OnDragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }


        private void OnDrop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var dropped = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (dropped == null || dropped.Length == 0) return;
            OpenDroppedFiles(FilterSupportedFiles(dropped), dropped.Length);
        }

        /// <summary>校验拖入文件（存在性 + 扩展名 + Magic Bytes），返回支持的文件列表。</summary>
        private static List<string> FilterSupportedFiles(IEnumerable<string> paths)
        {
            return paths.Where(p =>
            {
                try
                {
                    if (!File.Exists(p)) return false;
                    var ext = Path.GetExtension(p);
                    if (!FileTypes.IsSupported(ext))
                        return false;
                    // Magic Bytes 校验：验证文件头与扩展名一致（逻辑见 MagicBytes.MatchesExt）
                    // 缓冲区 12 字节：WEBP 的 "WEBP" 标记在偏移 8-11，8 字节不够判定（会越界）。
                    var header = new byte[12];
                    try
                    {
                        using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        if (fs.Read(header, 0, 12) < 4) return false;
                    }
                    catch { return false; }
                    return MagicBytes.MatchesExt(ext, header);
                }
                catch { return false; }
            }).ToList();
        }

        /// <summary>
        /// 打开拖入的文件（支持一次拖入多个）：
        /// 1 个 → 直接打开；≥2 个 → 弹窗询问打开方式（仅第一个 / 分栏前 2 个 / 取消），
        /// 不再静默自动分栏打断当前阅读。WPF 拖放与 WebView 拖放消息共用。
        /// totalDropped 为原始拖入数量（含不支持类型），用于提示忽略数。
        /// </summary>
        private void OpenDroppedFiles(List<string> supportedFiles, int totalDropped = -1)
        {
            if (supportedFiles.Count == 0)
            {
                StatusText.Text = "不支持的文件类型，请拖入 md / pdf / Office / epub / csv 文件";
                return;
            }
            var ignored = totalDropped >= 0 ? Math.Max(0, totalDropped - supportedFiles.Count) : 0;
            var ignoreMsg = ignored > 0 ? $"，已忽略 {ignored} 个不支持的文件" : "";

            // ≥2 个：先询问用户打开方式，避免自动分栏打扰
            if (supportedFiles.Count >= 2 && _app.Left != null && _app.Right != null)
            {
                var choice = ShowMultiDropChoice(supportedFiles, ignored);
                if (choice == "cancel")
                {
                    StatusText.Text = "已取消，文件未打开" + (ignored > 0 ? ignoreMsg : "");
                    return;
                }
                if (choice == "single")
                {
                    OpenDroppedSingle(supportedFiles[0]);
                    AddDroppedToHistory(supportedFiles, 1);
                    StatusText.Text = $"已打开 {Path.GetFileName(supportedFiles[0])}，其余 {supportedFiles.Count - 1} 个已加入最近文件{ignoreMsg}";
                    return;
                }
                // split：分栏打开前两个，其余加入最近文件
                SetSplitMode(true);
                OpenFileInternal(_app.Left, supportedFiles[0]);
                OpenFileInternal(_app.Right, supportedFiles[1]);
                _activePanel = _app.Left;
                UpdateActivePanelVisual();
                AddDroppedToHistory(supportedFiles, 2);
                StatusText.Text = $"已分栏打开 2 个文件，其余 {supportedFiles.Count - 2} 个已加入最近文件{ignoreMsg}";
                return;
            }

            // 单个文件：直接打开
            OpenDroppedSingle(supportedFiles[0]);
            if (ignored > 0)
                StatusText.Text = "已打开: " + Path.GetFileName(supportedFiles[0]) + ignoreMsg;
        }

        /// <summary>单个文件打开：填活动面板（分栏模式下左栏已有文件、右栏为空时填右栏）。
        /// 仅当目标确实是右栏时才自动开启分栏，其余情况保持当前布局——拖入多个选「仅第一个」时绝不自动分栏。</summary>
        private void OpenDroppedSingle(string path)
        {
            var target = ActiveOrLeft;
            if (target == null) return;
            if (_app.IsSplitMode && _app.Right != null && !string.IsNullOrEmpty(target.CurrentFile) && string.IsNullOrEmpty(_app.Right.CurrentFile))
                target = _app.Right;
            if (target == _app.Right && !_app.IsSplitMode)
                SetSplitMode(true);
            OpenFileInternal(target, path);
            _activePanel = target;
            UpdateActivePanelVisual();
        }

        /// <summary>把拖入但未打开的文件（start 及以后）加入最近文件列表，便于稍后一键取用。</summary>
        private void AddDroppedToHistory(List<string> files, int start)
        {
            if (start >= files.Count) return;
            foreach (var p in files.Skip(start))
                _app.History.Add(p);
        }

        /// <summary>
        /// 拖入多个文件时的打开方式确认弹窗（对齐应用设计语言：圆角卡片 + 主题按钮）。
        /// 返回 "single"（仅打开第一个）/ "split"（分栏打开前 2 个）/ "cancel"（不打开）。
        /// </summary>
        private string ShowMultiDropChoice(List<string> files, int ignored)
        {
            var result = "cancel";
            var win = new Window
            {
                Title = "打开方式",
                Width = 400,
                SizeToContent = SizeToContent.Height,
                Background = (Brush)FindResource("CardBackgroundBrush")
            };

            var panel = new StackPanel { Margin = new Thickness(20, 18, 20, 16) };
            var title = new TextBlock
            {
                Text = $"拖入了 {files.Count} 个文件",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextPrimaryBrush"),
                Margin = new Thickness(0, 0, 0, 8)
            };
            var names = string.Join("、", files.Take(3).Select(Path.GetFileName));
            if (files.Count > 3) names += " 等";
            if (ignored > 0) names += $"（另有 {ignored} 个不支持的文件已忽略）";
            var msg = new TextBlock
            {
                Text = names + "。选择打开方式：",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)FindResource("TextBodyBrush"),
                Margin = new Thickness(0, 0, 0, 14)
            };
            panel.Children.Add(title);
            panel.Children.Add(msg);

            // 选项 1：仅打开第一个（默认，最不打扰）
            var singleBtn = new Button
            {
                Content = "仅打开第一个（其余加入最近文件）",
                Style = (Style)FindResource("PrimaryBtn"),
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 8),
                IsDefault = true,
                Cursor = Cursors.Hand
            };
            singleBtn.Click += (_, _) => { result = "single"; win.Close(); };

            // 选项 2：分栏打开前 2 个
            var splitBtn = new Button
            {
                Content = "分栏打开前 2 个（其余加入最近文件）",
                Style = (Style)FindResource("ChipBtn"),
                FontSize = 12,
                Padding = new Thickness(12, 7, 12, 7),
                Margin = new Thickness(0, 0, 0, 8),
                Cursor = Cursors.Hand
            };
            splitBtn.Click += (_, _) => { result = "split"; win.Close(); };

            // 选项 3：取消
            var cancelBtn = new Button
            {
                Content = "取消",
                Style = (Style)FindResource("ChipBtn"),
                FontSize = 12,
                Padding = new Thickness(12, 7, 12, 7),
                Cursor = Cursors.Hand
            };
            cancelBtn.Click += (_, _) => { result = "cancel"; win.Close(); };

            panel.Children.Add(singleBtn);
            panel.Children.Add(splitBtn);
            panel.Children.Add(cancelBtn);

            ShowThemedDialog(win, w => w.Content = panel);
            return result;
        }


    }
}
