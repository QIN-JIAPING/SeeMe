/* SeeMe PDF viewer — PDF.js canvas 渲染（自绘，替代原生 embed）。
 * 以【经典脚本】从 appassets.example/pdfjs/viewer.js 加载（非 ESM）。
 * 依赖同目录 pdf.min.js（UMD 3.11.174，经 <script> 注入全局 window.pdfjsLib）。
 * 页面壳（由 C# RenderPdf 生成）内联 ThemeCss 双套变量 + setTheme，
 * 经 window.__PDF_CONF 传入 { pdf, zoom, pageMode, dbl, scale }。
 * 功能：适合宽度 / 适合页面 / 100% 三档缩放、滚动 / 分页两种翻页、双击放大。
 * __seemeApplyZoom(z)：Ctrl+0/=/− 注入的相对缩放（1.0 = 适合宽度）。 */
/* 库加载：UMD 经典版，无 import / 无顶层 await；window.pdfjsLib 由 pdf.min.js 提供。
 * worker 用经典 new Worker（非 module），彻底绕开 module worker 在 WebView2 虚拟主机下的坑。 */
let pdfjsLib = null;
let initPromise = null;

function log(msg) {
  try { if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage(JSON.stringify({ kind: 'pdf', msg: String(msg) })); } catch (e) {}
}

function initPdfjs() {
  if (initPromise) return initPromise;
  initPromise = (async function () {
    // UMD 经典版：pdf.min.js 经 <script> 注入全局 window.pdfjsLib（无 import，无 ESM）
    pdfjsLib = window.pdfjsLib;
    if (!pdfjsLib) throw new Error('pdfjsLib 全局未找到（pdf.min.js 未加载）');

    // 经典 worker 从虚拟主机加载（UMD worker 非 ESM，普通 new Worker 即可）。
    // 注意：不注入 workerPort —— 设置 workerPort 会迫使 PDF.js 走 fromPort 模式，
    // 数据获取被迫回到主线程（整本下载，无法 Range 流式）。不设 port 时 PDF.js 自己
    // 创建 worker，数据获取在 worker 线程内完成，配合 getDocument({url}) 才能按需取页。
    pdfjsLib.GlobalWorkerOptions.workerSrc = 'https://appassets.example/pdfjs/pdf.worker.min.js';
    return pdfjsLib;
  })();
  return initPromise;
}

(function () {
  'use strict';

  function earlyLog(msg) {
    try { if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage(JSON.stringify({ kind: 'pdf', msg: 'EARLY: ' + msg })); } catch (e) {}
  }
  earlyLog('viewer boot');

  // 虚拟主机对普通 GET 的 200 响应不带 Accept-Ranges/Content-Length，PDF.js 的
  // validateRangeRequestCapabilities 因此判定不支持 Range（要求 Content-Length 且 >= 2×chunk）
  // → 退化整本读进内存。这里对无 Range 的 PDF 请求注入这两个响应头（保留流式 body），
  // PDF.js 就能进入 range 模式按需取页。pdfTotalLen 由 probeRange 从 Content-Range 解析。
  var pdfTotalLen = 0;
  (function () {
    var origFetch = window.fetch;
    window.fetch = function (url, opts) {
      var res = origFetch.apply(this, arguments);
      try {
        var isPdf = String(url).indexOf('.pdf') > 0 && String(url).indexOf('pdffiles.example') > 0;
        var hdr = opts && opts.headers;
        var hasRange = hdr && ((hdr.get && hdr.get('Range')) || hdr.Range);
        if (isPdf && !hasRange) {
          return res.then(function (r) {
            if (!r || r.status !== 200) return r;
            var h = new Headers(r.headers);
            h.set('Accept-Ranges', 'bytes');
            if (pdfTotalLen > 0) h.set('Content-Length', String(pdfTotalLen));
            return new Response(r.body, { status: 200, statusText: r.statusText, headers: h });
          });
        }
      } catch (e) {}
      return res;
    };
  })();

  const conf = window.__PDF_CONF || {};
  const pdfUrl = conf.pdf || '';
  const zoomMode = (conf.zoom === 'fitPage' || conf.zoom === 'actual') ? conf.zoom : 'fitWidth';
  const pageMode = conf.pageMode === 'paged' ? 'paged' : 'scroll';
  const dblClickZoom = conf.dbl !== false;

  const viewer = document.getElementById('viewer');
  const bar = document.getElementById('bar');
  const btnFW = document.getElementById('btnFW');
  const btnFP = document.getElementById('btnFP');
  const btnActual = document.getElementById('btnActual');
  const pgNav = document.getElementById('pgNav');
  const btnPrev = document.getElementById('btnPrev');
  const btnNext = document.getElementById('btnNext');
  const pgInfo = document.getElementById('pgInfo');
  const status = document.getElementById('status');

  let pdfDoc = null;
  let total = 0;
  let curPage = 1;
  let relScale = 1;                 // __seemeApplyZoom 相对倍数（1 = 适合宽度）
  let activeZoom = zoomMode;        // 当前语义档位（fitWidth / fitPage / actual / custom）
  let pageSizes = [];               // 每页 pt 尺寸
  let busy = false;
  let resizeTimer = null;

  function $(sel) { return document.querySelector(sel); }

  function setStatus(t) { if (status) status.textContent = t; }

  /* ───────── 缩放计算 ───────── */
  function pagePt(i) { return pageSizes[i - 1] || { w: 612, h: 792 }; }

  function scaleFor(mode) {
    if (mode === 'actual') return 1.0;
    var s = pagePt(curPage);
    var w = viewer.clientWidth - 24, h = viewer.clientHeight - 12;
    if (mode === 'fitPage') return Math.min(w / s.w, h / s.h);
    return w / s.w; // fitWidth
  }

  function currentScale() {
    if (activeZoom === 'custom') return scaleFor('fitWidth') * relScale;
    return scaleFor(activeZoom);
  }

  /* ───────── 渲染 ───────── */
  function makeCanvas(page, pageNum, scale) {
    var vp = page.getViewport({ scale: scale });
    var dpr = window.devicePixelRatio || 1;
    var canvas = document.createElement('canvas');
    canvas.width = Math.floor(vp.width * dpr);
    canvas.height = Math.floor(vp.height * dpr);
    canvas.style.width = vp.width + 'px';
    canvas.style.height = vp.height + 'px';
    var ctx = canvas.getContext('2d');
    ctx.save();
    ctx.scale(dpr, dpr);
    var renderTask = page.render({ canvasContext: ctx, viewport: vp });
    var wrap = document.createElement('div');
    wrap.className = 'pdf-page';
    wrap.dataset.page = pageNum;
    var num = document.createElement('div');
    num.className = 'pg-num';
    num.textContent = pageNum + ' / ' + total;
    wrap.appendChild(canvas);
    wrap.appendChild(num);
    return { wrap: wrap, renderTask: renderTask };
  }

  async function renderScroll() {
    viewer.innerHTML = '';
    for (var i = 1; i <= total; i++) {
      var page = await pdfDoc.getPage(i);
      var scale = currentScale();
      var item = makeCanvas(page, i, scale);
      viewer.appendChild(item.wrap);
      if (i % 3 === 0) setStatus('渲染中 ' + i + ' / ' + total);
    }
    setStatus('');
    syncButtons();
  }

  async function renderPaged() {
    viewer.innerHTML = '';
    var page = await pdfDoc.getPage(curPage);
    var scale = currentScale();
    var item = makeCanvas(page, curPage, scale);
    viewer.appendChild(item.wrap);
    setStatus('');
    pgInfo.textContent = curPage + ' / ' + total;
    syncButtons();
  }

  function render() {
    if (busy || !pdfDoc) return;
    if (pageMode === 'paged') renderPaged();
    else renderScroll();
  }

  /* ───────── 工具栏 / 交互 ───────── */
  function syncButtons() {
    btnFW.classList.toggle('active', activeZoom === 'fitWidth' || (activeZoom === 'custom' && relScale === 1));
    btnFP.classList.toggle('active', activeZoom === 'fitPage');
    btnActual.classList.toggle('active', activeZoom === 'actual');
    if (pgNav) pgNav.style.display = pageMode === 'paged' ? 'flex' : 'none';
    if (pageMode === 'paged') {
      btnPrev.disabled = curPage <= 1;
      btnNext.disabled = curPage >= total;
    }
  }

  function setZoom(mode) {
    activeZoom = mode;
    relScale = 1;
    render();
  }

  function goPage(delta) {
    var next = Math.min(total, Math.max(1, curPage + delta));
    if (next !== curPage) { curPage = next; render(); }
  }

  /* Ctrl+0/=/− 注入：1.0 → 适合宽度，其他 → 相对倍数 */
  window.__seemeApplyZoom = function (z) {
    if (!pdfDoc) return;
    if (z === 1 || z <= 0) { setZoom('fitWidth'); return; }
    activeZoom = 'custom';
    relScale = z;
    render();
  };

  function zoomBy(delta) {
    var base = activeZoom === 'custom' ? relScale : 1;
    relScale = Math.min(2.5, Math.max(0.5, base + delta));
    activeZoom = 'custom';
    render();
  }

  window.addEventListener('keydown', function (e) {
    if (!pdfDoc) return;
    if (e.ctrlKey && (e.key === '=' || e.key === '+')) { zoomBy(0.1); e.preventDefault(); }
    else if (e.ctrlKey && (e.key === '-' || e.key === '_')) { zoomBy(-0.1); e.preventDefault(); }
    else if (pageMode === 'paged' && (e.key === 'ArrowLeft' || e.key === 'PageUp')) { goPage(-1); e.preventDefault(); }
    else if (pageMode === 'paged' && (e.key === 'ArrowRight' || e.key === 'PageDown')) { goPage(1); e.preventDefault(); }
  });

  viewer.addEventListener('dblclick', function (e) {
    if (!dblClickZoom || !pdfDoc) return;
    if (e.target && e.target.closest && e.target.closest('button')) return;
    // 双击：100% ↔ 适合宽度 切换
    setZoom(activeZoom === 'actual' ? 'fitWidth' : 'actual');
  });

  btnFW.addEventListener('click', function () { setZoom('fitWidth'); });
  btnFP.addEventListener('click', function () { setZoom('fitPage'); });
  btnActual.addEventListener('click', function () { setZoom('actual'); });
  btnPrev.addEventListener('click', function () { goPage(-1); });
  btnNext.addEventListener('click', function () { goPage(1); });

  window.addEventListener('resize', function () {
    clearTimeout(resizeTimer);
    resizeTimer = setTimeout(function () {
      if (!pdfDoc) return;
      if (activeZoom !== 'custom') render();
      else render(); // custom 也重排（保持相对倍数）
    }, 150);
  });

  // 探测虚拟主机是否支持 HTTP Range：206 + 可读 Content-Range → 流式按需取页（大文件省内存）；
  // 顺带从开放 Range 的 Content-Range 解析总长，供 fetch patch 注入 Content-Length。
  // 否则回退全量 ArrayBuffer（兼容不支持 Range 的环境）。
  async function probeRange(url) {
    try {
      var r1 = await fetch(url, { headers: { Range: 'bytes=0-31' } });
      var cr1 = r1.headers.get('content-range') || '';
      var r2 = await fetch(url, { headers: { Range: 'bytes=0-' } });
      var cr2 = r2.headers.get('content-range') || '';
      var cm0 = cr2.match(/bytes\s+\d+-\d+\/(\d+)/);
      if (cm0) pdfTotalLen = parseInt(cm0[1], 10);
      return r1.status === 206 && cr1.indexOf('bytes') === 0;
    } catch (err) { log('range probe failed: ' + err.message); return false; }
  }

  async function load() {
    if (!pdfUrl) { setStatus('无 PDF'); return; }
    setStatus('加载中…');
    try {
      await initPdfjs();
      if (await probeRange(pdfUrl)) {
        // 流式：fetch patch 已给无 Range 的 PDF 响应注入 Accept-Ranges + Content-Length，PDF.js 识别后
        // 走 range 模式。关键：必须 disableStream（否则 PDF.js 的流式 full reader 继续读完整本）+
        // disableAutoFetch（关闭后台预取），只按需取页，大文件内存友好。
        log('stream mode');
        pdfDoc = await pdfjsLib.getDocument({ url: pdfUrl, disableAutoFetch: true, disableStream: true, rangeChunkSize: 65536 }).promise;
      } else {
        // 回退：全量 fetch 后以 ArrayBuffer 传入 getDocument（虚拟主机不支持 Range 时）
        log('buffer mode');
        var pdfData = await (await fetch(pdfUrl)).arrayBuffer();
        log('pdf fetched ' + pdfData.byteLength + ' bytes');
        pdfDoc = await pdfjsLib.getDocument({ data: new Uint8Array(pdfData) }).promise;
      }
      total = pdfDoc.numPages;
      pageSizes = [];
      for (var i = 1; i <= total; i++) {
        var p = await pdfDoc.getPage(i);
        var vp = p.getViewport({ scale: 1 });
        pageSizes.push({ w: vp.width, h: vp.height });
      }
      setStatus('渲染中…');
      log('loaded ' + total + ' pages');
      render();
    } catch (err) {
      setStatus('PDF 加载失败：' + (err && err.message ? err.message : err));
      log('pdf load error: ' + (err && err.stack || err));
    }
  }

  load();
})();
