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
        /// <summary>WebView2 虚拟主机名，映射到本地 Resources 目录，用于安全加载离线 JS/CSS/字体，避免 file: 协议。</summary>
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
            .UseMathematics()  // KaTeX 数学公式支持（$$...$$ 与 $...$）
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

        // 已升级的代码块（UpgradePreBlocks 输出）：分离 pre 属性 / code 属性 / 正文，供 AddCodeLineNumbers 逐行包裹。
        // pre 属性用 [^>]* 吞掉 have-lines 与 spellcheck 等；code 属性同理。正文非贪婪，配合 IgnoreCase 覆盖 </CODE>。
        private static readonly Regex CodeBlockRegex = new(
            @"<pre class=""code-block""(?<attrs>[^>]*)><code(?<codeAttrs>[^>]*)>(?<body>[\s\S]*?)</code></pre>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public string ProcessRelativePaths(string html, string? baseDir)
        {
            if (string.IsNullOrEmpty(baseDir)) return html;
            return ImgTagRegex.Replace(html, m =>
            {
                // 剥离 on*/style 危险属性，仅保留安全属性回写
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
                    // 路径遍历防护：解析结果必须仍位于 baseDir 之内，否则视为越界拒绝读取
                    var baseFull = Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (!full.StartsWith(baseFull, StringComparison.OrdinalIgnoreCase))
                        return $"<img{rest}>";
                    if (!File.Exists(full)) return $"<img{rest}>";
                    // 读取大小上限，避免超大文件内联导致 OOM（DoS）
                    var fi = new FileInfo(full);
                    if (fi.Length > Limits.MaxInlineImageBytes) return $"<img{rest}>";
                    // 相对图片内联为 data: URI，既避免 file: 协议（CSP 已禁用），又保证离线可用
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

        /// <summary>
        /// 把代码块内的纯文本按行包裹为 <c>&lt;span class="seeme-line"&gt;</c>，为 CSS 行号提供钩子。
        ///
        /// <para><b>为什么在 C# 侧做而不在 JS 侧做</b>：Prism 高亮后代码内部是 <c>&lt;span class="token"&gt;</c>
        /// 嵌套结构，JS 侧再按 <c>\n</c> 切分会横穿 token 边界，需要拆/建 DOM 节点，既慢又容易破坏高亮。
        /// 而**此处（UpgradePreBlocks 之后、Prism 之前）代码还是 Markdig 输出的纯文本**，
        /// 尚未被 Prism 改写，按 <c>\n</c> 切分是安全的 —— 切完包好的 <c>&lt;span class="seeme-line"&gt;</c>
        /// 会被 Prism 当作普通容器，token 落在里面，行结构完整保留。</para>
        ///
        /// <para><b>行的拆分口径</b>：只包「行首」的 <c>&lt;span class="seeme-ln"&gt;</c>（行号，CSS 计数器生成
        /// 序号、<c>user-select:none</c> 保证复制不带行号），<c>\n</c> 字符保留在行内容之后，
        /// 这样行间空白与选中行为都不变。切勿把 <c>\n</c> 移出行外 —— 那会改变复制出的文本。</para>
        ///
        /// <para><b>⚠️ 必须先剥掉 Markdig 的尾随换行</b>：Markdig 的代码块正文末尾**恒有一个 <c>\n</c>**
        /// （它属于围栏分隔符，不产生视觉行）。若不剥就直接按 <c>\n</c> 切分，会得到两个真实缺陷：
        /// ① 每块代码末尾凭空多出一个空行容器（视觉多一行空白、行号多一个）；
        /// ② 行数统计整体 +1，导致**恰好等于** <see cref="Limits.MaxCodeLinesWithNumbers"/> 行的代码块
        /// 被误判超限而静默降级 —— 上限实际变成了 2999 行。两者都是本方法初版踩过的坑。</para>
        ///
        /// <para><b>必须跳过</b>：① 图表块（echarts 的 JSON 会被逐行包 span，<c>JSON.parse</c> 直接失败；
        /// markmap 的容器结构也会被破坏）。注意 <c>language-echarts</c>/<c>language-markmap</c>
        /// 挂在 <c>&lt;code&gt;</c> 上，**只查 <c>&lt;pre&gt;</c> 属性会漏判**；
        /// ② 超 <see cref="Limits.MaxCodeLinesWithNumbers"/> 行的块（DOM 节点爆炸，见该常量注释）。</para>
        /// </summary>
        public string AddCodeLineNumbers(string html)
        {
            if (string.IsNullOrEmpty(html) || !html.Contains("code-block", StringComparison.Ordinal)) return html;
            return CodeBlockRegex.Replace(html, m =>
            {
                var attrs = m.Groups["attrs"].Value;
                var codeAttrs = m.Groups["codeAttrs"].Value;
                var body = m.Groups["body"].Value;
                if (body.Length == 0) return m.Value;
                // 图表块：内容要被 JSON.parse / markmap 读取，插 span 会破坏解析。
                // ⚠️ language-echarts / language-markmap 是挂在 <code> 上的（不在 <pre> 属性里）——
                // 只查 attrs 会漏判，务必查 codeAttrs。
                if (codeAttrs.Contains("language-echarts", StringComparison.OrdinalIgnoreCase)
                    || codeAttrs.Contains("language-markmap", StringComparison.OrdinalIgnoreCase))
                    return m.Value;

                // 剥掉 Markdig 的尾随换行（见方法注释：不剥会多一个空行且行数整体 +1）
                var contentLen = body.Length;
                if (contentLen > 0 && body[contentLen - 1] == '\n')
                {
                    if (contentLen > 1 && body[contentLen - 2] == '\r') contentLen--;
                    contentLen--;
                }

                // 行数预检：先数 \n 个数再决定切分，避免超限块白做一次遍历
                var newlines = 0;
                for (var i = 0; i < contentLen; i++) { if (body[i] == '\n') newlines++; }
                var lineCount = newlines + 1;
                if (lineCount > Limits.MaxCodeLinesWithNumbers)
                {
                    CodeLineSkippedCount++;
                    return m.Value; // 静默降级：不注入行号，仍正常显示（复制不受影响）
                }

                var sb = new StringBuilder(contentLen + lineCount * 40);
                var start = 0;
                for (var i = 0; i < lineCount; i++)
                {
                    var nl = body.IndexOf('\n', start);
                    var end = nl < 0 || nl >= contentLen ? contentLen : nl;
                    sb.Append("<span class=\"seeme-line\">");
                    sb.Append(body, start, end - start);
                    sb.Append("</span>");
                    if (end >= contentLen) break;
                    sb.Append('\n');
                    start = end + 1;
                }
                return "<pre class=\"code-block have-lines\"" + attrs + "><code" + codeAttrs + ">"
                       + sb + "</code></pre>";
            });
        }

        /// <summary>因超出行号上限而静默降级的代码块计数，仅用于诊断日志。</summary>
        public int CodeLineSkippedCount { get; private set; }

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
        /// 从渲染后的 HTML 提取标题（h1-h4 的真实 id 与文本）生成目录卡片（TOC）。
        /// 直接读渲染结果而非从 md 猜 slug——Markdig 对中文标题会回退为 section/section-N，
        /// 猜 id 必然锚点失配导致点击无反应。超 32 项截断。
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

        /// <summary>从渲染后 HTML 提取标题结构（级别/真实 id/纯文本），供侧边栏大纲使用。空标题或空 id 跳过，上限 128 项。</summary>
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

        /// <summary>匹配渲染后 HTML 的标题标签：&lt;h1-h4 id="..."&gt;内容&lt;/h1-h4&gt;（惰性捕获内容）。</summary>
        private static readonly System.Text.RegularExpressions.Regex TocHeadingRegex = new(
            @"<h([1-4])\s+id=""([^""]*)""[^>]*>(.*?)</h\1>",
            System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        public string StripBom(string s)
        {
            if (s.Length >= 1 && s[0] == '\uFEFF') return s.Substring(1);
            return s;
        }

        /// <summary>外部调用：依据传入的主题管理器判断是否暗色。</summary>
        public bool IsDarkTheme(IThemeManager theme) => theme.Current == theme.Dark;

        /// <summary>内部调用：使用注入的 IThemeManager 判断是否暗色。</summary>
        private bool IsDark() => ThemeManager != null && ThemeManager.Current == ThemeManager.Dark;

        /// <summary>Markdown 渲染风格：default / github / simple（设置变更时由 MainWindow 写入）。</summary>
        public string MdStyle { get; set; } = "default";

        /// <summary>护眼模式（暖色滤镜，设置变更时由 MainWindow 写入）。</summary>
        public bool EyeCare { get; set; }

        /// <summary>正文基础字号 px（默认 14，缩放倍数以此为基础）。</summary>
        public double FontSize { get; set; } = 14;

        /// <summary>正文行高倍数（默认 1.65）。</summary>
        public double LineHeight { get; set; } = 1.65;

        /// <summary>由 MainWindow 在装配服务时注入，供内部渲染逻辑判断当前主题。</summary>
        public IThemeManager? ThemeManager { get; set; }

        /// <summary>由 MainWindow 在装配服务时注入，供内部渲染逻辑格式化文件大小。</summary>
        public IFileConverter? Converter { get; set; }

        /// <summary>由 MainWindow 注入：高亮 storage，根据 state.CurrentFile 查出当前文件的标注在渲染时注入页面。</summary>
        public IHighlightStore? HighlightStore { get; set; }

        public string Render(string bodyHtml, PanelState state, string frontMatterCard,
            FrameworkElement resourceElement, Func<string>? logErr = null, string? customCss = null)
        {
            var isDark = IsDark();
            // echarts / markmap 围栏代码块 → 容器（在任何样式处理前替换，避免被 Prism 捕获）
            bodyHtml = RenderFencedBlocks(bodyHtml);
            var hasCharts = bodyHtml.Contains("class=\"seeme-chart\"", StringComparison.Ordinal);
            var hasMarkmaps = bodyHtml.Contains("class=\"seeme-markmap\"", StringComparison.Ordinal);
            // 主题双套固定值：:root 恒为亮色、html.dark 恒为暗色（见 ThemeVars 唯一调色板）。
            // 之前 :root 从「当前主题」的 WPF 资源读取，暗色模式下生成的页面 :root 已是暗色值，
            // 切回亮色（移除 dark class）后页面仍取 :root 的暗色值 → 页面与窗口主题不一致。
            // 双套固定值后，任意主题下生成的页面都能通过 class 双向正确切换。
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
pre {{ background:var(--pre-bg); color:var(--pre-text); padding:10px 14px; border-radius:8px; overflow-x:auto; line-height:1.45; font-size:12px; margin:.5em 0; position:relative; }}
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
/* ── 代码块行号 + 行级复制（AddCodeLineNumbers 注入 .seeme-line）──
   counter 由 .seeme-line 自增并在 ::before 显示序号；::before 是生成内容，
   **既不进选中范围也不进 textContent**，所以整块复制天然不含行号，无需 JS 清洗。 */
pre.have-lines {{ counter-reset:seeme-ln; padding-left:0; }}
pre.have-lines .seeme-line {{ display:block; padding-left:56px; position:relative; min-height:1.45em; }}
pre.have-lines .seeme-line::before {{
  counter-increment:seeme-ln; content:counter(seeme-ln);
  position:absolute; left:0; width:44px; padding-right:10px; text-align:right;
  color:var(--quote-text); opacity:.55; user-select:none; -webkit-user-select:none; pointer-events:none;
}}
pre.have-lines .seeme-line:hover {{ background:rgba(127,127,127,.10); }}
/* 行级复制按钮：hover 该行时出现在行右侧（整块复制按钮见页脚工具栏） */
.seeme-lncpy {{
  position:absolute; right:2px; top:50%; transform:translateY(-50%);
  border:none; border-radius:4px; background:transparent; color:var(--quote-text);
  font-size:11px; line-height:1; padding:3px 5px; cursor:pointer; opacity:0;
  user-select:none; -webkit-user-select:none; transition:opacity .12s ease;
}}
pre.have-lines .seeme-line:hover .seeme-lncpy {{ opacity:.75; }}
.seeme-lncpy:hover {{ opacity:1; background:rgba(127,127,127,.20); }}
/* 代码块工具栏（整块复制 / 行号开关）：默认半透明，悬停代码块时显形 */
.seeme-codebar {{
  position:absolute; right:6px; top:6px; display:flex; gap:4px; opacity:0;
  transition:opacity .15s ease; z-index:2;
}}
pre.have-lines:hover .seeme-codebar, pre.have-lines:focus-within .seeme-codebar {{ opacity:1; }}
.seeme-codebar button {{
  border:1px solid var(--table-bdr); background:var(--bg); color:var(--quote-text);
  font-size:11px; line-height:1; padding:3px 7px; border-radius:4px; cursor:pointer;
  user-select:none; -webkit-user-select:none;
}}
.seeme-codebar button:hover {{ color:var(--text); border-color:var(--secondary); }}
pre.have-lines.seeme-nolines .seeme-line {{ padding-left:16px; }}
pre.have-lines.seeme-nolines .seeme-line::before {{ display:none; }}
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
  // 当前可见的最上方标题 id：大纲高亮跟随用（IntersectionObserver 维护，见下方 observeHeadings）
  var __curHeading = '';
  // 阅读百分比：内容不足一屏时恒为 100（视为已读完），避免除零得到 NaN
  function __pct(){{
    try {{
      var d = document.documentElement;
      var max = d.scrollHeight - window.innerHeight;
      if(max <= 0) return 100;
      var p = Math.round(100 * window.scrollY / max);
      return p < 0 ? 0 : (p > 100 ? 100 : p);
    }} catch(e) {{ return 0; }}
  }}
  // __hover = 鼠标当前是否位于本栏页面内。双栏下宿主据此判断「这一侧才是我正在操作的」。
  // 判定源用 mouseenter/wheel（不监听 mousemove：高频回调无必要）。
  var __hover = false;
  function report(){{ try {{ window.chrome.webview.postMessage(JSON.stringify({{kind:'scroll',y:window.scrollY,p:__pct(),h:__curHeading,m:__hover}})); }} catch(e){{}} }}
  // 进入本栏即上报一次：让活动栏（标题栏高亮、Ctrl+S 保存目标等）立刻跟随鼠标，
  // 无需先点一下或先滚一下。wheel 兜底覆盖「鼠标已在页内但 mouseenter 已错过」的情形。
  window.addEventListener('mouseenter', function(){{ if(!__hover){{ __hover = true; report(); }} }});
  window.addEventListener('wheel', function(){{ if(!__hover){{ __hover = true; report(); }} }}, {{ passive: true }});
  window.addEventListener('mouseleave', function(){{ __hover = false; }});
  // 页面失焦（切到另一栏 / 窗口失活）时清掉，避免两侧都自认持有鼠标
  window.addEventListener('blur', function(){{ __hover = false; }});
  function applyZoom(z){{ document.documentElement.style.fontSize=(z*{FontSize.ToString(System.Globalization.CultureInfo.InvariantCulture)})+'px'; }}
  // 标题可见性跟踪：threshold 用离散值数组（连续值会触发过多回调）。
  // 记录「视口内最靠上的标题」，据此高亮大纲对应项。
  function observeHeadings(){{
    try {{
      var hs = document.querySelectorAll('h1[id],h2[id],h3[id],h4[id],h5[id],h6[id]');
      if(!hs.length || !window.IntersectionObserver) return;
      var visible = {{}};
      var io = new IntersectionObserver(function(entries){{
        entries.forEach(function(en){{
          if(en.isIntersecting) visible[en.target.id] = true; else delete visible[en.target.id];
        }});
        var best = '', bestTop = Infinity;
        for(var id in visible){{
          var el = document.getElementById(id);
          if(!el) continue;
          var t = el.getBoundingClientRect().top;
          if(t < bestTop) {{ bestTop = t; best = id; }}
        }}
        // 没有任何标题在视口内（如滚动到图区）时保留上一次的 id，避免大纲高亮闪断
        if(best) __curHeading = best;
      }}, {{ rootMargin:'0px', threshold:[0, 0.1, 0.5, 1] }});
      hs.forEach(function(h){{ io.observe(h); }});
    }} catch(e) {{}}
  }}
  function tryRestore(){{
    if(__restoreY > 0) {{ window.scrollTo(0, __restoreY); }}
    applyZoom({state.FontScale.ToString(System.Globalization.CultureInfo.InvariantCulture)});
    try {{ if(window.Prism) Prism.highlightAll(); }} catch(e){{}}
    try {{ if(window.renderMathInElement) renderMathInElement(document.body,{{delimiters:[{{left:'$$',right:'$$',display:true}},{{left:'$',right:'$',display:false}}],throwOnError:false}}); }} catch(e){{}}
    try {{ if(window.mermaid) mermaid.initialize({{startOnLoad:true,theme:{(IsDark() ? "'dark'" : "'default'")}}}); }} catch(e){{}}
    observeHeadings();
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
    // 同页锚点（#...）保留浏览器原生跳转，避免破坏文内目录导航
    if(h.charAt(0) === '#') return;
    // 阻止默认行为：否则 WebView2 在 script-src 'unsafe-inline' 下会执行 javascript:/vbscript: 伪协议（XSS）。
    // 所有跨页跳转统一交给宿主在 OnWebMessage 中按 scheme 白名单处理。
    e.preventDefault();
    try {{ window.chrome.webview.postMessage(JSON.stringify({{kind:'link',href:h}})); }} catch(err){{}}
  }});
  window.__seemeApplyZoom = applyZoom;
  // 双击图片 → 全屏 lightbox 放大（再次点击 / Esc 关闭）。id 与护眼 filter 的 :not() 配合，避免自身被滤镜影响。
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
            // 图表导出脚本只在页面含图表时注入；Mermaid（```mermaid 经 Markdig 出 <div class="mermaid">）
            // 也算可导出图表 —— 它渲染后同样是 SVG DOM，走同一条序列化路径。
            var hasMermaid = bodyHtml.Contains("class=\"mermaid\"", StringComparison.Ordinal);
            var chartExportTag = (hasCharts || hasMarkmaps || hasMermaid)
                ? $"<script nonce='{nonce}'>{ChartExportScript}</script>\n" : "";

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
<script nonce='{nonce}'>{CodeBlockScript}</script>
{chartExportTag}<script nonce='{nonce}'>{SearchScript}</script>
<script nonce='{nonce}'>{annotationScript}</script>
<script nonce='{nonce}'>{KeyBridgeScript}</script>
</body></html>";
        }

    }

    /// <summary>文档大纲条目：Markdig 渲染后标题的真实 id（锚点跳转目标）与纯文本标题。</summary>
    public sealed class TocItem
    {
        public int Level;          // h1=1 ... h4=4
        public string Id = "";     // 渲染 HTML 中的 id 属性（已去 HTML 标签）
        public string Title = "";  // 纯文本标题
    }
}
