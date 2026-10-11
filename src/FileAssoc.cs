// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;

namespace SeeMe
{
    /// <summary>
    /// 文件关联（HKCU 级，无需管理员）：
    /// <list type="number">
    /// <item><description>把 SeeMe 支持的格式<b>按族分组</b>设为默认打开方式（每族一个 ProgId）；</description></item>
    /// <item><description>注册资源管理器右键菜单「用 SeeMe 打开」（对所有文件生效）；</description></item>
    /// <item><description>在各扩展名的「打开方式」列表登记（<c>OpenWithProgids</c>）。</description></item>
    /// </list>
    ///
    /// <para><b>历史与为什么改成分组</b>：本类原先只支持一个 ProgId（<c>SeeMe.Markdown</c>）与一个
    /// 扩展名 <c>.md</c>，而应用实际支持 28 个扩展名 —— 用户把 SeeMe 设为 Markdown 默认查看器后，
    /// 双击 PDF / docx 仍然打开别的程序，看不出"已关联"与"没关联"的区别。
    /// 现在每族独立勾选：既能让用户一次性接管全部格式，也能只接管 Markdown。</para>
    ///
    /// <para><b>🔴 两种删除必须区分</b>：取消关联时**只清除仍指向 SeeMe 的默认值**，
    /// 绝不 <c>DeleteSubKeyTree</c> 整个扩展名键 —— 那会连带删掉 Windows 与用户后来写入的
    /// <c>OpenWithProgids</c> / <c>OpenWithList</c> 等数据。
    /// 旧实现在 <c>.md</c> 上正是整键删除（见 <see cref="ClearDefault"/> 注释）。</para>
    /// </summary>
    public static class FileAssoc
    {
        /// <summary>
        /// 一个关联分组：一个 ProgId 覆盖一组同族扩展名。
        /// </summary>
        /// <param name="ProgId">写入注册表的程序标识（HKCU\Software\Classes\&lt;ProgId&gt;）。</param>
        /// <param name="Label">设置页显示名。</param>
        /// <param name="Exts">该组覆盖的扩展名（含点，小写）。</param>
        public sealed record Group(string ProgId, string Label, string[] Exts);

        /// <summary>
        /// 关联分组表。**分组粒度与 <see cref="FileTypes"/> 的族划分保持一致** ——
        /// 新增受支持格式时两处都要看。
        ///
        /// <para><b>图片单列</b>：把 SeeMe 设为图片默认查看器对用户既有习惯的侵入最大
        /// （系统照片应用通常已接管），故它单独成组、由用户显式勾选，不做任何默认勾选。</para>
        /// </summary>
        public static readonly Group[] Groups =
        {
            new("SeeMe.Markdown",   "Markdown",     new[] { ".md", ".markdown", ".mkd", ".mdown" }),
            new("SeeMe.Pdf",        "PDF",          new[] { ".pdf" }),
            new("SeeMe.Word",       "Word 文档",     new[] { ".docx", ".doc", ".docm", ".rtf", ".odt" }),
            new("SeeMe.Excel",      "Excel 表格",    new[] { ".xlsx", ".xls", ".xlsm", ".ods", ".csv" }),
            new("SeeMe.PowerPoint", "PowerPoint",   new[] { ".pptx", ".ppt", ".odp" }),
            new("SeeMe.Ebook",      "电子书",        new[] { ".epub" }),
            new("SeeMe.Image",      "图片",          new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico", ".tif", ".tiff" }),
        };

        private const string ClassesRoot = @"Software\Classes";
        private const string ContextMenuKey = ClassesRoot + @"\*\shell\SeeMe";

        private static string? Exe()
        {
            try
            {
                var p = Environment.ProcessPath;
                return string.IsNullOrEmpty(p) ? null : p;
            }
            catch { return null; }
        }

        private static string ProgIdKey(Group g) => ClassesRoot + "\\" + g.ProgId;

        private static string ExtKey(string ext) => ClassesRoot + "\\" + ext;

        // ──────────────── 状态查询 ────────────────

        /// <summary>
        /// 该分组是否已整体设为 SeeMe：组内<b>每个</b>扩展名都指向本组 ProgId 才算数
        /// （半途失败/用户手工改过其中一个扩展名时，如实显示为"未设置"，不谎报已关联）。
        /// </summary>
        public static bool IsDefault(Group g)
        {
            try
            {
                foreach (var ext in g.Exts)
                {
                    using var k = Registry.CurrentUser.OpenSubKey(ExtKey(ext));
                    if ((k?.GetValue("") as string) != g.ProgId) return false;
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>已设为默认的分组名（设置页状态行显示用）。</summary>
        public static IReadOnlyList<string> DefaultGroupLabels()
            => Groups.Where(IsDefault).Select(g => g.Label).ToList();

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

        // ──────────────── 注册 / 取消 ────────────────

        /// <summary>
        /// 注册分组的 ProgId（open command + 图标）并在组内每个扩展名的
        /// 「打开方式」列表登记。返回错误信息，成功返回 null。
        /// </summary>
        public static string? RegisterGroup(Group g)
        {
            var exe = Exe();
            if (exe == null) return "无法定位程序路径";
            try
            {
                using (var cmd = Registry.CurrentUser.CreateSubKey(ProgIdKey(g) + @"\shell\open\command"))
                    cmd?.SetValue("", $"\"{exe}\" \"%1\"");
                using (var icon = Registry.CurrentUser.CreateSubKey(ProgIdKey(g) + @"\DefaultIcon"))
                    icon?.SetValue("", $"\"{exe}\",0");

                foreach (var ext in g.Exts)
                {
                    // OpenWithProgids 的值惯例是 REG_NONE 空数据（仅靠"值名 = ProgId"表达登记）。
                    try
                    {
                        using var owp = Registry.CurrentUser.CreateSubKey(ExtKey(ext) + @"\OpenWithProgids");
                        owp?.SetValue(g.ProgId, Array.Empty<byte>(), RegistryValueKind.None);
                    }
                    catch { /* 登记失败不影响默认关联本身 */ }
                }
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        /// <summary>把分组的默认打开方式设为 SeeMe。返回错误信息，成功返回 null。</summary>
        public static string? SetDefault(Group g)
        {
            var err = RegisterGroup(g);
            if (err != null) return err;
            try
            {
                foreach (var ext in g.Exts)
                {
                    using var k = Registry.CurrentUser.CreateSubKey(ExtKey(ext));
                    k?.SetValue("", g.ProgId);
                }
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        /// <summary>
        /// 取消分组默认关联。
        ///
        /// <para><b>🔴 只删值、不删键</b>：<c>HKCU\Software\Classes\.md</c> 下除了默认值
        /// 可能还有 <c>OpenWithProgids</c> / <c>PersistentHandler</c> 等 Windows 与用户写入的数据，
        /// 整键删除（旧实现）会一并抹掉。这里只在该默认值**仍等于本组 ProgId** 时删除它，
        /// 等价于把"默认程序"恢复成未指定，其余数据原样保留。</para>
        /// </summary>
        public static void ClearDefault(Group g)
        {
            foreach (var ext in g.Exts)
            {
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(ExtKey(ext), writable: true))
                    {
                        if (k?.GetValue("") as string == g.ProgId)
                            k.DeleteValue("", throwOnMissingValue: false);
                    }
                    // 只摘掉自己那一项，同键下其他程序的登记保留
                    using (var owp = Registry.CurrentUser.OpenSubKey(ExtKey(ext) + @"\OpenWithProgids", writable: true))
                        owp?.DeleteValue(g.ProgId, throwOnMissingValue: false);
                }
                catch { }
            }
        }

        /// <summary>
        /// 按勾选结果批量应用：勾选的分组设为默认，未勾选的（若当前指向 SeeMe）清除。
        /// <b>未勾选且当前不属于 SeeMe 的格式一律不碰</b>，不会抢占用户已有的默认程序。
        /// </summary>
        public static string? ApplySelection(IEnumerable<Group> selected)
        {
            var set = new HashSet<string>(
                selected.Select(g => g.ProgId), StringComparer.OrdinalIgnoreCase);

            foreach (var g in Groups)
            {
                if (set.Contains(g.ProgId))
                {
                    var err = SetDefault(g);
                    if (err != null) return g.Label + "：" + err;
                }
                else
                {
                    ClearDefault(g);
                }
            }
            return null;
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

        /// <summary>
        /// 取消全部关联：删除全部 ProgId 与右键菜单，「打开方式」登记与默认值按
        /// <see cref="ClearDefault"/> 的克制口径逐项清除。
        /// </summary>
        public static void UnregisterAll()
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(ContextMenuKey, false); } catch { }
            foreach (var g in Groups)
            {
                try { Registry.CurrentUser.DeleteSubKeyTree(ProgIdKey(g), false); } catch { }
                ClearDefault(g);
            }
        }
    }
}
