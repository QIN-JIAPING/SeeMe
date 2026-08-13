// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SeeMe
{
    /// <summary>
    /// PDF 文本层磁盘缓存（%LOCALAPPDATA%\SeeMe\pdftext\）。
    /// 打开 PDF 时由 anydoc-wasm 提取文本并落盘，供「内容搜索（>关键词）」直接读取——
    /// 搜索不依赖文件当前是否打开。每个文件按完整路径 SHA256 取键：
    ///   {hash}.txt  → 已提取文本层（内容）
    ///   {hash}.no   → 扫描版 PDF（无文本层，空标记）
    /// 写失败仅记录，不影响 PDF.js 渲染。
    /// </summary>
    public static class PdfTextCache
    {
        private static readonly string CacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SeeMe", "pdftext");

        /// <summary>缓存最长保留天数，超过则启动时清理（防止磁盘无限增长）。</summary>
        private static readonly TimeSpan MaxCacheAge = TimeSpan.FromDays(30);

        private static string BasePath(string path)
        {
            var full = Path.GetFullPath(path);
            // 键包含文件最后修改时间：文件内容变更 → 键变化 → 旧缓存自然失效，
            // 避免「路径相同但内容已更新」时读到陈旧文本层。
            long lastWrite = 0;
            try { if (File.Exists(full)) lastWrite = File.GetLastWriteTimeUtc(full).Ticks; } catch { }
            var hash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(full + "|" + lastWrite)))[..32].ToLowerInvariant();
            return Path.Combine(CacheDir, hash);
        }

        /// <summary>写入提取的文本层。</summary>
        public static void Write(string path, string text)
        {
            try
            {
                if (!Directory.Exists(CacheDir)) Directory.CreateDirectory(CacheDir);
                File.WriteAllText(BasePath(path) + ".txt", text ?? "");
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[SeeMe] PdfTextCache.Write: " + ex.Message); }
        }

        /// <summary>标记为扫描版（无文本层）。</summary>
        public static void WriteNoText(string path)
        {
            try
            {
                if (!Directory.Exists(CacheDir)) Directory.CreateDirectory(CacheDir);
                File.WriteAllText(BasePath(path) + ".no", "");
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[SeeMe] PdfTextCache.WriteNoText: " + ex.Message); }
        }

        /// <summary>
        /// 读取缓存。返回 true 表示有缓存记录：hasTextLayer=true 且 text 为提取内容；
        /// hasTextLayer=false 表示扫描版。返回 false 表示从未提取过。
        /// </summary>
        public static bool TryRead(string path, out string text, out bool hasTextLayer)
        {
            text = "";
            hasTextLayer = false;
            try
            {
                var baseP = BasePath(path);
                if (File.Exists(baseP + ".txt"))
                {
                    text = File.ReadAllText(baseP + ".txt");
                    hasTextLayer = true;
                    return true;
                }
                if (File.Exists(baseP + ".no"))
                {
                    hasTextLayer = false;
                    return true;
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[SeeMe] PdfTextCache.TryRead: " + ex.Message); }
            return false;
        }

        /// <summary>
        /// 清理过期缓存：删除超过 MaxCacheAge 未更新的 .txt/.no 缓存文件。
        /// 应用启动时调用一次即可（避免每次读写都扫目录）。失败静默。
        /// </summary>
        public static void CleanupExpired()
        {
            try
            {
                if (!Directory.Exists(CacheDir)) return;
                var cutoff = DateTime.UtcNow - MaxCacheAge;
                foreach (var f in Directory.EnumerateFiles(CacheDir))
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext != ".txt" && ext != ".no") continue;
                    try
                    {
                        if (File.GetLastWriteTimeUtc(f) < cutoff) File.Delete(f);
                    }
                    catch { /* 单个文件删除失败不影响整体清理 */ }
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[SeeMe] PdfTextCache.CleanupExpired: " + ex.Message); }
        }
    }
}
