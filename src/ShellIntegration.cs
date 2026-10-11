// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Shell;

namespace SeeMe
{
    /// <summary>
    /// Windows 外壳集成：任务栏<b>跳转列表</b>（最近文件）与任务栏<b>进度条</b>。
    ///
    /// <para><b>设计原则：全部"锦上添花"，任何失败都不影响主功能。</b>
    /// 跳转列表会因组策略、从非标准路径启动（如映射网络盘）、
    /// 或 shell 扩展被禁而抛异常；任务栏进度在窗口尚未创建句柄时也可能失败。
    /// 因此这里每个入口都吞掉异常并只记 <c>Info</c>（可自愈的性能/装饰性回退，
    /// 符合 <see cref="SeeMeLog"/> 的级别判据）。</para>
    /// </summary>
    public static class ShellIntegration
    {
        /// <summary>跳转列表「最近」分组保留的条目上限（系统对分组条目数也有上限，过长的会被截断）。</summary>
        private const int MaxRecent = 10;

        /// <summary>上次成功应用的最近文件签名，用于跳过多余的 shell 调用。</summary>
        private static string? _lastSignature;

        /// <summary>
        /// 重建任务栏跳转列表：「任务」分组（新建窗口）+「最近」分组（历史文件）。
        ///
        /// <para><b>用自定义分组而不是 <c>JumpList.AddToRecentCategory</c></b>：后者依赖
        /// shell 自己维护的最近使用记录，其内容不受应用控制（用户从别处打开同名文件也会进去）。
        /// 自定义分组直接按 SeeMe 自己的历史列表渲染，与左栏「最近文件」一致。</para>
        ///
        /// <para><b>已不存在的文件不进列表</b>：跳转列表点进去报"找不到文件"比不显示更糟。</para>
        ///
        /// <para><b>重复调用廉价</b>：内容未变时直接返回，不做 shell 往返
        /// （本方法挂在历史变更回调上，而历史变更可能很频繁）。</para>
        /// </summary>
        public static void ApplyJumpList(IEnumerable<string> recentFiles)
        {
            try
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return;

                var files = new List<string>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in recentFiles)
                {
                    if (files.Count >= MaxRecent) break;
                    if (string.IsNullOrEmpty(f) || !File.Exists(f)) continue;
                    if (seen.Add(f)) files.Add(f!);
                }

                var signature = exe + "\u0001" + string.Join("\u0001", files);
                if (signature == _lastSignature) return;

                var list = new JumpList
                {
                    // 自带「最近」由下面的自定义分组承担，避免出现两个语义重复的分组
                    ShowRecentCategory = false,
                    ShowFrequentCategory = false,
                };

                list.JumpItems.Add(new JumpTask
                {
                    Title = "新建窗口",
                    Description = "打开一个新的 SeeMe 窗口",
                    ApplicationPath = exe,
                    Arguments = "",
                    IconResourcePath = exe,
                    CustomCategory = "任务",
                });

                foreach (var f in files)
                {
                    // 参数必须经校验后才拼装：这里生成的是**命令行**，
                    // 路径里的引号会改变参数结构（详见 BuildJumpArguments 注释）。
                    var args = BuildJumpArguments(f);
                    if (args == null)
                    {
                        SeeMeLog.Warn("ShellIntegration.JumpList", "路径含无法安全表达为命令行的字符，已跳过该条目");
                        continue;
                    }
                    list.JumpItems.Add(new JumpTask
                    {
                        Title = ShortenTitle(Path.GetFileName(f)),
                        Description = f,
                        ApplicationPath = exe,
                        Arguments = args,
                        IconResourcePath = exe,
                        CustomCategory = "最近",
                    });
                }

                JumpList.SetJumpList(Application.Current, list);
                list.Apply();
                _lastSignature = signature;
            }
            catch (Exception ex)
            {
                SeeMeLog.Info("ShellIntegration.ApplyJumpList", ex.Message);
            }
        }

        // ──────────────── 跳转列表参数构造（安全审查 2026-10-11 补） ────────────────

        /// <summary>任务标题的显示长度上限（超出由任务栏自行截断，先裁掉更整齐）。</summary>
        private const int MaxTitleChars = 120;

        /// <summary>路径长度上限（Windows 长路径上限 32767，4096 已远超真实需求）。</summary>
        private const int MaxPathChars = 4096;

        /// <summary>
        /// 把文件路径转成 <see cref="JumpTask.Arguments"/> 的命令行参数。
        /// 无法**安全**表达时返回 <c>null</c>，调用方跳过该条目。
        ///
        /// <para><b>为什么是"校验+跳过"而不是"转义"</b>：Windows 命令行转义规则
        /// （反斜杠紧邻引号的特殊处理、CommandLineToArgvW 的隐式规则）复杂且易错，
        /// 而这里根本没有"必须支持怪异路径"的需求 —— 跳转列表少一条无害，
        /// 拼错了却可能让 SeeMe 以意料之外的参数被拉起。用不可靠的转义去支持
        /// 一个不存在的需求，是典型的负收益。</para>
        ///
        /// <para><b>两个具体风险</b>：</para>
        /// <list type="bullet">
        /// <item><description>路径含 <c>"</c> → 提前闭合引号，后续内容被当成新参数。
        /// Windows 文件名本不允许 <c>"</c>，但历史记录是**可写文件**，
        /// 且 NTFS 上确实能用 <c>\\?\</c> 前缀造出含 <c>"</c> 的名字。</description></item>
        /// <item><description>路径以 <c>\</c> 结尾 → 拼出的 <c>\"</c> 会被命令行解析成**转义引号**，
        /// 参数结构整体错位。（<c>File.Exists</c> 为真时不可能是目录，实际不可达，
        /// 但这是"字符串拼引号"的固有缺陷，顺手一并挡掉。）</description></item>
        /// </list>
        /// </summary>
        public static string? BuildJumpArguments(string? path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (path!.Length > MaxPathChars) return null;
            if (path.IndexOf('"') >= 0) return null;
            if (path.EndsWith('\\')) return null;
            foreach (var ch in path)
            {
                if (char.IsControl(ch)) return null;
            }
            // 到这里路径不含引号 → 用引号包裹是安全的（空格/中文都能正确传参）
            return "\"" + path + "\"";
        }

        /// <summary>裁剪任务标题（任务栏对过长标题会自行截断，这里先裁避免出现半截乱码）。</summary>
        private static string ShortenTitle(string title)
            => string.IsNullOrEmpty(title) ? "SeeMe" 
             : title.Length <= MaxTitleChars ? title 
             : title.Substring(0, MaxTitleChars - 1) + "…";

        // ──────────────── 任务栏进度 ────────────────

        /// <summary>
        /// 在任务栏按钮上显示"不确定进度"（滚动条效果），用于 PDF / 图片导出这类
        /// 耗时可观、又无法给出百分比的操作 —— 窗口被遮挡时用户也能看到"还在忙"。
        /// </summary>
        public static void BeginBusy(Window? w, string? description = null)
        {
            try
            {
                if (w == null) return;
                w.TaskbarItemInfo ??= new TaskbarItemInfo();
                w.TaskbarItemInfo.ProgressState = TaskbarItemProgressState.Indeterminate;
                if (!string.IsNullOrEmpty(description)) w.TaskbarItemInfo.Description = description;
            }
            catch (Exception ex) { SeeMeLog.Info("ShellIntegration.BeginBusy", ex.Message); }
        }

        /// <summary>结束忙碌状态（必须在 <c>finally</c> 里调用，否则异常路径会留下永久滚动的任务栏图标）。</summary>
        public static void EndBusy(Window? w)
        {
            try
            {
                if (w?.TaskbarItemInfo == null) return;
                w.TaskbarItemInfo.ProgressState = TaskbarItemProgressState.None;
                w.TaskbarItemInfo.Description = "";
            }
            catch (Exception ex) { SeeMeLog.Info("ShellIntegration.EndBusy", ex.Message); }
        }
    }
}
