// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace SeeMe
{
    public partial class MainWindow
    {
        // ═══════ 文件职责：anydoc-wasm（WebAssembly 转 Markdown）+ Office 回退渲染 ═══════
        // 由 MainWindow.Rendering.cs 拆出（2026-09-10）。方案 B：docx/pptx/xlsx 通过 WebView2 页内
        // anydoc-wasm 转 Markdown，再走 Markdig 渲染。链路：文件目录映射虚拟主机（docfiles.example，
        // 与 PDF 同款模式）→ 桥接页 fetch 字节 → 经典 blob worker（initSync 字节初始化，绕开 module
        // worker）→ Markdown 回传 → Markdig 渲染。
        // 验证器/资源见 Resources/anydoc/；失败 / 超时 / 资源缺失 → 回退 FallbackOfficeAsync
        // （docx 走 docx-preview，xlsx/pptx 走 FileConverter）。隐藏 ExtractView 复用同一桥接页做
        // PDF 文本层提取。



        // ═══════════════ anydoc-wasm（WebAssembly 转 Markdown）渲染 ═══════════════
        // 方案 B：docx/pptx/xlsx 通过 WebView2 页内 anydoc-wasm 转 Markdown，再走 Markdig 渲染。
        // 链路：文件目录映射虚拟主机（docfiles.example，与 PDF 同款模式）→ 桥接页 fetch 字节 →
        // 经典 blob worker（initSync 字节初始化，绕开 module worker）→ Markdown 回传 → Markdig 渲染。
        // 失败 / 超时 / 资源缺失 → 回退 FileConverter（原 OpenXML/PdfPig 解析）。

        /// <summary>anydoc-wasm 离线资产是否齐全（worker + 经典胶水 + wasm 主体）。</summary>
        private static bool AnyDocAssetsPresent()
        {
            try
            {
                var root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "anydoc");
                return File.Exists(Path.Combine(root, "worker.js"))
                    && File.Exists(Path.Combine(root, "anydoc_wasm_classic.js"))
                    && File.Exists(Path.Combine(root, "anydoc_wasm_bg.wasm"));
            }
            catch { return false; }
        }

        /// <summary>docx/pptx/xlsx 渲染入口：优先 anydoc-wasm，缺失/失败自动回退 FileConverter。</summary>
        private async Task RenderOfficeAnyDoc(PanelState state, string ext)
        {
            if (string.IsNullOrEmpty(state.CurrentFile)) return;
            if (CheckFileSize(state)) return;
            // xlsx/xlsm 高保真：Univer 原生渲染（多 sheet / 公式 / 样式全保留），替代 anydoc→Markdown
            if (ext.Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".xlsm", StringComparison.OrdinalIgnoreCase))
            {
                await RenderExcelUniverAsync(state);
                return;
            }
            if (!AnyDocAssetsPresent()) { await FallbackOfficeAsync(state, ext); return; }
            var isDark = _theme.Current == _theme.Dark;
            // 小文档（≤1MB）跳过首个加载占位，直接进桥接页（自带 spinner），更快呈现；大文档保留占位防空白
            if (new FileInfo(state.CurrentFile).Length > 1L * 1024 * 1024)
                state.WebView.NavigateToString(BuildLoadingPage(isDark));

            var dir = Path.GetDirectoryName(state.CurrentFile);
            var name = Path.GetFileName(state.CurrentFile);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name)) { await FallbackOfficeAsync(state, ext); return; }

            // 与 RenderPdf 同款：把文件所在目录映射为虚拟主机，页面直接 fetch 字节（免 base64 / 大字符串注入）
            const string hostName = "docfiles.example";
            try
            {
                state.WebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    hostName, dir, CoreWebView2HostResourceAccessKind.Allow);
            }
            catch { /* 同一目录重复映射会抛异常，忽略（映射已存在） */ }

            var docUrl = $"https://{hostName}/{System.Uri.EscapeDataString(name)}";
            var token = Guid.NewGuid().ToString("N");
            state.AnyDocPending = new AnyDocPending { Token = token, File = state.CurrentFile, Ext = ext };
            state.WebView.NavigateToString(BuildAnyDocBridgePage(isDark, docUrl, token, ext));
            _ = AnyDocWatchdogAsync(state, token);
        }

        /// <summary>xlsx/xlsm 高保真渲染（Univer）：字节注入 Univer 页面原生渲染，多 sheet/公式/样式全保留。</summary>
        private async Task RenderExcelUniverAsync(PanelState state)
        {
            // 显式守卫：本方法是入口，无前置判空。空路径继续走下去会在
            // File.ReadAllBytes 抛 ArgumentNullException，错误页信息反而不如这里明确。
            if (state.CurrentFile is not { } path)
            {
                RenderErrorPage(state, new InvalidOperationException("未指定文件路径"));
                return;
            }

            try
            {
                var isDark = _theme.Current == _theme.Dark;
                var bytes = await Task.Run(() => File.ReadAllBytes(path));
                state.WebView.NavigateToString(RenderService.BuildUniverPage(isDark, bytes));
                MarkLoaded(state);
                SetOutline(null);
                LogInfo("Univer xlsx: " + Path.GetFileName(path));
            }
            catch (Exception ex)
            {
                LogErr("Univer render: " + ex);
                RenderErrorPage(state, ex);
            }
        }

        /// <summary>超时保护：桥接页/worker 5 秒未回结果则回退 FileConverter，避免永远停在加载页。</summary>
        private async Task AnyDocWatchdogAsync(PanelState state, string token)
        {
            await Task.Delay(5000);
            var pending = state.AnyDocPending;
            if (pending != null && pending.Token == token)
            {
                state.AnyDocPending = null;
                LogErr("AnyDoc timeout -> fallback: " + Path.GetFileName(state.CurrentFile));
                try { await FallbackOfficeAsync(state, pending.Ext); }
                catch (Exception ex) { LogErr("AnyDoc fallback: " + ex); }
            }
        }

        /// <summary>
        /// PDF 文本层提取：在隐藏的 ExtractView 里跑 anydoc 桥接页（fetch 字节 → worker → Markdown），
        /// 写缓存供内容搜索；扫描版写 .no 标记。结果经 OnExtractMessage 按令牌匹配面板回写。
        /// 后台执行，不打断面板当前视图；失败/超时静默，不阻断 PDF.js 渲染。
        /// </summary>
        private async Task TryExtractPdfTextAsync(PanelState state)
        {
            try
            {
                if (string.IsNullOrEmpty(state.CurrentFile)) return;
                if (!AnyDocAssetsPresent()) return;
                if (ExtractView?.CoreWebView2 == null) return; // 未初始化
                // 有文本缓存直接应用，不重跑桥接页。
                // 注意：.no 标记（上次判定扫描版）不短路——提取可能瞬时失败被误判（曾致文本型 PDF
                // 永远进不了文本视图），每次会话至少重试一次（anydoc 毫秒级，成本可忽略）。
                if (PdfTextCache.TryRead(state.CurrentFile, out var cachedText, out var cachedHasText) && cachedHasText)
                {
                    state.PdfText = cachedText ?? "";
                    state.PdfTextLayer = true;
                    _ = Dispatcher.BeginInvoke(new Action(() => RefreshStats(state)));
                    return;
                }
                var dir = Path.GetDirectoryName(state.CurrentFile);
                var name = Path.GetFileName(state.CurrentFile);
                if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name)) return;

                const string hostName = "docfiles.example";
                try
                {
                    ExtractView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                        hostName, dir, CoreWebView2HostResourceAccessKind.Allow);
                }
                catch { /* 同一目录重复映射会抛异常，忽略 */ }

                var docUrl = $"https://{hostName}/{System.Uri.EscapeDataString(name)}";
                var token = Guid.NewGuid().ToString("N");
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                state.AnyDocPending = new AnyDocPending
                {
                    Token = token, File = state.CurrentFile, Ext = ".pdf",
                    PdfExtract = true, ExtractTcs = tcs
                };
                ExtractView.NavigateToString(BuildAnyDocBridgePage(_theme.Current == _theme.Dark, docUrl, token, ".pdf"));
                // 等回传（OnExtractMessage 写缓存 + SetResult），后台超时 8s 静默放弃
                await Task.WhenAny(tcs.Task, Task.Delay(8000));
            }
            catch (Exception ex) { LogErr("Pdf text extract: " + ex.Message); }
        }

        /// <summary>anydoc 桥接页：加载经典 worker 胶水，fetch 文档字节转 Markdown，回传宿主。</summary>
        private string BuildAnyDocBridgePage(bool isDark, string docUrl, string token, string ext)
        {
            var cls = isDark ? " class='dark'" : "";
            var jsDocUrl = System.Text.Json.JsonSerializer.Serialize(docUrl);
            var jsToken = System.Text.Json.JsonSerializer.Serialize(token);
            var jsExt = System.Text.Json.JsonSerializer.Serialize(ext);
            var nonce = RenderService.NewNonce();
            return $@"<!DOCTYPE html>
<html{cls}><head><meta charset='utf-8'/>
<meta name='viewport' content='width=device-width,initial-scale=1'/>
<meta name='referrer' content='no-referrer'/>
<meta http-equiv='Content-Security-Policy' content=""default-src 'self' https://appassets.example https://docfiles.example; script-src 'nonce-{nonce}' https://appassets.example 'wasm-unsafe-eval'; style-src 'unsafe-inline'; img-src 'self' data: https://appassets.example; worker-src 'self' blob: https://appassets.example; connect-src 'self' https://appassets.example https://docfiles.example; base-uri 'self'; form-action 'none';"">
<style>
{RenderService.ThemeCss()}
{RenderService.PageResetCss}
  html,body {{ width:100%; height:100vh; }}
  body {{ display:flex; align-items:center; justify-content:center; background:var(--bg); color:var(--text);
         font-family:'Segoe UI','Microsoft YaHei',sans-serif; font-size:15px; }}
  {RenderService.SpinnerCss}
</style>
</head><body><span class='spin'></span>解析文档…</body>
<script nonce='{nonce}'>
window.__ANYDOC_CONF = {{ url: {jsDocUrl}, id: {jsToken}, ext: {jsExt} }};
(function(){{
  var conf = window.__ANYDOC_CONF;
  function post(obj){{ try {{ window.chrome.webview.postMessage(JSON.stringify(obj)); }} catch(e){{}} }}
  function fail(err){{ post({{kind:'anydoc-result', id: conf.id, ok:false, error: String(err)}}); }}
  function start(workerText){{
    var w = null;
    try {{ w = new Worker(URL.createObjectURL(new Blob([workerText], {{type:'text/javascript'}}))); }}
    catch(err){{ fail(err); return; }}
    w.onmessage = function(e){{
      if(e.data && e.data.id === conf.id){{
        w.terminate();
        post({{kind:'anydoc-result', id: conf.id, ok: !!e.data.ok, markdown: e.data.markdown || '', error: e.data.error || ''}});
      }}
    }};
    w.onerror = function(e){{ try{{ w.terminate(); }}catch(_){{}} fail(e.message || 'worker error'); }};
    w.postMessage({{ id: conf.id, url: conf.url, ext: conf.ext }});
  }}
  fetch('https://appassets.example/anydoc/worker.js')
    .then(function(r){{ if(!r.ok) throw new Error('worker fetch ' + r.status); return r.text(); }})
    .then(start)
    .catch(fail);
}})();
</script>
</body></html>";
        }

        /// <summary>
        /// anydoc Markdown → 主题化 HTML 页面（Markdig 管线 + 消毒/后处理 + 卡片 + Render 外壳）。
        /// Office 渲染与 PDF 文本视图共用；PDF 文本视图通过 extraCsp/bodySuffix 注入「页面图像」图库。
        /// </summary>
        private async Task<(string Html, List<TocItem> TocItems)> BuildAnyDocMarkdownPageAsync(PanelState state, string md)
        {
            var htmlRaw = await Task.Run(() =>
            {
                var parsed = Markdig.Markdown.ToHtml(md ?? "", _render.Pipeline);
                parsed = _render.ProcessRelativePaths(parsed, state.FileDir);
                parsed = _render.WrapTables(parsed);
                parsed = _render.UpgradePreBlocks(parsed);
                // 行号注入：UpgradePreBlocks 之后、Prism 之前（详见 RenderService.AddCodeLineNumbers）
                parsed = _render.AddCodeLineNumbers(parsed);
                parsed = _render.SanitizeLinkHrefs(parsed);
                return parsed;
            });
            var fmCard = _render.BuildFrontMatterCard(null);
            var statsCard = BuildStatsCard(md ?? "");
            var tocCard = _render.BuildTocCard(htmlRaw);
            var tocItems = _render.BuildTocItems(htmlRaw);
            EnsureRemoteImagePolicy(state, htmlRaw);
            var html = _render.Render(htmlRaw, state, fmCard + tocCard + statsCard, this, null, LoadCustomCss());
            return (html, tocItems);
        }

        /// <summary>把 anydoc 产出的 Markdown 走 Markdig 管线渲染（与 md 文件同链路，含全部消毒/后处理）。</summary>
        private async Task RenderAnyDocMarkdownAsync(PanelState state, string md)
        {
            if (string.IsNullOrEmpty(state.CurrentFile)) return;
            try
            {
                var (html, tocItems) = await BuildAnyDocMarkdownPageAsync(state, md);
                SetOutline(tocItems);
                // 存储 anydoc 原始 Markdown，供 docx 内联编辑使用（编辑模式直接编辑这份内容）
                state.DocxMarkdown = md;
                _ = Dispatcher.BeginInvoke(new Action(() => RefreshStats(state))); // anydoc 转换完成 → 统计卡刷新
                state.WebView.NavigateToString(html);
                MarkLoaded(state);
                LogInfo("AnyDoc ok: " + Path.GetFileName(state.CurrentFile) + " (" + (md?.Length ?? 0) + " chars)");
            }
            catch (Exception ex)
            {
                LogErr("AnyDoc render: " + ex);
                RenderErrorPage(state, ex);
            }
        }

        /// <summary>回退到本地解析器。docx 走 docx-preview 高保真渲染（替代 OpenXML 自解析）；
        /// xlsx/pptx 走 FileConverter；新增的 anydoc 格式（doc/ppt/rtf/odt/ods/odp/epub/csv）无回退，直接抛错走错误页。</summary>
        private async Task FallbackOfficeAsync(PanelState state, string ext)
        {
            if (string.IsNullOrEmpty(state.CurrentFile)) return;
            try
            {
                var isDark = _theme.Current == _theme.Dark;
                state.WebView.NavigateToString(BuildLoadingPage(isDark));
                if (ext.Equals(".docx", StringComparison.OrdinalIgnoreCase))
                {
                    // docx 高保真回退：docx-preview 浏览器端渲染（样式保真度远高于 OpenXML 自解析）
                    var bytes = await Task.Run(() => File.ReadAllBytes(state.CurrentFile));
                    state.WebView.NavigateToString(RenderService.BuildDocxPreviewPage(isDark, bytes));
                    MarkLoaded(state);
                    SetOutline(null);
                    return;
                }
                string content = await OfficeFallback.ConvertAsync(_converter, ext, state.CurrentFile);
                var html = _render.BuildOfficePage(content, state, this);
                state.WebView.NavigateToString(html);
                MarkLoaded(state);
                SetOutline(null);
            }
            catch (Exception ex)
            {
                LogErr("Office fallback: " + ex);
                RenderErrorPage(state, ex);
            }
        }
    }
}
