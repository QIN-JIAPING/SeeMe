// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows.Input;

namespace SeeMe
{
    /// <summary>
    /// 可自定义快捷键的**单一事实来源**：命令表（id / 分类 / 显示名 / 默认手势）
    /// 与手势的解析、格式化、持久化。
    ///
    /// <para><b>为什么要有这张表</b>：在此之前快捷键硬编码在
    /// <c>MainWindow.OnWindowKeyDown</c> 的一条 <c>if/else if</c> 长链里，
    /// 而设置页只能打印一份**写死的字符串列表**（还带着「自定义绑定 · 即将支持」的占位说明）——
    /// 两者随时可能不一致，且用户无法改键。</para>
    ///
    /// <para><b>持久化</b>：存在 <c>settings.json</c> 的 <c>keybindings</c> 键下，
    /// 值为 JSON 字符串（命令 id → 手势）。走 <see cref="AppSettings"/> 即自动经由
    /// <see cref="StoragePaths"/>，便携模式下不会漏。</para>
    ///
    /// <para><b>只存与默认值不同的项</b>：改回默认值等于删除该条目。
    /// 这样将来调整默认手势时，没动过该项的用户能自动获得新默认值。</para>
    ///
    /// <para><b>手势字符串格式</b>：<c>修饰键+修饰键+Key枚举名</c>，如 <c>Ctrl+Shift+D</c>、
    /// <c>F5</c>、<c>Ctrl+OemPlus</c>。用 <see cref="Key"/> 的枚举名而不是字符，
    /// 是为了与键盘布局/区域设置无关（<c>Key.OemQuestion</c> 在德语键盘上并不是 <c>/</c>）。</para>
    /// </summary>
    public static class ShortcutRegistry
    {
        /// <summary>一条可绑定命令。</summary>
        /// <param name="Id">稳定标识（持久化用，**改它等于让用户的绑定失效**）。</param>
        /// <param name="Category">设置页分组。</param>
        /// <param name="Label">设置页显示名。</param>
        /// <param name="DefaultGesture">出厂默认手势（见类注释的格式说明）。</param>
        public sealed record Command(string Id, string Category, string Label, string DefaultGesture);

        /// <summary>
        /// 全部可绑定命令。默认手势**必须与旧硬编码行为逐条一致**，
        /// 否则老用户升级后会感觉"快捷键坏了"。
        /// </summary>
        public static readonly Command[] All =
        {
            new("open",         "文件", "打开文件",           "Ctrl+O"),
            new("newWindow",    "文件", "新建窗口",           "Ctrl+N"),
            new("closeWindow",  "文件", "关闭窗口",           "Ctrl+W"),
            new("refresh",      "文件", "刷新当前文档",        "F5"),
            new("palette",      "视图", "命令面板",           "Ctrl+Shift+P"),
            new("split",        "视图", "双栏 / 取消分栏",     "Ctrl+T"),
            new("theme",        "视图", "切换明暗主题",        "Ctrl+Shift+D"),
            new("presentation", "视图", "全屏演示 / 专注阅读",  "F11"),
            new("find",         "视图", "页内查找",           "Ctrl+F"),
            new("zoomReset",    "视图", "缩放至 100%",        "Ctrl+D0"),
            new("zoomIn",       "视图", "放大",              "Ctrl+OemPlus"),
            new("zoomOut",      "视图", "缩小",              "Ctrl+OemMinus"),
            new("print",        "文档", "打印当前文档",        "Ctrl+P"),
        };

        private const string SettingsKey = "keybindings";

        /// <summary>
        /// 自定义绑定缓存。<b>必须有</b>：按键分派要对每条命令求一次当前手势
        /// （一次按键 13 次），若每次都重新解析 settings.json 的 JSON 会明显拖慢键盘响应。
        /// 只在 <see cref="SetGesture"/> / <see cref="ResetAll"/> 后失效。
        /// </summary>
        private static Dictionary<string, string>? _customCache;

        /// <summary>解析后的生效绑定缓存（命令 id → 手势），与 <see cref="_customCache"/> 同生命周期。</summary>
        private static Dictionary<string, string>? _resolvedCache;

        /// <summary>
        /// 显式「取消绑定」的存储值。
        ///
        /// <para><b>为什么不能直接删掉自定义项</b>：删除 = 回落到默认手势，
        /// 而用户按「清除绑定」的意图是"这条命令不要快捷键"（例如不想再让 Ctrl+P 触发打印）。
        /// 两者语义不同，必须用哨兵值区分。</para>
        /// </summary>
        public const string UnboundMarker = "none";

        public static Command? Find(string id) => All.FirstOrDefault(c => c.Id == id);

        public static string DefaultOf(string id) => Find(id)?.DefaultGesture ?? "";

        /// <summary>自定义项 + 默认值 → 该命令实际生效的手势（空串表示已被用户取消绑定）。</summary>
        private static string Effective(string id, Dictionary<string, string> custom)
        {
            if (!custom.TryGetValue(id, out var g) || string.IsNullOrWhiteSpace(g)) return DefaultOf(id);
            return g == UnboundMarker ? "" : g;
        }

        // ──────────────── 读写绑定 ────────────────

        /// <summary>取命令当前生效的手势（自定义优先，否则默认；空串 = 已取消绑定）。</summary>
        public static string GetGesture(string id) => Effective(id, LoadCustom());

        /// <summary>
        /// 全部命令的生效手势（命令 id → 手势）。供按键分派一次性取用，避免逐条调用
        /// <see cref="GetGesture"/> 时的字典查找与默认值拼装开销。
        /// </summary>
        public static IReadOnlyDictionary<string, string> Resolved()
        {
            if (_resolvedCache != null) return _resolvedCache;
            var custom = LoadCustom();
            var map = new Dictionary<string, string>(All.Length, StringComparer.OrdinalIgnoreCase);
            foreach (var c in All) map[c.Id] = Effective(c.Id, custom);
            _resolvedCache = map;
            return map;
        }

        /// <summary>
        /// 设置命令手势。
        /// <list type="bullet">
        /// <item><description><c>null</c> / 空 → <b>恢复默认</b>（删除自定义项）；</description></item>
        /// <item><description><see cref="UnboundMarker"/> → <b>取消绑定</b>（显式记录，不回落到默认）；</description></item>
        /// <item><description>等于默认值 → 同样删除自定义项，让将来调整默认手势能传递到本用户。</description></item>
        /// </list>
        /// </summary>
        public static void SetGesture(string id, string? gesture)
        {
            var custom = LoadCustom();
            if (string.IsNullOrWhiteSpace(gesture) || gesture == DefaultOf(id)) custom.Remove(id);
            else custom[id] = gesture!;
            SaveCustom(custom);
        }

        /// <summary>清空全部自定义绑定（恢复出厂快捷键）。</summary>
        public static void ResetAll()
            => SaveCustom(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        /// <summary>当前是否存在任何自定义绑定（设置页据此显示"恢复默认"是否可点）。</summary>
        public static bool HasCustomBindings() => LoadCustom().Count > 0;

        /// <summary>
        /// 与指定手势冲突的其他命令（同一手势绑到多条命令时，命令表靠前者生效 —— 后者静默失效，
        /// 这正是需要提醒用户的情形）。
        /// </summary>
        public static IReadOnlyList<Command> Conflicts(string gesture, string? exceptId)
        {
            if (string.IsNullOrWhiteSpace(gesture)) return Array.Empty<Command>();
            var resolved = Resolved();
            return All
                .Where(c => !string.Equals(c.Id, exceptId, StringComparison.OrdinalIgnoreCase))
                .Where(c => resolved.TryGetValue(c.Id, out var g)
                            && string.Equals(g, gesture, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        private static Dictionary<string, string> LoadCustom()
        {
            if (_customCache != null) return _customCache;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var raw = AppSettings.Get(SettingsKey, "");
                if (string.IsNullOrWhiteSpace(raw)) { _customCache = map; return map; }
                if (JsonNode.Parse(raw) is not JsonObject obj) { _customCache = map; return map; }
                foreach (var kv in obj)
                {
                    var v = kv.Value?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(v)) map[kv.Key] = v!;
                }
            }
            catch (Exception ex)
            {
                // 绑定读不出 → 全部回落默认值，快捷键仍然可用。留档。
                SeeMeLog.Error("ShortcutRegistry.Load", ex);
            }
            _customCache = map;
            return map;
        }

        private static void SaveCustom(Dictionary<string, string> map)
        {
            try
            {
                var obj = new JsonObject();
                foreach (var kv in map) obj[kv.Key] = kv.Value;
                AppSettings.Set(SettingsKey, obj.ToJsonString());
            }
            catch (Exception ex) { SeeMeLog.Error("ShortcutRegistry.Save", ex); }
            // 无论写盘成功与否都失效缓存：内存态必须与"用户刚做的选择"一致
            _customCache = map;
            _resolvedCache = null;
        }

        // ──────────────── 手势解析 / 格式化 ────────────────

        /// <summary>
        /// 把 WPF 的按键事件归一化成手势字符串。
        ///
        /// <para><b>小键盘加减归一</b>：<c>Key.Add</c>/<c>Key.Subtract</c>（小键盘）
        /// 与 <c>Key.OemPlus</c>/<c>Key.OemMinus</c>（主键盘 <c>=</c>/<c>-</c>）归一为同一手势 ——
        /// 旧硬编码里放大/缩小本就同时接受这两者，不归一会让「Ctrl 和 + 不能放大」成为回归。</para>
        /// </summary>
        public static string? Normalize(ModifierKeys mods, Key key)
        {
            if (key == Key.None) return null;
            key = key switch
            {
                Key.Add => Key.OemPlus,
                Key.Subtract => Key.OemMinus,
                _ => key,
            };
            return Format(mods, key);
        }

        /// <summary>修饰键 + 键 → 手势字符串（存储格式）。</summary>
        public static string Format(ModifierKeys mods, Key key)
        {
            var parts = new List<string>(4);
            if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
            parts.Add(key.ToString());
            return string.Join("+", parts);
        }

        /// <summary>解析手势字符串。空/非法返回 <c>false</c>。</summary>
        public static bool TryParse(string? gesture, out ModifierKeys mods, out Key key)
        {
            mods = ModifierKeys.None;
            key = Key.None;
            if (string.IsNullOrWhiteSpace(gesture)) return false;

            // 存储格式里键名永远是 Key 枚举名（不含 '+' 字符），故按 '+' 切分是安全的。
            var parts = gesture!.Split('+', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return false;

            for (var i = 0; i < parts.Length - 1; i++)
            {
                switch (parts[i].Trim().ToLowerInvariant())
                {
                    case "ctrl" or "control": mods |= ModifierKeys.Control; break;
                    case "shift": mods |= ModifierKeys.Shift; break;
                    case "alt": mods |= ModifierKeys.Alt; break;
                    case "win" or "windows": mods |= ModifierKeys.Windows; break;
                    default: return false;   // 有未知修饰键 → 整条判为非法
                }
            }
            return Enum.TryParse(parts[^1].Trim(), ignoreCase: true, out key) && key != Key.None;
        }

        /// <summary>手势 → 给人看的形式（<c>Ctrl+OemPlus</c> → <c>Ctrl + +</c>）。</summary>
        public static string ToDisplay(string? gesture)
        {
            if (!TryParse(gesture, out var mods, out var key)) return gesture ?? "";
            var parts = new List<string>(5);
            if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
            parts.Add(FriendlyKey(key));
            return string.Join(" + ", parts);
        }

        private static string FriendlyKey(Key k) => k switch
        {
            Key.OemPlus => "+",
            Key.OemMinus => "-",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemQuestion => "/",
            Key.OemSemicolon => ";",
            Key.OemQuotes => "'",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.OemPipe => "\\",
            Key.OemTilde => "`",
            Key.Add => "小键盘 +",
            Key.Subtract => "小键盘 -",
            Key.Multiply => "小键盘 *",
            Key.Divide => "小键盘 /",
            Key.Decimal => "小键盘 .",
            >= Key.D0 and <= Key.D9 => ((char)('0' + (k - Key.D0))).ToString(),
            >= Key.NumPad0 and <= Key.NumPad9 => "小键盘 " + (char)('0' + (k - Key.NumPad0)),
            _ => k.ToString(),
        };
    }
}
