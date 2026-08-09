using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace SeeMe
{
    /// <summary>
    /// 主题管理。构建完整 ResourceDictionary 替换到窗口，而非修改已有 Brush 对象。
    /// </summary>
    public class ThemeManager : IThemeManager
    {
        public string Light => "light";
        public string Dark  => "dark";

        private const string SettingsFileName = "settings.json";
        private const string ThemeKey = "theme";
        private const string WelcomeKey = "welcomeDismissed";

        private string _current = "light";
        public  string Current => _current;

        /// <summary>欢迎提示卡片是否已关闭且不再显示。</summary>
        public bool WelcomeDismissed { get; private set; }

        /// <summary>主题切换时触发。</summary>
        public event Action? Changed;

        /// <summary>动画效果开关（false 时主题切换直接赋值，不做颜色过渡动画）。</summary>
        public bool AnimationsEnabled { get; set; } = true;

        // ──────────────── 亮/暗色（唯一来源 ThemeColors，改色只改 ThemeColors.cs） ────────────────
        private readonly Dictionary<string, Color> LightColors =
            new(ThemeColors.ResourceColors(isDark: false));

        private readonly Dictionary<string, Color> DarkColors =
            new(ThemeColors.ResourceColors(isDark: true));

        // ──────────────── 阴影参数 ────────────────
        private const double LightCardOpacity = 0.06, LightCardDepth = 3;
        private const double DarkCardOpacity  = 0.18, DarkCardDepth  = 2;
        private const double LightSideOpacity = 0.04, LightSideDepth = 2;
        private const double DarkSideOpacity  = 0.16, DarkSideDepth  = 2;

        // ──────────────── API ────────────────

        /// <summary>预构建的字典缓存，避免每次切换都重建。</summary>
        private ResourceDictionary? _lightDict;
        private ResourceDictionary? _darkDict;

        public void Initialize()
        {
            var saved = LoadSavedTheme();
            _current = saved == Dark ? Dark : Light;
            WelcomeDismissed = LoadWelcomeDismissed();
            // 预构建两个字典
            _lightDict = BuildDictionary(Light);
            _darkDict  = BuildDictionary(Dark);
        }

        /// <summary>构建完整的主题 ResourceDictionary（颜色 + 阴影）。</summary>
        public ResourceDictionary BuildDictionary(string theme)
        {
            var dict  = new ResourceDictionary();
            var map   = theme == Dark ? DarkColors : LightColors;
            var isDark = theme == Dark;

            foreach (var kv in map)
                dict[kv.Key] = new SolidColorBrush(kv.Value);

            var accent = map["AccentBrush"];
            var shadowCol = isDark ? Colors.Black : accent;

            dict["CardShadow"] = new DropShadowEffect
            {
                BlurRadius  = 20,
                Opacity     = isDark ? DarkCardOpacity  : LightCardOpacity,
                ShadowDepth = isDark ? DarkCardDepth    : LightCardDepth,
                Color       = shadowCol,
            };
            dict["SidebarShadow"] = new DropShadowEffect
            {
                BlurRadius  = 14,
                Opacity     = isDark ? DarkSideOpacity  : LightSideOpacity,
                ShadowDepth = isDark ? DarkSideDepth    : LightSideDepth,
                Color       = shadowCol,
            };
            return dict;
        }

        /// <summary>已合入各窗口的主题字典引用（per-window）。合入一次后永不替换，切换只动画 Brush.Color。</summary>
        private readonly Dictionary<Window, ResourceDictionary> _installedByWindow = new();

        private static readonly TimeSpan AnimDuration = TimeSpan.FromMilliseconds(280);

        /// <summary>
        /// 应用主题到窗口（支持多窗口：单例 ThemeManager 服务所有窗口）。
        /// 窗口首次：合入预构建主题字典；后续切换：对每个 Brush/Effect 做颜色动画（渲染线程插值，主线程零阻塞）。
        /// 永不替换 MergedDictionaries——全量替换会强制重解析所有 DynamicResource 并销毁重建 Effect，导致卡死/闪屏。
        /// </summary>
        public void ApplyToWindow(Window window, string theme)
        {
            if (!_installedByWindow.TryGetValue(window, out var installed))
            {
                // 窗口首次合入：目标主题字典整体加入窗口资源（资源已是目标色，无动画直显）
                installed = theme == Dark ? _darkDict : _lightDict;
                window.Resources.MergedDictionaries.Clear();
                window.Resources.MergedDictionaries.Add(installed!);
                _installedByWindow[window] = installed!;
                // 窗口关闭时清理映射，避免内存泄漏
                window.Closed += (_, _) => _installedByWindow.Remove(window);
            }
            else
            {
                ApplyAnimated(window, theme, installed);
            }

            _current = theme;
            SaveTheme(theme);

            Changed?.Invoke();
        }

        /// <summary>
        /// 主题色（accent）切换：靛蓝 / 蓝 / 绿。
        /// 只动画 accent 相关键（AccentBrush/AccentLightBrush/ItemHover/ItemSelected/TitleBarActive），
        /// 并同步 LightColors/DarkColors 色板 map——否则后续明暗切换 ApplyAnimated 会把 accent 动画回旧值。
        /// 已安装窗口与缓存字典共享同一批 Brush 实例，动画循环直接改色即两处同步。
        /// </summary>
        public void ApplyAccent(string accent)
        {
            ThemeColors.ActiveAccent = accent;

            foreach (var kv in ThemeColors.AccentOverrides(isDark: false, accent))
                LightColors[kv.Key] = kv.Value;
            foreach (var kv in ThemeColors.AccentOverrides(isDark: true, accent))
                DarkColors[kv.Key] = kv.Value;

            var overrides = ThemeColors.AccentOverrides(Current == Dark, accent);
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseInOut };
            foreach (var window in _installedByWindow.Keys.ToList())
            {
                if (!_installedByWindow.TryGetValue(window, out var installed)) continue;
                foreach (var kv in overrides)
                {
                    var existing = window.TryFindResource(kv.Key) as SolidColorBrush;
                    if (existing != null && !existing.IsFrozen)
                    {
                        if (!AnimationsEnabled)
                        {
                            existing.BeginAnimation(SolidColorBrush.ColorProperty, null);
                            existing.Color = kv.Value;
                        }
                        else
                        {
                            existing.BeginAnimation(SolidColorBrush.ColorProperty,
                                new ColorAnimation(existing.Color, kv.Value, AnimDuration) { EasingFunction = ease },
                                HandoffBehavior.Compose);
                        }
                    }
                    else
                    {
                        installed[kv.Key] = new SolidColorBrush(kv.Value);
                    }
                }
            }
        }

        /// <summary>原子切换：同一时刻对所有 Brush 发起 ColorAnimation，渲染线程同帧插值，无空帧无卡顿。
        /// 显式 From=当前动画值 + HandoffBehavior.Compose：快速连切时新动画从实时值无缝接续，杜绝基值回跳。</summary>
        private void ApplyAnimated(Window window, string theme, ResourceDictionary installed)
        {
            var map    = theme == Dark ? DarkColors : LightColors;
            var isDark = theme == Dark;
            var ease   = new QuadraticEase { EasingMode = EasingMode.EaseInOut };

            if (!AnimationsEnabled)
            {
                // 动画关闭：直接赋值颜色，跳过动画
                foreach (var kv in map)
                    if (installed[kv.Key] is SolidColorBrush b) b.Color = kv.Value;
                var sCol = isDark ? Colors.Black : map["AccentBrush"];
                if (installed["CardShadow"] is DropShadowEffect cs) { cs.Color = sCol; cs.Opacity = isDark ? DarkCardOpacity : LightCardOpacity; cs.ShadowDepth = isDark ? DarkCardDepth : LightCardDepth; }
                if (installed["SidebarShadow"] is DropShadowEffect ss) { ss.Color = sCol; ss.Opacity = isDark ? DarkSideOpacity : LightSideOpacity; ss.ShadowDepth = isDark ? DarkSideDepth : LightSideDepth; }
                return;
            }

            foreach (var kv in map)
            {
                var existing = window.TryFindResource(kv.Key) as SolidColorBrush;
                if (existing != null && !existing.IsFrozen)
                {
                    var anim = new ColorAnimation(existing.Color, kv.Value, AnimDuration)
                    {
                        EasingFunction = ease,
                    };
                    existing.BeginAnimation(SolidColorBrush.ColorProperty, anim, HandoffBehavior.Compose);
                }
                else
                {
                    // 缺失或冻结：写入新的 unfrozen 实例到该窗口的主题字典（下次切换即可动画）
                    installed[kv.Key] = new SolidColorBrush(kv.Value);
                }
            }

            var shadowCol = isDark ? Colors.Black : map["AccentBrush"];
            AnimateShadow(window, "CardShadow", shadowCol,
                isDark ? DarkCardOpacity : LightCardOpacity,
                isDark ? DarkCardDepth : LightCardDepth, ease);
            AnimateShadow(window, "SidebarShadow", shadowCol,
                isDark ? DarkSideOpacity : LightSideOpacity,
                isDark ? DarkSideDepth : LightSideDepth, ease);
        }

        /// <summary>阴影切换：Color/Opacity 动画；ShadowDepth 直接赋值（参与动画会每帧触发效果重绘，可能闪白线）。</summary>
        private void AnimateShadow(Window window, string key, Color color, double opacity, double depth, IEasingFunction ease)
        {
            var effect = window.TryFindResource(key) as DropShadowEffect;
            if (effect == null || effect.IsFrozen)
            {
                if (_installedByWindow.TryGetValue(window, out var installed))
                    installed[key] = new DropShadowEffect
                    {
                        BlurRadius  = key == "CardShadow" ? 20 : 14,
                        Color       = color,
                        Opacity     = opacity,
                        ShadowDepth = depth,
                    };
                return;
            }
            effect.BeginAnimation(DropShadowEffect.ColorProperty,
                new ColorAnimation(effect.Color, color, AnimDuration) { EasingFunction = ease },
                HandoffBehavior.Compose);
            effect.BeginAnimation(DropShadowEffect.OpacityProperty,
                new DoubleAnimation(effect.Opacity, opacity, AnimDuration) { EasingFunction = ease },
                HandoffBehavior.Compose);
            // ShadowDepth 亮暗差异仅 1px（Card 3↔2），直接赋值不影响视觉连续性，避免效果每帧重绘
            effect.ShadowDepth = depth;
        }

        public void Toggle(Window window)
        {
            var next = _current == Light ? Dark : Light;
            ApplyToWindow(window, next);
        }

        // ──────────────── 持久化 ────────────────

        private string StoragePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SeeMe", SettingsFileName);

        private string? LoadSavedTheme()
        {
            try
            {
                using var doc = LoadSettingsDoc();
                if (doc != null
                    && doc.RootElement.TryGetProperty(ThemeKey, out var el)
                    && el.ValueKind == JsonValueKind.String)
                {
                    return el.GetString();
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[SeeMe] ThemeManager.LoadSavedTheme: " + ex.Message); }
            return null;
        }

        private bool LoadWelcomeDismissed()
        {
            try
            {
                using var doc = LoadSettingsDoc();
                if (doc != null
                    && doc.RootElement.TryGetProperty(WelcomeKey, out var el)
                    && el.ValueKind == JsonValueKind.True)
                {
                    return true;
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[SeeMe] ThemeManager.LoadWelcomeDismissed: " + ex.Message); }
            return false;
        }

        /// <summary>合并写 settings.json：读现有内容→更新指定键→写回，避免整文件覆盖丢键。</summary>
        private void SaveSettings(params (string Key, JsonNode? Value)[] updates)
        {
            try
            {
                var dir = Path.GetDirectoryName(StoragePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                System.Text.Json.Nodes.JsonObject obj = new();
                if (File.Exists(StoragePath))
                {
                    try
                    {
                        obj = JsonNode.Parse(File.ReadAllText(StoragePath)) as System.Text.Json.Nodes.JsonObject ?? new();
                    }
                    catch { /* 解析失败则从空对象重建 */ }
                }

                foreach (var (key, value) in updates)
                    obj[key] = value;

                File.WriteAllText(StoragePath, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[SeeMe] ThemeManager.SaveSettings: " + ex.Message); }
        }

        /// <summary>读 settings.json 根对象；文件缺失/损坏返回 null。</summary>
        private JsonDocument? LoadSettingsDoc()
        {
            if (!File.Exists(StoragePath)) return null;
            return JsonDocument.Parse(File.ReadAllText(StoragePath));
        }

        private void SaveTheme(string theme)
        {
            SaveSettings((ThemeKey, theme));
        }

        /// <summary>设置欢迎提示卡片是否永久关闭。</summary>
        public void SetWelcomeDismissed(bool dismissed)
        {
            WelcomeDismissed = dismissed;
            SaveSettings((WelcomeKey, dismissed));
        }
    }
}
