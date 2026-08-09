// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Collections.Generic;
using Markdig;

namespace SeeMe
{
    public class RenderService : IRenderService
    {
        /// <summary>WebView2 铏氭嫙涓绘満鍚嶏紝鏄犲皠鍒版湰鍦? Resources 鐩綍锛岀敤浜庡畨鍏ㄥ姞杞界绾? JS/CSS/瀛椾綋锛岄伩鍏? file: 鍗忚銆?</summary>
        public const string VirtualHost = "appassets.example";

        public MarkdownPipeline Pipeline { get; } = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .UsePipeTables()
            .UseGridTables()
            .UseTaskLists()
            .UseAutoLinks()
            .UseAutoIdentifiers()
            .UseEmojiAndSmiley()
            .UseMathematics()  // KaTeX 鏁板鍏紡鏀寔锛?$$...$$ 涓? $...$
            .DisableHtml()
            .Build();

        public readonly Regex YamlFrontMatterRegex = new(
            @"^---\s*\r?\n.*?\r?\n---\s*\r?\n",
            RegexOptions.Compiled | RegexOptions.Singleline);

        /// <summary>
        /// Matches a whole &lt;img&gt; tag including all attributes. Only src is rewritten;
        /// other attributes (alt/class/title...) are preserved. Markdig emits alt AFTER src,
        /// so the old regex (attrs before src only) dropped alt entirely.
        /// </summary>
        public readonly Regex ImgTagRegex = new(
            @"<img\b([^>]*)>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>Locates src=... inside a tag, keeping before/after attributes; closing quote matched by backreference.</summary>
        private static readonly Regex ImgSrcAttrRegex = new(
            @"^(?<before>[^>]*?)\bsrc=(?<q>['""])(?<src>[^""'>]+)\k<q>(?<after>[^>]*)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Whitelist filter: strip on* event attrs and style= from <img> to avoid XSS
        private static readonly Regex DangerousAttrRegex = new(
            @"\s*(on\w+|style)\s*=\s*(""[^""]*""|'[^']*'|[^\s>]*)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // 匹配 <a> 标签内的 href 属性（含引号），用于静态过滤危险伪协议（纵深防御）
        private static readonly Regex LinkHrefRegex = new(
            @"(?<prefix><a\b[^>]*?)href\s*=\s*(?<q>[""'])(?<href>[^""']*)\k<q>(?<suffix>[^>]*>)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private const long MaxInlineImageBytes = 5 * 1024 * 1024;

        public readonly Regex TableHtmlRegex = new(
            @"<table[^>]*>([\s\S]*?)</table>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public readonly Regex PreWithCodeRegex = new(
            @"<pre[^>]*>(\s*)<code([^>]*)>([\s\S]*?)</code>\s*</pre>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public string ProcessRelativePaths(string html, string? baseDir)
        {
            if (string.IsNullOrEmpty(baseDir)) return html;
            return ImgTagRegex.Replace(html, m =>
            {
                // 鍓ョ on*/style 鍗遍櫓灞炴?э紝浠呬繚鐣欏畨鍏ㄥ睘鎬у洖鍐?
                var rest = DangerousAttrRegex.Replace(m.Groups[1].Value, "");
                var attr = ImgSrcAttrRegex.Match(rest);
                if (!attr.Success) return m.Value;

                var src = attr.Groups["src"].Value;

                // 绝对/远程/已内联的资源原样保留（file: 会被 CSP 拦截，符合安全预期）
                if (src.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || src.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    || src.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                    || src.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                    || src.StartsWith("/", StringComparison.Ordinal))
                    return m.Value;

                try
                {
                    var full = Path.GetFullPath(Path.Combine(baseDir, src));
                    // 璺緞閬嶅巻闃叉姢锛氳В鏋愮粨鏋滃繀椤讳粛浣嶄簬 baseDir 涔嬪唴锛屽惁鍒欒涓鸿秺鐣屾嫆缁濊鍙?
                    var baseFull = Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (!full.StartsWith(baseFull, StringComparison.OrdinalIgnoreCase))
                        return m.Value;
                    if (!File.Exists(full)) return m.Value;
                    // 璇诲彇澶у皬涓婇檺锛岄伩鍏嶈秴澶ф枃浠跺唴鑱斿鑷? OOM锛圖oS锛?
                    var fi = new FileInfo(full);
                    if (fi.Length > MaxInlineImageBytes) return m.Value;
                    // 鐩稿鍥剧墖鍐呰仈涓? data: URI锛屾棦閬垮厤 file: 鍗忚锛圕SP 宸茬鐢級锛屽張淇濊瘉绂荤嚎鍙敤
                    var mime = MimeFromExt(Path.GetExtension(full));
                    var b64 = Convert.ToBase64String(File.ReadAllBytes(full));
                    return $"<img{attr.Groups["before"].Value}src={attr.Groups["q"].Value}data:{mime};base64,{b64}{attr.Groups["q"].Value}{attr.Groups["after"].Value}";
                }
                catch { return m.Value; }
            });
        }

        /// <summary>
        /// 静态过滤 &lt;a href&gt; 中的危险伪协议（纵深防御：与页面运行时点击拦截 + 宿主 scheme 白名单构成三保险）。
        /// 浏览器解析属性值前会先做 HTML 实体解码，因此先解码再判定 scheme，防 java&amp;#x73;cript: 类混淆。
        /// </summary>
        public string SanitizeLinkHrefs(string html)
        {
            return LinkHrefRegex.Replace(html, m =>
            {
                var decoded = System.Net.WebUtility.HtmlDecode(m.Groups["href"].Value);
                var colon = decoded.IndexOf(':');
                if (colon <= 0) return m.Value; // 相对链接 / 锚点 / 根路径，原样保留
                var scheme = decoded.Substring(0, colon).Trim().ToLowerInvariant();
                var rest = decoded.Substring(colon + 1);
                var dangerous = scheme == "javascript" || scheme == "vbscript"
                    || (scheme == "data"
                        && (rest.StartsWith("text/html", StringComparison.OrdinalIgnoreCase)
                            || rest.StartsWith("image/svg+xml", StringComparison.OrdinalIgnoreCase)));
                if (!dangerous) return m.Value;
                // href 置 "#"（页面内锚点，无副作用），保留链接文本
                return m.Groups["prefix"].Value + "href=\"#\"" + m.Groups["suffix"].Value;
            });
        }

        private static string MimeFromExt(string ext) => ext.ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".svg" => "image/svg+xml",
            ".ico" => "image/x-icon",
            _ => "application/octet-stream"
        };

        public string WrapTables(string html)
            => TableHtmlRegex.Replace(html, "<div style=\"overflow-x:auto;margin:.5em 0;\">$0</div>");

        public string UpgradePreBlocks(string html)
            => PreWithCodeRegex.Replace(html, "<pre class=\"code-block\" spellcheck=\"false\">$1<code$2 class=\"language-plaintext\">$3</code></pre>");

        public string? StripYamlFrontMatter(string md, out string? frontMatterBlock)
        {
            frontMatterBlock = null;
            var match = YamlFrontMatterRegex.Match(md);
            if (match.Success)
            {
                frontMatterBlock = match.Value;
                return md.Substring(match.Length);
            }
            return md;
        }

        public string BuildFrontMatterCard(string? block)
        {
            if (string.IsNullOrWhiteSpace(block)) return "";
            var body = block.TrimStart('-').Trim();
            var lines = body.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            var sb = new StringBuilder();
            foreach (var l in lines)
            {
                var idx = l.IndexOf(':');
                if (idx > 0)
                {
                    var k = l.Substring(0, idx).Trim();
                    var v = l.Substring(idx + 1).Trim().Trim('"').Trim('\'');
                    sb.Append($"<div class=\"fm-line\"><span class=\"fm-key\">{System.Security.SecurityElement.Escape(k)}</span><span class=\"fm-val\">{System.Security.SecurityElement.Escape(v)}</span></div>");
                }
            }
            return $"<div class=\"fm-card\">{sb}</div>";
        }

        /// <summary>
        /// 浠庢覆鏌撳悗鐨? HTML 鎻愬彇鏍囬锛坔1-h4 鐨勭湡瀹? id 涓庢枃鏈級鐢熸垚鐩綍鍗＄墖锛圱OC锛夈??
        /// 鐩存帴璇绘覆鏌撶粨鏋滆?岄潪浠? md 鐚? slug鈥斺?擬arkdig 瀵逛腑鏂囨爣棰樹細鍥為??鎴? section/section-N锛?
        /// 鐚? id 蹇呯劧閿氱偣澶遍厤瀵艰嚧鐐瑰嚮鏃犲弽搴斻?傝秴 32 椤规埅鏂??
        /// </summary>
        public string BuildTocCard(string renderedHtml)
        {
            var items = BuildTocItems(renderedHtml);
            if (items.Count == 0) return "";
            var sb = new StringBuilder();
            var count = 0;
            foreach (var it in items)
            {
                if (count >= 32) break;
                var indent = (it.Level - 2) * 14;
                if (indent < 0) indent = 0;
                var escId = System.Security.SecurityElement.Escape(it.Id);
                var escTitle = System.Security.SecurityElement.Escape(it.Title);
                sb.Append($"<div class=\"toc-item\" style=\"padding-left:{indent}px\">" +
                          $"<a href=\"#{escId}\">{escTitle}</a></div>");
                count++;
            }
            return $"<div class=\"fm-card\"><div class=\"toc-title\">📑 目录</div>{sb}</div>";
        }

        /// <summary>浠庢覆鏌撳悗 HTML 鎻愬彇鏍囬缁撴瀯锛堢骇鍒?/鐪熷疄 id/绾枃鏈級锛屼緵渚ц竟鏍忓ぇ绾蹭娇鐢ㄣ?傜┖鏍囬鎴栫┖ id 璺宠繃锛屼笂闄? 128 椤广??</summary>
        public List<TocItem> BuildTocItems(string renderedHtml)
        {
            var list = new List<TocItem>();
            if (string.IsNullOrWhiteSpace(renderedHtml)) return list;
            foreach (System.Text.RegularExpressions.Match m in TocHeadingRegex.Matches(renderedHtml))
            {
                if (list.Count >= 128) break;
                var level = int.Parse(m.Groups[1].Value);
                var id = m.Groups[2].Value;
                var title = System.Text.RegularExpressions.Regex.Replace(m.Groups[3].Value, "<[^>]+>", "").Trim();
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(title)) continue;
                list.Add(new TocItem { Level = level, Id = id, Title = title });
            }
            return list;
        }

        /// <summary>鍖归厤娓叉煋鍚? HTML 鐨勬爣棰樻爣绛撅細&lt;h1-h4 id="..."&gt;鍐呭&lt;/h1-h4&gt;锛堟儼鎬ф崟鑾峰唴瀹癸級銆?</summary>
        private static readonly System.Text.RegularExpressions.Regex TocHeadingRegex = new(
            @"<h([1-4])\s+id=""([^""]*)""[^>]*>(.*?)</h\1>",
            System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        public string StripBom(string s)
        {
            if (s.Length >= 1 && s[0] == '\uFEFF') return s.Substring(1);
            return s;
        }

        public System.Windows.Media.Color ParseMediaColor(string hex)
        {
            try
            {
                hex = hex.TrimStart('#');
                if (hex.Length == 6
                    && byte.TryParse(hex.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var r)
                    && byte.TryParse(hex.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)
                    && byte.TryParse(hex.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
                    return System.Windows.Media.Color.FromRgb(r, g, b);
            }
            catch { }
            return System.Windows.Media.Colors.White;
        }

        public string? TryBrushHex(FrameworkElement element, string key)
        {
            try
            {
                if (element.TryFindResource(key) is System.Windows.Media.SolidColorBrush b)
                    return $"#{b.Color.R:X2}{b.Color.G:X2}{b.Color.B:X2}";
            }
            catch { }
            return null;
        }

        public string BrushHex(FrameworkElement element, string key, string fallback) =>
            TryBrushHex(element, key) ?? fallback;

        /// <summary>澶栭儴璋冪敤锛氫緷鎹紶鍏ョ殑涓婚绠＄悊鍣ㄥ垽鏂槸鍚︽殫鑹层??</summary>
        public bool IsDarkTheme(IThemeManager theme) => theme.Current == theme.Dark;

        /// <summary>鍐呴儴璋冪敤锛氫娇鐢ㄦ敞鍏ョ殑 IThemeManager 鍒ゆ柇鏄惁鏆楄壊銆?</summary>
        private bool IsDark() => ThemeManager != null && ThemeManager.Current == ThemeManager.Dark;

        /// <summary>Markdown 娓叉煋椋庢牸锛歞efault / github / simple锛堣缃彉鏇存椂鐢? MainWindow 鍐欏叆锛夈??</summary>
        public string MdStyle { get; set; } = "default";

        /// <summary>鎶ょ溂妯″紡锛堟殩鑹叉护闀滐紝璁剧疆鍙樻洿鏃剁敱 MainWindow 鍐欏叆锛夈??</summary>
        public bool EyeCare { get; set; }

        /// <summary>姝ｆ枃鍩虹瀛楀彿 px锛堥粯璁? 14锛岀缉鏀惧?嶆暟浠ユ涓哄熀纭?锛夈??</summary>
        public double FontSize { get; set; } = 14;

        /// <summary>姝ｆ枃琛岄珮鍊嶆暟锛堥粯璁? 1.65锛夈??</summary>
        public double LineHeight { get; set; } = 1.65;

        /// <summary>鐢? MainWindow 鍦ㄨ閰嶆湇鍔℃椂娉ㄥ叆锛屼緵鍐呴儴娓叉煋閫昏緫鍒ゆ柇褰撳墠涓婚銆?</summary>
        public IThemeManager? ThemeManager { get; set; }

        /// <summary>鐢? MainWindow 鍦ㄨ閰嶆湇鍔℃椂娉ㄥ叆锛屼緵鍐呴儴娓叉煋閫昏緫鏍煎紡鍖栨枃浠跺ぇ灏忋??</summary>
        public IFileConverter? Converter { get; set; }

        /// <summary>鐢熸垚 :root锛堜寒锛?+ html.dark锛堟殫锛夊弻濂? CSS 鍙橀噺鍧楋紙鍞竴鏉ユ簮 ThemeColors锛夈??</summary>
        public static string ThemeCss() => ThemeColors.ThemeCss();

        /// <summary>鍩虹 setTheme锛氫粎鍒? dark class銆傚瓙椤甸潰濡傞渶鑱斿姩锛圥rism/Mermaid锛夊彲鑷鎵╁睍鍚屽悕鍑芥暟銆?</summary>
        public static string SetThemeScript =>
            "function setTheme(dark){var h=document.documentElement;if(dark)h.classList.add('dark');else h.classList.remove('dark');}";

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
        /// 缁熶竴椤甸潰澶栧３锛氭墍鏈? NavigateToString 鐨勭畝鍗曢〉闈紙閿欒/鍔犺浇/鍗犱綅/鏂囦欢涓㈠け锛夐兘璧拌繖閲岋紝
        /// 淇濊瘉蹇呯劧鍖呭惈瀹屾暣鍙屽 CSS 鍙橀噺涓? setTheme锛屾潨缁?"鍒囨崲瀹屽叏鏃犳晥"鐨勯〉闈€??
        /// </summary>
        public static string WrapPage(bool isDark, string title, string css, string bodyHtml, string extraJs = "")
        {
            var cls = isDark ? " class='dark'" : "";
            return $@"<!DOCTYPE html>
<html{cls}><head><meta charset='utf-8'/>
<meta name='viewport' content='width=device-width,initial-scale=1'/>
<title>{System.Security.SecurityElement.Escape(title)}</title>
<style>{ThemeCss()}{css}</style>
</head>
<body>
{bodyHtml}
<script>{SetThemeScript}{extraJs}</script>
</body></html>";
        }

        public string Render(string bodyHtml, PanelState state, string frontMatterCard,
            FrameworkElement resourceElement, Func<string>? logErr = null, string? customCss = null)
        {
            var isDark = IsDark();
            // 涓婚鍙屽鍥哄畾鍊硷細:root 鎭掍负浜壊銆乭tml.dark 鎭掍负鏆楄壊锛堣 ThemeVars 鍞竴璋冭壊鏉匡級銆?
            // 涔嬪墠 :root 浠庛?屽綋鍓嶄富棰樸?嶇殑 WPF 璧勬簮璇诲彇锛屾殫鑹叉ā寮忎笅鐢熸垚鐨勯〉闈? :root 宸叉槸鏆楄壊鍊硷紝
            // 鍒囧洖浜壊锛堢Щ闄? dark class锛夊悗椤甸潰浠嶅彇 :root 鐨勬殫鑹插?? 鈫? 椤甸潰涓庣獥鍙ｄ富棰樹笉涓?鑷淬??
            // 鍙屽鍥哄畾鍊煎悗锛屼换鎰忎富棰樹笅鐢熸垚鐨勯〉闈㈤兘鑳介?氳繃 class 鍙屽悜姝ｇ‘鍒囨崲銆?
            var css = $@"
{ThemeCss()}
{customCss ?? ""}
html,body {{ margin:0; padding:0; background:var(--bg); color:var(--text); transition:background-color .3s ease,color .3s ease; }}
body {{ font-family:'Microsoft YaHei','PingFang SC',Segoe UI,Helvetica,Arial,sans-serif; font-size:{FontSize.ToString(System.Globalization.CultureInfo.InvariantCulture)}px; line-height:{LineHeight.ToString(System.Globalization.CultureInfo.InvariantCulture)}; padding:16px 20px 40px; }}
h1,h2,h3,h4,h5,h6,p,a,li,code,pre,blockquote,table,th,td,tr,hr,.fm-card,.fm-line,.fm-key,.fm-val {{ transition:background-color .35s ease,color .35s ease,border-color .35s ease; }}
h1,h2,h3,h4,h5,h6 {{ color:var(--heading); font-weight:600; margin:1em 0 .4em; line-height:1.3; }}
h1 {{ font-size:1.5em; border-bottom:1px solid var(--h1-border); padding-bottom:.3em; }}
h2 {{ font-size:1.25em; border-bottom:1px solid var(--h2-border); padding-bottom:.2em; }}
h3 {{ font-size:1.1em; }}
p {{ margin:.5em 0; }}
a {{ color:var(--link); text-decoration:none; }}
a:hover {{ text-decoration:underline; }}
ul,ol {{ padding-left:1.4em; margin:.4em 0; }}
li {{ margin:.15em 0; }}
blockquote {{ border-left:3px solid var(--quote); background:var(--quote-bg); color:var(--quote-text); padding:.4em .8em; margin:.5em 0; border-radius:0 4px 4px 0; }}
code {{ font-family:'Consolas','JetBrains Mono',Menlo,monospace; background:var(--code-bg); padding:1px 4px; border-radius:3px; font-size:.88em; }}
pre {{ background:var(--pre-bg); color:var(--pre-text); padding:10px 14px; border-radius:8px; overflow-x:auto; line-height:1.45; font-size:12px; margin:.5em 0; }}
pre code {{ background:transparent; color:inherit; padding:0; font-size:inherit; }}
pre .token {{ background:transparent !important; }}
table {{ border-collapse:collapse; width:100%; margin:.5em 0; font-size:12px; }}
th,td {{ border:1px solid var(--table-bdr); padding:4px 8px; }}
th {{ background:var(--table-head); font-weight:600; }}
tr:hover td {{ background:var(--table-hov); }}
hr {{ border:none; border-top:1px solid var(--hr); margin:1em 0; }}
img {{ max-width:100%; border-radius:4px; display:block; margin:.5em 0; }}
input[type=checkbox] {{ margin-right:.3em; vertical-align:-2px; }}
.fm-card {{ background:var(--fm-bg); border-radius:8px; padding:10px 14px; margin:.5em 0 1em; font-size:12px; line-height:1.8; }}
.toc-title {{ font-weight:600; color:var(--heading); margin-bottom:4px; }}
.toc-item a {{ color:var(--link); text-decoration:none; }}
.toc-item a:hover {{ text-decoration:underline; }}
.fm-line {{ display:flex; gap:8px; }}
.fm-key {{ min-width:80px; color:var(--quote-text); font-weight:600; }}
.fm-val {{ color:var(--text); word-break:break-all; }}
.task-list {{ list-style:none; padding-left:0; }}
mark.seeme-hl {{ background:#FBBF24; color:#1F2937; border-radius:2px; padding:1px 2px; }}
mark.seeme-cur {{ background:#3B82F6; color:#fff; }}
/* 脚注（Markdig .UseFootnotes 输出）：上标锚点 + 底部注释列表 */
sup.footnote-ref {{ font-size:.72em; margin-left:2px; }}
sup.footnote-ref a {{ color:var(--link); text-decoration:none; }}
section.footnotes {{ margin-top:2em; padding-top:.8em; border-top:1px solid var(--hr); font-size:.85em; color:var(--quote-text); }}
section.footnotes ol {{ padding-left:1.2em; }}
section.footnotes li {{ margin:.25em 0; }}
section.footnotes li p {{ display:inline; }}
::-webkit-scrollbar {{ width:8px; height:8px; }}
::-webkit-scrollbar-track {{ background:transparent; }}
::-webkit-scrollbar-thumb {{ background:var(--table-bdr); border-radius:4px; }}    ::-webkit-scrollbar-thumb:hover {{ background:var(--quote-text); }}
    ";

            // Markdown 娓叉煋椋庢牸娉ㄥ叆锛坰imple / github 瑕嗙洊鍩虹鏍峰紡锛?
            switch (MdStyle)
            {
                case "simple":
                    css += "\nh1,h2 {{ border-bottom:none; }}\n.fm-card {{ display:none; }}\nblockquote {{ border-left-width:2px; }}\n";
                    break;
                case "github":
                    css += "\n.markdown-body {{ max-width:880px; margin:0 auto; }}\n";
                    break;
            }

            // 鎶ょ溂妯″紡锛氭殩鑹叉护闀溿?傛敞鎰忎笉鑳界敤 html/body 涓婄殑 filter鈥斺?旈偅浼氳 position:fixed 瀛愬厓绱?
            // 锛堝弻鍑绘斁澶? lightbox锛夌殑瀹氫綅鍙傝?冧粠瑙嗗彛鍙樻垚鏁翠釜鏂囨。鐩掋?傛敼涓哄彧浣滅敤浜庡唴瀹瑰厓绱犮??
            if (EyeCare)
                css += "\nbody > *:not(#seeme-ov) {{ filter:sepia(.22) saturate(.88) brightness(1.02); }}\nhtml.dark body > *:not(#seeme-ov) {{ filter:sepia(.18) saturate(.8) brightness(.96); }}\n";

            var scrollScript = $@"
(function(){{
  var __restoreY = {state.LastScrollY.ToString(System.Globalization.CultureInfo.InvariantCulture)};
  function report(){{ try {{ window.chrome.webview.postMessage(JSON.stringify({{kind:'scroll',y:window.scrollY}})); }} catch(e){{}} }}
  function applyZoom(z){{ document.documentElement.style.fontSize=(z*{FontSize.ToString(System.Globalization.CultureInfo.InvariantCulture)})+'px'; }}
  function tryRestore(){{
    if(__restoreY > 0) {{ window.scrollTo(0, __restoreY); }}
    applyZoom({state.FontScale.ToString(System.Globalization.CultureInfo.InvariantCulture)});
    try {{ if(window.Prism) Prism.highlightAll(); }} catch(e){{}}
    try {{ if(window.renderMathInElement) renderMathInElement(document.body,{{delimiters:[{{left:'$$',right:'$$',display:true}},{{left:'$',right:'$',display:false}}],throwOnError:false}}); }} catch(e){{}}
    try {{ if(window.mermaid) mermaid.initialize({{startOnLoad:true,theme:{(IsDark() ? "'dark'" : "'default'")}}}); }} catch(e){{}}
    report();
    window.removeEventListener('load', tryRestore);
  }}
  if(document.readyState === 'complete') tryRestore();
  else window.addEventListener('load', tryRestore);
  window.addEventListener('scroll', function(){{
    requestAnimationFrame(function(){{ report(); }});
  }});
  document.addEventListener('click', function(e){{
    var a = e.target && e.target.closest ? e.target.closest('a[href]') : null;
    if(!a) return;
    var h = a.getAttribute('href');
    if(!h) return;
    // 鍚岄〉閿氱偣锛?#...锛変繚鐣欐祻瑙堝櫒鍘熺敓璺宠浆锛岄伩鍏嶇牬鍧忔枃鍐呯洰褰曞鑸?
    if(h.charAt(0) === '#') return;
    // 闃绘榛樿琛屼负锛氬惁鍒? WebView2 鍦? script-src 'unsafe-inline' 涓嬩細鎵ц javascript:/vbscript: 浼崗璁紙XSS锛夈??
    // 鎵?鏈夌椤佃烦杞粺涓?浜ょ粰瀹夸富鍦? OnWebMessage 涓寜 scheme 鐧藉悕鍗曞鐞嗐??
    e.preventDefault();
    try {{ window.chrome.webview.postMessage(JSON.stringify({{kind:'link',href:h}})); }} catch(err){{}}
  }});
  window.__seemeApplyZoom = applyZoom;
  // 鍙屽嚮鍥剧墖 鈫? 鍏ㄥ睆 lightbox 鏀惧ぇ锛堝啀娆＄偣鍑? / Esc 鍏抽棴锛夈?俰d 涓庢姢鐪? filter 鐨? :not() 閰嶅悎锛岄伩鍏嶈嚜韬婊ら暅褰卞搷銆?
  var __ov=null;
  function __ovClose(){{ if(__ov){{ document.body.removeChild(__ov); __ov=null; }} }}
  document.addEventListener('dblclick', function(e){{
    var t=e.target;
    if(!t || t.tagName!=='IMG') return;
    e.preventDefault();
    __ovClose();
    __ov=document.createElement('div');
    __ov.id='seeme-ov';
    __ov.style.cssText='position:fixed;inset:0;background:rgba(0,0,0,.85);display:flex;align-items:center;justify-content:center;z-index:9999;cursor:zoom-out;';
    var img=document.createElement('img');
    img.src=t.src;
    img.style.cssText='max-width:92%;max-height:92%;border-radius:6px;box-shadow:0 8px 40px rgba(0,0,0,.5);';
    __ov.appendChild(img);
    __ov.addEventListener('click', __ovClose);
    document.body.appendChild(__ov);
  }});
  document.addEventListener('keydown', function(e){{ if(e.key==='Escape') __ovClose(); }});
}})();
function setTheme(dark){{
  var html=document.documentElement;
  if(dark) html.classList.add('dark'); else html.classList.remove('dark');
  // 浠ｇ爜楂樹寒涓婚鑱斿姩锛歱rism.min.css (浜?) 鈫? prism-tomorrow.min.css (鏆?)
  var links=document.querySelectorAll('link[rel=stylesheet]');
  for(var i=0;i<links.length;i++){{
    var href=links[i].getAttribute('href')||'';
    if(href.indexOf('prism.min.css')<0 && href.indexOf('prism-tomorrow.min.css')<0) continue;
    var isDarkCss = href.indexOf('prism-tomorrow')>=0;
    if(dark && !isDarkCss) links[i].setAttribute('href', href.replace('prism.min.css','prism-tomorrow.min.css'));
    else if(!dark && isDarkCss) links[i].setAttribute('href', href.replace('prism-tomorrow.min.css','prism.min.css'));
  }}
  // Mermaid 图表跟随主题：保留原始源码，重新初始化并渲染
  try {{
    if(window.mermaid){{
      document.querySelectorAll('.mermaid').forEach(function(el){{
        if(!el.getAttribute('data-orig')) el.setAttribute('data-orig', el.innerHTML);
        el.innerHTML = el.getAttribute('data-orig');
        el.removeAttribute('data-processed');
      }});
      mermaid.initialize({{startOnLoad:false, theme: dark ? 'dark' : 'default'}});
      mermaid.run({{nodes:document.querySelectorAll('.mermaid')}}).catch(function(e){{}});
    }}
  }}catch(e){{}}
}}
";
            string prismJs(string name) => $"https://{VirtualHost}/prism/prism-{name}.min.js";
            var prismCoreJs = prismJs("core");
            var prismTheme = $"https://{VirtualHost}/prism/{(IsDark() ? "prism-tomorrow.min.css" : "prism.min.css")}";

            string resFile(string dir, string file) => $"https://{VirtualHost}/{dir}/{file}";
            var katexCssUrl = resFile("katex", "katex.min.css");
            var katexJsUrl = resFile("katex", "katex.min.js");
            var autoRenderJsUrl = resFile("katex", "auto-render.min.js");
            var mermaidJsUrl = resFile("mermaid", "mermaid.min.js");

            var htmlClass = isDark ? " class='dark'" : "";

            return $@"<!DOCTYPE html>
<html{htmlClass}><head><meta charset='utf-8'/>
<meta name='viewport' content='width=device-width,initial-scale=1'/>
<meta http-equiv='Content-Security-Policy' content=""default-src 'self' https://appassets.example; script-src 'unsafe-inline' https://appassets.example; style-src 'unsafe-inline' https://appassets.example; img-src 'self' data: https://appassets.example; font-src 'self' data: https://appassets.example;"">
<link rel='stylesheet' href='{prismTheme}'/>
<link rel='stylesheet' href='{katexCssUrl}'/>
<style>{css}</style>
</head>
<body class=""markdown-body"">
{frontMatterCard}
{bodyHtml}
<script src='{prismCoreJs}'></script>
<script src='{prismJs("bash")}'></script>
<script src='{prismJs("c")}'></script>
<script src='{prismJs("csharp")}'></script>
<script src='{prismJs("css")}'></script>
<script src='{prismJs("javascript")}'></script>
<script src='{prismJs("json")}'></script>
<script src='{prismJs("markup")}'></script>
<script src='{prismJs("python")}'></script>
<script src='{prismJs("typescript")}'></script>
<script src='{prismJs("yaml")}'></script>
<script src='{katexJsUrl}'></script>
<script src='{autoRenderJsUrl}'></script>
<script src='{mermaidJsUrl}'></script>
<script>{scrollScript}</script>
<script>{SearchScript}</script>
</body></html>";
        }

        public string BuildOfficePage(string bodyHtml, PanelState state,
            FrameworkElement resourceElement)
        {
            var isDark = IsDark();

            // 鍙屽鍥哄畾鍊硷細:root 鎭掍寒鑹层?乭tml.dark 鎭掓殫鑹诧紙ThemeVars 鍞竴璋冭壊鏉匡級锛屼繚璇佷换鎰忎富棰樹笅鐢熸垚椤甸潰鍧囧彲鍙屽悜鍒囨崲
            var css = $@"
{ThemeCss()}

* {{ margin:0; padding:0; box-sizing:border-box; }}
html,body {{ background:var(--bg); color:var(--text); font-family:'Microsoft YaHei','PingFang SC',sans-serif; }}
{(EyeCare ? "body > * {{ filter:sepia(.22) saturate(.88) brightness(1.02); }} html.dark body > * {{ filter:sepia(.18) saturate(.8) brightness(.96); }}" : "")}
body {{ line-height:{LineHeight.ToString(System.Globalization.CultureInfo.InvariantCulture)}; font-size:{FontSize.ToString(System.Globalization.CultureInfo.InvariantCulture)}px; padding:16px 20px 40px; transition:background-color .3s ease,color .3s ease; }}
.content {{
  max-width:100%;
}}
.content h1,.content h2,.content h3,.content h4,.content h5,.content h6 {{ color:var(--heading); font-weight:600; margin:1em 0 .4em; line-height:1.3; }}
.content h1 {{
  font-size:1.5em; margin:.8em 0 .5em; padding-bottom:.3em; border-bottom:1px solid var(--h1-border);
}}
.content h2 {{
  font-size:1.25em; padding-bottom:.2em; border-bottom:1px solid var(--border);
}}
.content h3 {{ font-size:1.1em; }}
.content h4 {{ font-size:1em; }}
.content h5 {{ font-size:.92em; }}
.content h6 {{ font-size:.85em; color:var(--secondary); }}
.content p {{
  margin:.6em 0; font-size:{FontSize.ToString(System.Globalization.CultureInfo.InvariantCulture)}px; line-height:{LineHeight.ToString(System.Globalization.CultureInfo.InvariantCulture)};
}}
.content blockquote {{
  border-left:3px solid var(--quote); background:var(--quote-bg);
  padding:.5em 1em; margin:.8em 0; border-radius:0 4px 4px 0;
  color:var(--quote-text);
}}
.content code {{
  font-family:'Consolas','JetBrains Mono',monospace;
  background:var(--code-bg); padding:2px 6px; border-radius:3px;
  font-size:.9em;
}}
.content pre {{
  background:var(--code-bg); padding:12px 16px; border-radius:8px;
  overflow-x:auto; margin:.8em 0; font-size:12px; line-height:1.5;
}}
.content table {{
  border-collapse:collapse; width:100%; margin:1em 0; font-size:.93em;
}}
.content th, .content td {{
  border:1px solid var(--border); padding:6px 12px; text-align:left;
}}
.content th {{
  background:var(--card); font-weight:600; color:var(--heading);
}}
.content tr:hover td {{
  background:var(--row-hov);
}}
html.dark .content tr:hover td {{
  background:rgba(255,255,255,.06);
}}
.content img {{
  max-width:100%; border-radius:4px; margin:.5em 0;
}}
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
";
            var htmlClass = isDark ? " class='dark'" : "";

            return $@"<!DOCTYPE html>
<html{htmlClass}>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width,initial-scale=1'>
<meta http-equiv='Content-Security-Policy' content=""default-src 'self' https://appassets.example; script-src 'self' https://appassets.example 'unsafe-inline'; style-src 'unsafe-inline'; img-src 'self' data: https://appassets.example;"">
<style>{css}</style>
<script src=""https://appassets.example/scripts/office-theme.js""></script>
<script>window.setTheme=function(dark){{var d=!!dark;document.documentElement.classList.toggle('dark',d);if(document.body)document.body.classList.toggle('dark',d);}};</script>
</head>
<body>
  <div class='content'>
    {bodyHtml}
  </div>
<script>{SearchScript}</script>
</body>
</html>";
        }

        /// <summary>
        /// 绌虹櫧椤垫樉绀哄彲鍏抽棴鐨勬杩庢彁绀哄皬鍗＄墖锛堝彸涓嬭娴姩锛夛紝鏀寔"涓嶅啀鏄剧ず"锛堟寔涔呭寲鍒? settings.json锛夈??
        /// </summary>
        public string BuildWelcomePage(FrameworkElement resourceElement)
        {
            var isDark = IsDark();

            // 鍙屽鍥哄畾鍊硷細:root 鎭掍寒鑹层?乭tml.dark 鎭掓殫鑹诧紙ThemeVars 鍞竴璋冭壊鏉匡級锛屼繚璇佷换鎰忎富棰樹笅鐢熸垚椤甸潰鍧囧彲鍙屽悜鍒囨崲
            var htmlClass = isDark ? " class='dark'" : "";

            return $@"<!DOCTYPE html>
<html{htmlClass}>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width,initial-scale=1'>
<style>
{ThemeCss()}
* {{ margin:0; padding:0; box-sizing:border-box; }}
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
      <div class='empty-title'>鏈?夋嫨</div>
      <div class='empty-hint'>拖拽文件到此 · Ctrl+O 打开</div>
    </div>
  </div>
  <div id='tip' onclick='openFile()' title='点击打开文件'>
    <div class='tip-head'>
      <h2>欢迎使用 SeeMe</h2>
      <button class='close' onclick='event.stopPropagation();dismiss()' title='关闭提示'>×</button>
    </div>
    <div class='tip-body'>
      <div><kbd>Ctrl+O</kbd>打开文件</div>
      <div><kbd>Ctrl+F</kbd>查找</div>
      <div><kbd>Ctrl+0/+/鈭?</kbd>缂╂斁</div>
    </div>
    <div class='tip-foot'>
      <button onclick='event.stopPropagation();dismissForever()'>不再显示</button>
    </div>
  </div>
<script>
function setTheme(dark){{if(dark)document.documentElement.classList.add('dark');else document.documentElement.classList.remove('dark');}}
function dismiss(){{var t=document.getElementById('tip');if(t)t.style.display='none';}}
function openFile(){{
  try{{if(window.chrome&&chrome.webview)chrome.webview.postMessage(JSON.stringify({{kind:'open-file'}}));}}catch(e){{}}
}}
function dismissForever(){{
  try{{if(window.chrome&&chrome.webview)chrome.webview.postMessage(JSON.stringify({{kind:'dismiss-welcome'}}));}}catch(e){{}}
  dismiss();
}}
</script>
</body>
</html>";
        }
    }

    /// <summary>鏂囨。澶х翰鏉＄洰锛歁arkdig 娓叉煋鍚庢爣棰樼殑鐪熷疄 id锛堥敋鐐硅烦杞洰鏍囷級涓庣函鏂囨湰鏍囬銆?</summary>
    public sealed class TocItem
    {
        public int Level;          // h1=1 ... h4=4
        public string Id = "";     // 娓叉煋 HTML 涓殑 id 灞炴?э紙宸插幓 HTML 鏍囩锛?
        public string Title = "";  // 绾枃鏈爣棰?
    }
}
