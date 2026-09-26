// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace SeeMe
{
    public partial class MainWindow
    {
        // ═══════ 文件职责：WebView2 → 宿主消息入口（所有页面回传的解析与分发） ═══════
        // 由 MainWindow.xaml.cs 拆出（2026-09-10）。两个入口：
        // · OnWebMessage(state, e) —— 可见面板消息：pdf-read-log / pdf-outline / scroll /
        //   drop / edit-save / anydoc-result / link。含来源校验（Source == CoreWebView2.Source）。
        // · OnExtractMessage(e)    —— 隐藏 ExtractView 消息：PDF 文本层提取结果按令牌匹配面板。
        // 上游挂接点在 MainWindow.xaml.cs 的 OnLoaded（WebMessageReceived）。

        private void OnWebMessage(PanelState state, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string? raw = null;
            try
            {
                raw = e.TryGetWebMessageAsString();
                if (string.IsNullOrEmpty(raw)) return;
                using var doc = System.Text.Json.JsonDocument.Parse(raw);
                var root = doc.RootElement;
                if (!root.TryGetProperty("kind", out var kindEl)) return;
                var kind = kindEl.GetString();
                if (kind == "pdf-read-log")
                {
                    // PDF 图文重建诊断日志：仅 Debug 构建记录（内容级日志含文本片段，
                    // Release 不落盘，避免敏感 PDF 内容经日志泄露）。
#if DEBUG
                    var msg = root.TryGetProperty("msg", out var mEl) ? mEl.GetString() : "";
                    if (!string.IsNullOrEmpty(msg)) LogInfo("[PDF-READ] " + msg);
#endif
                    return;
                }
                if (kind == "pdf-outline")
                {
                    // PDF 文本视图大纲：重建脚本完成时上报书签/标题/页标记；令牌校验防过期页面覆盖
                    var tok = root.TryGetProperty("token", out var tokEl) ? tokEl.GetString() : "";
                    if (string.IsNullOrEmpty(tok) || tok != state.PdfOutlineToken) return;
                    if (!root.TryGetProperty("items", out var itemsEl)) return;
                    var list = new List<TocItem>();
                    foreach (var itEl in itemsEl.EnumerateArray())
                    {
                        var title = itEl.TryGetProperty("t", out var tEl) ? tEl.GetString() ?? "" : "";
                        var level = itEl.TryGetProperty("l", out var lEl) ? lEl.GetInt32() : 1;
                        var id = itEl.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                        if (!string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(id))
                            list.Add(new TocItem { Title = title, Level = Math.Max(1, Math.Min(6, level)), Id = id });
                    }
                    SetOutline(list.Count > 0 ? list : null);
                    return;
                }
                // 来源校验：仅接受来自当前 WebView 已加载页面的消息，防止伪造 postMessage 干扰 UI 或触发外链。
                // 任一来源为空（导航早期窗口）一律拒绝——宁可丢消息，不可放行来源不明的伪造消息。
                var webSrc = state.WebView?.CoreWebView2?.Source;
                if (string.IsNullOrEmpty(e.Source) || string.IsNullOrEmpty(webSrc) || e.Source != webSrc)
                    return;
                if (kind == "scroll" && root.TryGetProperty("y", out var yEl))
                {
                    state.LastScrollY = yEl.GetDouble();
                    // 阅读百分比（新增字段，旧页面无此字段时 TryGetProperty 返回 false，保持 0 不动）
                    if (root.TryGetProperty("p", out var pEl) && pEl.ValueKind == System.Text.Json.JsonValueKind.Number)
                    {
                        var pct = pEl.GetDouble();
                        if (!double.IsNaN(pct)) state.LastReadPercent = Math.Max(0, Math.Min(100, pct));
                    }
                    // 当前可见标题 id —— 大纲高亮跟随（浮动窗与 Tab 共用同一选中态）
                    if (root.TryGetProperty("h", out var hEl) && hEl.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        var hid = hEl.GetString();
                        if (!string.IsNullOrEmpty(hid) && hid != state.CurrentHeadingId)
                        {
                            state.CurrentHeadingId = hid;
                            // 从大纲跳转触发的滚动不回打选中态（防回环，详见 _isNavigatingFromOutline）
                            if (!_isNavigatingFromOutline) HighlightOutlineItem(hid);
                        }
                    }
                    // 鼠标归属：页面侧上报 m=true 表示滚轮/鼠标位于本栏。
                    // 双栏下用它把「正在操作的栏」定为活动栏 —— 悬停即生效，无需先点一下。
                    // 仅在 m 为 true 时切换（false 不反向切换，否则鼠标移到工具栏时会清空活动栏）。
                    if (root.TryGetProperty("m", out var mEl)
                        && mEl.ValueKind == System.Text.Json.JsonValueKind.True
                        && _app.IsSplitMode
                        && _activePanel != state)
                    {
                        _activePanel = state;
                        UpdateActivePanelVisual();
                    }
                    // 🔴 双栏「滚动同步」已于 2026-09-25 移除：两栏是独立 WebView2，本就各滚各的。
                    // 原先的同步（任一侧 scroll → 另一侧 window.scrollTo 到同位置）会让下滑时两栏一起动，
                    // 违背「只滚鼠标所在那一栏」的预期。如需恢复，务必先加用户可见开关，不要默认开启。
                    // 另：页面侧已在滚轮事件上报 `m`（鼠标是否在本栏），供宿主判定归属，见下方 mouse 分支。
                }
                else if (kind == "drop" && root.TryGetProperty("paths", out var pathsEl)
                         && pathsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    // WebView 页面内拖入文件（File.path 经 postMessage 传回），与窗口级 WPF 拖放共用打开逻辑
                    var paths = new List<string>();
                    foreach (var item in pathsEl.EnumerateArray())
                    {
                        var p = item.GetString();
                        if (!string.IsNullOrEmpty(p)) paths.Add(p);
                    }
                    if (paths.Count > 0)
                    {
                        var total = paths.Count;
                        Dispatcher.BeginInvoke(new Action(() =>
                            OpenDroppedFiles(FilterSupportedFiles(paths), total)));
                    }
                }
                else if (kind == "edit-save" && root.TryGetProperty("text", out var textEl))
                {
                    // 编辑页 Ctrl+S：把 textarea 内容写回文件（文本原样写；docx 经 pandoc 回写）
                    SaveEdit(state, textEl.GetString() ?? "");
                    return;
                }
                else if (kind == "anydoc-result" && root.TryGetProperty("id", out var anyIdEl))
                {
                    // anydoc-wasm 转换结果回传：令牌 + 源文件双重校验，过期/跨文件/伪造消息一律忽略
                    var id = anyIdEl.GetString();
                    var pending = state.AnyDocPending;
                    if (pending == null || pending.Token != id || pending.File != state.CurrentFile)
                        return;
                    state.AnyDocPending = null;
                    var ok = root.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
                    if (pending.PdfExtract)
                    {
                        // PDF 文本层提取模式：结果只写缓存/标记，不渲染（RenderPdf 继续显示 PDF.js）
                        if (ok && root.TryGetProperty("markdown", out var pdfMdEl)
                            && !string.IsNullOrEmpty(pdfMdEl.GetString()))
                        {
                            var pdfMd = pdfMdEl.GetString()!;
                            state.PdfText = pdfMd;
                            state.PdfTextLayer = true;
                            PdfTextCache.Write(state.CurrentFile, pdfMd);
                            LogInfo("PDF text layer: " + Path.GetFileName(state.CurrentFile) + " (" + pdfMd.Length + " chars)");
                        }
                        else
                        {
                            state.PdfText = "";
                            state.PdfTextLayer = false;
                            PdfTextCache.WriteNoText(state.CurrentFile);
                            LogInfo("PDF scan (no text layer): " + Path.GetFileName(state.CurrentFile));
                        }
                        pending.ExtractTcs?.TrySetResult(true);
                        _ = Dispatcher.BeginInvoke(new Action(() => RefreshStats(state))); // 提取完成 → 统计卡刷新
                        return;
                    }
                    if (ok && root.TryGetProperty("markdown", out var mdEl)
                        && !string.IsNullOrEmpty(mdEl.GetString()))
                    {
                        var md = mdEl.GetString();
                        Dispatcher.BeginInvoke(new Action(async () => await RenderAnyDocMarkdownAsync(state, md!)));
                    }
                    else
                    {
                        if (root.TryGetProperty("error", out var errEl) && !string.IsNullOrEmpty(errEl.GetString()))
                            LogErr("AnyDoc convert: " + errEl.GetString());
                        Dispatcher.BeginInvoke(new Action(async () => await FallbackOfficeAsync(state, pending.Ext)));
                    }
                    return;
                }
                else if (kind == "chart-export")
                {
                    // 图表导出数据回传：粘贴令牌后交给 MainWindow.Rendering.Chart.cs 落盘
                    _ = HandleChartExportAsync(root);
                }
                else if (kind == "chart-export-error")
                {
                    _chartExport = null;
                    var msg = root.TryGetProperty("msg", out var ceEl) ? ceEl.GetString() : "";
                    StatusText.Text = "导出图表失败: " + (string.IsNullOrEmpty(msg) ? "页面未返回原因" : msg);
                    if (!string.IsNullOrEmpty(msg)) LogErr("Chart export error: " + msg);
                }
                else if (kind == "link" && root.TryGetProperty("href", out var hrefEl))
                {
                    var href = hrefEl.GetString();
                    if (!string.IsNullOrEmpty(href))
                    {
                        // 纵深防御：显式拒绝危险伪协议（XSS 载体），即使 CSP 与 OpenInSystemBrowser 已拦截
                        var lower = href.ToLowerInvariant();
                        if (lower.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
                            || lower.StartsWith("vbscript:", StringComparison.OrdinalIgnoreCase))
                        {
                            LogErr("Blocked dangerous link scheme: " + lower);
                            return;
                        }
                        if (!(lower.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                             || lower.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                             || lower.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
                             || lower.StartsWith("#", StringComparison.Ordinal)))
                        {
                            OpenInSystemBrowser(href);
                        }
                    }
                }
                else if (kind == "dismiss-welcome")
                {
                    // 欢迎提示卡片"不再显示"：持久化开关 + 清空为透明背景页
                    _theme.SetWelcomeDismissed(true);
                    state.WebView?.NavigateToString(EmptyPage);
                }
                else if (kind == "search-result")
                {
                    // 页内搜索回报：更新打开中的搜索栏结果文案
                    var count = root.TryGetProperty("count", out var cEl) ? cEl.GetInt32() : 0;
                    var current = root.TryGetProperty("current", out var iEl) ? iEl.GetInt32() : 0;
                    StatusText.Text = count > 0 ? $"找到 {count} 处，当前第 {current} 处" : "未找到匹配内容";
                }
                else if (kind == "open-file")
                {
                    // 点击欢迎提示卡片 → 打开文件
                    _activePanel = state;
                    UpdateActivePanelVisual();
                    ShowOpenFor(state);
                }
                else if (kind == "highlight-add" && root.TryGetProperty("id", out var annIdEl) && root.TryGetProperty("text", out var annTextEl))
                {
                    // 高亮笔：JS 已把选区包裹为 <mark>，这里写入 HighlightStore（按文件路径持久化）
                    var id = annIdEl.GetString();
                    var text = annTextEl.GetString();
                    if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(text) || string.IsNullOrEmpty(state.CurrentFile)) return;
                    var ctx = root.TryGetProperty("context", out var ctxEl) ? ctxEl.GetString() ?? "" : "";
                    _highlights.Add(new HighlightItem { Id = id, File = state.CurrentFile, Text = text, Context = ctx });
                }
                else if (kind == "highlight-remove" && root.TryGetProperty("id", out var remIdEl))
                {
                    // 右键点击页面标注 → 删除（JS 已同步移除 mark）
                    var id = remIdEl.GetString();
                    if (!string.IsNullOrEmpty(id)) _highlights.Remove(id);
                }
                else if (kind == "highlight-fail" && root.TryGetProperty("text", out var annFailEl))
                {
                    // 高亮笔选区跨格式边界（strong/em/链接等）时 DOM 无法直接包裹：
                    // 明确提示用户改选纯文本，避免"画了没反应/字消失"的假象
                    var t = annFailEl.GetString() ?? "";
                    if (t.Length > 40) t = t.Substring(0, 40) + "…";
                    StatusText.Text = "标注失败：选区跨了格式边界（粗体/斜体/链接），请整段只选普通文本。";
                }
                else if (kind == "highlight-click")
                {
                    // 点击页面高亮标注 → 打开右侧面板查看/管理（未开才开，避免打断阅读）
                    if (NotesPanel != null && NotesPanel.Visibility != Visibility.Visible)
                        SetNotesPanelVisibility(true);
                }
                else if (kind == "palette-toggle")
                {
                    // WebView 内键盘桥接（Ctrl+Shift+P）→ 切换命令面板，绕开 Chromium/WPF 焦点吞键
                    ToggleCommandPalette();
                }
                else if (kind == "copy-text" && root.TryGetProperty("text", out var copyEl)
                         && copyEl.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    // 代码块复制（行级 / 整块）：页面侧取 textContent 回传，宿主执行剪贴板写入。
                    // 不直接在页面调 navigator.clipboard —— 见 RenderService.CodeBlockScript 注释。
                    var txt = copyEl.GetString();
                    if (string.IsNullOrEmpty(txt)) return;
                    // 上限防护：单次复制超过 1MB 的文本一律拒绝，避免一次误点整块超大代码
                    // 就把剪贴板与内存顶穿（阅读器场景下没人真需要复制 1MB 代码）。
                    if (txt.Length > Limits.MaxClipboardChars)
                    {
                        StatusText.Text = "复制内容过大，已取消（超过 " + (Limits.MaxClipboardChars / 1024) + "K 字符）";
                        LogWarn("copy-text rejected: " + txt.Length + " chars");
                        return;
                    }
                    try
                    {
                        System.Windows.Clipboard.SetText(txt);
                        var lines = 1;
                        foreach (var ch in txt) { if (ch == '\n') lines++; }
                        StatusText.Text = lines > 1 ? $"已复制 {lines} 行代码" : "已复制代码";
                    }
                    catch (Exception ex)
                    {
                        // 剪贴板被其他进程独占（CLIPBRD_E_CANT_OPEN）是常见瞬时失败，提示重试即可
                        StatusText.Text = "复制失败: " + ex.Message;
                        LogErr("copy-text failed: " + ex.Message);
                    }
                }
            }
            catch (Exception ex) { LogErr("Web msg parse: " + ex.Message + " | rawLen=" + (raw?.Length ?? -1)); }
        }

        /// <summary>
        /// 隐藏提取 WebView 的消息入口：anydoc-result 按令牌匹配正在提取的面板（双栏并发安全），
        /// PDF 文本层结果只写缓存/标记，不渲染；完成后刷新统计卡与查看器徽标。
        /// </summary>
        private void OnExtractMessage(CoreWebView2WebMessageReceivedEventArgs e)
        {
            string? raw = null;
            try
            {
                raw = e.TryGetWebMessageAsString();
                if (string.IsNullOrEmpty(raw)) return;
                using var doc = System.Text.Json.JsonDocument.Parse(raw);
                var root = doc.RootElement;
                if (!root.TryGetProperty("kind", out var kindEl) || kindEl.GetString() != "anydoc-result") return;
                if (!root.TryGetProperty("id", out var idEl)) return;

                // 按令牌找到发起提取的面板（AnyDocPending 每面板单槽，双栏并发各自匹配）
                PanelState? state = null;
                foreach (var st in new[] { _app.Left, _app.Right })
                {
                    if (st?.AnyDocPending is { PdfExtract: true } p && p.Token == idEl.GetString())
                    { state = st; break; }
                }
                if (state == null) return;
                var pending = state.AnyDocPending;
                if (pending == null || pending.File != state.CurrentFile) return;
                state.AnyDocPending = null;

                var ok = root.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
                if (ok && root.TryGetProperty("markdown", out var mdEl) && !string.IsNullOrEmpty(mdEl.GetString()))
                {
                    var md = mdEl.GetString()!;
                    state.PdfText = md;
                    state.PdfTextLayer = true;
                    PdfTextCache.Write(state.CurrentFile, md);
                    LogInfo("PDF text layer: " + Path.GetFileName(state.CurrentFile) + " (" + md.Length + " chars)");
                }
                else if (ok)
                {
                    // anydoc 成功但无文本 → 真·扫描版：写 .no 标记（仅此类写入，避免污染缓存）
                    state.PdfText = "";
                    state.PdfTextLayer = false;
                    PdfTextCache.WriteNoText(state.CurrentFile);
                    LogInfo("PDF scan (no text layer): " + Path.GetFileName(state.CurrentFile));
                }
                else
                {
                    // 提取出错（worker/fetch/wasm 失败）：不写 .no——否则错误被当作"扫描版"永久缓存，
                    // 后续打开不再重试（曾致文本型 PDF 永远进不了文本视图）。
                    state.PdfText = "";
                    state.PdfTextLayer = null;
                    var err = root.TryGetProperty("error", out var errEl) ? errEl.GetString() : "";
                    LogErr("Pdf text extract failed: " + Path.GetFileName(state.CurrentFile)
                           + (string.IsNullOrEmpty(err) ? "" : " | " + err));
                }
                pending.ExtractTcs?.TrySetResult(true);
                // 查看器已先行显示：刷新统计卡 + 注入文本层/扫描版徽标
                _ = Dispatcher.BeginInvoke(new Action(() => RefreshStats(state)));
            }
            catch (Exception ex) { LogErr("Extract msg: " + ex.Message + " | raw=" + (raw ?? "<null>")); }
        }
    }
}
