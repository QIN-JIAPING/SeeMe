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

        /// <summary>编辑页：格式工具栏 + 查找替换 + 粘贴图片存盘 + Ctrl+S 保存 + 输入停顿自动保存。</summary>
        private string BuildEditPage(bool isDark, string content, string ext, bool autoSave, int autoDelay)
        {
            var cls = isDark ? " class='dark'" : "";
            var esc = System.Security.SecurityElement.Escape(content);
            var extLabel = System.Security.SecurityElement.Escape(ext.TrimStart('.'));
            var nonce = RenderService.NewNonce();
            // autoHint 由页面脚本在启动时算出（它要跟随设置实时刷新），故这里不再注入静态文本。

            // 脚本/样式/工具栏放在独立的 verbatim 常量里，经占位符替换后插入。
            //
            // 🔴 为什么不直接写在这个 $@"" 模板里：模板是**插值 verbatim** 字符串，
            // 其中 JS 的每一对 {} 都要写成 {{}}、每一个 " 都要写成 ""。
            // 本项目在 RenderService.cs 上正是因此（注释里两个裸 "）一次性报出 118 个编译错误。
            // 拆成非插值常量后，brace 与引号都是字面量，**不存在写漏的机会**。
            var script = EditPageScript
                .Replace(PlaceholderAutoSave, autoSave ? "true" : "false")
                .Replace(PlaceholderAutoDelayMs,
                    (autoDelay * 1000).ToString(System.Globalization.CultureInfo.InvariantCulture));
            // 扩展名同样走占位符：常量是非插值字符串，直接写 {extLabel} 只会原样输出字面量。
            var toolbar = EditPageToolbar.Replace(PlaceholderExtLabel, extLabel);

            return $@"<!DOCTYPE html>
<html{cls}><head><meta charset='utf-8'/>
<meta name='viewport' content='width=device-width,initial-scale=1'/>
<meta name='referrer' content='no-referrer'/>
<meta http-equiv='Content-Security-Policy' content=""default-src 'self' https://appassets.example; script-src 'nonce-{nonce}' https://appassets.example; style-src 'unsafe-inline'; base-uri 'self'; form-action 'none';"">
<style>
{RenderService.ThemeCss()}
{RenderService.PageResetCss}
{EditPageCss}
</style>
</head><body>
{toolbar}
<textarea id='ed' spellcheck='false' autofocus>{esc}</textarea>
<script nonce='{nonce}'>
{script}
</script>
</body></html>";
        }

        private const string PlaceholderAutoSave = "__SEEME_AUTOSAVE__";
        private const string PlaceholderAutoDelayMs = "__SEEME_AUTODELAYMS__";
        private const string PlaceholderExtLabel = "__SEEME_EXTLABEL__";

        /// <summary>
        /// 编辑页样式。**非插值 verbatim 常量**（单引号 + 单花括号，无需转义）。
        ///
        /// <para>布局改用 flex 列而非原来的 <c>position:fixed</c> + <c>top:32px</c> 魔法数字：
        /// 工具栏 + 查找栏 + 状态行的高度会随是否显示查找栏变化，写死的偏移量必然错位。</para>
        /// </summary>
        private const string EditPageCss = @"
  html,body { height:100vh; display:flex; flex-direction:column; background:var(--bg); color:var(--text); }
  #bar { flex:0 0 auto; background:var(--card); border-bottom:1px solid var(--border);
    font-size:11px; color:var(--secondary); }
  #tbr { display:flex; flex-wrap:wrap; align-items:center; gap:2px; padding:5px 10px;
    border-bottom:1px solid var(--border); }
  .tb { cursor:pointer; padding:2px 7px; border-radius:4px; font-size:11px; color:var(--text);
    user-select:none; white-space:nowrap; }
  .tb:hover { background:var(--row-hov); }
  .tsep { width:1px; height:14px; background:var(--border); margin:0 5px; }
  #findbar { display:none; align-items:center; gap:6px; padding:5px 10px;
    border-bottom:1px solid var(--border); font-size:11px; color:var(--secondary); }
  #findbar input { flex:0 0 148px; padding:3px 7px; font-size:11px; outline:none;
    border:1px solid var(--border); border-radius:4px; background:var(--bg); color:var(--text); }
  #fhint { color:var(--accent); }
  #status { display:flex; align-items:center; gap:6px; padding:5px 12px; font-size:11px; }
  #status b { color:var(--accent); }
  kbd { border:1px solid var(--border); border-radius:4px; padding:0 5px; font-size:10px; font-family:inherit; }
  #saveStatus { margin-left:auto; color:var(--accent); opacity:0; transition:opacity .3s; }
  #dirty { display:none; color:#e5484d; font-weight:600; }
  #ed { flex:1 1 auto; min-height:0; width:100%; border:none; outline:none; resize:none;
    background:var(--bg); color:var(--text);
    font-family:'Consolas','JetBrains Mono','Microsoft YaHei',monospace; font-size:13px; line-height:1.55;
    padding:14px 18px 40px; tab-size:4; white-space:pre; overflow:auto; }
  ::-webkit-scrollbar { width:8px; height:8px; }
  ::-webkit-scrollbar-track { background:transparent; }
  ::-webkit-scrollbar-thumb { background:var(--border); border-radius:4px; }";

        /// <summary>编辑页顶栏（格式工具栏 + 查找替换栏 + 状态行）。非插值 verbatim 常量。</summary>
        private const string EditPageToolbar = @"<div id='bar'>
<div id='tbr'>
  <span class='tb' data-act='bold' title='粗体 (Ctrl+B)'><b>B</b></span>
  <span class='tb' data-act='italic' title='斜体 (Ctrl+I)'><i>I</i></span>
  <span class='tb' data-act='strike' title='删除线'><s>S</s></span>
  <span class='tsep'></span>
  <span class='tb' data-act='h2' title='二级标题'>H2</span>
  <span class='tb' data-act='h3' title='三级标题'>H3</span>
  <span class='tsep'></span>
  <span class='tb' data-act='ul' title='无序列表'>• 列表</span>
  <span class='tb' data-act='ol' title='有序列表'>1. 列表</span>
  <span class='tb' data-act='quote' title='引用'>❝ 引用</span>
  <span class='tsep'></span>
  <span class='tb' data-act='code' title='行内代码'>`代码`</span>
  <span class='tb' data-act='codeblock' title='代码块'>▤ 代码块</span>
  <span class='tb' data-act='link' title='链接 (Ctrl+K)'>🔗 链接</span>
  <span class='tb' data-act='table' title='插入表格'>⊞ 表格</span>
  <span class='tb' data-act='hr' title='分割线'>— 分割线</span>
</div>
<div id='findbar'>
  <input id='fq' type='text' placeholder='查找'/>
  <input id='fr' type='text' placeholder='替换为'/>
  <span class='tb' id='fprev' title='上一个'>↑</span>
  <span class='tb' id='fnext' title='下一个'>↓</span>
  <span class='tb' id='frep' title='替换当前'>替换</span>
  <span class='tb' id='frepa' title='全部替换'>全部替换</span>
  <span class='tb' id='fclose' title='关闭 (Esc)'>✕</span>
  <span id='fhint'></span>
</div>
<div id='status'>✏️ 编辑 <b>__SEEME_EXTLABEL__</b> · <kbd>Ctrl</kbd>+<kbd>S</kbd> 保存<span id='autoHint'></span> · <kbd>Ctrl</kbd>+<kbd>F</kbd> 查找 · <kbd>Ctrl</kbd>+<kbd>B</kbd>/<kbd>I</kbd>/<kbd>K</kbd> 格式 · 粘贴图片自动存入 assets<span id='dirty'>● 未保存</span><span id='saveStatus'></span></div>
</div>";

        /// <summary>
        /// 编辑页脚本。**非插值 verbatim 常量**：三处动态值用占位符替换（见 <see cref="BuildEditPage"/>）。
        ///
        /// <para>这里只用单引号字符串、只写单花括号 —— 正因如此才不需要 `""` / `{{}}` 转义，
        /// 也就不存在"漏转义一个字符 → 上百个编译错误"的风险。</para>
        /// </summary>
        private const string EditPageScript = @"(function(){
  var ed = document.getElementById('ed');
  var st = document.getElementById('saveStatus');
  var dirty = document.getElementById('dirty');
  var autoSave = __SEEME_AUTOSAVE__;
  var autoDelayMs = __SEEME_AUTODELAYMS__; // 可被宿主 __setAutoSave 实时更新（设置改动即时生效）
  var timer = null, flashTimer = null;
  var lastSaved = ed.value; // 最近一次已提交保存的内容快照，用于判断是否有未落盘改动

  // ── 顶栏状态 ──
  function updateAutoHint(){
    var h = document.getElementById('autoHint');
    h.textContent = autoSave ? ' · 停顿 ' + Math.round(autoDelayMs / 1000) + 's 自动保存' : '';
  }
  window.__setAutoSave = function(enabled, delayMs){
    autoSave = !!enabled;
    if(delayMs > 0) autoDelayMs = delayMs;
    updateAutoHint();
  };
  function updateDirty(){ dirty.style.display = (ed.value !== lastSaved) ? 'inline' : 'none'; }
  function flash(msg){
    st.textContent = msg; st.style.opacity = '1';
    clearTimeout(flashTimer);
    flashTimer = setTimeout(function(){ st.style.opacity = '0'; }, 2000);
  }
  function doSave(){
    lastSaved = ed.value;
    updateDirty();
    try { window.chrome.webview.postMessage(JSON.stringify({kind:'edit-save', text: ed.value})); } catch(err){}
  }
  function armAuto(){
    if(!autoSave) return;
    clearTimeout(timer);
    timer = setTimeout(function(){ timer = null; doSave(); flash('✓ 已自动保存'); }, autoDelayMs);
  }
  ed.addEventListener('input', function(){ updateDirty(); armAuto(); });

  // ── 文本操作基元 ──
  // setRangeText 不会触发 input 事件，故手工补 updateDirty/armAuto。
  function replaceRange(start, end, text){
    ed.setRangeText(text, start, end, 'end');
    updateDirty(); armAuto(); ed.focus();
  }
  function wrapSel(before, after){
    var s = ed.selectionStart, e = ed.selectionEnd;
    var sel = ed.value.slice(s, e);
    replaceRange(s, e, before + sel + after);
    if(!sel){ var c = s + before.length; ed.setSelectionRange(c, c); }
  }
  function prefixLines(prefix, numbered){
    var s = ed.selectionStart, e = ed.selectionEnd;
    var ls = ed.value.lastIndexOf('\n', s - 1) + 1;
    var le = ed.value.indexOf('\n', e); if(le < 0) le = ed.value.length;
    var out = ed.value.slice(ls, le).split('\n').map(function(line, i){
      var stripped = line.replace(/^\s*(?:[-*+]|\d+\.)\s+/, '');
      return (numbered ? (i + 1) + '. ' : prefix) + stripped;
    }).join('\n');
    replaceRange(ls, le, out);
  }
  function insertBlock(text){
    var s = ed.selectionStart, e = ed.selectionEnd;
    var needsNl = s > 0 && ed.value.charAt(s - 1) !== '\n';
    replaceRange(s, e, (needsNl ? '\n\n' : '') + text + '\n');
  }
  window.__seemeInsertImage = function(rel, alt){
    insertBlock('![' + (alt || '') + '](' + rel + ')');
    flash('✓ 图片已保存到 ' + rel);
  };
  window.__seemePasteResult = function(msg){ flash(msg); };

  // ── 工具栏 ──
  var ACT = {
    bold:      function(){ wrapSel('**','**'); },
    italic:    function(){ wrapSel('*','*'); },
    strike:    function(){ wrapSel('~~','~~'); },
    h2:        function(){ prefixLines('## '); },
    h3:        function(){ prefixLines('### '); },
    ul:        function(){ prefixLines('- '); },
    ol:        function(){ prefixLines('', true); },
    quote:     function(){ prefixLines('> '); },
    code:      function(){ wrapSel('`','`'); },
    codeblock: function(){
      var s = ed.selectionStart, e = ed.selectionEnd;
      insertBlock('```\n' + ed.value.slice(s, e) + '\n```');
    },
    link: function(){
      var s = ed.selectionStart, e = ed.selectionEnd;
      var t = ed.value.slice(s, e) || '链接文字';
      replaceRange(s, e, '[' + t + '](url)');
    },
    table: function(){ insertBlock('| 列 1 | 列 2 |\n| --- | --- |\n|  |  |'); },
    hr:    function(){ insertBlock('---'); }
  };
  var tbr = document.getElementById('tbr');
  if(tbr) tbr.addEventListener('click', function(ev){
    var el = ev.target && ev.target.closest ? ev.target.closest('.tb') : null;
    if(!el) return;
    var fn = ACT[el.getAttribute('data-act')];
    if(fn) fn();
    ed.focus();
  });

  // ── 粘贴图片：页面只回传 data URL，落盘与相对路径插入由宿主完成 ──
  ed.addEventListener('paste', function(ev){
    var cd = ev.clipboardData;
    if(!cd || !cd.items) return;
    for(var i = 0; i < cd.items.length; i++){
      var it = cd.items[i];
      if(it.kind === 'file' && it.type && it.type.indexOf('image/') === 0){
        ev.preventDefault();
        var blob = it.getAsFile();
        if(!blob) return;
        var fr = new FileReader();
        fr.onload = function(){
          try {
            window.chrome.webview.postMessage(JSON.stringify({
              kind:'edit-paste-image', name: blob.name || 'pasted', dataUrl: fr.result }));
          } catch(err){ flash('图片粘贴失败'); }
        };
        fr.readAsDataURL(blob);
        return;
      }
    }
  });

  // ── 键盘 ──
  ed.addEventListener('keydown', function(e){
    var ctrl = e.ctrlKey || e.metaKey;
    if(!ctrl || e.shiftKey || e.altKey) return;
    var k = (e.key || '').toLowerCase();
    if(k === 's'){ e.preventDefault(); clearTimeout(timer); doSave(); flash('✓ 已保存'); }
    else if(k === 'b'){ e.preventDefault(); ACT.bold(); }
    else if(k === 'i'){ e.preventDefault(); ACT.italic(); }
    else if(k === 'k'){ e.preventDefault(); ACT.link(); }
    else if(k === 'f'){ e.preventDefault(); toggleFind(true); }
  });

  // ── 查找 / 替换（textarea 无法高亮，命中即以选中呈现并滚动到视口） ──
  var fb = document.getElementById('findbar');
  var fq = document.getElementById('fq');
  var fr = document.getElementById('fr');
  var fhint = document.getElementById('fhint');
  function toggleFind(show){
    fb.style.display = show ? 'flex' : 'none';
    if(show){ fq.focus(); fq.select(); }
  }
  function findNext(back){
    var q = fq.value;
    if(!q){ fhint.textContent = ''; return; }
    var text = ed.value;
    var from = back ? ed.selectionStart - 1 : ed.selectionEnd;
    var idx = back ? text.lastIndexOf(q, Math.max(0, from)) : text.indexOf(q, from);
    if(idx < 0) idx = back ? text.lastIndexOf(q) : text.indexOf(q); // 环绕到另一端
    if(idx < 0){ fhint.textContent = '无匹配'; return; }
    ed.focus();
    ed.setSelectionRange(idx, idx + q.length);
    fhint.textContent = '';
  }
  function replaceOne(){
    var q = fq.value;
    if(!q) return;
    if(ed.value.slice(ed.selectionStart, ed.selectionEnd) === q)
      replaceRange(ed.selectionStart, ed.selectionEnd, fr.value);
    findNext(false);
  }
  function replaceAll(){
    var q = fq.value;
    if(!q) return;
    var text = ed.value, out = '', i = 0, n = 0;
    for(;;){
      var j = text.indexOf(q, i);
      if(j < 0){ out += text.slice(i); break; }
      out += text.slice(i, j) + fr.value; n++; i = j + q.length;
    }
    if(n === 0){ fhint.textContent = '无匹配'; return; }
    ed.value = out;
    lastSaved = null;      // 让「● 未保存」亮起：替换没有走 doSave
    updateDirty(); armAuto();
    fhint.textContent = '已替换 ' + n + ' 处';
  }
  document.getElementById('fprev').addEventListener('click', function(){ findNext(true); });
  document.getElementById('fnext').addEventListener('click', function(){ findNext(false); });
  document.getElementById('frep').addEventListener('click', replaceOne);
  document.getElementById('frepa').addEventListener('click', replaceAll);
  document.getElementById('fclose').addEventListener('click', function(){ toggleFind(false); ed.focus(); });
  fq.addEventListener('keydown', function(e){
    if(e.key === 'Enter'){ e.preventDefault(); findNext(e.shiftKey); }
    else if(e.key === 'Escape'){ e.preventDefault(); toggleFind(false); ed.focus(); }
  });
  fr.addEventListener('keydown', function(e){
    if(e.key === 'Enter'){ e.preventDefault(); replaceOne(); }
    else if(e.key === 'Escape'){ e.preventDefault(); toggleFind(false); ed.focus(); }
  });

  // 切页/关闭兜底：未落盘的改动一并回传宿主保存，避免退出编辑丢字
  window.addEventListener('beforeunload', function(){
    if(ed.value !== lastSaved){
      try { window.chrome.webview.postMessage(JSON.stringify({kind:'edit-save', text: ed.value})); } catch(err){}
    }
  });

  updateAutoHint();
  ed.focus();
})();";

        /// <summary>
        /// 编辑页粘贴图片：把页面回传的 data URL 落到文档同级的 <c>assets\</c>，再让页面插入相对路径。
        ///
        /// <para><b>为什么落盘在宿主侧</b>：页面没有文件系统权限（也不该有）。
        /// 相对路径（而非绝对路径）是刻意的 —— 文档连同 <c>assets\</c> 一起移动/提交到仓库后仍然可用。</para>
        /// </summary>
        private async Task HandlePastedImageAsync(PanelState? state, string? name, string? dataUrl)
        {
            if (state?.WebView?.CoreWebView2 == null) return;
            if (string.IsNullOrEmpty(state.CurrentFile)) return;

            try
            {
                var (bytes, ext) = PastedImageCodec.Decode(dataUrl, Limits.MaxPastedImageBytes);
                if (bytes == null)
                {
                    await PostEditPageCallAsync(state,
                        "window.__seemePasteResult && window.__seemePasteResult('图片无法识别或超过 "
                        + (Limits.MaxPastedImageBytes / 1048576) + "MB 上限')");
                    return;
                }

                // 内容必须与自称的类型一致：MIME 只是页面**自称**，载荷可以是任意字节。
                // 与仓库既有的 MagicBytes 口径一致，防止经此路径落盘"扩展名伪造"的文件。
                if (!PastedImageCodec.ContentMatchesExt(ext, bytes))
                {
                    LogWarn("Paste image rejected: content/ext mismatch " + ext);
                    await PostEditPageCallAsync(state,
                        "window.__seemePasteResult && window.__seemePasteResult('图片内容与其类型不符，已拒绝保存')");
                    return;
                }

                var docDir = Path.GetDirectoryName(state.CurrentFile!) ?? ".";
                var assetsDir = Path.Combine(docDir, "assets");
                Directory.CreateDirectory(assetsDir);

                // 名字净化在 PastedImageCodec 里（public 纯函数，可被测试覆盖）——
                // 它同样是不可信的页面输入，不适合留在本文件的 private 方法里。
                var fileName = PastedImageCodec.BuildFileName(name, ext);
                await File.WriteAllBytesAsync(Path.Combine(assetsDir, fileName), bytes);

                var rel = "assets/" + fileName;
                var alt = Path.GetFileNameWithoutExtension(fileName);
                await PostEditPageCallAsync(state,
                    "window.__seemeInsertImage && window.__seemeInsertImage("
                    + System.Text.Json.JsonSerializer.Serialize(rel) + ","
                    + System.Text.Json.JsonSerializer.Serialize(alt) + ")");
                LogInfo("Pasted image saved: " + rel);
            }
            catch (Exception ex)
            {
                LogErr("Paste image: " + ex.Message);
                try
                {
                    await PostEditPageCallAsync(state,
                        "window.__seemePasteResult && window.__seemePasteResult('图片保存失败')");
                }
                catch { }
            }
        }

        /// <summary>在编辑页执行一段脚本（失败仅记录，不影响编辑）。</summary>
        private async Task PostEditPageCallAsync(PanelState state, string js)
        {
            try
            {
                if (state.WebView?.CoreWebView2 == null) return;
                await state.WebView.CoreWebView2.ExecuteScriptAsync(js);
            }
            catch (Exception ex) { LogErr("Edit page script: " + ex.Message); }
        }

        /// <summary>
        /// 粘贴图片的落盘文件名。
        ///
        /// <para><b>实现已移到 <see cref="PastedImageCodec.BuildFileName"/></b>：那里是 public 纯函数，
        /// 能被单元测试覆盖（项目无 <c>InternalsVisibleTo</c>，留在本文件的 private 方法则测不到）。
        /// 保留这个薄壳只是为了让本文件的调用点读起来仍像"编辑页自己的事"。</para>
        /// </summary>
        private static string BuildPastedImageName(string? rawName, string ext)
            => PastedImageCodec.BuildFileName(rawName, ext);

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
