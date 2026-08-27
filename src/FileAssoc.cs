// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

using System;
using Microsoft.Win32;

namespace SeeMe
{
    /// <summary>
    /// 文件关联（HKCU 级，无需管理员）：
    /// 1) 把 .md 的默认打开方式设为 SeeMe（ProgId = SeeMe.Markdown）；
    /// 2) 注册资源管理器右键菜单「用 SeeMe 打开」（对所有文件生效，覆盖 SeeMe 支持的全部格式）。
    /// 全部写入 HKCU\Software\Classes，取消时按条件删除，不误伤用户后续修改的关联。
    /// </summary>
    public static class FileAssoc
    {
        private const string ProgId = "SeeMe.Markdown";
        private const string ContextMenuKey = @"Software\Classes\*\shell\SeeMe";
        private const string MdExtKey = @"Software\Classes\.md";
        private const string ProgIdOpenKey = @"Software\Classes\SeeMe.Markdown\shell\open\command";
        private const string ProgIdIconKey = @"Software\Classes\SeeMe.Markdown\DefaultIcon";

        private static string? Exe()
        {
            try
            {
                var p = Environment.ProcessPath;
                return string.IsNullOrEmpty(p) ? null : p;
            }
            catch { return null; }
        }

        /// <summary>.md 的当前用户级 ProgId 是否为 SeeMe。</summary>
        public static bool IsMdDefault()
        {
            try
            {
                using var md = Registry.CurrentUser.OpenSubKey(MdExtKey);
                return (md?.GetValue("") as string) == ProgId;
            }
            catch { return false; }
        }

        /// <summary>右键菜单是否已注册。</summary>
        public static bool IsContextMenuRegistered()
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(ContextMenuKey);
                return k != null;
            }
            catch { return false; }
        }

        /// <summary>注册 ProgId（open command + 图标）并把 .md 默认打开方式指到 SeeMe。返回错误信息，成功返回 null。</summary>
        public static string? RegisterMdDefault()
        {
            var exe = Exe();
            if (exe == null) return "无法定位程序路径";
            try
            {
                using (var cmd = Registry.CurrentUser.CreateSubKey(ProgIdOpenKey))
                    cmd?.SetValue("", $"\"{exe}\" \"%1\"");
                using (var icon = Registry.CurrentUser.CreateSubKey(ProgIdIconKey))
                    icon?.SetValue("", $"\"{exe}\",0");
                using (var md = Registry.CurrentUser.CreateSubKey(MdExtKey))
                    md?.SetValue("", ProgId);
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        /// <summary>注册资源管理器右键菜单「用 SeeMe 打开」（HKCU\*\shell\SeeMe）。返回错误信息，成功返回 null。</summary>
        public static string? RegisterContextMenu()
        {
            var exe = Exe();
            if (exe == null) return "无法定位程序路径";
            try
            {
                using (var shell = Registry.CurrentUser.CreateSubKey(ContextMenuKey))
                {
                    shell?.SetValue("", "用 SeeMe 打开");
                    shell?.SetValue("Icon", $"\"{exe}\",0");
                }
                using (var cmd = Registry.CurrentUser.CreateSubKey(ContextMenuKey + @"\command"))
                    cmd?.SetValue("", $"\"{exe}\" \"%1\"");
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        /// <summary>取消全部关联：删除 ProgId 与右键菜单；.md 仅在仍指向 SeeMe 时删除（不误伤用户后改的关联）。</summary>
        public static void UnregisterAll()
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\SeeMe.Markdown", false); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(ContextMenuKey, false); } catch { }
            try
            {
                using var md = Registry.CurrentUser.OpenSubKey(MdExtKey);
                if ((md?.GetValue("") as string) == ProgId)
                    Registry.CurrentUser.DeleteSubKeyTree(MdExtKey, false);
            }
            catch { }
        }
    }
}
