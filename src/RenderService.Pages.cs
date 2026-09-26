// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Collections.Generic;

namespace SeeMe
{
    /// <summary>RenderService 分部类：整页 HTML 构建（大纲 / 错误占位 / Office / Univer / 欢迎页）。</summary>
    public partial class RenderService
    {
        /// <summary>
        /// 大纲思维导图页：把标题树（TocItems 生成的 Markdown）用 markmap 渲染成思维导图。
        /// 复用 MarkmapInitScript（读 .seeme-markmap[data-md]），全屏容器，仅供侧边栏大纲导图模式使用。
        /// </summary>
        public static string BuildOutlineMapPage(bool isDark, string markdownMd)
        {
            var nonce = NewNonce();
            var esc = System.Security.SecurityElement.Escape(markdownMd);
            return $@"<!DOCTYPE html><html{(isDark ? " class='dark'" : "")}><head>
<meta charset='utf-8'/>
<meta name='referrer' content='no-referrer'/>
<meta http-equiv='Content-Security-Policy' content=""default-src 'self' https://appassets.example; script-src 'nonce-{nonce}' https://appassets.example; style-src 'unsafe-inline' https://appassets.example; base-uri 'self'; form-action 'none';"">
<style>
{ThemeCss()}
html,body {{ margin:0; padding:0; background:var(--bg); color:var(--text); }}
.seeme-markmap {{ width:100vw; height:100vh; }}
.seeme-markmap text {{ font-family:'Microsoft YaHei','PingFang SC',sans-serif; font-size:11px; }}
html.dark .seeme-markmap text {{ fill:var(--text); }}
html.dark .seeme-markmap path {{ stroke:var(--secondary); }}
</style></head><body>
<div class=""seeme-markmap"" data-md='{esc}'></div>
<script type='module' nonce='{nonce}'>{MarkmapInitScript}</script>
</body></html>";
        }

        public static string WrapPage(bool isDark, string title, string css, string bodyHtml, string extraJs = "")
        {
            var cls = isDark ? " class='dark'" : "";
            var nonce = NewNonce();
            return $@"<!DOCTYPE html>
<html{cls}><head><meta charset='utf-8'/>
<meta name='viewport' content='width=device-width,initial-scale=1'/>
<meta name='referrer' content='no-referrer'/>
<meta http-equiv='Content-Security-Policy' content=""default-src 'self' https://appassets.example; script-src 'nonce-{nonce}' https://appassets.example; style-src 'unsafe-inline'; img-src 'self' data: https://appassets.example; base-uri 'self'; form-action 'none';"">
<title>{System.Security.SecurityElement.Escape(title)}</title>
            <style>{ThemeCss()}{css}</style>
</head>
<body>
{bodyHtml}
<script nonce='{nonce}'>{SetThemeScript}{extraJs}</script>
<script nonce='{nonce}'>{KeyBridgeScript}</script>
</body></html>";
        }

        /// <summary>
        /// docx 高保真渲染页（docx-preview）：替换 OpenXML 自解析回退。
        /// 文档字节以 base64 注入，页面内解码后由 docx-preview 浏览器端渲染，样式保真度远高于自解析。
        /// </summary>
        public static string BuildDocxPreviewPage(bool isDark, byte[] bytes)
        {
            var nonce = NewNonce();
            var b64 = Convert.ToBase64String(bytes);
            return $@"<!DOCTYPE html><html{(isDark ? " class='dark'" : "")}><head>
<meta charset='utf-8'/>
<meta name='referrer' content='no-referrer'/>
<meta http-equiv='Content-Security-Policy' content=""default-src 'self' https://appassets.example; script-src 'nonce-{nonce}' https://appassets.example; style-src 'unsafe-inline' https://appassets.example; img-src 'self' data: https://appassets.example; font-src 'self' data: https://appassets.example; base-uri 'self'; form-action 'none';"">
<style>
{ThemeCss()}
html,body {{ margin:0; padding:0; background:var(--bg); color:var(--text); }}
#host {{ max-width:860px; margin:0 auto; padding:16px 20px 48px; }}
.loading-msg {{ text-align:center; color:var(--quote-text); padding:48px 0; }}
.docx-wrapper img {{ max-width:100%; }}
.docx-wrapper table {{ border-collapse:collapse; }}
</style></head><body>
<div id='host'><div class='loading-msg'>正在用 docx-preview 渲染…</div></div>
<script src='https://appassets.example/docxjs/docx-preview.min.js'></script>
<script nonce='{nonce}'>
(function(){{
  var b64='{b64}';
  var host=document.getElementById('host');
  function fail(m){{ host.innerHTML='<div class=""loading-msg"">docx-preview 渲染失败：'+m+'</div>'; }}
  try{{
    var bin=atob(b64), len=bin.length, bytes=new Uint8Array(len);
    for(var i=0;i<len;i++) bytes[i]=bin.charCodeAt(i);
    if(!window.docx){{ fail('加载 docx-preview.js 失败'); return; }}
    window.docx.renderAsync(bytes.buffer, host, null, {{
      className:'docx', inWrapper:true, breakPages:true, ignoreFonts:true, experimental:true
    }}).catch(function(err){{ fail(String(err&&err.message||err)); }});
  }}catch(e){{ fail(e.message); }}
}})();
</script>
<script nonce='{nonce}'>{KeyBridgeScript}</script>
</body></html>";
        }

        /// <summary>
        /// xlsx 高保真渲染页（Univer）：替代 anydoc→Markdown 丢失多 sheet/公式/样式的现状。
        /// 文件字节以 base64 注入，页面加载 React UMD + Univer preset 后 loadXlsx 原生渲染。
        /// 页面脚本带失败回显（fail 信息写入容器），C# 侧可按需回退 anydoc。
        /// </summary>
        public static string BuildUniverPage(bool isDark, byte[] bytes)
        {
            var nonce = NewNonce();
            var b64 = Convert.ToBase64String(bytes);
            return $@"<!DOCTYPE html><html{(isDark ? " class='dark'" : "")}><head>
<meta charset='utf-8'/>
<meta name='referrer' content='no-referrer'/>
<meta http-equiv='Content-Security-Policy' content=""default-src 'self' https://appassets.example; script-src 'nonce-{nonce}' https://appassets.example; style-src 'unsafe-inline' https://appassets.example; img-src 'self' data: blob:; worker-src 'self' blob: https://appassets.example; connect-src 'self' https://appassets.example; base-uri 'self'; form-action 'none';"">
<link rel='stylesheet' href='https://appassets.example/univer/preset-sheets.css'/>
<style>
{ThemeCss()}
html,body {{ margin:0; height:100%; background:var(--bg); }}
#app {{ height:100vh; }}
.loading-msg {{ padding:48px; text-align:center; color:var(--secondary); }}
.fail-msg {{ padding:32px; text-align:center; color:var(--danger); }}
</style></head><body>
<div id='app'><div class='loading-msg'>正在用 Univer 渲染表格…</div></div>
<script src='https://appassets.example/univer/react.production.min.js'></script>
<script src='https://appassets.example/univer/react-dom.production.min.js'></script>
<script src='https://appassets.example/univer/univer-preset.js'></script>
<script nonce='{nonce}'>
(function(){{
  var b64='{b64}';
  var app=document.getElementById('app');
  function fail(m){{ app.innerHTML='<div class=""fail-msg"">Univer 渲染失败：'+m+'</div>'; }}
  try{{
    var U=window.UniverPresets||window.UniverPreset||{{}};
    if(!U.createUniver){{ fail('preset 加载失败'); return; }}
    var preset=U.UniverPresetSheets||U.Sheets;
    var univer=U.createUniver(preset||{{}});
    var api=univer.__getAPI?univer.__getAPI():null;
    if(!api||!api.loadXlsx){{ fail('API 不可用'); return; }}
    var bin=atob(b64), len=bin.length, bytes=new Uint8Array(len);
    for(var i=0;i<len;i++) bytes[i]=bin.charCodeAt(i);
    api.loadXlsx(bytes.buffer).then(function(){{ app.querySelector('.loading-msg') && app.querySelector('.loading-msg').remove(); }}).catch(fail);
  }}catch(e){{ fail(e.message); }}
}})();
</script>
</body></html>";
        }

        public string BuildOfficePage(string bodyHtml, PanelState state,
            FrameworkElement resourceElement)
        {
            var isDark = IsDark();

    // 双套固定值：:root 恒亮色、html.dark 恒暗色（ThemeVars 唯一调色板），保证任意主题下
            var css = $@"
{ThemeCss()}
{PageResetCss}
html,body {{ background:var(--bg); color:var(--text); font-family:'Microsoft YaHei','PingFang SC',sans-serif; }}
{(EyeCare ? "body > * {{ filter:sepia(.22) saturate(.88) brightness(1.02); }} html.dark body > * {{ filter:sepia(.18) saturate(.8) brightness(.96); }}" : "")}
body {{ line-height:{LineHeight.ToString(System.Globalization.CultureInfo.InvariantCulture)}; font-size:{FontSize.ToString(System.Globalization.CultureInfo.InvariantCulture)}px; padding:16px 20px 40px; transition:background-color .3s ease,color .3s ease; }}
.content {{
  max-width:100%;
}}
{MdDocumentCss}
.page-break {{
  text-align:center; margin:32px 0; position:relative;
}}
.page-break::before {{
  content:''; position:absolute; top:50%; left:0; right:0;
  height:1px; background:var(--border);
}}
.page-break span {{
  background:var(--bg); padding:0 16px; position:relative;
  font-size:12px; color:var(--secondary);
}}
.pdf-line {{
    background:transparent !important;
    border:none !important;
    margin:0 !important;
    padding:0 !important;
    display:inline !important;
}}
.pdf-line + .pdf-line {{
    display:block !important;
    margin-bottom:0.6em !important;
}}
.pdf-code {{
    display:block !important;
    background:var(--code-bg) !important;
    padding:8px 12px !important;
    border-radius:6px !important;
    margin:0.5em 0 !important;
    font-family:'Consolas','JetBrains Mono',monospace !important;
    font-size:.86em !important;
    line-height:{LineHeight.ToString(System.Globalization.CultureInfo.InvariantCulture)} !important;
    overflow-x:auto !important;
    white-space:pre !important;
}}
.sheet-title {{ font-size:1.1em; color:var(--heading); font-weight:600; margin:12px 0 6px; padding-bottom:4px; border-bottom:1px solid var(--border); }}
.excel-table {{ border-collapse:collapse; width:100%; font-size:.9em; margin:0; }}
.excel-table th,.excel-table td {{ border:1px solid var(--border); padding:4px 10px; white-space:nowrap; }}
.excel-table th {{ background:var(--card); font-weight:600; color:var(--heading); position:sticky; top:0; }}
.excel-table tr:nth-child(even) td {{ background:var(--row-hov); }}
html.dark .excel-table tr:nth-child(even) td {{ background:rgba(255,255,255,.03); }}
.excel-table tr:hover td {{ background:rgba(0,0,0,.04); }}
html.dark .excel-table tr:hover td {{ background:rgba(255,255,255,.06); }}
.ppt-slide {{ background:var(--card); border:1px solid var(--border); border-radius:10px; margin:0 0 14px; overflow:hidden; }}
.ppt-slide-header {{ background:var(--bg); padding:7px 14px; font-size:.78em; font-weight:600; color:var(--heading); border-bottom:1px solid var(--border); }}
.ppt-slide-body {{ padding:10px 16px 12px; }}
.ppt-text {{ margin:.25em 0; line-height:{LineHeight.ToString(System.Globalization.CultureInfo.InvariantCulture)}; font-size:.95em; }}
.error-msg {{ color:var(--danger); }}
.empty-msg {{ color:var(--text); opacity:.5; font-style:italic; }}
::-webkit-scrollbar {{ width:8px; height:8px; }}
::-webkit-scrollbar-track {{ background:transparent; }}
::-webkit-scrollbar-thumb {{ background:var(--border); border-radius:4px; }}
::-webkit-scrollbar-thumb:hover {{ background:var(--secondary); }}
mark.seeme-ann {{ background:#FDE68A; color:#1F2937; border-radius:2px; padding:1px 2px; cursor:pointer; }}
html.dark mark.seeme-ann {{ background:#B45309; color:#FDE68A; }}
body.seeme-ann-active {{ cursor:text; }}
body.seeme-ann-active ::selection {{ background:#FDE68A; }}
@media print {{
html, body {{ background:#fff !important; color:#000 !important; }}
body {{ padding:0 !important; }}
mark.seeme-hl, mark.seeme-ann {{ -webkit-print-color-adjust:exact; print-color-adjust:exact; }}
}}
";
        var htmlClass = isDark ? " class='dark'" : "";
        var nonce = NewNonce();

        // 高亮标注：按当前文件路径取存储的标注，随页面加载按文本重新包裹
        var annData = (HighlightStore?.ForFile(state.CurrentFile ?? "") ?? Array.Empty<HighlightItem>())
            .Select(i => new { i.Id, i.Text, i.Note }).ToList();
        var annotationScript = BuildAnnotationScript(System.Text.Json.JsonSerializer.Serialize(annData));

        return $@"<!DOCTYPE html>
<html{htmlClass}>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width,initial-scale=1'>
<meta name='referrer' content='no-referrer'/>
<meta http-equiv='Content-Security-Policy' content=""default-src 'self' https://appassets.example; script-src 'nonce-{nonce}' https://appassets.example; style-src 'unsafe-inline'; img-src 'self' data: https://appassets.example; base-uri 'self'; form-action 'none';"">
<style>{css}</style>
<script src=""https://appassets.example/scripts/office-theme.js""></script>
<script nonce='{nonce}'>{SetThemeScript}</script>
</head>
<body>
  <div class='content'>
    {bodyHtml}
  </div>
<script nonce='{nonce}'>{SearchScript}</script>
<script nonce='{nonce}'>{annotationScript}</script>
</body>
</html>";
        }

        /// <summary>
        /// 空白页显示可关闭的欢迎提示小卡片（右下角浮动），支持"不再显示"（持久化到 settings.json）。
        /// </summary>
        public string BuildWelcomePage(FrameworkElement resourceElement)
        {
            var isDark = IsDark();

    // 双套固定值：:root 恒亮色、html.dark 恒暗色（ThemeVars 唯一调色板），保证任意主题下
            var htmlClass = isDark ? " class='dark'" : "";
            var nonce = NewNonce();

            return $@"<!DOCTYPE html>
<html{htmlClass}>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width,initial-scale=1'>
<meta name='referrer' content='no-referrer'/>
<style>
{ThemeCss()}
{PageResetCss}
html,body {{ height:100%; background:var(--bg); }}
body {{
  background:var(--bg); color:var(--text);
  font-family:'Microsoft YaHei','PingFang SC',system-ui,sans-serif;
  transition:background-color .15s ease,color .15s ease;
}}
#tip {{
  position:fixed; left:50%; bottom:28px; transform:translateX(-50%); width:320px;
  background:var(--card); border:1px solid var(--border); border-radius:12px;
  box-shadow:0 8px 32px rgba(0,0,0,0.10);
  padding:14px 16px 10px;
  cursor:pointer;
  transition:background-color .15s ease,border-color .15s ease;
}}
#tip:hover {{ border-color:var(--accent); }}
html.dark #tip {{ box-shadow:0 8px 32px rgba(0,0,0,0.35); }}
.tip-head {{ display:flex; align-items:center; gap:10px; margin-bottom:10px; }}
h2 {{ font-size:14px; font-weight:600; color:var(--heading); flex:1; }}
.empty {{
  position:fixed; inset:0; display:flex; align-items:center; justify-content:center;
  pointer-events:none;
}}
.empty-box {{
  display:flex; flex-direction:column; align-items:center; gap:10px;
  padding:44px 30px; border:1.5px dashed var(--border); border-radius:18px;
}}  .empty-icon {{ display:flex; opacity:.6; }}
.empty-title {{ font-size:18px; font-weight:600; color:var(--secondary); }}
.empty-hint {{ font-size:12px; color:var(--secondary); opacity:.85; }}
.close {{
  border:none; background:transparent; color:var(--secondary); font-size:16px; line-height:1;
  cursor:pointer; padding:2px 6px; border-radius:6px;
}}
.close:hover {{ background:var(--bg); color:var(--heading); }}
.tip-body {{ font-size:12px; line-height:2; color:var(--secondary); }}
.tip-body kbd {{
  font-family:'Consolas',monospace; font-size:11px; padding:2px 6px; border-radius:5px;
  background:var(--bg); border:1px solid var(--border); color:var(--heading);
  min-width:64px; text-align:center; display:inline-block; margin-right:10px;
}}
.tip-foot {{ display:flex; justify-content:flex-end; margin-top:10px; }}
.tip-foot button {{
  border:1px solid var(--border); background:transparent; color:var(--secondary);
  font-size:11px; cursor:pointer; padding:4px 10px; border-radius:6px;
  transition:background-color .15s ease,border-color .15s ease,color .15s ease;
}}
.tip-foot button:hover {{ background:var(--bg); border-color:var(--accent); color:var(--heading); text-decoration:none; }}
</style>
</head>
<body>
  <div class='empty'>
    <div class='empty-box'>
      <div class='empty-icon'>
        <svg width='46' height='46' viewBox='0 0 46 46' fill='none'
             stroke='var(--border)' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'>
          <circle cx='23' cy='23' r='20'/>
          <path d='M23 31 V15 M16 22 L23 15 L30 22'/>
        </svg>
      </div>
      <div class='empty-title'>未选择</div>
      <div class='empty-hint'>拖拽文件到此 · Ctrl+O 打开</div>
    </div>
  </div>
  <div id='tip' title='点击打开文件'>
    <div class='tip-head'>
      <h2>欢迎使用 SeeMe</h2>
      <button id='tipClose' class='close' title='关闭提示'>×</button>
    </div>
    <div class='tip-body'>
      <div><kbd>Ctrl+O</kbd>打开文件</div>
      <div><kbd>Ctrl+F</kbd>查找</div>
      <div><kbd>Ctrl+0/+/−</kbd>缩放</div>
    </div>
    <div class='tip-foot'>
      <button id='tipNever'>不再显示</button>
    </div>
  </div>
<script nonce='{nonce}'>
{SetThemeScript}
function dismiss(){{var t=document.getElementById('tip');if(t)t.style.display='none';}}
function openFile(){{
  try{{if(window.chrome&&chrome.webview)chrome.webview.postMessage(JSON.stringify({{kind:'open-file'}}));}}catch(e){{}}
}}
function dismissForever(){{
  try{{if(window.chrome&&chrome.webview)chrome.webview.postMessage(JSON.stringify({{kind:'dismiss-welcome'}}));}}catch(e){{}}
  dismiss();
}}
document.getElementById('tip').addEventListener('click', openFile);
document.getElementById('tipClose').addEventListener('click', function(e){{e.stopPropagation();dismiss();}});
document.getElementById('tipNever').addEventListener('click', function(e){{e.stopPropagation();dismissForever();}});
</script>
</body>
</html>";
        }
    }
}
