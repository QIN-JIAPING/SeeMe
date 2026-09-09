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
    public partial class RenderService : IRenderService
    {
        /// <summary>WebView2 铏氭嫙涓绘満鍚嶏紝鏄犲皠鍒版湰鍦? Resources 鐩綍锛岀敤浜庡畨鍏ㄥ姞杞界绾? JS/CSS/瀛椾綋锛岄伩鍏? file: 鍗忚銆?</summary>
        public const string VirtualHost = "appassets.example";

        /// <summary>
        /// Generate a per-render CSP nonce: script-src uses 'nonce-xxx' instead of 'unsafe-inline',
        /// inline scripts must carry the matching nonce attribute to execute — injected
        /// malicious &lt;script&gt; (no nonce) is blocked by CSP.
        /// </summary>
        public static string NewNonce()
        {
            Span<byte> buf = stackalloc byte[16];
            System.Security.Cryptography.RandomNumberGenerator.Fill(buf);
            return Convert.ToBase64String(buf);
        }

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

        public readonly Regex TableHtmlRegex = new(
            @"<table[^>]*>([\s\S]*?)</table>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public readonly Regex PreWithCodeRegex = new(
            @"<pre[^>]*>(\s*)<code([^>]*)>([\s\S]*?)</code>\s*</pre>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // 围栏代码块 → 图表/思维导图：匹配 UpgradePreBlocks 升级后的形态
        private static readonly Regex FencedBlockRegex = new(
            @"<pre class=""code-block""[^>]*><code class=""language-(?<lang>echarts|markmap)""[^>]*>(?<body>[\s\S]*?)</code></pre>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public string ProcessRelativePaths(string html, string? baseDir)
        {
            if (string.IsNullOrEmpty(baseDir)) return html;
            return ImgTagRegex.Replace(html, m =>
            {
                // 鍓ョ on*/style 鍗遍櫓灞炴?э紝浠呬繚鐣欏畨鍏ㄥ睘鎬у洖鍐?
                var rest = DangerousAttrRegex.Replace(m.Groups[1].Value, "");
                var attr = ImgSrcAttrRegex.Match(rest);
                if (!attr.Success) return $"<img{rest}>";

                var src = attr.Groups["src"].Value;

                // 绝对/远程/已内联的资源原样保留（file: 会被 CSP 拦截，符合安全预期）
                if (src.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || src.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    || src.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                    || src.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                    || src.StartsWith("/", StringComparison.Ordinal))
                    return $"<img{rest}>";

                try
                {
                    var full = Path.GetFullPath(Path.Combine(baseDir, src));
                    // 璺緞閬嶅巻闃叉姢锛氳В鏋愮粨鏋滃繀椤讳粛浣嶄簬 baseDir 涔嬪唴锛屽惁鍒欒涓鸿秺鐣屾嫆缁濊鍙?
                    var baseFull = Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (!full.StartsWith(baseFull, StringComparison.OrdinalIgnoreCase))
                        return $"<img{rest}>";
                    if (!File.Exists(full)) return $"<img{rest}>";
                    // 璇诲彇澶у皬涓婇檺锛岄伩鍏嶈秴澶ф枃浠跺唴鑱斿鑷? OOM锛圖oS锛?
                    var fi = new FileInfo(full);
                    if (fi.Length > Limits.MaxInlineImageBytes) return $"<img{rest}>";
                    // 鐩稿鍥剧墖鍐呰仈涓? data: URI锛屾棦閬垮厤 file: 鍗忚锛圕SP 宸茬鐢級锛屽張淇濊瘉绂荤嚎鍙敤
                    var mime = MimeFromExt(Path.GetExtension(full));
                    var b64 = Convert.ToBase64String(File.ReadAllBytes(full));
                    return $"<img{attr.Groups["before"].Value}src={attr.Groups["q"].Value}data:{mime};base64,{b64}{attr.Groups["q"].Value}{attr.Groups["after"].Value}";
                }
                catch { return $"<img{rest}>"; }
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
            => PreWithCodeRegex.Replace(html, m =>
            {
                var attrs = m.Groups[2].Value; // <code> 标签原有属性（如 class="language-csharp"）
                // 已有 class（带语言）则原样保留，避免追加出重复的 class 属性；无语言才补 language-plaintext
                var codeOpen = attrs.Contains("class=", StringComparison.OrdinalIgnoreCase)
                    ? "<code" + attrs + ">"
                    : "<code" + attrs + " class=\"language-plaintext\">";
                return "<pre class=\"code-block\" spellcheck=\"false\">" + m.Groups[1].Value
                       + codeOpen + m.Groups[3].Value + "</code></pre>";
            });

        /// <summary>
        /// 将 ```echarts / ```markmap 围栏代码块替换为图表 / 思维导图容器。
        /// 内容存进 data-* 属性（HTML 转义），页面加载时由注入脚本 JSON.parse 后渲染。
        /// Markdig 对代码块内容做了实体转义，这里先反转义回原始 JSON / Markdown 文本。
        /// </summary>
        public string RenderFencedBlocks(string html)
        {
            if (string.IsNullOrEmpty(html)
                || (!html.Contains("language-echarts", StringComparison.OrdinalIgnoreCase)
                    && !html.Contains("language-markmap", StringComparison.OrdinalIgnoreCase)))
                return html;
            return FencedBlockRegex.Replace(html, m =>
            {
                var lang = m.Groups["lang"].Value.ToLowerInvariant();
                var raw = System.Net.WebUtility.HtmlDecode(m.Groups["body"].Value);
                var esc = System.Security.SecurityElement.Escape(raw);
                return lang == "echarts"
                    ? $"<div class=\"seeme-chart\" data-echarts='{esc}'></div>"
                    : $"<div class=\"seeme-markmap\" data-md='{esc}'></div>";
            });
        }

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

        /// <summary>鐢? MainWindow 娉ㄥ叆锛氶珮浜? storage锛屾牴鎹? state.CurrentFile 鏌ヨ鍑哄綋鍓嶆枃浠剁殑鏍囨敞鍦ㄦ父鏌撴椂娉ㄥ叆椤甸潰銆?</summary>
        public IHighlightStore? HighlightStore { get; set; }

        public string Render(string bodyHtml, PanelState state, string frontMatterCard,
            FrameworkElement resourceElement, Func<string>? logErr = null, string? customCss = null)
        {
            var isDark = IsDark();
            // echarts / markmap 围栏代码块 → 容器（在任何样式处理前替换，避免被 Prism 捕获）
            bodyHtml = RenderFencedBlocks(bodyHtml);
            var hasCharts = bodyHtml.Contains("class=\"seeme-chart\"", StringComparison.Ordinal);
            var hasMarkmaps = bodyHtml.Contains("class=\"seeme-markmap\"", StringComparison.Ordinal);
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
{DocumentCss(false)}
p {{ margin:.5em 0; }}
a {{ color:var(--link); text-decoration:none; }}
a:hover {{ text-decoration:underline; }}
ul,ol {{ padding-left:1.4em; margin:.4em 0; }}
li {{ margin:.15em 0; }}
blockquote {{ padding:.4em .8em; margin:.5em 0; }}
code {{ font-family:'Consolas','JetBrains Mono',Menlo,monospace; background:var(--code-bg); padding:1px 4px; border-radius:3px; font-size:.88em; }}
pre {{ background:var(--pre-bg); color:var(--pre-text); padding:10px 14px; border-radius:8px; overflow-x:auto; line-height:1.45; font-size:12px; margin:.5em 0; }}
pre code {{ background:transparent; color:inherit; padding:0; font-size:inherit; }}
pre .token {{ background:transparent !important; }}
table {{ border-collapse:collapse; width:100%; margin:.5em 0; font-size:12px; }}
th,td {{ border:1px solid var(--table-bdr); padding:4px 8px; }}
th {{ background:var(--table-head); font-weight:600; }}
tr:hover td {{ background:var(--table-hov); }}
hr {{ border:none; border-top:1px solid var(--hr); margin:1em 0; }}
img {{ display:block; margin:.5em 0; }}
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
mark.seeme-cur {{ background:var(--accent); color:#fff; }}
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
.seeme-chart {{ width:100%; height:420px; margin:.6em 0; }}
.seeme-markmap {{ width:100%; height:520px; margin:.6em 0; border:1px solid var(--table-bdr); border-radius:8px; overflow:hidden; }}
.seeme-markmap svg {{ width:100%; height:100%; display:block; }}
.seeme-markmap text {{ font-family:'Microsoft YaHei','PingFang SC',sans-serif; }}
html.dark .seeme-markmap text {{ fill:var(--text); }}
html.dark .seeme-markmap path {{ stroke:var(--secondary); }}
    ";

            // Markdown 渲染风格注入口（simple / github 覆盖基础样式）
            switch (MdStyle)
            {
                case "simple":
                    // 简约：去装饰、更大字距、内容加宽，一眼可辨
                    css += "\nh1,h2 {{ border-bottom:none; }}\n.fm-card {{ display:none; }}\nblockquote {{ border-left-width:2px; }}\n"
                         + "body {{ font-size:{FontSize + 1}px; line-height:{LineHeight + 0.15}; }}\n"
                         + ".markdown-body {{ max-width:960px; margin:0 auto; }}\n"
                         + "h1,h2,h3 {{ letter-spacing:.02em; }}\n"
                         + "pre {{ border-radius:4px; border:1px solid var(--table-bdr); }}\n";
                    break;
                case "github":
                    // GitHub 风：880 居中 + 卡片式区块 + 标题分隔明显
                    css += "\n.markdown-body {{ max-width:880px; margin:0 auto; padding:24px 32px; "
                         + "background:var(--card-bg, transparent); border-radius:10px; border:1px solid var(--table-bdr); }}\n"
                         + "h1 {{ border-bottom:2px solid var(--h1-border); }}\n"
                         + "h2 {{ border-bottom:1px solid var(--h2-border); }}\n"
                         + "blockquote {{ border-left-width:4px; }}\n"
                         + "code {{ background:var(--code-bg); }}\n";
                    break;
            }

            // 护眼模式：暖色滤镜。注意不能用 html/body 上的 filter——那会让 position:fixed 子元素
            // （双击放大 lightbox）的定位参考从视口变成整个文档盒。改为只作用于内容元素。
            // 强度调到肉眼可辨（sepia 0.35），暗色下同样加深。
            if (EyeCare)
                css += "\nbody > *:not(#seeme-ov) {{ filter:sepia(.35) saturate(.82) brightness(1.04) !important; }}\nhtml.dark body > *:not(#seeme-ov) {{ filter:sepia(.3) saturate(.78) brightness(.95) !important; }}\n";

            // 高亮标注 mark 样式 + 高亮笔光标 + 打印样式（@media print：白底黑字、隐藏元信息卡）
            css += @"
mark.seeme-ann {{ background:#FDE68A; color:#1F2937; border-radius:2px; padding:1px 2px; cursor:pointer; }}
html.dark mark.seeme-ann {{ background:#B45309; color:#FDE68A; }}
body.seeme-ann-active {{ cursor:text; }}
body.seeme-ann-active ::selection {{ background:#FDE68A; }}
@media print {{
  html, body {{ background:#fff !important; color:#000 !important; }}
  body {{ padding:0 !important; }}
  .fm-card {{ display:none !important; }}
  mark.seeme-hl, mark.seeme-ann {{ -webkit-print-color-adjust:exact; print-color-adjust:exact; }}
  /* PDF 导出开关：包含目录时，TOC 卡片在打印态显示，否则保留隐藏 */
  body.pdf-toc .fm-card.toc-card {{ display:block !important; }}
  body.pdf-toc .fm-card:not(.toc-card) {{ display:none !important; }}
  /* 页码：Chromium 支持 @page @bottom-center 用 Paged Media 计数器 */
  @page {{ @bottom-center {{ content: counter(page) "" / "" counter(pages); margin-bottom:12px; }} }}
}}
";

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
{SetThemeScript}
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

            // 远程图片策略：默认 img-src 不放行外网（防 IP/UA/阅读行为泄露给文档作者控制的图片服务器）；
            // 仅当用户显式授权该文档后放行 https:。配合页面 no-referrer，即便放行也不携带文档路径。
            var remoteImg = state.AllowRemoteImages ? " https:" : "";
            var nonce = NewNonce();

            var echartsJsUrl = hasCharts ? $"<script src='https://{VirtualHost}/echarts/echarts.min.js'></script>\n" : "";
            var chartInit = hasCharts ? $"<script nonce='{nonce}'>{ChartInitScript}</script>\n" : "";
            var markmapInit = hasMarkmaps ? $"<script type='module' nonce='{nonce}'>{MarkmapInitScript}</script>\n" : "";

            // 高亮标注：按当前文件路径取存储的标注，随页面加载按文本重新包裹
            var annData = (HighlightStore?.ForFile(state.CurrentFile ?? "") ?? Array.Empty<HighlightItem>())
                .Select(i => new { i.Id, i.Text, i.Note }).ToList();
            var annotationScript = BuildAnnotationScript(System.Text.Json.JsonSerializer.Serialize(annData));

            return $@"<!DOCTYPE html>
<html{htmlClass}><head><meta charset='utf-8'/>
<meta name='viewport' content='width=device-width,initial-scale=1'/>
<meta name='referrer' content='no-referrer'/>
<meta http-equiv='Content-Security-Policy' content=""default-src 'self' https://appassets.example; script-src 'nonce-{nonce}' https://appassets.example; style-src 'unsafe-inline' https://appassets.example; img-src 'self' data: https://appassets.example{remoteImg}; font-src 'self' data: https://appassets.example; base-uri 'self'; form-action 'none';"">
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
{echartsJsUrl}{chartInit}{markmapInit}
<script nonce='{nonce}'>{scrollScript}</script>
<script nonce='{nonce}'>{SearchScript}</script>
<script nonce='{nonce}'>{annotationScript}</script>
<script nonce='{nonce}'>{KeyBridgeScript}</script>
</body></html>";
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
