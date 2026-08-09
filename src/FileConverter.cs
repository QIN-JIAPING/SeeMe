// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Wordprocessing;
using P = DocumentFormat.OpenXml.Presentation;
using D = DocumentFormat.OpenXml.Drawing;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace SeeMe
{
    /// <summary>
    /// 将 Excel (.xlsx) 和 PowerPoint (.pptx) 转换为 HTML 字符串，
    /// 使用与 SeeMe 渲染 Markdown 时一致的 CSS 变量体系。
    /// </summary>
    public class FileConverter : IFileConverter
    {
        private const int MaxRowsPerSheet = 5000;
        private const int MaxSheets = 50;
        private const int MaxPptSlides = 200;

        // ──────────────── Excel -> HTML ────────────────

        /// <summary>将 Excel 文件路径转为内嵌样式的 HTML 表格。</summary>
        public Task<string> ExcelToHtml(string filePath)
            => Task.Run(() => ExcelToHtmlSync(filePath));

        private string ExcelToHtmlSync(string filePath)
        {
            var sb = new StringBuilder();
            sb.Append("<div class=\"excel-container\">");

            try
            {
                using var doc = SpreadsheetDocument.Open(filePath, false);
                var wbPart = doc.WorkbookPart;
                if (wbPart == null)
                {
                    sb.Append("<p class=\"error-msg\">无法读取工作簿结构</p></div>");
                    return sb.ToString();
                }

                var sheets = wbPart.Workbook.Sheets?
                    .OfType<Sheet>()
                    .Take(MaxSheets)
                    .ToList() ?? new List<Sheet>();

                // 预构建共享字符串索引：sst.ElementAt(idx) 在 OpenXML 上是 O(n)/次，
                // 大 Excel（数万条共享字符串）下总复杂度 O(n²) 导致转换卡死。改为预建 List → O(1) 访问。
                var sharedStrings = new List<string>();
                try
                {
                    var sst = wbPart.SharedStringTablePart?.SharedStringTable;
                    if (sst != null)
                    {
                        foreach (var el in sst)
                            sharedStrings.Add(el.InnerText);
                    }
                }
                catch { /* 损坏的共享字符串表忽略，降级为原始值 */ }

                if (sheets.Count == 0)
                {
                    sb.Append("<p class=\"empty-msg\">未找到工作表</p></div>");
                    return sb.ToString();
                }

                foreach (var sheet in sheets)
                {
                    var sheetName = System.Security.SecurityElement.Escape(sheet.Name ?? "Sheet");
                    sb.Append($"<h2 class=\"sheet-title\">📊 {sheetName}</h2>");

                    var wsPart = wbPart.GetPartById(sheet.Id!) as WorksheetPart;
                    if (wsPart == null)
                    {
                        sb.Append("<p class=\"error-msg\">无法读取工作表</p>");
                        continue;
                    }

                    var sheetData = wsPart.Worksheet.GetFirstChild<SheetData>();
                    if (sheetData == null)
                    {
                        sb.Append("<p class=\"empty-msg\">空工作表</p>");
                        continue;
                    }

                    var rows = sheetData.Elements<Row>().Take(MaxRowsPerSheet).ToList();
                    if (rows.Count == 0)
                    {
                        sb.Append("<p class=\"empty-msg\">空工作表</p>");
                        continue;
                    }

                    sb.Append("<div style=\"overflow-x:auto;margin:.5em 0 1.5em;\"><table class=\"excel-table\"><tbody>");

                    foreach (var row in rows)
                    {
                        sb.Append("<tr>");
                        var cells = row.Elements<Cell>().ToList();

                        // 找出本行最大列号，补齐空列
                        uint maxCol = 0;
                        foreach (var cell in cells)
                        {
                            var colRef = cell.CellReference?.Value;
                            if (colRef != null)
                            {
                                var colNum = ColumnLetterToNumber(Regex.Replace(colRef, @"\d", ""));
                                if (colNum > maxCol) maxCol = colNum;
                            }
                        }
                        if (maxCol == 0 && cells.Count > 0)
                            maxCol = (uint)cells.Count;

                        // 构建列索引 -> Cell 的映射
                        var cellMap = new Dictionary<uint, Cell>();
                        foreach (var cell in cells)
                        {
                            var colRef = cell.CellReference?.Value;
                            if (colRef != null)
                                cellMap[ColumnLetterToNumber(Regex.Replace(colRef, @"\d", ""))] = cell;
                        }

                        for (uint c = 1; c <= maxCol; c++)
                        {
                            if (cellMap.TryGetValue(c, out var cell))
                            {
                                var val = GetCellValue(cell, sharedStrings);
                                var tag = (row.RowIndex?.Value ?? 0) == 1 ? "th" : "td";
                                sb.Append($"<{tag}>{System.Security.SecurityElement.Escape(val)}</{tag}>");
                            }
                            else
                            {
                                var tag = (row.RowIndex?.Value ?? 0) == 1 ? "th" : "td";
                                sb.Append($"<{tag}></{tag}>");
                            }
                        }

                        sb.Append("</tr>");
                    }

                    sb.Append("</tbody></table></div>");
                }

                var fi = new FileInfo(filePath);
                if (fi.Exists)
                {
                    sb.Append($"<div class=\"file-info\">文件大小：{FormatSizeBytes(fi.Length)} ｜ 工作表数：{sheets.Count}</div>");
                }
            }
            catch (Exception ex)
            {
                sb.Append($"<p class=\"error-msg\">转换 Excel 失败：{System.Security.SecurityElement.Escape(ex.Message)}</p>");
            }

            sb.Append("</div>");
            return sb.ToString();
        }

        private string GetCellValue(Cell cell, List<string> sharedStrings)
        {
            if (cell.CellValue == null) return "";

            var raw = cell.CellValue.InnerText;

            // 共享字符串（预构建索引，O(1) 访问）
            if (cell.DataType?.Value == CellValues.SharedString)
            {
                if (int.TryParse(raw, out var idx) && idx >= 0 && idx < sharedStrings.Count)
                    return sharedStrings[idx];
                return raw;
            }

            // 内联字符串
            if (cell.DataType?.Value == CellValues.InlineString)
            {
                var ist = cell.GetFirstChild<DocumentFormat.OpenXml.Spreadsheet.InlineString>();
                return ist?.Text?.Text ?? raw;
            }

            // 数字 —— 尝试去掉末尾多余的小数
            if (double.TryParse(raw, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var num))
            {
                // 整数不显示小数
                if (num == Math.Truncate(num) && Math.Abs(num) < 1e15)
                    return ((long)num).ToString();
                return num.ToString(System.Globalization.CultureInfo.CurrentCulture);
            }

            return raw;
        }

        private uint ColumnLetterToNumber(string letters)
        {
            uint result = 0;
            foreach (var ch in letters.ToUpperInvariant())
            {
                result = result * 26 + (uint)(ch - 'A' + 1);
            }
            return result;
        }

        // ──────────────── PPT -> HTML ────────────────

        /// <summary>将 PowerPoint 文件路径转为 HTML 幻灯片展示。</summary>
        public Task<string> PptToHtml(string filePath)
            => Task.Run(() => PptToHtmlSync(filePath));

        private string PptToHtmlSync(string filePath)
        {
            var sb = new StringBuilder();
            sb.Append("<div class=\"ppt-container\">");

            try
            {
                using var doc = PresentationDocument.Open(filePath, false);
                var presPart = doc.PresentationPart;
                if (presPart == null)
                {
                    sb.Append("<p class=\"error-msg\">无法读取演示文稿结构</p></div>");
                    return sb.ToString();
                }

                var slideIdList = presPart.Presentation.SlideIdList;
                if (slideIdList == null)
                {
                    sb.Append("<p class=\"empty-msg\">未找到幻灯片</p></div>");
                    return sb.ToString();
                }

                var slideIds = slideIdList.OfType<P.SlideId>()
                    .Take(MaxPptSlides)
                    .ToList();

                for (int i = 0; i < slideIds.Count; i++)
                {
                    var slidePart = presPart.GetPartById(slideIds[i].RelationshipId!) as SlidePart;
                    if (slidePart == null) continue;

                    var slideNum = i + 1;
                    sb.Append($"<div class=\"ppt-slide\">");
                    sb.Append($"<div class=\"ppt-slide-header\">📄 幻灯片 {slideNum} / {slideIds.Count}</div>");
                    sb.Append("<div class=\"ppt-slide-body\">");

                    var shapes = slidePart.Slide.Descendants<P.Shape>();

                    var hasContent = false;
                    foreach (var shape in shapes)
                    {
                        var text = ExtractShapeText(shape);
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            sb.Append($"<p class=\"ppt-text\">{System.Security.SecurityElement.Escape(text)}</p>");
                            hasContent = true;
                        }
                    }

                    if (!hasContent)
                        sb.Append("<p class=\"empty-msg\">（空白幻灯片）</p>");

                    sb.Append("</div></div>");
                }
            }
            catch (Exception ex)
            {
                sb.Append($"<p class=\"error-msg\">转换 PPT 失败：{System.Security.SecurityElement.Escape(ex.Message)}</p>");
            }

            sb.Append("</div>");
            return sb.ToString();
        }

        private string ExtractShapeText(P.Shape shape)
        {
            var texts = shape.Descendants<D.Text>()
                .Select(t => t.Text)
                .Where(t => !string.IsNullOrWhiteSpace(t));
            return string.Join("", texts);
        }

        // ──────────────── PDF -> HTML ────────────────

        /// <summary>提取 PDF 文本，转 HTML 段落格式输出。</summary>
        public Task<string> PdfToHtml(string filePath)
            => Task.Run(() => PdfToHtmlSync(filePath));

        private string PdfToHtmlSync(string filePath)
        {
            var sb = new StringBuilder();
            sb.Append("<div class=\"pdf-container\">");

            try
            {
                using var pdf = PdfDocument.Open(filePath);
                var pageCount = pdf.NumberOfPages;
                sb.Append($"<div class=\"doc-summary\">共 {pageCount} 页</div>");

                for (int i = 1; i <= pageCount; i++)
                {
                    var page = pdf.GetPage(i);
                    sb.Append($"<div class=\"pdf-page\">");
                    sb.Append($"<div class=\"pdf-page-header\">📄 第 {i} / {pageCount} 页</div>");
                    sb.Append("<div class=\"pdf-page-body\">");

                    // 用字母坐标精确重建行
                    var letters = page.Letters.ToList();
                    if (letters.Count > 0)
                    {
                        // 按 Y 坐标分组（允许 5pt 误差）
                        var lines = new List<List<UglyToad.PdfPig.Content.Letter>>();
                        lines.Add(new List<UglyToad.PdfPig.Content.Letter>());

                        foreach (var ch in letters)
                        {
                            var currentLine = lines[^1];
                            if (currentLine.Count > 0)
                            {
                                var lastY = currentLine[^1].StartBaseLine.Y;
                                // 如果 Y 偏移超过阈值则换行
                                if (Math.Abs(ch.StartBaseLine.Y - lastY) > 5.0)
                                    lines.Add(new List<UglyToad.PdfPig.Content.Letter>());
                            }
                            lines[^1].Add(ch);
                        }

                        foreach (var lineChars in lines)
                        {
                            if (lineChars.Count == 0) continue;
                            FlushLineLetters(sb, lineChars);
                        }
                    }
                    else
                    {
                        sb.Append("<p class=\"empty-msg\">（空白页）</p>");
                    }

                    sb.Append("</div></div>");
                }
            }
            catch (Exception ex)
            {
                sb.Append($"<p class=\"error-msg\">读取 PDF 失败：{System.Security.SecurityElement.Escape(ex.Message)}</p>");
            }

            sb.Append("</div>");
            return sb.ToString();
        }

        /// <summary>将一行字母转为 HTML，按水平间距决定空格/缩进（缩进阈值按平均字符宽度自适应）。</summary>
        private void FlushLineLetters(StringBuilder sb, List<UglyToad.PdfPig.Content.Letter> letters)
        {
            const double spaceThreshold = 0.3;  // 字符宽度的倍数，超过则插入空格
            var text = new StringBuilder();
            double? prevRight = null;
            double? firstLeft = null;
            double charWidth = 0;

            foreach (var ch in letters)
            {
                var rect = ch.BoundingBox;
                if (firstLeft == null) firstLeft = rect.Left;
                if (prevRight != null)
                {
                    var gap = rect.Left - prevRight.Value;
                    if (gap > charWidth * spaceThreshold)
                        text.Append(' ');
                }
                charWidth = rect.Width;
                prevRight = rect.Right;
                text.Append(ch.Value);
            }

            var line = text.ToString().TrimEnd();
            if (string.IsNullOrWhiteSpace(line)) return;

            var escaped = System.Security.SecurityElement.Escape(line);
            var indent = firstLeft ?? 0;

            // 缩进较多 → 代码块。阈值按本行平均字符宽度自适应（约 4 个字符宽），
            // 替代固定 40pt：小字号 PDF 缩进 2 字符也能识别为代码，大字号正文不会误判。
            var avgCharWidth = letters.Count > 0 ? letters.Average(l => l.BoundingBox.Width) : 10.0;
            if (indent > avgCharWidth * 4)
                sb.Append($"<pre class=\"pdf-code\">{escaped}</pre>");
            else
                sb.Append($"<p class=\"pdf-line\">{escaped}</p>");
        }

        // ──────────────── DOCX -> HTML ────────────────

        /// <summary>将 Word (.docx) 文件转为 HTML。</summary>
        public Task<string> DocxToHtml(string filePath)
            => Task.Run(() => DocxToHtmlSync(filePath));

        private string DocxToHtmlSync(string filePath)
        {
            var sb = new StringBuilder();
            sb.Append("<div class=\"docx-container\">");

            try
            {
                using var doc = WordprocessingDocument.Open(filePath, false);
                var body = doc.MainDocumentPart?.Document.Body;
                if (body == null)
                {
                    sb.Append("<p class=\"empty-msg\">文档为空</p></div>");
                    return sb.ToString();
                }

                foreach (var elem in body.Elements())
                {
                    if (elem is Paragraph para)
                    {
                        var styleName = GetParagraphStyleName(para);
                        var align = GetParagraphAlignment(para);

                        var sbLine = new StringBuilder();
                        foreach (var run in para.Elements<DocumentFormat.OpenXml.Wordprocessing.Run>())
                        {
                            var text = string.Concat(run.Elements<DocumentFormat.OpenXml.Wordprocessing.Text>().Select(t => t.Text));
                            if (string.IsNullOrEmpty(text)) continue;
                            text = System.Security.SecurityElement.Escape(text);

                            var isBold = run.RunProperties?.Bold != null;
                            var isItalic = run.RunProperties?.Italic != null;
                            var isUnderline = run.RunProperties?.Underline != null;
                            var fontSize = run.RunProperties?.FontSize?.Val?.Value;

                            if (isBold) sbLine.Append("<strong>");
                            if (isItalic) sbLine.Append("<em>");
                            sbLine.Append(text);
                            if (isItalic) sbLine.Append("</em>");
                            if (isBold) sbLine.Append("</strong>");
                        }

                        var line = sbLine.ToString();
                        if (string.IsNullOrWhiteSpace(line)) continue;

                        // 按样式决定标签
                        if (styleName != null && styleName.StartsWith("Heading", StringComparison.OrdinalIgnoreCase))
                        {
                            var level = 1;
                            if (styleName.Length > 7 && int.TryParse(styleName.AsSpan(7), out var l))
                                level = Math.Clamp(l, 1, 6);
                            sb.Append($"<h{level} class=\"docx-heading\">{line}</h{level}>");
                        }
                        else
                        {
                            var cls = align != null ? $" class=\"docx-para\" style=\"text-align:{align}\"" : " class=\"docx-para\"";
                            sb.Append($"<p{cls}>{line}</p>");
                        }
                    }
                    else if (elem is DocumentFormat.OpenXml.Wordprocessing.Table table)
                    {
                        sb.Append("<table class=\"docx-table\">");
                        foreach (var row in table.Elements<TableRow>())
                        {
                            sb.Append("<tr>");
                            foreach (var cell in row.Elements<DocumentFormat.OpenXml.Wordprocessing.TableCell>())
                            {
                                var cellText = string.Concat(cell.Descendants<DocumentFormat.OpenXml.Wordprocessing.Text>().Select(t => t.Text));
                                sb.Append($"<td>{System.Security.SecurityElement.Escape(cellText)}</td>");
                            }
                            sb.Append("</tr>");
                        }
                        sb.Append("</table>");
                    }
                }
            }
            catch (Exception ex)
            {
                sb.Append($"<p class=\"error-msg\">读取 Word 失败：{System.Security.SecurityElement.Escape(ex.Message)}</p>");
            }

            sb.Append("</div>");
            return sb.ToString();
        }

        private string? GetParagraphStyleName(Paragraph para)
        {
            var ppr = para.ParagraphProperties;
            var sid = ppr?.ParagraphStyleId;
            return sid?.Val?.Value;
        }

        private string? GetParagraphAlignment(Paragraph para)
        {
            var ppr = para.ParagraphProperties;
            var just = ppr?.Justification;
            if (just == null) return null;
            var jv = just.Val?.Value;
            if (jv == DocumentFormat.OpenXml.Wordprocessing.JustificationValues.Center) return "center";
            if (jv == DocumentFormat.OpenXml.Wordprocessing.JustificationValues.Right) return "right";
            if (jv == DocumentFormat.OpenXml.Wordprocessing.JustificationValues.Both) return "justify";
            return null;
        }

        // ──────────────── 通用 ────────────────

        public string FormatSizeBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
        }
    }
}
