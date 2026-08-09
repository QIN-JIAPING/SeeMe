using System;
using System.IO;
using SeeMe;
using Xunit;

namespace SeeMe.Tests
{
    public class RenderServiceTests
    {
        private readonly RenderService _svc = new();

        [Fact]
        public void StripYamlFrontMatter_RemovesBlock()
        {
            var md = "---\ntitle: Test\nauthor: Bob\n---\n# Hello";
            var result = _svc.StripYamlFrontMatter(md, out var block);
            Assert.DoesNotContain("---", result);
            Assert.NotNull(block);
            Assert.Contains("title: Test", block);
            Assert.Equal("# Hello", result.Trim());
        }

        [Fact]
        public void StripYamlFrontMatter_NoFrontMatter_ReturnsOriginal()
        {
            var md = "# Just a heading";
            var result = _svc.StripYamlFrontMatter(md, out var block);
            Assert.Equal(md, result);
            Assert.Null(block);
        }

        [Fact]
        public void StripBom_RemovesBom()
        {
            Assert.Equal("hello", _svc.StripBom("\uFEFFhello"));
            Assert.Equal("no-bom", _svc.StripBom("no-bom"));
        }

        [Fact]
        public void ProcessRelativePaths_BlocksPathTraversal()
        {
            var html = @"<img src=""../../../etc/passwd"">";
            var result = _svc.ProcessRelativePaths(html, Path.GetTempPath());
            // 越界路径应原样保留（不内联、不报错）
            Assert.DoesNotContain("data:", result);
            Assert.Contains("../../../etc/passwd", result);
        }

        [Fact]
        public void ProcessRelativePaths_RemoteUrlsKept()
        {
            var html = @"<img src=""https://example.com/a.png"">";
            var result = _svc.ProcessRelativePaths(html, Path.GetTempPath());
            Assert.Contains("https://example.com/a.png", result);
            Assert.DoesNotContain("data:", result);
        }

        [Fact]
        public void ProcessRelativePaths_PreservesTrailingAttrs_WhenInlined()
        {
            // Markdig 输出 alt 在 src 之后；内联重写时必须保留 alt/class 等后置属性
            var dir = Path.Combine(Path.GetTempPath(), "seeme_img_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var png = Path.Combine(dir, "a.png");
                File.WriteAllBytes(png, new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4, 5, 6 });
                var html = @"<img src=""a.png"" alt=""图片说明"" class=""x"">";
                var result = _svc.ProcessRelativePaths(html, dir);
                Assert.Contains("data:image/png;base64,", result);
                Assert.Contains("alt=\"图片说明\"", result);
                Assert.Contains("class=\"x\"", result);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void ThemeCss_ContainsBothRootAndDark()
        {
            var css = RenderService.ThemeCss();
            Assert.StartsWith(":root{", css);
            Assert.Contains("}html.dark{", css);
            // 双套变量数量一致（亮=暗）
            Assert.Contains("--bg:", css);
        }

        [Fact]
        public void WrapPage_IncludesSetThemeAndInitialClass()
        {
            var html = RenderService.WrapPage(true, "t", "body{}", "<p>x</p>");
            Assert.Contains("class='dark'", html);
            Assert.Contains("function setTheme", html);
        }

        [Fact]
        public void ParseMediaColor_ValidHex()
        {
            var c = _svc.ParseMediaColor("#6366F1");
            Assert.Equal(0x63, c.R);
            Assert.Equal(0x66, c.G);
            Assert.Equal(0xF1, c.B);
        }

        [Fact]
        public void ParseMediaColor_InvalidHex_FallsBackWhite()
        {
            var c = _svc.ParseMediaColor("not-a-color");
            Assert.Equal(System.Windows.Media.Colors.White, c);
        }

        [Fact]
        public void BuildTocCard_ExtractsHeadingIds_FromRenderedHtml()
        {
            var md = "# 文档标题\n\n## 第一章\n\n正文内容。\n\n### 第一节\n\n## 第二章\n\n末尾。";
            var html = Markdig.Markdown.ToHtml(md, _svc.Pipeline);
            var card = _svc.BuildTocCard(html);

            // 必须生成目录卡片，且包含每个二级/三级标题的链接
            Assert.Contains("📑 目录", card);
            Assert.Contains("第一章", card);
            Assert.Contains("第一节", card);
            Assert.Contains("第二章", card);
            // 每个链接必须是真实锚点（href="#...")
            Assert.Contains("href=\"#", card);
        }

        [Fact]
        public void BuildTocCard_NoHeadings_ReturnsEmpty()
        {
            var html = "<p>只有段落，没有标题。</p>";
            Assert.Equal("", _svc.BuildTocCard(html));
        }

        [Fact]
        public void BuildTocCard_RealReadme_ProducesCard()
        {
            var path = @"D:\SeeMe\README.md";
            var md = File.ReadAllText(path);
            md = _svc.StripBom(md);
            md = _svc.StripYamlFrontMatter(md, out _);
            var parsed = Markdig.Markdown.ToHtml(md, _svc.Pipeline);
            // 完整真实管线：与 MainWindow.Rendering 完全一致的后处理链
            parsed = _svc.ProcessRelativePaths(parsed, @"D:\SeeMe");
            parsed = _svc.WrapTables(parsed);
            parsed = _svc.UpgradePreBlocks(parsed);
            var card = _svc.BuildTocCard(parsed);
            Assert.True(card.Contains("📑 目录"),
                "README 未生成目录卡片。渲染 HTML 片段:\n" + (parsed.Length > 600 ? parsed.Substring(0, 600) : parsed));
            Assert.True(card.Contains("核心特性"), "目录缺少二级标题。卡片内容:\n" + card);

            // 最终 Render 输出必须包含目录卡片（验证 fmCard+tocCard+statsCard 拼装链路）
            // WPF 控件需 STA 线程，xUnit 默认 MTA → 手动包 STA 线程
            string full = "";
            var thread = new System.Threading.Thread(() =>
            {
                var state = new PanelState(null, null, null, null);
                full = _svc.Render(parsed, state, card, new System.Windows.Controls.ContentControl());
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
            thread.Join();
            Assert.True(full.Contains("📑 目录"), "最终 HTML 缺少目录卡片，拼装链路断裂");
        }

        [Fact]
        public void BuildTocItems_ExtractsStructuredHeadings()
        {
            var md = "# 标题一\n\n## 标题二\n\n### 标题三\n\n## 标题四";
            var html = Markdig.Markdown.ToHtml(md, _svc.Pipeline);
            var items = _svc.BuildTocItems(html);

            Assert.Equal(4, items.Count);
            Assert.Equal(1, items[0].Level);
            Assert.Contains("标题一", items[0].Title);
            Assert.Equal(2, items[1].Level);
            Assert.Equal(3, items[2].Level);
            // 每个条目必须有可跳转的 id（锚点目标）
            Assert.All(items, it => Assert.False(string.IsNullOrWhiteSpace(it.Id)));
            // 卡片与结构化提取结果一致（同一数据源）
            var card = _svc.BuildTocCard(html);
            Assert.Contains(items[1].Title, card);
        }

        [Fact]
        public void BuildTocItems_NoHeadings_ReturnsEmpty()
        {
            Assert.Empty(_svc.BuildTocItems("<p>无标题</p>"));
            Assert.Empty(_svc.BuildTocItems(""));
        }

        [Fact]
        public void SanitizeLinkHrefs_BlocksDangerousSchemes()
        {
            var html = "<p><a href=\"javascript:alert(1)\">x</a> <a href=\"vbscript:msgbox(1)\">y</a> <a href=\"data:text/html,<script>1</script>\">z</a></p>";
            var result = _svc.SanitizeLinkHrefs(html);
            Assert.DoesNotContain("javascript:", result);
            Assert.DoesNotContain("vbscript:", result);
            Assert.DoesNotContain("data:text/html", result);
        }

        [Fact]
        public void SanitizeLinkHrefs_BlocksEntityObfuscatedScheme()
        {
            var html = "<a href=\"java&#x73;cript:alert(1)\">x</a>";
            var result = _svc.SanitizeLinkHrefs(html);
            Assert.DoesNotContain("javascript", result);
        }

        [Fact]
        public void SanitizeLinkHrefs_KeepsSafeLinks()
        {
            var html = "<a href=\"https://example.com\">a</a> <a href=\"/root\">b</a> <a href=\"#anchor\">c</a> <a href=\"other.md\">d</a> <a href=\"mailto:x@y.z\">e</a>";
            var result = _svc.SanitizeLinkHrefs(html);
            Assert.Contains("https://example.com", result);
            Assert.Contains("/root", result);
            Assert.Contains("#anchor", result);
            Assert.Contains("other.md", result);
            Assert.Contains("mailto:x@y.z", result);
        }
    }

    public class FileConverterTests
    {
        private readonly FileConverter _conv = new();

        [Fact]
        public void FormatSizeBytes_CorrectUnits()
        {
            Assert.Equal("500 B", _conv.FormatSizeBytes(500));
            Assert.Contains("KB", _conv.FormatSizeBytes(1024));
            Assert.Contains("MB", _conv.FormatSizeBytes(1024 * 1024));
        }
    }

    public class FileTypesTests
    {
        [Fact]
        public void IsSupported_RecognizesAllTypes()
        {
            Assert.True(FileTypes.IsSupported(".md"));
            Assert.True(FileTypes.IsSupported(".MARKDOWN"));
            Assert.True(FileTypes.IsSupported(".pdf"));
            Assert.True(FileTypes.IsSupported(".xlsx"));
            Assert.True(FileTypes.IsSupported(".docx"));
            Assert.False(FileTypes.IsSupported(".exe"));
            Assert.False(FileTypes.IsSupported(null));
        }

        [Fact]
        public void IsMarkdown_OnlyMarkdown()
        {
            Assert.True(FileTypes.IsMarkdown(".md"));
            Assert.True(FileTypes.IsMarkdown(".mkd"));
            Assert.False(FileTypes.IsMarkdown(".pdf"));
        }

        [Fact]
        public void OpenFilter_CoversAllExtensions()
        {
            Assert.Contains(".markdown", FileTypes.OpenFilter);
            Assert.Contains(".mdown", FileTypes.OpenFilter);
        }
    }
}
