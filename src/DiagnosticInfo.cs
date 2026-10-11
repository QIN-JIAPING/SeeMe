// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace SeeMe
{
    /// <summary>
    /// 诊断信息的**脱敏**处理：把"复制给开发者"的文本里的本机身份信息抹掉。
    ///
    /// <para><b>为什么需要</b>：诊断信息包含数据目录、安装位置、日志文件路径与日志尾部，
    /// 其中前两者必然带着<b>用户名</b>（<c>C:\Users\&lt;name&gt;…</c>），
    /// 日志里也常出现用户文件的完整路径。用户点"复制"时未必意识到这一点。</para>
    ///
    /// <para><b>定位：纵深防御的一层，不是保证</b>。它挡的是"无意间把用户名和路径发出去"，
    /// 挡不住用户主动粘贴敏感内容。真正的护栏是"先把要复制的内容显示给用户看"
    /// （见 <see cref="DiagnosticPreviewWindow"/>）—— 脱敏只是把默认文本降一档敏感度。</para>
    ///
    /// <para><b>刻意不脱敏裸用户名</b>：把 <c>16530</c> 这类短用户名全局替换掉，
    /// 会误伤正常文本（数字、版本号、路径片段里到处都是）。收益不确定、误报确定，
    /// 故只处理**路径前缀**这种高置信形态。</para>
    /// </summary>
    public static class DiagnosticInfo
    {
        /// <summary><c>Bearer &lt;token&gt;</c>（JWT / OAuth 令牌最常见的外形）。</summary>
        private static readonly Regex BearerPattern = new(
            @"\bBearer\s+[A-Za-z0-9\-\._~\+/]{8,}=*",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// <c>key = value</c> / <c>key: value</c> 形态的凭据。
        /// 键名用白名单，避免把普通文本里的冒号误判成凭据。
        /// </summary>
        private static readonly Regex CredentialPattern = new(
            @"(?<key>\b(?:password|passwd|pwd|token|secret|api[-_]?key|access[-_]?key|client[-_]?secret|authorization|apikey)\b)\s*[:=]\s*\S+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// 脱敏。<paramref name="userProfilePath"/> 及其正斜杠形态会被替换为 <c>%USERPROFILE%</c>
        /// （一次性覆盖 AppData / 桌面 / 文档 / 日志等所有子路径）。
        /// </summary>
        /// <param name="text">待脱敏文本；null/空原样返回。</param>
        /// <param name="userProfilePath">用户主目录，通常传 <c>Environment.GetFolderPath(UserProfile)</c>。</param>
        public static string Redact(string? text, string? userProfilePath)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            var result = text!;

            // 1) 主目录前缀 → %USERPROFILE%
            var root = Trim(userProfilePath);
            if (!string.IsNullOrEmpty(root))
            {
                result = result.Replace(root!, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
                // Windows 上路径分隔符混用（日志、异常消息里都见过），正斜杠形态一并处理
                var slashForm = root!.Replace('\\', '/');
                if (!string.Equals(slashForm, root, StringComparison.Ordinal))
                    result = result.Replace(slashForm, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
            }

            // 2) 先处理 Bearer，再处理 key=value（顺序影响结果的可读性，不影响安全性）
            result = BearerPattern.Replace(result, "Bearer <redacted>");
            result = CredentialPattern.Replace(result, "${key}=<redacted>");

            return result;
        }

        /// <summary>去掉尾部路径分隔符；空白返回 null。</summary>
        private static string? Trim(string? path)
            => string.IsNullOrWhiteSpace(path) ? null : path!.TrimEnd('\\', '/');

        /// <summary>
        /// 诊断信息里附带的日志行数上限（够看清最后一次崩溃，又不至于长到无法阅读）。
        /// 放在这里而不是 SettingsDialog，是为了让"包含多少行"与脱敏同处一个可测单元。
        /// </summary>
        public const int LogTailLines = 60;

        /// <summary>读取日志文件尾部若干行；失败返回可读说明而不是抛异常。</summary>
        public static string ReadLogTail(string logPath, int maxLines, IReadOnlyList<string>? preloadedLines = null)
        {
            try
            {
                if (preloadedLines != null)
                {
                    var lines = preloadedLines;
                    if (lines.Count <= maxLines) return string.Join(Environment.NewLine, lines);
                    return $"（共 {lines.Count} 行，此处只取最后 {maxLines} 行）" + Environment.NewLine
                         + string.Join(Environment.NewLine, Slice(lines, lines.Count - maxLines));
                }
                if (!System.IO.File.Exists(logPath))
                    return "（暂无日志文件：本次运行未产生 Warn/Error）";
                var all = System.IO.File.ReadAllLines(logPath);
                if (all.Length <= maxLines) return string.Join(Environment.NewLine, all);
                return $"（共 {all.Length} 行，此处只取最后 {maxLines} 行）" + Environment.NewLine
                     + string.Join(Environment.NewLine, Slice(all, all.Length - maxLines));
            }
            catch (Exception ex) { return "（读取日志失败：" + ex.Message + "）"; }
        }

        private static IEnumerable<string> Slice(string[] arr, int from)
        {
            for (var i = from; i < arr.Length; i++) yield return arr[i];
        }

        private static IEnumerable<string> Slice(IReadOnlyList<string> arr, int from)
        {
            for (var i = from; i < arr.Count; i++) yield return arr[i];
        }
    }
}
