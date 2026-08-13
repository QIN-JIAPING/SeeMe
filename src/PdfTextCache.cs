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

        private static string BasePath(string path)
        {
            var full = Path.GetFullPath(path);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..32].ToLowerInvariant();
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
    }
}
