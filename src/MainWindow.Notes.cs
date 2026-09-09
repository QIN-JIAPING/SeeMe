// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;

namespace SeeMe
{
    public partial class MainWindow
    {
        // ═══════════════════ 高亮笔（选中 → <mark>，持久化到 HighlightStore） ═══════════════════

        private static readonly SolidColorBrush AnnActiveBrush = new(Color.FromRgb(0xFD, 0xE6, 0x8A));

        /// <summary>高亮笔开关：全局唯一，持久化到 AppSettings，重启/退出不丢，切换文件时由 NavigationCompleted 重新注入页面。</summary>
        private bool AnnModeOn => AppSettings.Get(AppSettings.AnnModeKey, false);

        private void OnToggleAnnL(object sender, RoutedEventArgs e) => ToggleAnnMode(_app.Left);

        private void OnToggleAnnR(object sender, RoutedEventArgs e) => ToggleAnnMode(_app.Right);

        /// <summary>切换全局高亮笔模式：写 AppSettings 持久化，注入当前面板 JS，同步左右按钮视觉。</summary>
        private void ToggleAnnMode(PanelState? state)
        {
            var newVal = !AnnModeOn;
            AppSettings.Set(AppSettings.AnnModeKey, newVal);
            ApplyAnnMode(state);
            SyncAnnBtns();
            StatusText.Text = newVal
                ? "高亮笔已开启：选中文本自动添加标注（右键标注可删除）"
                : "高亮笔已关闭";
        }

        /// <summary>把全局高亮笔状态注入页面（PDF/Office 页无该函数时静默跳过）。</summary>
        private void ApplyAnnMode(PanelState? state)
        {
            if (state?.WebView?.CoreWebView2 == null) return;
            try
            {
                var on = AnnModeOn;
                state.WebView.CoreWebView2.ExecuteScriptAsync(
                    $"window.__setAnnMode ? window.__setAnnMode({(on ? "true" : "false")}) : ''");
            }
            catch (Exception ex) { LogErr("ApplyAnnMode: " + ex.Message); }
        }

        /// <summary>同步左右标题栏高亮笔按钮的视觉状态（背景色 + ToolTip）。</summary>
        private void SyncAnnBtns()
        {
            var on = AnnModeOn;
            var bg = on ? AnnActiveBrush : Brushes.Transparent;
            var tip = on ? "关闭高亮笔" : "高亮笔（选中文本添加标注）";
            if (AnnBtnL != null) { AnnBtnL.Background = bg; AnnBtnL.ToolTip = tip; }
            if (AnnBtnR != null) { AnnBtnR.Background = bg; AnnBtnR.ToolTip = tip; }
        }

        // ═══════════════════ 笔记面板（用户自由输入的笔记，与标注解耦） ═══════════════════

        private string _selectedNoteId = "";

        /// <summary>打开笔记面板前信息面板的可见状态：关闭笔记时按此恢复（尊重用户主动隐藏的偏好）。</summary>
        private bool _infoPanelWasVisible = true;

        private void OnToggleNotesPanel(object sender, RoutedEventArgs e) =>
            SetNotesPanelVisibility(NotesPanel.Visibility != Visibility.Visible);

        private void OnCloseNotesPanel(object sender, RoutedEventArgs e) => SetNotesPanelVisibility(false);

        /// <summary>显示/隐藏右侧笔记面板（与信息面板互斥；关闭时恢复到打开前的信息面板状态，列宽统一管理不留白）。</summary>
        private void SetNotesPanelVisibility(bool visible)
        {
            try
            {
                if (visible)
                {
                    _infoPanelWasVisible = InfoPanel.Visibility == Visibility.Visible;
                    NotesPanel.Visibility = Visibility.Visible;
                    InfoPanel.Visibility = Visibility.Collapsed;
                    if (NotesToggleBtn != null)
                        NotesToggleBtn.Background = FindResource("ItemSelectedBrush") as Brush ?? Brushes.Transparent;
                    RefreshNotesPanel();
                    StatusText.Text = "笔记面板：输入内容点「添加」新建笔记，选中已有笔记可直接修改（自动保存）";
                }
                else
                {
                    NotesPanel.Visibility = Visibility.Collapsed;
                    // 恢复到打开笔记前的信息面板状态：仅当打开前可见才恢复，不覆盖用户主动隐藏
                    if (_infoPanelWasVisible && InfoPanel.Visibility != Visibility.Visible)
                        InfoPanel.Visibility = Visibility.Visible;
                    if (NotesToggleBtn != null) NotesToggleBtn.Background = Brushes.Transparent;
                    StatusText.Text = "笔记面板已隐藏";
                }
                // 统一列宽：面板可见 → 190，都隐藏 → 0（主内容区自动占满，不留白；带平滑动画）
                // WebView 渲染恢复由 UpdateRightPanelColWidth 统一处理（动画完成后恢复）
                UpdateRightPanelColWidth();
            }
            catch (Exception ex) { LogErr("SetNotesPanelVisibility: " + ex.Message); }
        }

        /// <summary>当前有打开文件的面板（优先活动面板，其次左右栏）。</summary>
        private PanelState? ActiveFileState()
        {
            var st = ActiveOrLeft;
            if (st != null && !string.IsNullOrEmpty(st.CurrentFile)) return st;
            if (!string.IsNullOrEmpty(_app.Left?.CurrentFile)) return _app.Left;
            if (!string.IsNullOrEmpty(_app.Right?.CurrentFile)) return _app.Right;
            return null;
        }

        /// <summary>笔记面板条目公共骨架（唯一来源）：ListBoxItem 外壳 + 可选头部 + 底部行（时间戳 + 删除按钮）。
        /// head 用于在底部行之前插入条目主体（标注摘要 / 笔记正文）。</summary>
        private ListBoxItem MakeNotesEntry(string tag, string stampText, string deleteTip, Brush secondary, Action<StackPanel>? head = null)
        {
            var li = new ListBoxItem { Tag = tag, Cursor = Cursors.Hand, Margin = new Thickness(2, 1, 2, 1) };
            var sp = new StackPanel();
            head?.Invoke(sp);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };
            row.Children.Add(new TextBlock
            {
                Text = stampText,
                FontSize = 9,
                Foreground = secondary,
                VerticalAlignment = VerticalAlignment.Center
            });
            var del = new Button
            {
                Content = "删除",
                Tag = tag,
                FontSize = 9,
                Cursor = Cursors.Hand,
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(8, 0, 0, 0),
                ToolTip = deleteTip
            };
            del.Click += OnNotesItemRemove;
            row.Children.Add(del);
            sp.Children.Add(row);
            li.Content = sp;
            return li;
        }

        /// <summary>按当前面板文件重建列表：先列高亮标注（只读+可删），再列用户笔记（可编辑/删除）。</summary>
        private void RefreshNotesPanel()
        {
            if (NotesPanel == null || _notes == null) return;
            NotesList.Items.Clear();
            var st = ActiveFileState();
            var file = st?.CurrentFile;
            var highlights = string.IsNullOrEmpty(file) ? Array.Empty<HighlightItem>() : _highlights.ForFile(file);
            var notes = string.IsNullOrEmpty(file) ? Array.Empty<NoteItem>() : _notes.ForFile(file);
            var total = highlights.Count + notes.Count;
            if (NotesCountText != null)
                NotesCountText.Text = total > 0 ? $"{total} 条（标注 {highlights.Count} · 笔记 {notes.Count}）" : "暂无标注与笔记";
            var secondary = TryFindResource("TextSecondaryBrush") as Brush ?? Brushes.Gray;
            var body = TryFindResource("TextBodyBrush") as Brush ?? Brushes.Black;
            var accent = TryFindResource("AccentBrush") as Brush ?? Brushes.CornflowerBlue;
            var markBg = TryFindResource("ItemSelectedBrush") as Brush ?? Brushes.LightYellow;
            // ── 高亮标注条目（只读：文本摘要 + 来源行 + 删除）──
            foreach (var h in highlights)
            {
                NotesList.Items.Add(MakeNotesEntry("hl:" + h.Id, h.Created.ToString("MM-dd HH:mm"),
                    "删除该高亮标注", secondary, sp =>
                {
                    sp.Children.Add(new TextBlock
                    {
                        Text = "💡 高亮",
                        FontSize = 9,
                        Foreground = accent,
                        FontWeight = FontWeights.SemiBold,
                        Margin = new Thickness(0, 0, 0, 2)
                    });
                    var t = (h.Text ?? "").Replace('\n', ' ').Trim();
                    sp.Children.Add(new TextBlock
                    {
                        Text = string.IsNullOrEmpty(t) ? "（空标注）" : t,
                        FontSize = 11,
                        Foreground = body,
                        TextWrapping = TextWrapping.Wrap,
                        MaxHeight = 96,
                        Background = markBg
                    });
                    if (!string.IsNullOrWhiteSpace(h.Note))
                        sp.Children.Add(new TextBlock
                        {
                            Text = "✎ " + h.Note,
                            FontSize = 10,
                            Foreground = secondary,
                            TextWrapping = TextWrapping.Wrap,
                            Margin = new Thickness(0, 3, 0, 0)
                        });
                }));
            }
            // ── 用户笔记条目 ──
            foreach (var it in notes)
            {
                NotesList.Items.Add(MakeNotesEntry(it.Id, "修改 " + it.Modified.ToString("MM-dd HH:mm"),
                    "删除该笔记", secondary, sp =>
                {
                    sp.Children.Add(new TextBlock
                    {
                        Text = string.IsNullOrWhiteSpace(it.Content) ? "（空笔记）" : it.Content,
                        FontSize = 11,
                        Foreground = body,
                        TextWrapping = TextWrapping.Wrap,
                        MaxHeight = 96
                    });
                }));
            }
            _selectedNoteId = "";
            NotesNoteBox.Text = "";
            if (NotesNoteHint != null) NotesNoteHint.Visibility = Visibility.Visible;
        }

        /// <summary>点击笔记列表项：预填编辑框（修改模式）。</summary>
        private void OnNotesListSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (NotesList.SelectedItem is ListBoxItem li && li.Tag is string id && _notes != null)
            {
                // 高亮条目不是笔记：仅选中高亮、不进笔记编辑流
                if (id.StartsWith("hl:", StringComparison.Ordinal)) { _selectedNoteId = ""; return; }
                _selectedNoteId = id;
                var it = _notes.Items.FirstOrDefault(x => x.Id == id);
                NotesNoteBox.Text = it?.Content ?? "";
                if (NotesNoteHint != null)
                    NotesNoteHint.Visibility = string.IsNullOrEmpty(NotesNoteBox.Text)
                        ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        /// <summary>添加/保存笔记：未选中 → 新建当前文件笔记；已选中 → 保存修改。列表刷新并选中新条目。</summary>
        private void OnNotesAddNote(object sender, RoutedEventArgs e)
        {
            var st = ActiveFileState();
            if (st == null || string.IsNullOrEmpty(st.CurrentFile)) { StatusText.Text = "当前未打开文件，无法添加笔记"; return; }
            var content = NotesNoteBox.Text?.Trim() ?? "";
            if (content.Length == 0) { StatusText.Text = "笔记内容为空，未保存"; return; }

            if (string.IsNullOrEmpty(_selectedNoteId))
            {
                var item = _notes.Add(st.CurrentFile, content);
                RefreshNotesPanel();
                SelectNotesItem(item.Id);
                StatusText.Text = "笔记已添加";
            }
            else
            {
                _notes.Update(_selectedNoteId, NotesNoteBox.Text ?? "");
                StatusText.Text = "笔记已保存";
            }
        }

        /// <summary>编辑框内容变化：有选中笔记时 400ms 防抖自动保存（修改即时生效）。</summary>
        private void OnNotesNoteBoxChanged(object sender, TextChangedEventArgs e)
        {
            if (NotesNoteHint != null)
                NotesNoteHint.Visibility = string.IsNullOrEmpty(NotesNoteBox.Text)
                    ? Visibility.Visible : Visibility.Collapsed;
            if (string.IsNullOrEmpty(_selectedNoteId))
            {
                if (NotesNoteBox.Text.Length > 0)
                    StatusText.Text = "输入内容后点「添加」新建笔记；或先选择一条笔记修改（自动保存）";
                return;
            }
            if (_noteSaveTimer == null)
            {
                _noteSaveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                _noteSaveTimer.Tick += (_, _) =>
                {
                    _noteSaveTimer.Stop();
                    if (string.IsNullOrEmpty(_selectedNoteId)) return;
                    try
                    {
                        _notes.Update(_selectedNoteId, NotesNoteBox.Text);
                        StatusText.Text = "笔记已自动保存";
                    }
                    catch (Exception ex) { LogErr("Note auto-save: " + ex.Message); }
                };
            }
            _noteSaveTimer.Stop();
            _noteSaveTimer.Start();
        }

        private System.Windows.Threading.DispatcherTimer? _noteSaveTimer;

        private void OnNotesItemRemove(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b || b.Tag is not string id) return;
            if (id.StartsWith("hl:", StringComparison.Ordinal))
            {
                // 删除高亮标注：清存储 + 刷新笔记面板 + 重渲染当前文件移除页面 mark
                var hid = id.Substring(3);
                _highlights.Remove(hid);
                RefreshNotesPanel();
                var st = ActiveFileState();
                if (st?.CurrentFile != null)
                    _ = ReloadFileAsync(st, true, st.ResetCts());
                StatusText.Text = "已删除该高亮标注";
                return;
            }
            _notes.Remove(id);
            if (_selectedNoteId == id) _selectedNoteId = "";
            RefreshNotesPanel();
        }

        private void OnNotesClearFile(object sender, RoutedEventArgs e)
        {
            var st = ActiveFileState();
            if (st == null || string.IsNullOrEmpty(st.CurrentFile)) { StatusText.Text = "当前未打开文件"; return; }
            _notes.ClearFile(st.CurrentFile);
            StatusText.Text = "已清除该文件的全部笔记";
        }

        /// <summary>选中笔记列表中的指定条目（新建后定位用）。</summary>
        private void SelectNotesItem(string id)
        {
            if (NotesList == null) return;
            foreach (var obj in NotesList.Items)
            {
                if (obj is ListBoxItem li && li.Tag is string tag && tag == id)
                {
                    li.IsSelected = true;
                    NotesList.ScrollIntoView(li);
                    break;
                }
            }
        }

        // ═══════════════════ 笔记导出 Markdown ═══════════════════

        /// <summary>生成笔记 Markdown：文件头 + 用户笔记列表（按修改时间倒序）。</summary>
        private string BuildNotesMarkdown(string filePath)
        {
            var items = _notes.ForFile(filePath);
            var sb = new StringBuilder();
            sb.AppendLine($"# 笔记 · {Path.GetFileName(filePath)}");
            sb.AppendLine();
            sb.AppendLine($"> 来源：{filePath}");
            sb.AppendLine($"> 导出时间：{DateTime.Now:yyyy-MM-dd HH:mm}");
            sb.AppendLine();
            if (items.Count == 0)
            {
                sb.AppendLine("（该文件暂无笔记）");
                return sb.ToString();
            }
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                sb.AppendLine($"## 笔记 {i + 1}  ·  修改于 {it.Modified:yyyy-MM-dd HH:mm}");
                sb.AppendLine();
                sb.AppendLine(string.IsNullOrWhiteSpace(it.Content) ? "（空笔记）" : it.Content);
                sb.AppendLine();
            }
            return sb.ToString();
        }

        private void OnNotesExport(object sender, RoutedEventArgs e)
        {
            var st = ActiveFileState();
            if (st == null || string.IsNullOrEmpty(st.CurrentFile)) { StatusText.Text = "当前未打开文件，无法导出"; return; }
            var md = BuildNotesMarkdown(st.CurrentFile);
            var dlg = new SaveFileDialog
            {
                Title = "导出笔记为 Markdown",
                Filter = "Markdown 文件 (*.md)|*.md|所有文件 (*.*)|*.*",
                FileName = "笔记 - " + Path.GetFileNameWithoutExtension(st.CurrentFile) + ".md"
            };
            if (dlg.ShowDialog(this) == true)
            {
                try
                {
                    File.WriteAllText(dlg.FileName, md, new UTF8Encoding(true));
                    StatusText.Text = "笔记已导出: " + dlg.FileName;
                    LogInfo("Notes exported: " + dlg.FileName);
                }
                catch (Exception ex)
                {
                    StatusText.Text = "导出失败: " + ex.Message;
                    LogErr("Notes export: " + ex.Message);
                }
            }
        }

        // ═══════════════════ 打印 ═══════════════════

        /// <summary>调用 WebView2 系统打印对话框（Markdown/Office/PDF 文本视图均走当前 DOM 打印）。</summary>
        private void PrintActivePanel()
        {
            var st = ActiveOrLeft;
            if (st?.WebView?.CoreWebView2 == null)
            {
                StatusText.Text = "没有可打印的页面";
                return;
            }
            try
            {
                st.WebView.CoreWebView2.ShowPrintUI(CoreWebView2PrintDialogKind.Browser);
            }
            catch (Exception ex)
            {
                StatusText.Text = "打印失败: " + ex.Message;
                LogErr("Print: " + ex.Message);
            }
        }

        private void OnPrint(object sender, RoutedEventArgs e) => PrintActivePanel();
    }
}