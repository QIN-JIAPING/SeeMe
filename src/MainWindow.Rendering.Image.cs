// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

// MainWindow 分部类 —— 独立图片文件查看
//
// 承载 .png / .jpg / .gif / .bmp / .webp / .ico / .tif(f) 的直接查看。
// 这是 MainWindow.Rendering.cs 调度链上「图片分支」的实现侧。
//
// 安全设计（改本文件前必读）：
//   · 图片字节**不走 data: 内联**，而是复用已有的文档虚拟主机
//     （docfiles.example）+ GuardDocumentHost 的「单文件名白名单」。
//     页面只能取到与当前文件同名的资源，读不到同目录其他文件。
//   · .svg **不在 FileTypes.Image 中**（XML 可内嵌脚本，XSS 面与已封堵的 HTML 注入同类）。
//     本文件因此无需为 SVG 做任何特殊消毒 —— 它在入口就被挡掉了。
//   · 本文件额外做像素上限与文件大小校验（DoS 纵深），这是图像链路独有的。
//
// ⚠️ 已知缺口（2026-09-18 核实，非本文件引入）：
//   MagicBytes 校验目前**只在拖拽与 WebView 拖放两条路径**生效
//   （MainWindow.Files.cs:540 与 MainWindow.Messages.cs:109 的 FilterSupportedFiles）。
//   而主打开路径 OpenFileInternal（文件对话框 / 最近文件 / 命令行 / 文件关联）
//   **没有** IsSupported 与魔数校验 —— 即伪造扩展名的文件可以直接被打开。
//   这是既有行为，修它会影响所有格式（可能误拒当前能打开的文件），
//   需要显式决策，故未在本轮顺手改。图片链路的像素/体积上限是独立于该缺口的第二道闸。
//
// 依赖的 _theme 字段与 LogErr / LogInfo / MarkLoaded / CheckFileSize / BuildNoticePage
// 均定义于本 partial 类的其他文件，跨文件可见。

using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace SeeMe
{
    public partial class MainWindow
    {
        /// <summary>
        /// 图片查看入口：构建极简 HTML 页，用 &lt;img&gt; 铺满视口。
        ///
        /// <para>与 PDF 的差别：PDF 需要 JS 逐页重建，图片只需浏览器原生渲染，
        /// 因此这里不引入任何前端库，纯 CSS + 少量缩放脚本。</para>
        /// </summary>
        private void RenderImage(PanelState state)
        {
            if (string.IsNullOrEmpty(state.CurrentFile)) return;
            var path = state.CurrentFile!;

            // 1. 组件级文件大小闸（Images 的上限与文档不同：文档 50MB，图片 64MB）
            if (CheckImageSize(state, path)) { MarkLoaded(state); SetOutline(null); return; }

            // 2. 像素上限（读文件头，不解码整图）—— 20000×20000 解码后约 1.6GB，必须提前拒绝
            if (!TryReadImageDimensions(path, out var w, out var h))
            {
                state.WebView.NavigateToString(BuildNoticePage(
                    _theme.Current == _theme.Dark, "无法识别的图片",
                    "文件头无法解析出图片尺寸，可能已损坏或扩展名与内容不符。"));
                MarkLoaded(state); SetOutline(null);
                return;
            }
            if (w > Limits.MaxImagePixels || h > Limits.MaxImagePixels)
            {
                state.WebView.NavigateToString(BuildNoticePage(
                    _theme.Current == _theme.Dark, "图片过大",
                    $"尺寸 {w}×{h} 超过 {Limits.MaxImagePixels}×{Limits.MaxImagePixels} 上限，为避免内存耗尽不予显示。"));
                MarkLoaded(state); SetOutline(null);
                LogWarn("Image rejected (too large): " + w + "x" + h + " " + Path.GetFileName(path));
                return;
            }

            // 3. 目录映射到文档虚拟主机（与 Markdown/Office 用同一个 host，命名白名单已覆盖）
            var url = MapImageVirtualHost(state, path);
            if (string.IsNullOrEmpty(url))
            {
                RenderErrorPage(state, new Exception("无法映射图片目录"));
                return;
            }

            state.WebView.NavigateToString(BuildImagePage(state, url, w, h));
            MarkLoaded(state);
            SetOutline(null);
            LogInfo("Image view: " + Path.GetFileName(path) + " (" + w + "x" + h + ")");
        }

        /// <summary>图片专属大小上限校验（超限显示提示页并返回 true）。</summary>
        private bool CheckImageSize(PanelState state, string path)
        {
            try
            {
                var fi = new FileInfo(path);
                if (fi.Length > Limits.MaxImageFileBytes)
                {
                    var mb = Limits.MaxImageFileBytes / (1024 * 1024);
                    state.WebView.NavigateToString(BuildNoticePage(
                        _theme.Current == _theme.Dark, "图片过大",
                        $"文件超过 {mb}MB 限制，无法预览。"));
                    return true;
                }
            }
            catch { /* 读取失败交给后续流程报错 */ }
            return false;
        }

        /// <summary>
        /// 把图片所在目录映射到 docfiles.example，返回可 &lt;img src&gt; 的 URL。
        ///
        /// <para><b>为什么复用 docfiles.example 而不是新建 host</b>：
        /// GuardDocumentHost 已经为 docfiles.example 注册了 WebResourceRequestedFilter，
        /// 且白名单规则是「请求文件名 == 当前文件文件名」。图片场景天然满足该规则
        /// （页面只请求自己那一张图）。新开 host 反而要改 GuardDocumentHost 的过滤器集合，
        /// 扩大攻击面。</para>
        /// </summary>
        private string? MapImageVirtualHost(PanelState state, string path)
        {
            var dir = Path.GetDirectoryName(path);
            var name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name)) return null;
            try
            {
                state.WebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "docfiles.example", dir, CoreWebView2HostResourceAccessKind.Allow);
            }
            catch { /* 同一目录重复映射会抛异常，忽略（映射已存在） */ }
            return $"https://docfiles.example/{Uri.EscapeDataString(name)}";
        }

        /// <summary>
        /// 图片查看页：棋盘格背景（透明图可辨）+ &lt;img&gt; 居中 + 缩放交互。
        ///
        /// <para><b>CSP 要点</b>：只放行 docfiles.example（取图字节）。
        /// 连接目标不含任何外网 —— 图片页不需要网络。</para>
        /// </summary>
        private string BuildImagePage(PanelState state, string imgUrl, int w, int h)
        {
            var isDark = _theme.Current == _theme.Dark;
            var htmlClass = isDark ? " class='dark'" : "";
            var jsUrl = System.Text.Json.JsonSerializer.Serialize(imgUrl);
            var nonce = RenderService.NewNonce();
            var jsName = System.Text.Json.JsonSerializer.Serialize(Path.GetFileName(state.CurrentFile ?? ""));
            var jsScale = state.FontScale.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return $@"<!DOCTYPE html>
<html{htmlClass}><head><meta charset='utf-8'/>
<meta name='viewport' content='width=device-width,initial-scale=1'/>
<meta name='referrer' content='no-referrer'/>
<meta http-equiv='Content-Security-Policy' content=""default-src 'self'; script-src 'nonce-{nonce}'; style-src 'unsafe-inline'; img-src 'self' https://docfiles.example data: blob:; connect-src 'none'; base-uri 'none'; form-action 'none';"">
<style>
{RenderService.ThemeCss()}
{RenderService.PageResetCss}
  html,body {{ width:100%; height:100vh; margin:0; }}
  body {{ background:var(--bg); color:var(--text); transition:background-color .3s ease,color .3s ease;
          display:flex; flex-direction:column; overflow:hidden; }}
  /* 棋盘格：透明 PNG 在浅色背景上原本看不出透明区域，方格底可辨识 */
  #stage {{ flex:1; overflow:auto; display:flex; align-items:center; justify-content:center;
            background-color:{(!isDark ? "#f0f0f0" : "#1a1a1a")};
            background-image:
              linear-gradient(45deg,{(isDark ? "#242424" : "#d8d8d8")} 25%,transparent 25%),
              linear-gradient(-45deg,{(isDark ? "#242424" : "#d8d8d8")} 25%,transparent 25%),
              linear-gradient(45deg,transparent 75%,{(isDark ? "#242424" : "#d8d8d8")} 75%),
              linear-gradient(-45deg,transparent 75%,{(isDark ? "#242424" : "#d8d8d8")} 75%);
            background-size:20px 20px;
            background-position:0 0,0 10px,10px -10px,-10px 0; }}
  #stage.fit #img {{ max-width:100%; max-height:100%; }}
  #stage.actual #img {{ max-width:none; max-height:none; }}
  #img {{ display:block; transform-origin:center center;
          box-shadow:0 1px 3px rgba(0,0,0,.25),0 8px 24px rgba(0,0,0,.10); }}
  #bar {{ flex:0 0 auto; display:flex; align-items:center; gap:10px; padding:6px 14px;
          border-top:1px solid var(--border); background:var(--card);
          font-size:12px; color:var(--secondary); user-select:none; }}
  #bar .name {{ font-weight:600; color:var(--text); overflow:hidden; text-overflow:ellipsis; white-space:nowrap; flex:1; }}
  #bar button {{ font:inherit; font-size:12px; padding:3px 10px; cursor:pointer;
                 background:transparent; color:var(--text);
                 border:1px solid var(--border); border-radius:5px; }}
  #bar button:hover {{ border-color:var(--accent); color:var(--accent); }}
  #bar button.on {{ border-color:var(--accent); color:var(--accent); }}
  .dim {{ opacity:.7; }}
</style>
</head><body>
  <div id='stage' class='fit'>
    <img id='img' src='{imgUrl}' alt='' />
  </div>
  <div id='bar'>
    <span class='name' id='name'></span>
    <span class='dim' id='dim'>{(w > 0 && h > 0 ? $"{w} × {h}" : "")}</span>
    <span class='dim' id='zoom'>100%</span>
    <button id='bFit' class='on' title='适应窗口 (Ctrl+0)'>适应窗口</button>
    <button id='bOne' title='原始尺寸 (Ctrl+1)'>1:1</button>
  </div>
<script nonce='{nonce}'>
(function(){{
  var stage=document.getElementById('stage'), img=document.getElementById('img');
  var bFit=document.getElementById('bFit'), bOne=document.getElementById('bOne');
  var zEl=document.getElementById('zoom'), nameEl=document.getElementById('name');
  nameEl.textContent = {jsName};
  var scale = {jsScale} > 0 ? {jsScale} : 1;

  function apply(){{ img.style.transform = 'scale(' + scale + ')'; zEl.textContent = Math.round(scale*100) + '%'; }}
  function setMode(m){{
    stage.className = m;
    bFit.classList.toggle('on', m === 'fit');
    bOne.classList.toggle('on', m === 'actual');
    if(m === 'fit') {{ scale = {jsScale} > 0 ? {jsScale} : 1; apply(); }}
  }}
  bFit.onclick = function(){{ setMode('fit'); }};
  bOne.onclick = function(){{ setMode('actual'); scale = 1; apply(); }};

  // Ctrl + 滚轮缩放（与文档页快捷键约定一致）
  stage.addEventListener('wheel', function(e){{
    if(!e.ctrlKey) return;
    e.preventDefault();
    scale = Math.min(8, Math.max(0.1, scale + (e.deltaY < 0 ? 0.1 : -0.1)));
    apply();
  }}, {{ passive:false }});

  // Ctrl+0 适应窗口 / Ctrl+1 原始尺寸
  document.addEventListener('keydown', function(e){{
    if(!e.ctrlKey) return;
    if(e.key === '0') {{ e.preventDefault(); setMode('fit'); }}
    else if(e.key === '1') {{ e.preventDefault(); bOne.onclick(); }}
  }});

  // 图片加载完成后回报实际尺寸（文件头尺寸与实际解码尺寸不一致时以实际为准）
  img.addEventListener('load', function(){{
    var d = document.getElementById('dim');
    if(d && !d.textContent) d.textContent = img.naturalWidth + ' × ' + img.naturalHeight;
    try{{ window.chrome.webview.postMessage(JSON.stringify({{kind:'image-loaded',w:img.naturalWidth,h:img.naturalHeight}})); }}catch(e){{}}
  }});
  img.addEventListener('error', function(){{
    stage.innerHTML = '<div style=""padding:40px;color:var(--secondary);font-size:14px;"">图片加载失败</div>';
  }});
  apply();
}})();
</script>
</body></html>";
        }

        /// <summary>
        /// 从文件头解析图片宽高（不解码整图，避免大图先炸内存再判超限）。
        ///
        /// <para>覆盖 PNG / GIF / BMP / JPEG / WEBP 五种主流格式的文件头尺寸字段。
        /// ICO / TIFF 结构特殊（可含多帧、多目录），不做精细解析 —— 返回 false，
        /// 调用方据此跳过像素上限检查（这两类格式极少出现超大尺寸）。</para>
        ///
        /// <para><b>为什么是 <c>public</c> 而非 <c>internal</c></b>：本项目未配置
        /// <c>InternalsVisibleTo</c>，测试程序集只能访问 public 成员。这是超大图 DoS 防护的
        /// 唯一判定依据，必须有测试覆盖（本类其他方法保持 private）。</para>
        ///
        /// <para>解析失败返回 false，调用方给「无法识别的图片」提示页。</para>
        /// </summary>
        public static bool TryReadImageDimensions(string path, out int width, out int height)
        {
            width = 0; height = 0;
            try
            {
                // 读前 64KB：足够覆盖各格式的尺寸字段（JPEG 需扫描 SOF 段，可能较靠后）
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var buf = new byte[Math.Min(65536, fs.Length)];
                var read = fs.Read(buf, 0, buf.Length);
                if (read < 8) return false;

                // PNG：8 字节签名 + IHDR 块（宽高在偏移 16/20，大端）
                if (buf[0] == 0x89 && buf[1] == 0x50 && buf[2] == 0x4E && buf[3] == 0x47)
                {
                    if (read < 24) return false;
                    width = BE32(buf, 16); height = BE32(buf, 20);
                    return true;
                }
                // GIF：宽高在偏移 6/8（小端 16 位）
                if (buf[0] == 0x47 && buf[1] == 0x49 && buf[2] == 0x46)
                {
                    if (read < 10) return false;
                    width = buf[6] | (buf[7] << 8); height = buf[8] | (buf[9] << 8);
                    return true;
                }
                // BMP：宽高在偏移 18/22（小端 32 位，高度可为负表示自上而下）
                if (buf[0] == 0x42 && buf[1] == 0x4D)
                {
                    if (read < 26) return false;
                    width = LE32(buf, 18); height = Math.Abs(LE32(buf, 22));
                    return true;
                }
                // WEBP：位于 RIFF 容器内，有三种子格式（VP8 / VP8L / VP8X），尺寸位置各异
                if (read >= 30 && buf[0] == 0x52 && buf[1] == 0x49 && buf[2] == 0x46 && buf[3] == 0x46
                    && buf[8] == 0x57 && buf[9] == 0x45 && buf[10] == 0x42 && buf[11] == 0x50)
                    return TryReadWebpDimensions(buf, read, out width, out height);
                // JPEG：扫描 SOFn 段（尺寸在其中），跳过其他段
                if (buf[0] == 0xFF && buf[1] == 0xD8)
                    return TryReadJpegDimensions(buf, read, out width, out height);

                return false;   // ICO / TIFF 等：不解析，交由调用方跳过像素检查
            }
            catch { return false; }
        }

        /// <summary>WEBP 三种子格式的尺寸解析（VP8 有损 / VP8L 无损 / VP8X 扩展）。</summary>
        private static bool TryReadWebpDimensions(byte[] b, int read, out int width, out int height)
        {
            width = 0; height = 0;
            if (read >= 30 && b[12] == 0x56 && b[13] == 0x50 && b[14] == 0x38 && b[15] == 0x20) // "VP8 "
            {
                // 有损：尺寸在帧头 6.1.3，偏移 26/28，14 位有效
                width = (b[26] | (b[27] << 8)) & 0x3FFF;
                height = (b[28] | (b[29] << 8)) & 0x3FFF;
                return width > 0 && height > 0;
            }
            if (read >= 25 && b[12] == 0x56 && b[13] == 0x50 && b[14] == 0x38 && b[15] == 0x4C) // "VP8L"
            {
                // 无损：偏移 21 起 14 位宽、14 位高
                var bits = b[21] | (b[22] << 8) | (b[23] << 16) | (b[24] << 24);
                width = (bits & 0x3FFF) + 1;
                height = ((bits >> 14) & 0x3FFF) + 1;
                return true;
            }
            if (read >= 30 && b[12] == 0x56 && b[13] == 0x50 && b[14] == 0x38 && b[15] == 0x58) // "VP8X"
            {
                // 扩展：偏移 24 起 24 位宽高（减一存储）
                width = (b[24] | (b[25] << 8) | (b[26] << 16)) + 1;
                height = (b[27] | (b[28] << 8) | (b[29] << 16)) + 1;
                return true;
            }
            return false;
        }

        /// <summary>JPEG 的 SOFn 段扫描：跳过 APPn / COM 等段，找到 SOF0-SOF15（除 DHT/DAC 等非尺寸段）。</summary>
        private static bool TryReadJpegDimensions(byte[] b, int read, out int width, out int height)
        {
            width = 0; height = 0;
            var i = 2; // 跳过 SOI
            while (i + 9 < read)
            {
                if (b[i] != 0xFF) { i++; continue; }
                var marker = b[i + 1];
                // 填充字节 FF FF… 跳过
                if (marker == 0xFF) { i++; continue; }
                // SOFn（含尺寸）：C0-CF 中排除 C4(DHT) / C8(JPG) / CC(DAC)
                if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
                {
                    // 段结构：FF Cn | len(2) | precision(1) | height(2) | width(2)
                    height = (b[i + 5] << 8) | b[i + 6];
                    width = (b[i + 7] << 8) | b[i + 8];
                    return width > 0 && height > 0;
                }
                // 无长度字段的独立标记
                if (marker == 0xD8 || marker == 0xD9) { i += 2; continue; }
                if (i + 3 >= read) break;
                var segLen = (b[i + 2] << 8) | b[i + 3];
                if (segLen < 2) break;      // 畸形段长度，防死循环
                i += 2 + segLen;
            }
            return false;
        }

        private static int BE32(byte[] b, int i) => (b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3];
        private static int LE32(byte[] b, int i) => b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24);
    }
}
