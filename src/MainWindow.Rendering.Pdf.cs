// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

// MainWindow 分部类 —— PDF / Excel / PPT 渲染
//
// 本文件是 MainWindow.Rendering.cs 的第一块拆分（2026-09-10）。
// 承载三种格式的具体渲染实现：
//   PDF    RenderPdf → MapPdfVirtualHost（虚拟主机映射）→ BuildPdfReadingPage（图文重建页）
//          RenderPdfTextView（文本层提取 / 扫描版标记）
//   Excel  RenderExcel（xlsx/xlsm 走 Univer，见 RenderExcelUniverAsync）
//   PPT    RenderPpt
//   Word   RenderDocx（anydoc-wasm → Markdown；回退见 FileConverter/OfficeFallback）
//
// 与主调度文件的分工：MainWindow.Rendering.cs 负责"什么文件走哪条路"的判定与
// Markdown 链路；本文件负责"这条路怎么走"的具体实现。
//
// 依赖的 _theme / _render / _converter 字段与 LogErr / LogInfo / MarkLoaded
// 等私有方法均定义于本 partial 类的其他文件，跨文件可见。

// 说明：using 集保守保留原 MainWindow.Rendering.cs 的全部条目——
// 多余 using 仅产生 IDE 提示（可后续清理），漏引则是编译错误。
// 拆分改动应聚焦"位置"，不夹带"清理"。

using System;
using System.Text;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;
using System.Collections.Generic;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace SeeMe
{
    public partial class MainWindow
    {
        // ═══════════════ PDF / Excel / PPT 渲染 ═══════════════

        private void RenderPdf(PanelState state)
        {
            if (string.IsNullOrEmpty(state.CurrentFile)) return;
            if (CheckFileSize(state)) return;
            // PDF 统一走文本视图：浏览器端重建图文混排（PDF.js 提取文本 + 内联图片）
            RenderPdfTextView(state);
        }

        /// <summary>
        /// PDF 文本视图：浏览器端用 PDF.js 重建图文混排——按 y 坐标交错文本行与图片
        /// （图片内联在原文位置，点击放大），扫描版（无文本层）则仅显示各页图片。
        /// 主题/缩放/搜索与 Markdown 页一致；anydoc 文本层仍在后台提取（供内容搜索与统计卡）。
        /// </summary>
        private void RenderPdfTextView(PanelState state)
        {
            if (string.IsNullOrEmpty(state.CurrentFile)) return;
            try
            {
                // PDF 文件目录映射虚拟主机（页面需 fetch 字节重建图文）
                var pdfUrl = MapPdfVirtualHost(state);
                if (string.IsNullOrEmpty(pdfUrl))
                {
                    RenderErrorPage(state, new Exception("无法映射 PDF 目录"));
                    return;
                }
                // 后台提取文本层（供内容搜索 >关键词 / 统计卡），不阻塞图文重建
                _ = TryExtractPdfTextAsync(state);
                state.PdfOutlineToken = Guid.NewGuid().ToString("N");
                state.WebView.NavigateToString(BuildPdfReadingPage(state, pdfUrl));
                MarkLoaded(state);
                LogInfo("Pdf text view: " + Path.GetFileName(state.CurrentFile));
            }
            catch (Exception ex)
            {
                LogErr("Pdf text view: " + ex.Message);
                if (state.WebView?.CoreWebView2 != null)
                    RenderErrorPage(state, ex);
            }
        }

        /// <summary>把当前 PDF 所在目录映射到 pdffiles.example 虚拟主机，返回可 fetch 的 PDF URL；失败返回 null。</summary>
        private string? MapPdfVirtualHost(PanelState state)
        {
            var dir = Path.GetDirectoryName(state.CurrentFile);
            var name = Path.GetFileName(state.CurrentFile);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name)) return null;
            const string hostName = "pdffiles.example";
            try
            {
                state.WebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    hostName, dir, CoreWebView2HostResourceAccessKind.Allow);
            }
            catch { /* 同一目录重复映射会抛异常，忽略（映射已存在） */ }
            return $"https://{hostName}/{System.Uri.EscapeDataString(name)}";
        }

        /// <summary>
        /// PDF 图文混排页外壳：主题 CSS + 状态栏 + 文档容器 + PDF.js（appassets）+ 重建脚本。
        /// CSP 放行 pdffiles.example（fetch PDF 字节）与 PDF.js worker；字号缩放/搜索与 Markdown 页一致。
        /// </summary>
        private string BuildPdfReadingPage(PanelState state, string pdfUrl)
        {
            var isDark = _theme.Current == _theme.Dark;
            var htmlClass = isDark ? " class='dark'" : "";
            var jsPdfUrl = System.Text.Json.JsonSerializer.Serialize(pdfUrl);
            var jsRestoreY = state.LastScrollY.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var jsToken = System.Text.Json.JsonSerializer.Serialize(state.PdfOutlineToken ?? "");
            var nonce = RenderService.NewNonce();
            // 高亮标注：PDF 页同样注入（页面加载时文本未生成匹配不到，buildDoc 完成后由 __seemeApplyAnn 重应用）
            var annData = (_render.HighlightStore?.ForFile(state.CurrentFile ?? "") ?? Array.Empty<HighlightItem>())
                .Select(i => new { i.Id, i.Text, i.Note }).ToList();
            var annotationScript = RenderService.BuildAnnotationScript(
                System.Text.Json.JsonSerializer.Serialize(annData));
            return $@"<!DOCTYPE html>
<html{htmlClass}><head><meta charset='utf-8'/>
<meta name='viewport' content='width=device-width,initial-scale=1'/>
<meta name='referrer' content='no-referrer'/>
<meta http-equiv='Content-Security-Policy' content=""default-src 'self' https://appassets.example; script-src 'nonce-{nonce}' https://appassets.example; style-src 'unsafe-inline'; img-src 'self' data: blob:; connect-src 'self' https://appassets.example https://pdffiles.example; worker-src 'self' https://appassets.example blob:; base-uri 'self'; form-action 'none';"">
<style>
{RenderService.ThemeCss()}
{RenderService.PageResetCss}
  html {{ font-size:14px; }}
  html,body {{ width:100%; height:100vh; }}
  body {{ background:var(--bg); color:var(--text); transition:background-color .3s ease,color .3s ease; }}
  {RenderService.MdDocumentCss}
  #pdf-doc {{ max-width:860px; margin:0 auto; padding:4px 20px 60px; font-size:1rem; line-height:1.75; }}
  .pdf-page .pdf-pg-title {{ font-size:.85rem; font-weight:600; color:var(--secondary); margin:26px 0 10px;
                  padding-bottom:6px; border-bottom:1px dashed var(--h2-border); }}
  .pdf-fig {{ margin:16px auto; text-align:center; }}
  .pdf-fig img {{ max-width:100%; border-radius:4px; cursor:zoom-in;
                 box-shadow:0 1px 3px rgba(0,0,0,.25),0 6px 18px rgba(0,0,0,.08); }}
  html.dark .pdf-fig img {{ box-shadow:0 1px 3px rgba(0,0,0,.5),0 6px 18px rgba(0,0,0,.35); }}
  .pdf-fig figcaption {{ font-size:.72rem; color:var(--secondary); margin-top:5px; }}
  .pdf-fig-ph {{ font-size:.75rem; color:var(--quote-text); padding:10px; }}
  .pdf-note {{ margin:14px 0; padding:10px 14px; border-left:3px solid var(--quote); background:var(--quote-bg);
              color:var(--quote-text); font-size:.8rem; border-radius:0 4px 4px 0; }}
  mark.seeme-hl {{ background:#FBBF24; color:#1F2937; border-radius:2px; padding:1px 2px; }}
  mark.seeme-cur {{ background:var(--accent); color:#fff; }}
  {(isDark && _render is RenderService rsp && rsp.EyeCare ? "body > * { filter:sepia(.3) saturate(.78) brightness(.95) !important; }" : "")}
  {(!isDark && _render is RenderService rs2 && rs2.EyeCare ? "body > * { filter:sepia(.35) saturate(.82) brightness(1.04) !important; }" : "")}
  ::-webkit-scrollbar {{ width:8px; height:8px; }}
  ::-webkit-scrollbar-thumb {{ background:var(--h1-border); border-radius:4px; }}
</style>
</head><body>
  <div id='pdf-doc' class='content'></div>
<script nonce='{nonce}'>
window.__PDF_READ_CONF = {{ pdf: {jsPdfUrl}, restoreY: {jsRestoreY}, tok: {jsToken} }};
{RenderService.SetThemeScript}
</script>
<script type='module' nonce='{nonce}'>
import * as pdfjsLib from 'https://appassets.example/pdfjs/pdf4.min.js';
window.pdfjsLib = pdfjsLib;
document.dispatchEvent(new Event('seeme-pdfjs-ready'));
</script>
<script nonce='{nonce}'>{RenderService.SearchScript}</script>
<script nonce='{nonce}'>
{PdfReadingScript}
</script>
<script nonce='{nonce}'>{annotationScript}</script>
<script nonce='{nonce}'>{RenderService.KeyBridgeScript}</script>
</body></html>";
        }

        /// <summary>
        /// 图文重建脚本：逐页 getTextContent（文本行）+ getOperatorList（图片框），按 y 坐标交错排版；
        /// 图片手动构造裁剪视口渲染（懒加载，按原生分辨率），点击后超采样二次渲染放大。
        /// 扫描版（无文本）→ 仅显示各页图片 + 顶部提示。
        /// </summary>
        private const string PdfReadingScript = @"
(function () {
  var conf = window.__PDF_READ_CONF || {};
  var pdfUrl = conf.pdf || '';
  var restoreY = conf.restoreY || 0;
  var docEl = document.getElementById('pdf-doc');
  // 库引导：PDF.js 4.x 为纯 ESM，由页面内联 module 挂 window.pdfjsLib 后广播 seeme-pdfjs-ready。
  // 本脚本（经典脚本）先于 module 执行，因此用事件等待库就绪再取 PDF 字节。
  function boot() {
    if (!pdfUrl || !window.pdfjsLib) { return; }
    pdfjsLib.GlobalWorkerOptions.workerSrc = 'https://appassets.example/pdfjs/pdf4.worker.min.js';
    fetch(pdfUrl)
      .then(function (r) { if (!r.ok) throw new Error('fetch ' + r.status); return r.arrayBuffer(); })
      .then(function (buf) {
        return pdfjsLib.getDocument({
          data: new Uint8Array(buf), disableAutoFetch: true, disableStream: true,
          cMapUrl: 'https://appassets.example/pdfjs/cmaps/', cMapPacked: true,
          standardFontDataUrl: 'https://appassets.example/pdfjs/standard_fonts/'
        }).promise;
      })
      .then(buildDoc)
      .catch(function (err) { console.error('[pdf-read] load failed:', err); });
  }
  if (window.pdfjsLib) { boot(); }
  else { document.addEventListener('seeme-pdfjs-ready', boot, { once: true }); }

  // 诊断日志 → 宿主 error.log（kind:pdf-read-log，C# 侧记录）
  function dbg(msg) {
    try { window.chrome.webview.postMessage(JSON.stringify({ kind: 'pdf-read-log', msg: String(msg) })); } catch (e) {}
  }

  // 字号缩放（宿主 ApplyPanelZoom 调用）：与 Markdown 页一致，改 html 字号（CSS 用 rem 继承）
  window.__seemeApplyZoom = function (z) {
    document.documentElement.style.fontSize = (z * 14) + 'px';
  };

  // HTML 转义（PDF 文本可能含 < > &）
  function esc(s) {
    return String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
  }
  // 智能拼接 + 行内样式：CJK 间不加空格，拉丁词间加空格；粗体/斜体包 <strong>/<em>
  function joinPartsHtml(parts) {
    var html = '', lastCh = '';
    for (var i = 0; i < parts.length; i++) {
      var p = parts[i];
      if (!p || !p.s) continue;
      var seg = esc(p.s);
      if (p.b) seg = '<strong>' + seg + '</strong>';
      if (p.i) seg = '<em>' + seg + '</em>';
      if (html.length) {
        var lastLatin = /[A-Za-z0-9]/.test(lastCh);
        var firstLatin = /[A-Za-z0-9]/.test(p.s.charAt(0));
        if (lastLatin && firstLatin) html += ' ';
      }
      html += seg;
      lastCh = p.s.charAt(p.s.length - 1);
    }
    return html;
  }

  var totalTextChars = 0;
  var total = 0;

    function buildDoc(doc) {
    total = doc.numPages;
    var cur = 0;
    function next() {
      cur++;
      if (cur > total) {
        if (totalTextChars === 0) showNoTextNote();
        if (restoreY > 0) window.scrollTo(0, restoreY);
        dbg('done: pages=' + total + ' textChars=' + totalTextChars);
        buildOutline(doc);
        // 全部页面渲染完成后再应用高亮标注（标注脚本加载时 PDF 文本尚未生成）
        if (window.__seemeAnn && window.__seemeApplyAnn) {
          window.__seemeApplyAnn(window.__seemeAnn.items);
        }
        return;
      }
      buildPage(doc, cur).then(next);
    }
    next();
  }

  async function buildPage(doc, n) {
    var page = await doc.getPage(n);
    var tc = await page.getTextContent();
    var opList = await page.getOperatorList();
    var vp1 = page.getViewport({ scale: 1 });
    var O = pdfjsLib.OPS;

    // ── 图片框：跟踪 opList 的变换状态（save/restore/transform）。
    //    PDF 语义：图片画在单位正方形内，Do 时的 CTM 就是它在用户空间的
    //    位置与尺寸（实测 opList：transform [w,0,0,h,x,y] → paintImageXObject
    //    [objId, 像素宽, 像素高]，像素尺寸只影响解码不影响布局）。
    //    所以用当前矩阵映射单位正方形四角求轴对齐包围盒。
    var hasText = false;
    for (var ti = 0; ti < tc.items.length; ti++) {
      if (tc.items[ti] && tc.items[ti].str && tc.items[ti].str.trim()) { hasText = true; break; }
    }
    var imgs = [];
    var stack = [];
    var cur = [1, 0, 0, 1, 0, 0];
    function mul(T, M) {
      return [
        T[0] * M[0] + T[2] * M[1], T[1] * M[0] + T[3] * M[1],
        T[0] * M[2] + T[2] * M[3], T[1] * M[2] + T[3] * M[3],
        T[0] * M[4] + T[2] * M[5] + T[4], T[1] * M[4] + T[3] * M[5] + T[5]
      ];
    }
    function mapPt(x, y) {
      return [cur[0] * x + cur[2] * y + cur[4], cur[1] * x + cur[3] * y + cur[5]];
    }
    function pushImg(pxW, pxH) {
      var p00 = mapPt(0, 0), p10 = mapPt(1, 0), p01 = mapPt(0, 1), p11 = mapPt(1, 1);
      var xs = [p00[0], p10[0], p01[0], p11[0]];
      var ys = [p00[1], p10[1], p01[1], p11[1]];
      var x = Math.min.apply(null, xs), y = Math.min.apply(null, ys);
      var bw = Math.max.apply(null, xs) - x, bh = Math.max.apply(null, ys) - y;
      if (!isFinite(bw) || !isFinite(bh) || bw < 8 || bh < 8) return; // 装饰性小图
      // 有文本时跳过几乎整页的背景图（水印/底图）；无文本的扫描页保留（图即内容）
      if (hasText && bw > vp1.width * 0.99 && bh > vp1.height * 0.99) return;
      imgs.push({ x: x, y: y, w: bw, h: bh, pxW: pxW, pxH: pxH }); // pxW/pxH = 源图像素尺寸
    }
    for (var i = 0; i < opList.fnArray.length; i++) {
      var fn = opList.fnArray[i];
      var a = opList.argsArray[i];
      if (fn === O.save) {
        stack.push(cur.slice());
      } else if (fn === O.restore) {
        cur = stack.pop() || cur;
      } else if (fn === O.transform && a && a.length >= 6) {
        cur = mul(cur, a);
      } else if (fn === O.paintImageXObject && a && a.length >= 1) {
        pushImg(a.length >= 3 ? +a[1] : 0, a.length >= 3 ? +a[2] : 0);
      } else if (fn === O.paintInlineImageXObject && a && a[0]) {
        pushImg(+a[0].width || 0, +a[0].height || 0);
      }
      // paintImageMaskXObject（透明度蒙版/阴影模板）不视为正文图片，跳过
    }

    // ── 文本行：按基线 y 分桶（±3 容差），记录字号与字体。
    //    Word 导出的 PDF 用字号表达标题层级、字体名带 Bold/Italic 表达强调。
    var lines = [];
    var items = tc.items || [];
    var sizeCount = {};
    for (var k = 0; k < items.length; k++) {
      var it = items[k];
      if (!it || !it.str) continue;
      var yy = (it.transform && it.transform.length >= 6) ? it.transform[5] : 0;
      var line = null;
      for (var L = 0; L < lines.length; L++) {
        if (Math.abs(lines[L].y - yy) < 3) { line = lines[L]; break; }
      }
      if (!line) { line = { y: yy, parts: [], sz: 0, bold: false, italic: false }; lines.push(line); }
      var sz = Math.abs(it.height || (it.transform && it.transform[0]) || 0);
      var fn = '';
      try {
        var fObj = it.fontName ? page.commonObjs.get(it.fontName) : null;
        fn = (fObj && (fObj.name || fObj.fallbackName)) || '';
      } catch (e) { }
      var bold = /Bold|Heavy|Black|Demibold/i.test(fn);
      var italic = /Italic|Oblique/i.test(fn);
      line.parts.push({ s: it.str, sz: sz, b: bold, i: italic });
      if (sz > line.sz) line.sz = sz;
      if (bold) line.bold = true;
      if (italic) line.italic = true;
      sizeCount[sz] = (sizeCount[sz] || 0) + it.str.length;
    }
    // 正文基准字号 = 按字符数最多的字号
    var bodySz = 0, bestN = 0;
    for (var bz in sizeCount) {
      if (sizeCount[bz] > bestN) { bestN = sizeCount[bz]; bodySz = +bz; }
    }
    if (!bodySz) bodySz = 11;
    function headingLevel(sz) {
      if (sz >= bodySz * 1.4) return 1;
      if (sz >= bodySz * 1.2) return 2;
      if (sz >= bodySz * 1.1) return 3;
      return 0;
    }

    // 文档顺序：PDF y 向上增长 → y 大者在前
    lines.sort(function (A, B) { return B.y - A.y; });
    imgs.sort(function (A, B) { return B.y - A.y; });
    dbg('p' + n + ': textItems=' + items.length + ' textLines=' + lines.length + ' images=' + imgs.length + ' bodySz=' + bodySz);

    var sec = document.createElement('section');
    sec.className = 'pdf-page';
    sec.setAttribute('data-pg', n);
    sec.setAttribute('data-vph', vp1.height);
    var h = document.createElement('h3');
    h.className = 'pdf-pg-title';
    h.id = 'pdf-pg-' + n;
    h.textContent = '第 ' + n + ' 页';
    sec.appendChild(h);

    var paraParts = null;
    var prevY = null;
    var headingSeq = 0;
    var gapTh = vp1.height * 0.03; // 段间距阈值（页面高度的 3%）

    function flushPara() {
      if (paraParts) {
        var html = joinPartsHtml(paraParts);
        var txt = html.replace(/<[^>]+>/g, '');
        if (txt.trim()) {
          var p = document.createElement('p');
          p.innerHTML = html;
          sec.appendChild(p);
          totalTextChars += txt.replace(/\s/g, '').length;
        }
        paraParts = null;
      }
    }
    // 大字号行 → MD 风格标题（h1/h2/h3），正文行进段落
    function addHeading(ln) {
      flushPara();
      var lv = headingLevel(ln.sz);
      var el = document.createElement('h' + lv);
      el.id = 'pdf-h' + (++headingSeq);
      el.innerHTML = joinPartsHtml(ln.parts);
      sec.appendChild(el);
      totalTextChars += el.textContent.replace(/\s/g, '').length;
    }
    function addImg(box) {
      flushPara();
      var fig = document.createElement('figure');
      fig.className = 'pdf-fig';
      fig.__box = box;
      fig.__page = page;
      var ph = document.createElement('div');
      ph.className = 'pdf-fig-ph';
      ph.textContent = '图片加载中…';
      var img = document.createElement('img');
      img.alt = '第 ' + n + ' 页图片';
      img.style.visibility = 'hidden';
      var cap = document.createElement('figcaption');
      cap.textContent = '第 ' + n + ' 页 · 图片（点击放大）';
      fig.appendChild(ph);
      fig.appendChild(img);
      fig.appendChild(cap);
      sec.appendChild(fig);
      var rendered = false;
      function tryRender() {
        if (rendered) return;
        rendered = true;
        renderCrop(page, box, img, ph, null);
      }
      if ('IntersectionObserver' in window) {
        var io = new IntersectionObserver(function (entries) {
          entries.forEach(function (en) {
            if (en.isIntersecting) { tryRender(); io.disconnect(); }
          });
        }, { rootMargin: '500px 0px' });
        io.observe(img);
      } else {
        tryRender();
      }
    }

    // 交错合并：lines 与 imgs 均按 y 降序
    var li = 0, ii = 0;
    while (li < lines.length || ii < imgs.length) {
      var lineY = li < lines.length ? lines[li].y : -Infinity;
      var imgY = ii < imgs.length ? imgs[ii].y : -Infinity;
      if (ii < imgs.length && imgY >= lineY) {
        addImg(imgs[ii]);
        ii++;
      } else if (li < lines.length) {
        var ln = lines[li];
        li++;
        if (headingLevel(ln.sz) > 0) { addHeading(ln); prevY = ln.y; continue; }
        var htxt = joinPartsHtml(ln.parts).replace(/<[^>]+>/g, '');
        if (!htxt.trim()) continue;
        if (prevY === null || (prevY - ln.y) > gapTh) flushPara();
        if (!paraParts) paraParts = [];
        paraParts.push.apply(paraParts, ln.parts);
        prevY = ln.y;
      }
    }
    flushPara();
    docEl.appendChild(sec);
  }

  function showNoTextNote() {
    var note = document.createElement('div');
    note.className = 'pdf-note';
    note.textContent = '🖨 该 PDF 没有可提取的文本层（扫描版），仅显示页面图像。';
    docEl.insertBefore(note, docEl.firstChild);
  }

  // ── 大纲：优先 PDF 书签（Word 导出自带），其次重建标题（h1-h3），最后各页标记 ──
  var outlineTargets = {};
  window.__pdfOutlineTargets = outlineTargets;
  window.__pdfScrollTo = function (id) {
    var t = outlineTargets[id];
    if (!t) return;
    if (t.el) {
      var el = document.getElementById(t.el);
      if (el) el.scrollIntoView({ behavior: 'smooth', block: 'start' });
      return;
    }
    // 书签：按页面内 y 比例定位（PDF y 向上 → 距顶比例 = (页高 - top)/页高）
    var sec = docEl.querySelector('.pdf-page[data-pg=""' + t.pg + '""]');
    if (!sec) return;
    var vpH = +sec.getAttribute('data-vph') || 1;
    var ratio = Math.min(1, Math.max(0, (vpH - t.top) / vpH));
    window.scrollTo({ top: sec.offsetTop + sec.offsetHeight * ratio - 30, behavior: 'smooth' });
  };
  function postOutline(items) {
    try {
      window.chrome.webview.postMessage(JSON.stringify({ kind: 'pdf-outline', token: conf.tok || '', items: items }));
    } catch (e) { }
  }
  function buildOutline(doc) {
    doc.getOutline().then(function (bm) {
      if (bm && bm.length) {
        // 书签树 → 扁平化（level 递增），逐项解析目标页与 y 坐标
        var flat = [];
        (function walk(list, lv) {
          for (var i = 0; i < list.length; i++) {
            var it = list[i];
            flat.push({ t: it.title || '', l: lv, d: it.dest });
            if (it.items && it.items.length) walk(it.items, lv + 1);
          }
        })(bm, 1);
        var items = [], tasks = [];
        for (var i = 0; i < flat.length; i++) {
          var f = flat[i], id = 'pdf-bm' + i;
          (function (f, id) {
            var push = function (pg, top) {
              outlineTargets[id] = { pg: pg, top: top };
              items.push({ t: f.t, l: f.l, id: id });
            };
            if (f.d && f.d.length && f.d[0]) {
              tasks.push(doc.getPageIndex(f.d[0]).then(function (pi) {
                var top = (f.d.length > 3 && f.d[1] && f.d[1].name === 'XYZ' && isFinite(f.d[3])) ? f.d[3] : 0;
                push(pi + 1, top);
              }).catch(function () { push(1, 0); }));
            } else {
              push(1, 0);
            }
          })(f, id);
        }
        Promise.all(tasks).then(function () { postOutline(items); });
        return;
      }
      // 无书签 → 用重建出的标题
      var hs = docEl.querySelectorAll('.pdf-page h1, .pdf-page h2, .pdf-page h3');
      var headingItems = [];
      for (var i = 0; i < hs.length; i++) {
        var el = hs[i];
        if (el.classList.contains('pdf-pg-title')) continue;
        outlineTargets[el.id] = { el: el.id };
        headingItems.push({ t: el.textContent.trim(), l: +el.tagName.charAt(1), id: el.id });
      }
      if (headingItems.length) { postOutline(headingItems); return; }
      // 再没有 → 各页标记
      var pgs = docEl.querySelectorAll('.pdf-page');
      var pgItems = [];
      for (var j = 0; j < pgs.length; j++) {
        var s = pgs[j];
        var id = (s.querySelector('.pdf-pg-title') || {}).id;
        if (!id) continue;
        outlineTargets[id] = { el: id };
        pgItems.push({ t: '第 ' + s.getAttribute('data-pg') + ' 页', l: 1, id: id });
      }
      if (pgItems.length) postOutline(pgItems);
    }).catch(function () { });
  }

  // 渲染图片区域 → dataURL（thumbW 为 null 用默认缩略宽度；指定宽度用于 lightbox 高分辨率二次渲染）
  // 本 bundle 未导出 PageViewport → 手动构造裁剪视口（PDF 用户空间 → canvas，
  // y 翻转 + 平移到原点），只渲染图片区域，不浪费整页。
  function cropVp(box, scale, dpr) {
    var s = scale * dpr;
    return {
      width: box.w * scale, height: box.h * scale, rotation: 0, scale: scale,
      transform: [s, 0, 0, -s, -s * box.x, s * (box.y + box.h)]
    };
  }
  function renderCrop(page, box, img, ph, thumbW) {
    try {
      var maxW = Math.min(1500, Math.max(400, (docEl.clientWidth || 800) - 60));
      var displayW = thumbW || Math.min(460, maxW);
      var displayScale = displayW / box.w;
      // 源图原生分辨率：渲染倍率不低于它（源图多清晰就多清晰）
      var natScale = (box.pxW && box.pxW > 0) ? box.pxW / box.w : displayScale;
      // 缩略图：最多 2× 显示宽度防内存膨胀；lightbox：1.5× 原生超采样，长边 ≤ 2400
      var want = thumbW
        ? Math.min(natScale * 1.5, 2400 / Math.max(box.w, box.h))
        : Math.min(natScale, displayScale * 2);
      var scale = Math.max(displayScale, want);
      var dpr = window.devicePixelRatio || 1;
      var vp = cropVp(box, scale, dpr);
      var canvas = document.createElement('canvas');
      canvas.width = Math.max(1, Math.floor(vp.width * dpr));
      canvas.height = Math.max(1, Math.floor(vp.height * dpr));
      var ctx = canvas.getContext('2d');
      // isEvalSupported:false —— CVE-2024-4367 官方缓解：禁用字体矩阵 eval 编译路径
      //（页面 CSP 无 unsafe-eval 本已拦截，此处显式关闭构成双保险）
      return page.render({ canvasContext: ctx, viewport: vp, isEvalSupported: false }).promise
        .then(function () {
          var url = canvas.toDataURL('image/jpeg', 0.95);
          if (img) { img.src = url; img.style.visibility = 'visible'; }
          if (ph) ph.style.display = 'none';
          canvas.width = 0; canvas.height = 0;
          return url;
        })
        .catch(function (err) {
          if (ph) ph.textContent = '图片渲染失败';
          console.error('[pdf-read] renderCrop failed:', err);
          return null;
        });
    } catch (err) {
      if (ph) ph.textContent = '图片渲染失败';
      console.error('[pdf-read] renderCrop throw:', err);
      return Promise.resolve(null);
    }
  }

  // 点击图片 → 按更高分辨率二次渲染并打开 lightbox
  var ov = null;
  function openLightbox(src, cap) {
    closeLightbox();
    ov = document.createElement('div');
    ov.style.cssText = 'position:fixed;inset:0;background:rgba(0,0,0,.88);display:flex;align-items:center;justify-content:center;z-index:9999;cursor:zoom-out;flex-direction:column;';
    var img = document.createElement('img');
    img.src = src;
    img.style.cssText = 'max-width:94%;max-height:86%;border-radius:6px;box-shadow:0 8px 40px rgba(0,0,0,.5);';
    var capEl = document.createElement('div');
    capEl.textContent = cap || '';
    capEl.style.cssText = 'color:#ccc;font-size:12px;margin-top:10px;';
    ov.appendChild(img);
    ov.appendChild(capEl);
    ov.addEventListener('click', closeLightbox);
    document.body.appendChild(ov);
  }
  function closeLightbox() {
    if (ov) { document.body.removeChild(ov); ov = null; }
  }
  docEl.addEventListener('click', function (e) {
    var t = e.target;
    var fig = t && t.closest ? t.closest('.pdf-fig') : null;
    if (!fig) return;
    var img = fig.querySelector('img');
    if (!img || !img.src || img.style.visibility === 'hidden') return;
    var cap = fig.querySelector('figcaption');
    if (fig.__page && fig.__box) {
      renderCrop(fig.__page, fig.__box, null, null, 1100).then(function (url) {
        if (url) openLightbox(url, cap ? cap.textContent : '');
      });
    } else {
      openLightbox(img.src, cap ? cap.textContent : '');
    }
  });
  document.addEventListener('keydown', function (e) { if (e.key === 'Escape') closeLightbox(); });
})();
";

        private async Task RenderDocx(PanelState state)
        {
            if (string.IsNullOrEmpty(state.CurrentFile)) return;
            if (CheckFileSize(state)) return;
            var isDark = _theme.Current == _theme.Dark;
            state.WebView.NavigateToString(BuildLoadingPage(isDark));
            var content = await _converter.DocxToHtml(state.CurrentFile);
            var html = _render.BuildOfficePage(content, state, this);
            state.WebView.NavigateToString(html);
        }


        private async Task RenderExcel(PanelState state)
        {
            if (string.IsNullOrEmpty(state.CurrentFile)) return;
            if (CheckFileSize(state)) return;
            var isDark = _theme.Current == _theme.Dark;
            state.WebView.NavigateToString(BuildLoadingPage(isDark));
            var content = await _converter.ExcelToHtml(state.CurrentFile);
            var html = _render.BuildOfficePage(content, state, this);
            state.WebView.NavigateToString(html);
        }


        private async Task RenderPpt(PanelState state)
        {
            if (string.IsNullOrEmpty(state.CurrentFile)) return;
            if (CheckFileSize(state)) return;
            var isDark = _theme.Current == _theme.Dark;
            state.WebView.NavigateToString(BuildLoadingPage(isDark));
            var content = await _converter.PptToHtml(state.CurrentFile);
            var html = _render.BuildOfficePage(content, state, this);
            state.WebView.NavigateToString(html);
        }
    }
}
