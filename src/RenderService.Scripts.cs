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
        /// 椤靛唴鎼滅储鑴氭湰锛坢d 涓? Office 椤靛叡鐢級锛氬涓荤粡 __seemeSearch/Next/Prev 璋冪敤锛?
        /// 楂樹寒 mark 骞剁粡 postMessage 鍥炴姤 search-result銆俆reeWalker 蹇収鍏堟敹闆嗗啀鏀? DOM锛堥伩鍏嶈烦鑺傜偣锛夈??
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
