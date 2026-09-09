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
                _ => true // .md / .csv 等纯文本跳过
            };
        }
    }
}
