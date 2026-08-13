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
                state.CurrentFile = Path.GetFullPath(path);
                state.FileDir = Path.GetDirectoryName(state.CurrentFile);
                state.TitleText.Text = Path.GetFileName(state.CurrentFile);
                state.TitleText.ToolTip = Path.GetDirectoryName(state.CurrentFile) ?? "";
                state.EditMode = false; // 切换文件即退出编辑态（回到预览）
                UpdateEditButtonVisibility();
                UpdateInfoPanel(state);
                // 取消之前的加载，避免异步竞争
                _ = ReloadFileAsync(state, true, state.ResetCts());
                SetupWatcher(state);
                UpdateWindowTitle();
                _app.History.Add(state.CurrentFile);
                StatusText.Text = "已打开: " + Path.GetFileName(state.CurrentFile) + "（实时刷新中）";
                LogInfo("Opened: " + Path.GetFileName(state.CurrentFile));
            }
            catch (Exception ex)
            {
                StatusText.Text = "打开失败: " + ex.Message;
                LogErr("Open file: " + ex);
            }
        }

        // ═══════════════ 内联编辑（文本类 + docx pandoc 回写） ═══════════════

        private static readonly HashSet<string> EditableTextExts = new(StringComparer.OrdinalIgnoreCase)
        {
            ".md", ".markdown", ".txt", ".html", ".htm", ".css", ".js", ".mjs", ".json", ".csv"
        };

        private const long MaxEditBytes = 5L * 1024 * 1024; // 编辑上限 5MB，避免超大文本卡死 textarea

        /// <summary>可编辑文件：文本类扩展名 或 docx（docx 走 Markdown 编辑 + pandoc 回写）。</summary>
        private static bool CanEditFile(string? path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return EditableTextExts.Contains(ext) || ext == ".docx";
        }

        private void OnToggleEditL(object sender, RoutedEventArgs e) => ToggleEditMode(_app.Left);
        private void OnToggleEditR(object sender, RoutedEventArgs e) => ToggleEditMode(_app.Right);

        private void ToggleEditMode(PanelState? state)
        {
            if (state == null || state.WebView?.CoreWebView2 == null) return;
            if (state.EditMode) { ExitEditMode(state); return; }
            EnterEditMode(state);
        }

        private void EnterEditMode(PanelState state)
        {
            try
            {
                if (string.IsNullOrEmpty(state.CurrentFile) || !File.Exists(state.CurrentFile))
                { StatusText.Text = "没有可编辑的文件"; return; }
                var fi = new FileInfo(state.CurrentFile);
                if (fi.Length > MaxEditBytes)
                { StatusText.Text = "文件超过 5MB 编辑上限，请用外部编辑器"; return; }

                string text;
                if (Path.GetExtension(state.CurrentFile).ToLowerInvariant() == ".docx")
                {
                    if (string.IsNullOrEmpty(state.DocxMarkdown))
                    { StatusText.Text = "文档尚未转换完成，请稍后再试"; return; }
                    text = state.DocxMarkdown!;
                    state.EditEncoding = 0;
                }
                else
                {
                    var bytes = File.ReadAllBytes(state.CurrentFile);
                    state.EditEncoding = DetectTextEncoding(bytes);
                    text = DecodeText(bytes, state.EditEncoding);
                }

                state.EditMode = true;
                state.EditSource = text;
                var isDark = _theme.Current == _theme.Dark;
                var autoSave = AppSettings.Get(AppSettings.AutoSaveKey, true);
                var autoDelay = AppSettings.Get(AppSettings.AutoSaveDelayKey, 10);
                state.WebView.NavigateToString(BuildEditPage(isDark, text, Path.GetExtension(state.CurrentFile), autoSave, autoDelay));
                StatusText.Text = autoSave
                    ? $"编辑模式：停顿 {autoDelay} 秒自动保存，Ctrl+S 手动保存，再点「编辑」返回预览"
                    : "编辑模式：Ctrl+S 保存，再点「编辑」返回预览";
                LogInfo("Edit mode: " + Path.GetFileName(state.CurrentFile));
            }
            catch (Exception ex)
            {
                LogErr("Enter edit: " + ex);
                StatusText.Text = "进入编辑失败: " + ex.Message;
            }
        }

        private async void ExitEditMode(PanelState state)
        {
            // 自动保存的 10s 计时未到或用户提前退出：先把未落盘的改动写盘，再回预览
            StatusText.Text = "已退出编辑，正在保存…";
            await FlushPendingEditAsync(state);
            state.EditMode = false;
            StatusText.Text = "已退出编辑，正在刷新预览…";
            _ = ReloadFileAsync(state, true, state.ResetCts());
        }

        private static string DecodeText(byte[] bytes, int enc) => enc switch
        {
            1 => Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3),
            2 => Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2),
            3 => Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2),
            4 => Encoding.GetEncoding("GB18030").GetString(bytes),
            _ => Encoding.UTF8.GetString(bytes)
        };

        private static int DetectTextEncoding(byte[] bytes)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return 1;
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return 2;
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return 3;
            var asUtf8 = Encoding.UTF8.GetString(bytes);
            if (asUtf8.IndexOf('\uFFFD') < 0) return 0;
            return 4;
        }

        private static Encoding SaveEncoding(int enc) => enc switch
        {
            1 => new UTF8Encoding(true),
            2 => Encoding.Unicode,
            3 => Encoding.BigEndianUnicode,
            4 => Encoding.GetEncoding("GB18030"),
            _ => new UTF8Encoding(false)
        };

        /// <summary>编辑页：主题化 textarea + Ctrl+S 保存 + 输入停顿自动保存（间隔 5/10/30s，postMessage edit-save 回传宿主）。</summary>
        private string BuildEditPage(bool isDark, string content, string ext, bool autoSave, int autoDelay)
        {
            var cls = isDark ? " class='dark'" : "";
            var esc = System.Security.SecurityElement.Escape(content);
            var extLabel = System.Security.SecurityElement.Escape(ext.TrimStart('.'));
            var autoHint = autoSave ? $" · 停顿 {autoDelay}s 自动保存" : "";
            var autoFlag = autoSave ? "true" : "false";
            var autoDelayMs = autoDelay * 1000;
            var nonce = RenderService.NewNonce();
            return $@"<!DOCTYPE html>
<html{cls}><head><meta charset='utf-8'/>
<meta name='viewport' content='width=device-width,initial-scale=1'/>
<meta name='referrer' content='no-referrer'/>
<meta http-equiv='Content-Security-Policy' content=""default-src 'self' https://appassets.example; script-src 'nonce-{nonce}' https://appassets.example; style-src 'unsafe-inline';"">
<style>
{RenderService.ThemeCss()}
  * {{ margin:0; padding:0; box-sizing:border-box; }}
  html,body {{ width:100%; height:100vh; background:var(--bg); color:var(--text); }}
  #bar {{ position:fixed; top:0; left:0; right:0; z-index:10; display:flex; align-items:center; gap:8px;
    padding:6px 12px; background:var(--card); border-bottom:1px solid var(--border); font-size:11px; color:var(--secondary); }}
  #bar b {{ color:var(--accent); }}
  kbd {{ border:1px solid var(--border); border-radius:4px; padding:0 5px; font-size:10px; font-family:inherit; }}
  #saveStatus {{ margin-left:auto; color:var(--accent); opacity:0; transition:opacity .3s; }}
  #dirty {{ display:none; color:#e5484d; font-weight:600; }}
  #ed {{ position:absolute; top:32px; left:0; right:0; bottom:0; width:100%; height:calc(100vh - 32px);
    border:none; outline:none; resize:none; background:var(--bg); color:var(--text);
    font-family:'Consolas','JetBrains Mono','Microsoft YaHei',monospace; font-size:13px; line-height:1.55;
    padding:14px 18px 40px; tab-size:4; white-space:pre; overflow:auto; }}
  ::-webkit-scrollbar {{ width:8px; height:8px; }}
  ::-webkit-scrollbar-track {{ background:transparent; }}
  ::-webkit-scrollbar-thumb {{ background:var(--border); border-radius:4px; }}
</style>
</head><body>
<div id='bar'>✏️ 编辑 <b>{extLabel}</b> · <kbd>Ctrl</kbd>+<kbd>S</kbd> 保存<span id='autoHint'>{autoHint}</span> · 再点标题栏「编辑」返回预览<span id='dirty'>● 未保存</span><span id='saveStatus'></span></div>
<textarea id='ed' spellcheck='false' autofocus>{esc}</textarea>
<script nonce='{nonce}'>
(function(){{
  var ed = document.getElementById('ed');
  var st = document.getElementById('saveStatus');
  var dirty = document.getElementById('dirty');
  var autoSave = {autoFlag};
  var autoDelayMs = {autoDelayMs}; // 可被宿主 __setAutoSave 实时更新（设置改动即时生效）
  var timer = null, flashTimer = null;
  var lastSaved = ed.value; // 最近一次已提交保存的内容快照，用于判断是否有未落盘改动
  // 顶栏自动保存提示文本：开关/间隔变更时实时刷新，不重建页面不丢内容
  function updateAutoHint(){{
    var h = document.getElementById('autoHint');
    h.textContent = autoSave ? ' · 停顿 ' + Math.round(autoDelayMs / 1000) + 's 自动保存' : '';
  }}
  window.__setAutoSave = function(enabled, delayMs){{
    autoSave = !!enabled;
    if(delayMs > 0) autoDelayMs = delayMs;
    updateAutoHint();
  }};
  // 未保存指示：内容与已落盘快照不一致时亮起「● 未保存」，落盘后熄灭
  function updateDirty(){{
    dirty.style.display = (ed.value !== lastSaved) ? 'inline' : 'none';
  }}
  function flash(msg){{
    st.textContent = msg; st.style.opacity = '1';
    clearTimeout(flashTimer);
    flashTimer = setTimeout(function(){{ st.style.opacity = '0'; }}, 2000);
  }}
  function doSave(){{
    lastSaved = ed.value;
    updateDirty();
    try {{ window.chrome.webview.postMessage(JSON.stringify({{kind:'edit-save', text: ed.value}})); }} catch(err){{}}
  }}
  // 自动保存：每次 input 重置计时，停止输入 autoDelayMs 毫秒后自动落盘
  function armAuto(){{
    if(!autoSave) return;
    clearTimeout(timer);
    timer = setTimeout(function(){{ timer = null; doSave(); flash('✓ 已自动保存'); }}, autoDelayMs);
  }}
  ed.addEventListener('input', function(){{ updateDirty(); armAuto(); }});
  ed.addEventListener('keydown', function(e){{
    if((e.ctrlKey || e.metaKey) && (e.key === 's' || e.key === 'S')){{
      e.preventDefault();
      clearTimeout(timer);
      doSave();
      flash('✓ 已保存');
    }}
  }});
  // 切页/关闭兜底：未落盘的改动一并回传宿主保存，避免退出编辑丢字
  window.addEventListener('beforeunload', function(){{
    if(ed.value !== lastSaved){{
      try {{ window.chrome.webview.postMessage(JSON.stringify({{kind:'edit-save', text: ed.value}})); }} catch(err){{}}
    }}
  }});
  ed.focus();
}})();
</script>
</body></html>";
        }

        private async Task SaveEditAsync(PanelState state, string text)
        {
            if (!state.EditMode) return;
            if (string.IsNullOrEmpty(state.CurrentFile)) return;
            state.EditSource = text ?? "";
            var name = Path.GetFileName(state.CurrentFile);
            try
            {
                if (Path.GetExtension(state.CurrentFile).ToLowerInvariant() == ".docx")
                    await SaveEditAsDocxAsync(state, state.EditSource);
                else
                    SaveEditAsText(state, state.EditSource);
                StatusText.Text = "已保存: " + name;
                LogInfo("Edit saved: " + name);
            }
            catch (Exception ex)
            {
                LogErr("Edit save: " + ex);
                StatusText.Text = "保存失败: " + ex.Message;
            }
        }

        /// <summary>编辑页 Ctrl+S / 自动保存消息入口（async void 桥接）。</summary>
        private async void SaveEdit(PanelState state, string text) => await SaveEditAsync(state, text);

        /// <summary>
        /// 退出编辑/切换文件前把未落盘的改动写盘：自动保存的 10s 计时未到或用户提前退出时，
        /// 从编辑页读取最新内容，与上次已保存内容不一致才落盘（避免无谓写入触发刷新）。
        /// </summary>
        private async Task FlushPendingEditAsync(PanelState state)
        {
            if (!state.EditMode) return;
            if (state.WebView?.CoreWebView2 == null) return;
            if (string.IsNullOrEmpty(state.CurrentFile)) return;
            try
            {
                var raw = await state.WebView.CoreWebView2.ExecuteScriptAsync(
                    "document.getElementById('ed') ? JSON.stringify(document.getElementById('ed').value) : ''");
                if (string.IsNullOrEmpty(raw)) return;
                var text = System.Text.Json.JsonSerializer.Deserialize<string>(raw);
                if (text == null || text == state.EditSource) return; // 无新改动，跳过写入
                await SaveEditAsync(state, text);
            }
            catch (Exception ex) { LogErr("Flush edit: " + ex.Message); }
        }

        private void SaveEditAsText(PanelState state, string text)
        {
            File.WriteAllText(state.CurrentFile!, text, SaveEncoding(state.EditEncoding));
            MarkLoaded(state); // 更新时间戳/大小，让内容感知防抖与后续刷新正确
        }

        /// <summary>docx 保存：编辑的 Markdown 经 pandoc 回写原 docx（临时文件 + 原子替换 + .bak 备份）。</summary>
        private async Task SaveEditAsDocxAsync(PanelState state, string text)
        {
            var dir = Path.GetDirectoryName(state.CurrentFile) ?? ".";
            var name = Path.GetFileName(state.CurrentFile);
            var guid = Guid.NewGuid().ToString("N");
            var tempMd = Path.Combine(dir, "." + name + ".seeme-" + guid + ".md");
            var tempDocx = Path.Combine(dir, "." + name + ".seeme-" + guid + ".docx");
            var backup = Path.Combine(dir, name + ".bak");
            try
            {
                File.WriteAllText(tempMd, text, new UTF8Encoding(false));
                var (ok, msg) = await _pandoc.ExportAsync(tempMd, tempDocx, "docx");
                if (!ok)
                {
                    StatusText.Text = "docx 回写失败: " + msg;
                    LogErr("AnyDoc docx write-back: " + msg);
                    return;
                }
                var tmp = new FileInfo(tempDocx);
                if (!tmp.Exists || tmp.Length == 0)
                {
                    StatusText.Text = "docx 回写失败: pandoc 输出为空";
                    return;
                }
                // 原子替换 + 自动 .bak 备份：转换成功才动原文件，失败绝不破坏原 docx。
                // 暂停两侧 watcher：File.Replace 的改名/备份事件会把面板 CurrentFile 误指到 .bak（
                // 曾观察到 Reload cancelled (md) 泄漏），暂停期间保存不触发任何误刷新。
                var wl = _app.Left?.Watcher;
                var wr = _app.Right?.Watcher;
                try
                {
                    if (wl != null) wl.EnableRaisingEvents = false;
                    if (wr != null) wr.EnableRaisingEvents = false;
                    File.Replace(tempDocx, state.CurrentFile!, backup);
                }
                finally
                {
                    if (wl != null) wl.EnableRaisingEvents = true;
                    if (wr != null) wr.EnableRaisingEvents = true;
                }
                state.DocxMarkdown = text; // 保留最新内容，再次进入编辑不回退
                MarkLoaded(state);
            }
            finally
            {
                try { if (File.Exists(tempMd)) File.Delete(tempMd); } catch { }
                try { if (File.Exists(tempDocx)) File.Delete(tempDocx); } catch { }
            }
        }

        /// <summary>渲染主题一致的错误页（红色错误提示 + 主题化背景），避免转换/读取异常导致白屏。</summary>

        private void RefreshRecentFilesList()
        {
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
                        return new RecentItem
                        {
                            Display = (_bookmarks.Contains(p) ? "📌 " : "") + shortName,
                            Full = name
                        };
                    }
                    catch { return new RecentItem { Display = p, Full = p }; }
                }).ToList();
                // Tag 必须与显示顺序一致（书签置顶改变了索引），否则 GetSelectedRecentPath 取错文件
                RecentFilesList.Tag = ordered;
            }
            catch (Exception ex) { LogErr("Refresh list: " + ex.Message); }
        }

        /// <summary>
        /// 内容搜索（搜索框输入 ">关键词"）：后台线程扫描历史文件内容（限量 256KB/文件），
        /// 命中即列出。避免大文件/大量文件在 UI 线程同步读取卡顿。
        /// </summary>
        private async void RefreshByContent(string keyword)
        {
            try
            {
                if (string.IsNullOrEmpty(keyword)) { RefreshRecentFilesList(); return; }
                var kw = keyword.ToLowerInvariant();
                // pdfStats: [0]=扫描版 PDF（无文本层） [1]=未提取文本的 PDF
                var pdfStats = new int[2];
                var hits = await Task.Run(() =>
                {
                    var results = new List<string>();
                    foreach (var p in _app.History.Entries)
                    {
                        if (string.IsNullOrEmpty(p) || !File.Exists(p)) continue;
                        try
                        {
                            if (FileTypes.IsPdf(Path.GetExtension(p)))
                            {
                                // PDF：只搜索已提取的文本层缓存（打开过一次即提取落盘）
                                if (PdfTextCache.TryRead(p, out var pdfText, out var hasText))
                                {
                                    if (hasText && !string.IsNullOrEmpty(pdfText)
                                        && pdfText.ToLowerInvariant().Contains(kw))
                                        results.Add(p);
                                    else if (!hasText) pdfStats[0]++;
                                }
                                else pdfStats[1]++;
                                continue;
                            }
                            var fi = new FileInfo(p);
                            if (fi.Length > 256 * 1024) continue; // 限量
                            var content = File.ReadAllText(p);
                            if (content.ToLowerInvariant().Contains(kw))
                                results.Add(p);
                        }
                        catch { }
                    }
                    return results;
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


        private void OnSearchFilter(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            try
            {
                _searchFilter = (SearchBox.Text ?? "").Trim();
                // 空输入时显示占位提示
                if (SearchHint != null)
                    SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text)
                        ? Visibility.Visible : Visibility.Collapsed;
                RefreshRecentFilesList();
            }
            catch { }
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

        /// <summary>最近文件列表项：Display=显示名（可缩短），Full=完整文件名（tooltip）。</summary>
        private sealed class RecentItem
        {
            public string Display { get; set; } = "";
            public string Full { get; set; } = "";
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
                target = _activePanel ?? _app.Left;
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
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
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

            win.Content = panel;
            win.ShowDialog();
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
            if (!_app.IsSplitMode) SetSplitMode(true);
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
                    // Magic Bytes 校验：验证文件头与扩展名一致
                    var header = new byte[8];
                    try
                    {
                        using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        if (fs.Read(header, 0, 8) < 4) return false;
                    }
                    catch { return false; }
                    return ext switch
                    {
                        ".pdf" => header[0] == 0x25 && header[1] == 0x50 && header[2] == 0x44 && header[3] == 0x46,  // %PDF
                        ".docx" or ".docm" or ".xlsx" or ".xlsm" or ".pptx" or ".odt" or ".ods" or ".odp" or ".epub" =>
                            header[0] == 0x50 && header[1] == 0x4B && header[2] == 0x03 && header[3] == 0x04, // ZIP/PK
                        ".doc" or ".xls" or ".ppt" =>
                            header[0] == 0xD0 && header[1] == 0xCF && header[2] == 0x11 && header[3] == 0xE0, // OLE2
                        ".rtf" =>
                            header[0] == 0x7B && header[1] == 0x5C && header[2] == 0x72 && header[3] == 0x74 && header[4] == 0x66, // {\rtf
                        _ => true // .md / .csv 等纯文本跳过
                    };
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
            var target = _activePanel ?? _app.Left;
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
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
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

            win.Content = panel;
            win.ShowDialog();
            return result;
        }


    }
}
