// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

namespace SeeMe
{
    /// <summary>
    /// 文件头（Magic Bytes）与扩展名一致性校验：防止扩展名伪造的文件被打开（安全纵深防御）。
    /// 由 MainWindow.Files.cs 的 FilterSupportedFiles 抽取而来，便于单测。
    /// </summary>
    public static class MagicBytes
    {
        /// <summary>
        /// 校验文件头字节与扩展名一致。
        /// 纯文本类型（.md/.csv 等）无固定文件头，恒返回 true 跳过校验。
        /// </summary>
        public static bool MatchesExt(string ext, byte[] header)
        {
            if (header == null || header.Length < 4) return false;
            return ext.ToLowerInvariant() switch
            {
                ".pdf" => header[0] == 0x25 && header[1] == 0x50 && header[2] == 0x44 && header[3] == 0x46,  // %PDF
                ".docx" or ".docm" or ".xlsx" or ".xlsm" or ".pptx" or ".odt" or ".ods" or ".odp" or ".epub" =>
                    header[0] == 0x50 && header[1] == 0x4B && header[2] == 0x03 && header[3] == 0x04, // ZIP/PK
                ".doc" or ".xls" or ".ppt" =>
                    header[0] == 0xD0 && header[1] == 0xCF && header[2] == 0x11 && header[3] == 0xE0, // OLE2
                ".rtf" =>
                    header.Length >= 5
                    && header[0] == 0x7B && header[1] == 0x5C && header[2] == 0x72 && header[3] == 0x74 && header[4] == 0x66, // {\rtf

                // ── 图片：伪造扩展名的情况比文档更多（.jpg 里塞 exe 是常见投毒手法），必须逐类校验 ──
                ".png" => header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47, // \x89PNG
                ".jpg" or ".jpeg" => header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF,           // SOI + marker
                ".gif" => header.Length >= 6
                          && header[0] == 0x47 && header[1] == 0x49 && header[2] == 0x46 // "GIF"
                          && header[3] == 0x38                                            // "8"
                          && (header[4] == 0x37 || header[4] == 0x39)                      // "7"(87a) / "9"(89a)
                          && header[5] == 0x61,                                            // "a"
                ".bmp" => header[0] == 0x42 && header[1] == 0x4D,                                          // "BM"
                ".webp" => header.Length >= 12
                           && header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46 // "RIFF"
                           && header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50, // "WEBP"
                ".ico" => header[0] == 0x00 && header[1] == 0x00 && header[2] == 0x01 && header[3] == 0x00, // ICONDIR
                ".tif" or ".tiff" => (header[0] == 0x49 && header[1] == 0x49 && header[2] == 0x2A && header[3] == 0x00)  // "II*\0" 小端
                                     || (header[0] == 0x4D && header[1] == 0x4D && header[2] == 0x00 && header[3] == 0x2A), // "MM\0*" 大端

                _ => true // .md / .csv 等纯文本跳过
            };
        }
    }
}
