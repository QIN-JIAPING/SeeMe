// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

// MainWindow 分部类 —— 内联编辑
//
// 承载「文本类 / docx 文件在页面内直接编辑并保存」的完整链路：
//   CanEditFile / EditableTextExts   准入判断（哪些扩展名可编辑）
//   ToggleEditMode / EnterEditMode / ExitEditMode  编辑模式进出
//   BuildEditPage                    编辑页 HTML 构建（textarea + 自动保存脚本）
//   SaveEditAsync / FlushPendingEditAsync          保存与防抖落盘
//   SaveEditAsText / SaveEditAsDocxAsync           写回磁盘（docx 走 pandoc）
//
// 设计要点：docx 的编辑不是"原地改二进制"，而是 pandoc docx→md 转出为文本编辑，
// 保存时再 md→docx 回写（见 SaveEditAsDocxAsync），因此它复用文本编辑的 UI，
// 但多了临时文件与格式保真度的取舍。
//
// 依赖的 _app / _render / _pandoc 等字段与 OpenFileInternal / LogErr 等私有方法
// 均定义于本 partial 类的其他文件（主要是 MainWindow.Files.cs），跨文件可见。

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace SeeMe
{
    public partial class MainWindow
    {
        // ═══════════════ 内联编辑（文本类 + docx pandoc 回写） ═══════════════

        private static readonly HashSet<string> EditableTextExts = new(StringComparer.OrdinalIgnoreCase)
        {
            ".md", ".markdown", ".txt", ".html", ".htm", ".css", ".js", ".mjs", ".json", ".csv"
        };

        /// <summary>可编辑文件：文本类扩展名 或 docx（docx 走 Markdown 编辑 + pandoc 回写）。</summary>
        private static bool CanEditFile(string? path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return EditableTextExts.Contains(ext) || ext == ".docx";
        }

        private void OnToggleEditL(object sender, RoutedEventArgs e) => ToggleEditMode(_app.Left);
        private void OnToggleEditR(object sender, RoutedEventArgs e) => ToggleEditMode(_app.Right);

        /// <summary>编辑切换进行中标志：防止快速多次点击「编辑」导致 Exit/Enter 并发读写文件（曾致内容乱码）。</summary>
        private bool _editBusy;

        private void ToggleEditMode(PanelState? state)
        {
            if (state == null || state.WebView?.CoreWebView2 == null) return;
            if (_editBusy) { StatusText.Text = "正在保存编辑内容，请稍候…"; return; }
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
                if (fi.Length > Limits.MaxEditBytes)
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
                    state.EditEncoding = TextEncoding.Detect(bytes);
                    text = TextEncoding.Decode(bytes, state.EditEncoding);
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

        private void ExitEditMode(PanelState state)
        {
            if (_editBusy) return;
            _editBusy = true;
            StatusText.Text = "已退出编辑，正在保存…";
            _ = ExitEditCoreAsync(state);
        }

        /// <summary>退出编辑核心：刷新未落盘改动 → 退出标记 → 刷新预览。串行执行，防止并发读写文件。</summary>
        private async Task ExitEditCoreAsync(PanelState state)
        {
            try
            {
                await FlushPendingEditAsync(state);
                state.EditMode = false;
                StatusText.Text = "已退出编辑，正在刷新预览…";
                _ = ReloadFileAsync(state, true, state.ResetCts());
            }
            catch (Exception ex) { LogErr("Exit edit: " + ex.Message); }
            finally { _editBusy = false; }
        }

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
<meta http-equiv='Content-Security-Policy' content=""default-src 'self' https://appassets.example; script-src 'nonce-{nonce}' https://appassets.example; style-src 'unsafe-inline'; base-uri 'self'; form-action 'none';"">
<style>
{RenderService.ThemeCss()}
{RenderService.PageResetCss}
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
                // 注意：不要用 JSON.stringify(value)——ExecuteScriptAsync 会把 JS 返回值再序列化一次，
                // 双重序列化会把换行/引号转义成字面 \n / \"，写盘后文件内容被污染（多次编辑逐层叠加乱码）。
                // 直接返回原始 value，由 ExecuteScriptAsync 序列化 + C# 反序列化还原原文。
                var raw = await state.WebView.CoreWebView2.ExecuteScriptAsync(
                    "document.getElementById('ed') ? document.getElementById('ed').value : ''");
                if (string.IsNullOrEmpty(raw)) return;
                var text = System.Text.Json.JsonSerializer.Deserialize<string>(raw);
                if (text == null || text == state.EditSource) return; // 无新改动，跳过写入
                await SaveEditAsync(state, text);
            }
            catch (Exception ex) { LogErr("Flush edit: " + ex.Message); }
        }

        private void SaveEditAsText(PanelState state, string text)
        {
            File.WriteAllText(state.CurrentFile!, text, TextEncoding.GetEncoding(state.EditEncoding));
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
    }
}
