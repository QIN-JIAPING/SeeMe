// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SeeMe
{
    /// <summary>
    /// 应用设置持久化（%LOCALAPPDATA%\SeeMe\settings.json，与 ThemeManager 共用同一文件）。
    /// 采用「读现有 → 改指定键 → 整文件写回」的合并写，与 ThemeManager 的写操作互不覆盖。
    /// 静态内存缓存 + Set 即写盘，使用处直接读 Get。
    /// </summary>
    public static class AppSettings
    {
        private static readonly string StoragePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SeeMe", "settings.json");

        // ── 键名 ──
        public const string ThemeModeKey        = "themeMode";        // light / dark / system
        public const string StartupOpenKey      = "startupOpen";      // none / last
        public const string StartupSplitKey     = "startupSplit";     // bool
        public const string HistorySizeKey      = "historySize";      // 10 / 15 / 20 / 50
        public const string DefaultZoomKey      = "defaultZoom";      // 1.0 / 1.25 / 1.5
        public const string InfoPanelVisibleKey = "infoPanelVisible"; // bool
        public const string CustomCssKey        = "customCssPath";    // string
        public const string ImageFormatKey      = "imageFormat";      // png / jpg
        public const string ImageScaleKey       = "imageScale";       // 1 / 2
        public const string ExportPathModeKey   = "exportPathMode";   // ask / fixed
        public const string ExportPathKey       = "exportPath";       // string
        public const string AccentKey           = "accent";           // indigo / blue / green
        public const string MdStyleKey          = "mdStyle";          // default / github / simple
        public const string EyeCareKey          = "eyeCare";          // bool
        public const string DevToolsKey         = "devTools";         // bool（F12 开发者工具）
        public const string FontSizeKey          = "fontSize";         // 13 / 14 / 16 / 18
        public const string LineHeightKey        = "lineHeight";       // 1.4 / 1.65 / 2.0
        public const string AnimationsKey        = "animations";       // bool（主题过渡动画）
        public const string CloseBehaviorKey     = "closeBehavior";    // exit / tray / ask
        public const string AutoSaveKey           = "autoSave";         // bool（编辑模式自动保存）
        public const string AutoSaveDelayKey      = "autoSaveDelay";    // int 秒（5 / 10 / 30）
        public const string RemoteImageAllowKey   = "remoteImageAllow"; // string[]（允许加载远程图片的文档路径）
        public const string AnnModeKey            = "annMode";          // bool（高亮笔开关，重启/退出后保持）

        private static readonly Dictionary<string, JsonNode?> Cache = LoadAll();

        private static Dictionary<string, JsonNode?> LoadAll()
        {
            var map = new Dictionary<string, JsonNode?>();
            try
            {
                if (File.Exists(StoragePath))
                {
                    var root = JsonNode.Parse(File.ReadAllText(StoragePath)) as JsonObject;
                    if (root != null)
                        foreach (var kv in root)
                            map[kv.Key] = kv.Value?.DeepClone();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[SeeMe] AppSettings.LoadAll: " + ex.Message);
            }
            return map;
        }

        /// <summary>读字符串设置，缺失/类型不符返回 fallback。</summary>
        public static string Get(string key, string fallback)
        {
            return Cache.TryGetValue(key, out var v) && v != null && v.GetValueKind() == JsonValueKind.String
                ? v.GetValue<string>() ?? fallback : fallback;
        }

        public static bool Get(string key, bool fallback)
        {
            if (Cache.TryGetValue(key, out var v) && v != null)
            {
                if (v.GetValueKind() == JsonValueKind.True) return true;
                if (v.GetValueKind() == JsonValueKind.False) return false;
            }
            return fallback;
        }

        public static int Get(string key, int fallback)
        {
            return Cache.TryGetValue(key, out var v) && v != null && v.GetValueKind() == JsonValueKind.Number
                ? v.GetValue<int>() : fallback;
        }

        public static double Get(string key, double fallback)
        {
            return Cache.TryGetValue(key, out var v) && v != null && v.GetValueKind() == JsonValueKind.Number
                ? v.GetValue<double>() : fallback;
        }

        /// <summary>写设置：更新内存缓存并合并写盘。失败仅记录，不影响运行。</summary>
        public static void Set(string key, JsonNode? value)
        {
            Cache[key] = value?.DeepClone();
            try
            {
                var dir = Path.GetDirectoryName(StoragePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                JsonObject obj = new();
                if (File.Exists(StoragePath))
                {
                    try
                    {
                        obj = JsonNode.Parse(File.ReadAllText(StoragePath)) as JsonObject ?? new();
                    }
                    catch { /* 损坏则重建 */ }
                }
                obj[key] = value;
                File.WriteAllText(StoragePath, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[SeeMe] AppSettings.Set: " + ex.Message);
            }
        }

        /// <summary>读取当前系统明暗主题（HKCU 个性化注册表）。失败返回亮色。</summary>
        public static string ResolveSystemTheme()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                var v = key?.GetValue("AppsUseLightTheme");
                if (v is int i) return i == 0 ? "dark" : "light";
            }
            catch { }
            return "light";
        }

        /// <summary>该文档是否已被用户授权加载远程图片（路径大小写不敏感）。</summary>
        public static bool IsRemoteImageAllowed(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                if (Cache.TryGetValue(RemoteImageAllowKey, out var v) && v is JsonArray arr)
                {
                    foreach (var item in arr)
                    {
                        if (item != null && item.GetValueKind() == JsonValueKind.String
                            && string.Equals(item.GetValue<string>(), path, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>记录/撤销该文档的远程图片授权（合并写盘，不覆盖其他设置）。</summary>
        public static void SetRemoteImageAllowed(string path, bool allow)
        {
            if (string.IsNullOrEmpty(path)) return;
            var arr = new JsonArray();
            try
            {
                if (Cache.TryGetValue(RemoteImageAllowKey, out var v) && v is JsonArray old)
                {
                    foreach (var item in old)
                    {
                        if (item == null || item.GetValueKind() != JsonValueKind.String) continue;
                        var s = item.GetValue<string>();
                        if (string.Equals(s, path, StringComparison.OrdinalIgnoreCase)) continue; // 去掉旧条目
                        arr.Add(s);
                    }
                }
                if (allow) arr.Add(path);
            }
            catch { arr = new JsonArray(); if (allow) arr.Add(path); }
            Set(RemoteImageAllowKey, arr);
        }
    }
}
