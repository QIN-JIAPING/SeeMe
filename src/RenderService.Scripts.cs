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
    /// <summary>RenderService 分部类：注入页面的 JS 片段（setTheme / 搜索 / 标注 / 键盘桥 / 图表）。</summary>
    public partial class RenderService
    {
        /// <summary>
        /// 统一 setTheme（全页面唯一来源）：html.dark 切换 + body.dark 同步 + seeme-theme 事件
        /// + Prism 样式表亮暗互换 + Mermaid 重绘。各子能力自带存在性守卫，
        /// 在没有 Prism/Mermaid/ECharts 的占位页上均为无害空操作。
        /// </summary>
        public static string SetThemeScript =>
@"function setTheme(dark){
  dark = !!dark;
  var html=document.documentElement;
  if(dark) html.classList.add('dark'); else html.classList.remove('dark');
  if(document.body) document.body.classList.toggle('dark', dark);
  try{ window.dispatchEvent(new CustomEvent('seeme-theme',{detail:dark})); }catch(e){}
  var links=document.querySelectorAll('link[rel=stylesheet]');
  for(var i=0;i<links.length;i++){
    var href=links[i].getAttribute('href')||'';
    if(href.indexOf('prism.min.css')<0 && href.indexOf('prism-tomorrow.min.css')<0) continue;
    var isDarkCss = href.indexOf('prism-tomorrow')>=0;
    if(dark && !isDarkCss) links[i].setAttribute('href', href.replace('prism.min.css','prism-tomorrow.min.css'));
    else if(!dark && isDarkCss) links[i].setAttribute('href', href.replace('prism-tomorrow.min.css','prism.min.css'));
  }
  try {
    if(window.mermaid){
      document.querySelectorAll('.mermaid').forEach(function(el){
        if(!el.getAttribute('data-orig')) el.setAttribute('data-orig', el.innerHTML);
        el.innerHTML = el.getAttribute('data-orig');
        el.removeAttribute('data-processed');
      });
      mermaid.initialize({startOnLoad:false, theme: dark ? 'dark' : 'default'});
      mermaid.run({nodes:document.querySelectorAll('.mermaid')}).catch(function(e){});
    }
  }catch(e){}
}";

        /// <summary>ECharts init: parse data-echarts JSON, re-init on seeme-theme event.</summary>
        public static string ChartInitScript =>
            @"(function(){
  function themeDark(){ return document.documentElement.classList.contains('dark'); }
  function initAll(){
    document.querySelectorAll('.seeme-chart').forEach(function(el){
      if(el.__seemeChart) return;
      try{
        var opt=JSON.parse(el.getAttribute('data-echarts'));
        var c=echarts.init(el, themeDark()?'dark':null);
        c.setOption(opt);
        el.__seemeChart=c;
        window.addEventListener('resize', function(){ try{ c.resize(); }catch(e){} });
      }catch(e){ el.textContent='[ECharts 渲染失败] ' + e.message; }
    });
  }
  initAll();
  document.addEventListener('seeme-theme', function(){
    document.querySelectorAll('.seeme-chart').forEach(function(el){
      var c=el.__seemeChart;
      if(c){ try{ c.dispose(); }catch(e){} el.__seemeChart=null; el.removeAttribute('_echarts_instance_'); }
    });
    initAll();
  });
})();";

        /// <summary>markmap mind map: ESM module script, render data-md Markdown into SVG.</summary>
        public static string MarkmapInitScript =>
            @"import { Transformer } from 'https://appassets.example/markmap/markmap-lib.min.js';
import { Markmap } from 'https://appassets.example/markmap/markmap-view.min.js';
(function(){
  var transformer=new Transformer();
  document.querySelectorAll('.seeme-markmap').forEach(function(el){
    try{
      var md=el.getAttribute('data-md')||'';
      var data=transformer.transform(md);
      var svg=document.createElementNS('http://www.w3.org/2000/svg','svg');
      el.appendChild(svg);
      Markmap.create(svg, {colorFreezeLevel:2, duration:300, initialExpandLevel:3}, data.root);
    }catch(e){ el.textContent='[Markmap 渲染失败] ' + e.message; }
  });
})();";

        /// <summary>
        /// 页内搜索脚本（md 与 Office 页共用）：宿主经 __seemeSearch/Next/Prev 调用，
        /// 高亮 mark 并经 postMessage 回报 search-result。TreeWalker 快照先收集再改，避免边改边遍历。
        /// </summary>
        public static string SearchScript =>
            @"
var __searchMarks=[], __searchIdx=0, __searchQuery='';
function __searchReport(){ try{ window.chrome.webview.postMessage(JSON.stringify({kind:'search-result',count:__searchMarks.length,current:__searchMarks.length?__searchIdx+1:0})); }catch(e){} }
function __searchClear(){
  for(var i=0;i<__searchMarks.length;i++){ var m=__searchMarks[i]; if(m && m.parentNode) m.parentNode.replaceChild(document.createTextNode(m.textContent), m); }
  __searchMarks=[]; __searchIdx=0;
}
window.__seemeSearch=function(q){
  __searchClear();
  __searchQuery=(q||'');
  if(!__searchQuery){ __searchReport(); return; }
  var needle=__searchQuery.toLowerCase();
  var allNodes=[];
  var walker=document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT, null, false);
  var node;
  while((node=walker.nextNode())) allNodes.push(node);
  for(var n=0;n<allNodes.length;n++){
    var tn=allNodes[n];
    var p=tn.parentNode;
    if(!p || p.tagName==='SCRIPT' || p.tagName==='STYLE' || p.tagName==='MARK') continue;
    var anc=p.closest ? p.closest('.fm-card') : null;
    if(anc) continue;
    var text=tn.textContent, lower=text.toLowerCase(), idx=0;
    while((idx=lower.indexOf(needle, idx))>=0){
      try{
        var range=document.createRange();
        range.setStart(tn, idx); range.setEnd(tn, idx+needle.length);
        var mark=document.createElement('mark');
        mark.className='seeme-hl';
        range.surroundContents(mark);
        __searchMarks.push(mark);
        text=tn.textContent; lower=text.toLowerCase(); idx=0;
      }catch(e){ break; }
    }
  }
  if(__searchMarks.length) __searchMarks[0].scrollIntoView({behavior:'smooth',block:'center'});
  __searchReport();
};
window.__seemeSearchNext=function(){
  if(!__searchMarks.length) return;
  __searchMarks[__searchIdx].className='seeme-hl';
  __searchIdx=(__searchIdx+1)%__searchMarks.length;
  __searchMarks[__searchIdx].className='seeme-hl seeme-cur';
  __searchMarks[__searchIdx].scrollIntoView({behavior:'smooth',block:'center'});
  __searchReport();
};
window.__seemeSearchPrev=function(){
  if(!__searchMarks.length) return;
  __searchMarks[__searchIdx].className='seeme-hl';
  __searchIdx=(__searchIdx-1+__searchMarks.length)%__searchMarks.length;
  __searchMarks[__searchIdx].className='seeme-hl seeme-cur';
  __searchMarks[__searchIdx].scrollIntoView({behavior:'smooth',block:'center'});
  __searchReport();
};";

        /// <summary>
        /// 代码块增强脚本：行级复制按钮 + 整块复制按钮 + 行号开关。
        ///
        /// <para><b>复制内容取自 textContent 而不是 innerHTML</b> —— innerHTML 会带上 Prism 的
        /// <c>&lt;span class="token"&gt;</c> 标签，粘到编辑器里是一堆 HTML。textContent 天然只含纯文本；
        /// 行号由 CSS <c>::before</c> 生成，**不进 textContent**，所以复制结果不含行号，无需清洗。</para>
        ///
        /// <para><b>剪贴板走宿主</b>：WebView2 内 <c>navigator.clipboard.writeText</c> 需要
        /// 页面获得焦点与安全上下文授权，实测在 NavigateToString 的 about:blank 派生页上不可靠；
        /// 统一 postMessage 回宿主用 WPF <c>Clipboard.SetText</c> 执行（与既有「复制路径」同一实现）。</para>
        /// </summary>
        public static string CodeBlockScript =>
@"(function(){
  if(window.__seemeCode) return; window.__seemeCode=true;
  function post(obj){ try{ window.chrome.webview.postMessage(JSON.stringify(obj)); }catch(e){} }
  function copyText(t){ if(!t) return; post({kind:'copy-text',text:t}); }
  document.querySelectorAll('pre.have-lines').forEach(function(pre){
    // ── 行级复制按钮：只挂在带行号的行上（超限降级的块没有 .seeme-line 子节点，自然跳过）──
    pre.querySelectorAll(':scope > code > .seeme-line').forEach(function(ln){
      var b=document.createElement('button');
      b.className='seeme-lncpy'; b.type='button'; b.textContent='⧉'; b.title='复制此行';
      b.addEventListener('click', function(e){
        e.preventDefault(); e.stopPropagation();
        // textContent 已含该行末尾换行（\n 保留在行内容之后），复制后可直接粘成一行
        copyText(ln.textContent);
      });
      ln.appendChild(b);
    });
    // ── 块工具栏：整块复制 + 行号显示开关（纯观感，不影响复制内容）──
    var bar=document.createElement('div');
    bar.className='seeme-codebar';
    var copyBtn=document.createElement('button');
    copyBtn.type='button'; copyBtn.textContent='复制'; copyBtn.title='复制整块代码';
    copyBtn.addEventListener('click', function(e){
      e.preventDefault(); e.stopPropagation();
      var code=pre.querySelector('code');
      copyText(code?code.textContent:'');
    });
    var lnBtn=document.createElement('button');
    lnBtn.type='button'; lnBtn.textContent='行号'; lnBtn.title='显示 / 隐藏行号';
    lnBtn.addEventListener('click', function(e){
      e.preventDefault(); e.stopPropagation();
      pre.classList.toggle('seeme-nolines');
    });
    bar.appendChild(copyBtn); bar.appendChild(lnBtn);
    pre.appendChild(bar);
  });
})();";

        /// <summary>
        /// 图表导出脚本：把页内 ECharts / markmap / Mermaid 图表序列化为 SVG 或 PNG 回传宿主。
        ///
        /// <para><b>为什么必须经宿主落盘</b>：页面在 WebView2 里，CSP 与 file: 限制下无法直接写盘；
        /// 且 SVG 文本需要宿主决定编码与保存路径。所以页面只负责「导出内容 → postMessage」，
        /// 宿主负责「弹保存框 → 写文件」（见 MainWindow.Rendering.Chart.cs）。</para>
        ///
        /// <para><b>三种图的导出途径各不相同</b>：
        /// ① ECharts：有官方 <c>getDataURL()</c>，可直接出 PNG；SVG 需 renderer 为 svg 模式，
        ///    本应用用的是默认 canvas renderer，故对 ECharts 只提供 PNG。
        /// ② markmap：输出的是真实 SVG DOM，直接 <c>outerHTML</c> 序列化即可，**无需栅格化**（矢量无损）。
        /// ③ Mermaid：渲染后同样是 SVG DOM，序列化路径与 markmap 一致。</para>
        ///
        /// <para><b>PNG 栅格化的画布污染陷阱</b>：SVG 序列化后用 <c>&lt;img&gt; + canvas</c> 转 PNG 时，
        /// 若 SVG 内引用了外部资源（图片/字体）会让 canvas 变成 tainted，<c>toDataURL</c> 抛 SecurityError。
        /// 这里统一走 <c>data:</c> URI 加载 SVG（不触网），并对异常回退为「提示改导出 SVG」。</para>
        /// </summary>
        public static string ChartExportScript =>
@"(function(){
  if(window.__seemeChartExport) return; window.__seemeChartExport=true;
  function post(obj){ try{ window.chrome.webview.postMessage(JSON.stringify(obj)); }catch(e){} }
  // 图表容器定位：data-echart-index / data-markmap-index / data-mermaid-index 由页面按顺序标注。
  // 用「页面内的第 N 个同类图表」作为稳定标识 —— 页面重渲染后顺序不变，索引仍可复用。
  function collect(kind){
    if(kind==='echarts') return Array.prototype.slice.call(document.querySelectorAll('.seeme-chart'));
    if(kind==='markmap') return Array.prototype.slice.call(document.querySelectorAll('.seeme-markmap'));
    return Array.prototype.slice.call(document.querySelectorAll('.mermaid'));
  }
  function svgOf(el){
    if(!el) return '';
    var svg = el.tagName==='SVG' ? el : el.querySelector('svg');
    if(!svg) return '';
    // 序列化前补上命名空间与显式尺寸：缺 xmlns 的 SVG 存成文件后无法被浏览器/编辑器识别
    var clone = svg.cloneNode(true);
    if(!clone.getAttribute('xmlns')) clone.setAttribute('xmlns','http://www.w3.org/2000/svg');
    if(!clone.getAttribute('xmlns:xlink')) clone.setAttribute('xmlns:xlink','http://www.w3.org/1999/xlink');
    var w = svg.getBoundingClientRect().width, h = svg.getBoundingClientRect().height;
    if(w>0 && !clone.getAttribute('width')) clone.setAttribute('width', Math.round(w));
    if(h>0 && !clone.getAttribute('height')) clone.setAttribute('height', Math.round(h));
    return '<?xml version=""1.0"" encoding=""UTF-8""?>' + new XMLSerializer().serializeToString(clone);
  }
  function svgToPng(svgText, scale){
    return new Promise(function(resolve, reject){
      var el = collect('markmap').concat(collect('mermaid')).find(function(c){
        var s = c.tagName==='SVG'?c:c.querySelector('svg'); return s && svgText.indexOf(s.getAttribute('id')||'\u0000')>=0;
      });
      var base = el ? (el.tagName==='SVG'?el:el.querySelector('svg')) : null;
      var box = base ? base.getBoundingClientRect() : {width:800,height:520};
      var w = Math.max(1, Math.round(box.width)), h = Math.max(1, Math.round(box.height));
      var img = new Image();
      img.onload = function(){
        try{
          var cv = document.createElement('canvas');
          cv.width = w*scale; cv.height = h*scale;
          var ctx = cv.getContext('2d');
          // 背景铺白/铺当前主题底色：SVG 默认透明，PNG 在深色编辑器里会看不见内容
          var bg = getComputedStyle(document.body).backgroundColor || '#ffffff';
          ctx.fillStyle = (bg && bg!=='rgba(0, 0, 0, 0)') ? bg : '#ffffff';
          ctx.fillRect(0,0,cv.width,cv.height);
          ctx.setTransform(scale,0,0,scale,0,0);
          ctx.drawImage(img,0,0,w,h);
          resolve(cv.toDataURL('image/png'));
        }catch(e){ reject(e); }
      };
      img.onerror = function(){ reject(new Error('SVG 解码失败')); };
      img.src = 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(svgText);
    });
  }
  // tok 是宿主每次导出请求生成的会话令牌，原样回传 → 宿主据此丢弃过期/伪造的回传。
  // 不校验它的话，页面上任何 postMessage 都能顶替用户选中的那次导出。
  window.__seemeExportChart = function(kind, index, fmt, tok){
    try{
      var list = collect(kind);
      if(!list.length) return post({kind:'chart-export-error',msg:'未找到可导出的图表'});
      var el = list[index];
      if(!el) return post({kind:'chart-export-error',msg:'图表序号越界（可能页面已变化，请重试）'});
      if(kind==='echarts'){
        if(!el.__seemeChart) return post({kind:'chart-export-error',msg:'图表尚未渲染完成'});
        // ECharts 官方接口，默认 canvas renderer → 只能出 PNG
        var url = el.__seemeChart.getDataURL({type:'png', pixelRatio:2, backgroundColor:null});
        return post({kind:'chart-export', token:tok, format:'png', data:url, index:index, chartKind:kind});
      }
      var svg = svgOf(el);
      if(!svg) return post({kind:'chart-export-error',msg:'该图表尚未生成 SVG'});
      if(fmt==='svg') return post({kind:'chart-export', token:tok, format:'svg', data:svg, index:index, chartKind:kind});
      svgToPng(svg, 2).then(function(dataUrl){
        post({kind:'chart-export', token:tok, format:'png', data:dataUrl, index:index, chartKind:kind});
      }).catch(function(e){
        post({kind:'chart-export-error',msg:'PNG 转换失败（该图可能引用外部资源），请改导出 SVG：' + e});
      });
    }catch(e){ post({kind:'chart-export-error', msg:String(e)}); }
  };
})();";

        /// <summary>
        /// 页内高亮标注脚本（md 与 Office 页共用）：选中文本 → &lt;mark&gt;，按文件持久化到 HighlightStore；
        /// 加载时把存储的标注按文本匹配重新包裹；右键标注可删除；点击标注通知宿主聚焦笔记面板对应条目。
        /// </summary>
        public static string BuildAnnotationScript(string itemsJson)
        {
            const string s = @"
// ═══ 选区高亮 + 笔记标注 ═══
window.__seemeAnn = { mode:false, items:[] };
window.__setAnnMode = function(on){ window.__seemeAnn.mode = !!on; if(document.body) document.body.classList.toggle('seeme-ann-active', on); };
function __annPost(obj){ try{ window.chrome.webview.postMessage(JSON.stringify(obj)); }catch(e){} }
window.__seemeApplyAnn = function(items){
  var marks = document.querySelectorAll('mark.seeme-ann');
  for(var i=marks.length-1;i>=0;i--){ var m=marks[i]; var t=document.createTextNode(m.textContent); if(m.parentNode) m.parentNode.replaceChild(t,m); }
  window.__seemeAnn.items = items || [];
  var list = window.__seemeAnn.items;
  var walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT, null, false);
  var nodes=[]; var n; while((n=walker.nextNode())) nodes.push(n);
  // 每条标注独立在全部文本节点中找匹配（同段落多条标注都能恢复；之前按节点优先只恢复第一条）
  for(var j=0;j<list.length;j++){
    var it=list[j]; if(!it||!it.text) continue;
    for(var k=0;k<nodes.length;k++){
      var tn=nodes[k]; var p=tn.parentNode;
      if(!p) continue;
      var tag=(p.tagName||'').toUpperCase();
      if(tag==='SCRIPT'||tag==='STYLE'||tag==='MARK'||tag==='CODE') continue;
      if(p.closest){ if(p.closest('pre')||p.closest('code')||p.closest('.fm-card')) continue; }
      var idx=tn.textContent.indexOf(it.text);
      if(idx<0) continue;
      try{
        var range=document.createRange();
        range.setStart(tn,idx); range.setEnd(tn,idx+it.text.length);
        var mark=document.createElement('mark');
        mark.className='seeme-ann'; mark.setAttribute('data-id',it.id);
        if(it.note) mark.title=it.note;
        range.surroundContents(mark);
      }catch(e){}
      break;
    }
  }
};
document.addEventListener('mouseup', function(e){
  var ann=window.__seemeAnn; if(!ann.mode) return;
  var sel=window.getSelection(); if(!sel||sel.isCollapsed) return;
  var text=(sel.toString()||'').trim();
  if(!text) return;
  var node=sel.anchorNode;
  if(node&&node.parentElement&&node.parentElement.closest){
    var skip=node.parentElement.closest('pre')||node.parentElement.closest('code')||node.parentElement.closest('.fm-card')||node.parentElement.closest('mark.seeme-ann');
    if(skip){ sel.removeAllRanges(); return; }
  }
  var range=sel.getRangeAt(0);
  if(!range||range.collapsed){ sel.removeAllRanges(); return; }
  try{
    var mark=document.createElement('mark');
    mark.className='seeme-ann';
    var id='ann'+Date.now().toString(36)+Math.floor(Math.random()*1e6).toString(36);
    mark.setAttribute('data-id',id);
    try{
      range.surroundContents(mark);
    }catch(err2){
      // 选区跨格式边界（strong/em/链接/标题）时 surroundContents 抛错：
      // 不产生脏状态，明确上报让宿主提示改选纯文本段落
      __annPost({kind:'highlight-fail',text:text,err:String(err2)});
      sel.removeAllRanges();
      return;
    }
    var ctxEl=mark.closest('h1,h2,h3,h4,h5,h6,p,blockquote,li,td,th');
    var ctx=ctxEl?ctxEl.textContent.trim():'';
    if(ctx.length>160) ctx=ctx.substring(0,160)+'…';
    __annPost({kind:'highlight-add',id:id,text:text,context:ctx});
  }catch(err){ __annPost({kind:'highlight-fail',text:text,err:String(err)}); }
  sel.removeAllRanges();
});
document.addEventListener('contextmenu', function(e){
  var t=e.target; var m=(t&&t.closest)?t.closest('mark.seeme-ann'):null;
  if(!m) return;
  e.preventDefault();
  var id=m.getAttribute('data-id');
  var txt=document.createTextNode(m.textContent);
  if(m.parentNode) m.parentNode.replaceChild(txt,m);
  __annPost({kind:'highlight-remove',id:id});
});
document.addEventListener('click', function(e){
  var t=e.target; var m=(t&&t.closest)?t.closest('mark.seeme-ann'):null;
  if(!m) return;
  var id=m.getAttribute('data-id');
  if(id) __annPost({kind:'highlight-click',id:id});
});
window.__seemeJumpToText = function(text){
  if(!text) return false;
  var walker=document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT, null, false);
  var tn; while((tn=walker.nextNode())){
    if(!tn.textContent || tn.textContent.indexOf(text)<0) continue;
    var p=tn.parentNode; if(!p) continue;
    var tag=(p.tagName||'').toUpperCase();
    if(tag==='SCRIPT'||tag==='STYLE'||tag==='MARK') continue;
    try{
      var idx=tn.textContent.indexOf(text);
      var range=document.createRange();
      range.setStart(tn, idx); range.setEnd(tn, idx+text.length);
      var m=document.createElement('mark');
      m.className='seeme-jump-hl';
      m.style.cssText='background:var(--accent);color:#fff;border-radius:2px;padding:1px 2px;';
      range.surroundContents(m);
      m.scrollIntoView({behavior:'smooth', block:'center'});
      setTimeout(function(){ try{ var t=document.createTextNode(m.textContent); m.parentNode.replaceChild(t,m);}catch(e){} }, 2200);
      return true;
    }catch(e){}
  }
  return false;
};
window.__seemeApplyAnn(__ANN_ITEMS__);
";
            return s.Replace("__ANN_ITEMS__", itemsJson ?? "[]");
        }

        /// <summary>
/// 全局键盘桥接：WebView2 有焦点时 WPF PreviewKeyDown 不可靠（Chromium 吞部分组合键），所以在每个渲染页注入 JS 监听器，
/// 把 Ctrl+Shift+P / F11 等直接 postMessage 给宿主——彻底绕开 WebView 焦点吞键问题。
/// </summary>
public const string KeyBridgeScript = @"
(function(){
  if(window.__seemeKeyBridge) return; window.__seemeKeyBridge=true;
  function post(kind, extra){ try{ window.chrome.webview.postMessage(JSON.stringify(Object.assign({kind:kind}, extra||{}))); }catch(e){} }
  document.addEventListener('keydown', function(e){
    var c=!!e.ctrlKey, s=!!e.shiftKey;
    if(c && s && (e.key==='P'||e.key==='p')){ e.preventDefault(); post('palette-toggle'); return; }
    if(!c && !s && e.key==='F11'){ e.preventDefault(); post('presentation-toggle'); return; }
  }, true);
})();";
    }
}
