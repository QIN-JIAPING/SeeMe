// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

using System.IO;
using System.Text;

namespace SeeMe
{
    /// <summary>
    /// 文本编码探测与解码助手：统一处理 Markdown / 纯文本文件的编码识别。
    /// 编码标识语义：0=无 BOM UTF-8、1=UTF-8-BOM、2=UTF-16LE、3=UTF-16BE、4=GB18030（GBK 超集）。
    /// 由 MainWindow.Files.cs 与 MainWindow.Rendering.cs 的重复逻辑合并而来，消除重复并便于单测。
    /// </summary>
    public static class TextEncoding
    {
        static TextEncoding()
        {
            // 非 UTF 编码（GB18030/GBK）在 .NET Core 需显式注册编码提供器，否则 GetEncoding 抛异常。
            // 注册是幂等的，此处自举（不依赖 App 启动顺序），保证任何入口使用本类都可用。
            try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { }
        }

        /// <summary>探测字节序列的文本编码，返回编码标识（见类注释）。</summary>
        public static int Detect(byte[] bytes)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return 1;
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return 2;
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return 3;
            var asUtf8 = Encoding.UTF8.GetString(bytes);
            if (asUtf8.IndexOf('\uFFFD') < 0) return 0;
            return 4;
        }

        /// <summary>按编码标识解码（自动剥离 BOM）。</summary>
        public static string Decode(byte[] bytes, int enc) => enc switch
        {
            1 => Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3),
            2 => Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2),
            3 => Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2),
            4 => Encoding.GetEncoding("GB18030").GetString(bytes),
            _ => Encoding.UTF8.GetString(bytes)
        };

        /// <summary>按编码标识返回写盘用的 Encoding（保持原 BOM 语义）。</summary>
        public static Encoding GetEncoding(int enc) => enc switch
        {
            1 => new UTF8Encoding(true),
            2 => Encoding.Unicode,
            3 => Encoding.BigEndianUnicode,
            4 => Encoding.GetEncoding("GB18030"),
            _ => new UTF8Encoding(false)
        };

        /// <summary>
        /// 按 BOM → UTF-8 → GB18030/系统默认 顺序探测读取文本文件，避免 GBK/ANSI 编码的
        /// 中文 .md 文件被按 UTF-8 解码成乱码。GB18030 是 GBK 的超集（含 GB2312），
        /// 且现代 .NET 内置支持，无需注册代码页。
        /// </summary>
        public static string ReadAuto(string path)
        {
            var bytes = File.ReadAllBytes(path);
            var enc = Detect(bytes);
            if (enc == 4)
            {
                // GB18030 对任意字节序列几乎都可解码，但保留系统默认编码兜底以防极端情况
                try { return Encoding.GetEncoding("GB18030").GetString(bytes); }
                catch { return Encoding.Default.GetString(bytes); }
            }
            return Decode(bytes, enc);
        }
    }
}
