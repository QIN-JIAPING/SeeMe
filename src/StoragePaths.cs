// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

using System;
using System.IO;

namespace SeeMe
{
    /// <summary>
    /// 数据目录的**单一事实来源**：所有持久化路径（设置 / 历史 / 标注 / 笔记 / 书签 /
    /// 阅读进度 / 日志 / 缓存 / 主题）都必须经由此处解析，不得各自拼 <c>%LOCALAPPDATA%</c>。
    ///
    /// <para><b>为什么必须收敛</b>：便携模式要求"数据跟着程序走"。收敛之前全项目有
    /// <b>14 处</b>各自拼 <c>Environment.GetFolderPath(LocalApplicationData)</c> 的代码 ——
    /// 只要漏掉一处，那一项数据在便携模式下就会仍写进用户 AppData，
    /// 用户把 U 盘插到另一台机器上会发现"设置变了、标注没了"，且极难排查。
    /// 集中到本类后，便携模式的开关只需在这一个地方生效。</para>
    ///
    /// <para><b>便携模式的判定</b>：程序目录下存在 <c>portable.flag</c> 文件即启用，
    /// 数据落在 <c>&lt;程序目录&gt;\data\</c>。用"存在标记文件"而不是"是否可写"来判断，
    /// 是因为可写性探测在 Program Files 下会因 UAC 虚化而给出误导性结果
    /// （见 <see cref="ProbePortableWritable"/>）。</para>
    ///
    /// <para><b>⚠️ 不做自动迁移</b>：开启便携模式后，原先在 AppData 里的数据**不会**自动搬过来。
    /// 这是刻意的 —— 自动迁移需要复制大量文件且可能半途失败留下不一致状态。
    /// 设置界面必须明确提示用户，并提供手动迁移入口（见 SettingsDialog 的便携模式区块）。</para>
    /// </summary>
    public static class StoragePaths
    {
        /// <summary>便携模式标记文件名（放在程序目录，非数据目录）。</summary>
        public const string PortableFlagName = "portable.flag";

        /// <summary>便携模式下的数据子目录名。</summary>
        public const string PortableDataDirName = "data";

        /// <summary>非便携模式下的 AppData 目录名。</summary>
        public const string AppDataDirName = "SeeMe";

        private static string? _overrideRoot;

        /// <summary>程序所在目录（便携标记与 data 目录的基准）。</summary>
        public static string AppDirectory => AppDomain.CurrentDomain.BaseDirectory;

        /// <summary>便携模式下是否启用（程序目录存在 portable.flag）。</summary>
        public static bool IsPortable => File.Exists(Path.Combine(AppDirectory, PortableFlagName));

        /// <summary>
        /// 数据根目录：便携模式 → <c>&lt;程序目录&gt;\data</c>；否则 → <c>%LOCALAPPDATA%\SeeMe</c>。
        ///
        /// <para>测试可通过 <see cref="OverrideRootForTest"/> 覆盖。覆盖优先于一切判定，
        /// 保证测试永不触碰真实用户目录。</para>
        /// </summary>
        public static string Root
        {
            get
            {
                if (!string.IsNullOrEmpty(_overrideRoot)) return _overrideRoot!;
                return IsPortable
                    ? Path.Combine(AppDirectory, PortableDataDirName)
                    : Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        AppDataDirName);
            }
        }

        /// <summary>日志目录（<c>Root\logs</c>）。</summary>
        public static string LogsDir => Path.Combine(Root, "logs");

        /// <summary>缓存目录（<c>Root\cache</c>，PDF 文本层等）。</summary>
        public static string CacheDir => Path.Combine(Root, "cache");

        /// <summary>把数据根的相对名解析为绝对路径（如 <c>settings.json</c> → <c>Root\settings.json</c>）。</summary>
        public static string Combine(string relative) => Path.Combine(Root, relative);

        /// <summary>确保数据根存在；失败返回 false（调用方自行决定是否降级）。</summary>
        public static bool EnsureRoot()
        {
            try
            {
                Directory.CreateDirectory(Root);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 探测便携模式的数据目录是否真的可写。
        ///
        /// <para><b>为什么需要显式探测</b>：程序装在 <c>C:\Program Files\</c> 时，
        /// <c>portable.flag</c> 可能存在，但写 <c>data\</c> 会失败 —— 而 Windows 的
        /// UAC 文件虚化（VirtualStore）会**让写入看起来成功了**，实际数据被重定向到
        /// <c>%LOCALAPPDATA%\VirtualStore\...</c>，用户以为便携却没便携。
        /// 所以这里用"真实写一个文件再删掉"来验证，而不是只查目录权限位。</para>
        /// </summary>
        public static bool ProbePortableWritable()
        {
            var probe = Path.Combine(Root, ".write-probe");
            try
            {
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 切换便携模式：创建/删除 <c>portable.flag</c>。
        /// 返回是否操作成功；失败原因由调用方结合 <see cref="ProbePortableWritable"/> 提示。
        ///
        /// <para><b>注意</b>：本方法只动标记文件，**不迁移数据**。切换后已加载的陈旧路径
        /// 需重启应用才会全部生效（<see cref="Root"/> 是每次求值的，但已构造好的存储实例
        /// 持有的是构造时的路径）。</para>
        /// </summary>
        public static bool SetPortable(bool enabled)
        {
            var flag = Path.Combine(AppDirectory, PortableFlagName);
            try
            {
                if (enabled)
                {
                    File.WriteAllText(flag, "SeeMe portable mode");
                    return true;
                }
                if (File.Exists(flag)) File.Delete(flag);
                return true;
            }
            catch { return false; }
        }

        /// <summary>测试专用：覆盖数据根（传 null 恢复真实解析）。</summary>
        public static void OverrideRootForTest(string? root) => _overrideRoot = root;

        /// <summary>
        /// 把 AppData 里的既有数据复制到便携 data 目录（**只复制，不删除源**）。
        /// 返回成功复制的文件数；目标已存在同名文件时跳过，避免覆盖用户在便携目录里的改动。
        /// </summary>
        public static int CopyAppDataToPortable()
        {
            var src = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppDataDirName);
            var dst = Path.Combine(AppDirectory, PortableDataDirName);
            if (!Directory.Exists(src)) return 0;

            var copied = 0;
            try
            {
                Directory.CreateDirectory(dst);
                foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(src, f);
                    var target = Path.Combine(dst, rel);
                    var dir = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    if (File.Exists(target)) continue;
                    File.Copy(f, target);
                    copied++;
                }
            }
            catch (Exception ex) { SeeMeLog.Error("Portable migration", ex); }
            return copied;
        }
    }
}
