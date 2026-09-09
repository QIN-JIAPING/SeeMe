// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

using System;
using System.Threading.Tasks;

namespace SeeMe
{
    /// <summary>
    /// anydoc-wasm 失败/超时后的本地回退分发决策。
    /// 仅 docx/xlsx/pptx 有 FileConverter（OpenXml/PdfPig）本地解析器；
    /// 其余 anydoc 格式（doc/ppt/rtf/odt/ods/odp/epub/csv）无回退，只能抛错走错误页。
    /// 由 MainWindow.Rendering.cs 的 FallbackOfficeAsync 抽取而来，便于单测钉住「哪些格式有回退」。
    /// </summary>
    public static class OfficeFallback
    {
        /// <summary>该扩展名是否有本地回退解析器。</summary>
        public static bool HasLocalConverter(string? ext) => ext switch
        {
            ".docx" or ".xlsx" or ".pptx" => true,
            _ => false
        };

        /// <summary>按扩展名分发到本地回退转换器；无回退解析器时抛 NotSupportedException。</summary>
        public static Task<string> ConvertAsync(IFileConverter converter, string ext, string filePath) => ext switch
        {
            ".xlsx" => converter.ExcelToHtml(filePath),
            ".pptx" => converter.PptToHtml(filePath),
            ".docx" => converter.DocxToHtml(filePath),
            _ => throw new NotSupportedException("该格式无本地回退解析器（需 anydoc-wasm）：" + ext)
        };
    }
}
