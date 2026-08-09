using System;
using System.Text;
using System.Windows.Media;

namespace SeeMe
{
    /// <summary>
    /// 主题颜色统一来源（唯一调色板）。RenderService（CSS 变量）与 ThemeManager（WPF 资源）共用，
    /// 改主题只改这一处。:root 恒为亮色、html.dark 恒为暗色，与渲染时当前主题彻底脱钩。
    /// </summary>
    public static class ThemeColors
    {
        // ═══════════ WPF 资源色（语义命名，供 ThemeManager 构建 Brush） ═══════════

        public static class Light
        {
            public static readonly Color Accent          = Color.FromRgb(0x63, 0x66, 0xF1);
            public static readonly Color AccentLight     = Color.FromRgb(0xEE, 0xF0, 0xFF);
            public static readonly Color Heading         = Color.FromRgb(0x1E, 0x1B, 0x4B);
            public static readonly Color Text            = Color.FromRgb(0x33, 0x41, 0x55);
            public static readonly Color Secondary       = Color.FromRgb(0x94, 0xA3, 0xB8);
            public static readonly Color IconFg          = Color.FromRgb(0x64, 0x74, 0x8B);
            public static readonly Color DarkBtn         = Color.FromRgb(0x1E, 0x1B, 0x4B);
            public static readonly Color LightGray       = Color.FromRgb(0xE2, 0xE8, 0xF0);
            public static readonly Color InputBg         = Color.FromRgb(0xF8, 0xFA, 0xFC);
            public static readonly Color Bg              = Color.FromRgb(0xF1, 0xF5, 0xF9);
            public static readonly Color Sidebar         = Color.FromRgb(0xFF, 0xFF, 0xFF);
            public static readonly Color Card            = Color.FromRgb(0xFF, 0xFF, 0xFF);
            public static readonly Color TitleBar        = Color.FromRgb(0xFF, 0xFF, 0xFF);
            public static readonly Color TitleBarActive  = Color.FromRgb(0xEE, 0xF0, 0xFF);
            public static readonly Color ItemHover       = Color.FromRgb(0xF4, 0xF5, 0xFF);
            public static readonly Color ItemSelected    = Color.FromRgb(0xE0, 0xE3, 0xFF);
        }

        public static class Dark
        {
            public static readonly Color Accent          = Color.FromRgb(0x81, 0x8C, 0xF8);
            public static readonly Color AccentLight     = Color.FromRgb(0x1E, 0x1B, 0x4B);
            public static readonly Color Heading         = Color.FromRgb(0xF1, 0xF5, 0xF9);
            public static readonly Color Text            = Color.FromRgb(0xCB, 0xD5, 0xE1);
            public static readonly Color Secondary       = Color.FromRgb(0x94, 0xA3, 0xB8);
            public static readonly Color IconFg          = Color.FromRgb(0xCB, 0xD5, 0xE1);
            public static readonly Color DarkBtn         = Color.FromRgb(0xCB, 0xD5, 0xE1);
            public static readonly Color LightGray       = Color.FromRgb(0x33, 0x3B, 0x48);
            public static readonly Color InputBg         = Color.FromRgb(0x1E, 0x23, 0x2E);
            public static readonly Color Bg              = Color.FromRgb(0x0F, 0x13, 0x1A);
            public static readonly Color Sidebar         = Color.FromRgb(0x16, 0x1B, 0x24);
            public static readonly Color Card            = Color.FromRgb(0x1C, 0x23, 0x2E);
            public static readonly Color TitleBar        = Color.FromRgb(0x1C, 0x23, 0x2E);
            // 活动标题栏必须与空闲色有明显区分（曾用 #1E1B4B，与 #1C232E 几乎同色，
            // 分栏后暗色下"当前选中栏"高亮肉眼不可见）。取暗色选中态 ItemSelected 同色，语义一致且可辨。
            public static readonly Color TitleBarActive  = Color.FromRgb(0x2E, 0x30, 0x50);
            public static readonly Color ItemHover       = Color.FromRgb(0x24, 0x2B, 0x38);
            public static readonly Color ItemSelected    = Color.FromRgb(0x2E, 0x30, 0x50);
        }

        /// <summary>按主题返回 WPF 资源名 → 颜色映射（与 XAML 中 DynamicResource 键一一对应）。
        /// accent：indigo / blue / green，为 null 时用当前 ActiveAccent。</summary>
        public static System.Collections.Generic.IReadOnlyDictionary<string, Color> ResourceColors(bool isDark, string? accent = null)
        {
            accent ??= ActiveAccent;
            // 嵌套类型 Light/Dark 不能作为值参与三元，直接展开两套（键顺序与字典内容完全一致）
            var map = isDark
                ? new System.Collections.Generic.Dictionary<string, Color>
                {
                    ["AccentBrush"]               = Dark.Accent,
                    ["AccentLightBrush"]          = Dark.AccentLight,
                    ["TextPrimaryBrush"]           = Dark.Heading,
                    ["TextBodyBrush"]              = Dark.Text,
                    ["TextSecondaryBrush"]         = Dark.Secondary,
                    ["IconForegroundBrush"]        = Dark.IconFg,
                    ["DangerBrush"]               = Color.FromRgb(0xF8, 0x71, 0x71),
                    ["DarkBtnBrush"]               = Dark.DarkBtn,
                    ["LightGrayBrush"]             = Dark.LightGray,
                    ["InputBgBrush"]               = Dark.InputBg,
                    ["WindowBackgroundBrush"]      = Dark.Bg,
                    ["SidebarBackgroundBrush"]     = Dark.Sidebar,
                    ["CardBackgroundBrush"]        = Dark.Card,
                    ["TitleBarBackgroundBrush"]    = Dark.TitleBar,
                    ["TitleBarActiveBackgroundBrush"] = Dark.TitleBarActive,
                    ["ItemHoverBrush"]             = Dark.ItemHover,
                    ["ItemSelectedBrush"]          = Dark.ItemSelected,
                }
                : new System.Collections.Generic.Dictionary<string, Color>
                {
                    ["AccentBrush"]               = Light.Accent,
                    ["AccentLightBrush"]          = Light.AccentLight,
                    ["TextPrimaryBrush"]           = Light.Heading,
                    ["TextBodyBrush"]              = Light.Text,
                    ["TextSecondaryBrush"]         = Light.Secondary,
                    ["IconForegroundBrush"]        = Light.IconFg,
                    ["DangerBrush"]               = Color.FromRgb(0xEF, 0x44, 0x44),
                    ["DarkBtnBrush"]               = Light.DarkBtn,
                    ["LightGrayBrush"]             = Light.LightGray,
                    ["InputBgBrush"]               = Light.InputBg,
                    ["WindowBackgroundBrush"]      = Light.Bg,
                    ["SidebarBackgroundBrush"]     = Light.Sidebar,
                    ["CardBackgroundBrush"]        = Light.Card,
                    ["TitleBarBackgroundBrush"]    = Light.TitleBar,
                    ["TitleBarActiveBackgroundBrush"] = Light.TitleBarActive,
                    ["ItemHoverBrush"]             = Light.ItemHover,
                    ["ItemSelectedBrush"]          = Light.ItemSelected,
                };

            ApplyAccentOverrides(map, isDark, accent);
            return map;
        }

        // ═══════════ 主题色（accent）多色板 ═══════════

        /// <summary>当前激活的主题色（由设置/主题切换写入，WPF 资源与 CSS 变量共用）。</summary>
        public static string ActiveAccent = "indigo";

        /// <summary>accent 主色（亮/暗）。</summary>
        public static (Color Light, Color Dark) AccentPair(string accent) => accent switch
        {
            "blue"  => (Color.FromRgb(0x3B, 0x82, 0xF6), Color.FromRgb(0x60, 0xA5, 0xFA)),
            "green" => (Color.FromRgb(0x10, 0xB9, 0x81), Color.FromRgb(0x34, 0xD3, 0x99)),
            _       => (Color.FromRgb(0x63, 0x66, 0xF1), Color.FromRgb(0x81, 0x8C, 0xF8)),
        };

        /// <summary>accent 浅色底（亮/暗）。</summary>
        public static Color AccentLightColor(string accent, bool isDark) => accent switch
        {
            "blue"  => isDark ? Color.FromRgb(0x17, 0x25, 0x54) : Color.FromRgb(0xEF, 0xF6, 0xFF),
            "green" => isDark ? Color.FromRgb(0x06, 0x4E, 0x3B) : Color.FromRgb(0xEC, 0xFD, 0xF5),
            _       => isDark ? Color.FromRgb(0x1E, 0x1B, 0x4B) : Color.FromRgb(0xEE, 0xF0, 0xFF),
        };

        /// <summary>accent 衍生选中/悬停色（亮/暗）。</summary>
        public static (Color Hover, Color Selected) AccentTints(string accent, bool isDark) => accent switch
        {
            "blue"  => isDark ? (Color.FromRgb(0x1E, 0x2A, 0x44), Color.FromRgb(0x1E, 0x3A, 0x5F))
                               : (Color.FromRgb(0xF0, 0xF7, 0xFF), Color.FromRgb(0xDB, 0xEA, 0xFE)),
            "green" => isDark ? (Color.FromRgb(0x12, 0x35, 0x2B), Color.FromRgb(0x0B, 0x3A, 0x2E))
                               : (Color.FromRgb(0xF0, 0xFD, 0xF9), Color.FromRgb(0xD1, 0xFA, 0xE5)),
            _       => isDark ? (Color.FromRgb(0x24, 0x2B, 0x38), Color.FromRgb(0x2E, 0x30, 0x50))
                               : (Color.FromRgb(0xF4, 0xF5, 0xFF), Color.FromRgb(0xE0, 0xE3, 0xFF)),
        };

        /// <summary>accent 相关资源键 → 颜色（主题色切换时只改这些键）。</summary>
        public static System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<string, Color>>
            AccentOverrides(bool isDark, string accent)
        {
            var (mainL, mainD) = AccentPair(accent);
            var light = AccentLightColor(accent, isDark);
            var (h, s) = AccentTints(accent, isDark);
            yield return new System.Collections.Generic.KeyValuePair<string, Color>("AccentBrush", isDark ? mainD : mainL);
            yield return new System.Collections.Generic.KeyValuePair<string, Color>("AccentLightBrush", light);
            yield return new System.Collections.Generic.KeyValuePair<string, Color>("ItemHoverBrush", h);
            yield return new System.Collections.Generic.KeyValuePair<string, Color>("ItemSelectedBrush", s);
            yield return new System.Collections.Generic.KeyValuePair<string, Color>("TitleBarActiveBackgroundBrush", light);
        }

        private static void ApplyAccentOverrides(System.Collections.Generic.Dictionary<string, Color> map,
            bool isDark, string accent)
        {
            foreach (var kv in AccentOverrides(isDark, accent))
                map[kv.Key] = kv.Value;
        }

        // ═══════════ CSS 变量（RenderService 用，亮/暗双套编译期固定） ═══════════

        private static readonly (string N, string L, string D)[] Vars =
        {
            ("--bg",         "#F1F5F9", "#0F131A"),
            ("--card",       "#FFFFFF", "#1C232E"),
            ("--text",       "#334155", "#CBD5E1"),
            ("--heading",    "#1E1B4B", "#F1F5F9"),
            ("--secondary",  "#94A3B8", "#94A3B8"),
            ("--border",     "#E2E8F0", "#333B48"),
            ("--accent",     "#6366F1", "#818CF8"),
            ("--h1-border",  "#1E1B4B", "#CBD5E1"),
            ("--h2-border",  "#E2E8F0", "#333B48"),
            ("--link",       "#6366F1", "#818CF8"),
            ("--quote",      "#6366F1", "#818CF8"),
            ("--quote-bg",   "#EEF0FF", "#1E1B4B"),
            ("--quote-text", "#94A3B8", "#94A3B8"),
            ("--code-bg",    "#F1F5F9", "#333B48"),
            ("--pre-bg",     "#1E1B4B", "#161B24"),
            ("--pre-text",   "#E2E8F0", "#E2E8F0"),
            ("--table-bdr",  "#E2E8F0", "#333B48"),
            ("--table-head", "#E2E8F0", "#333B48"),
            ("--table-hov",  "#F8FAFC", "#242B38"),
            ("--hr",         "#E2E8F0", "#333B48"),
            ("--fm-bg",      "#EEF0FF", "#1E1B4B"),
            ("--danger",     "#EF4444", "#F87171"),
            ("--logo-bg",    "#1C1F26", "#1C1F26"),
            ("--row-hov",    "rgba(0,0,0,.02)", "rgba(255,255,255,.06)"),
        };

        /// <summary>生成 :root（亮）+ html.dark（暗）双套 CSS 变量块，accent 相关变量跟随 ActiveAccent。</summary>
        public static string ThemeCss()
        {
            var a = ActiveAccent;
            var sb = new StringBuilder(":root{");
            foreach (var v in Vars)
            {
                var val = v.L;
                if (v.N is "--accent" or "--link" or "--quote") val = AccentHex(a, false);
                else if (v.N is "--quote-bg" or "--fm-bg") val = AccentLightHex(a, false);
                sb.Append(v.N).Append(':').Append(val).Append(';');
            }
            sb.Append("}html.dark{");
            foreach (var v in Vars)
            {
                var val = v.D;
                if (v.N is "--accent" or "--link" or "--quote") val = AccentHex(a, true);
                else if (v.N is "--quote-bg" or "--fm-bg") val = AccentLightHex(a, true);
                sb.Append(v.N).Append(':').Append(val).Append(';');
            }
            return sb.Append('}').ToString();
        }

        private static string AccentHex(string accent, bool dark)
        {
            var (l, d) = AccentPair(accent);
            var c = dark ? d : l;
            return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
        }

        private static string AccentLightHex(string accent, bool dark)
        {
            var c = AccentLightColor(accent, dark);
            return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
        }
    }
}
