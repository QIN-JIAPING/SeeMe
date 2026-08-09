// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace SeeMe
{
    /// <summary>
    /// pandoc 导出后端。检测系统安装的 pandoc，提供 md→docx/pdf/latex 命令行封装。
    /// 用户可通过 SetCustomPath 指定手动安装路径。
    /// </summary>
    public class PandocExportService
    {
        private const string SettingsFile = "pandoc_settings.json";
        private string? _customPath;

        /// <summary>pandoc 可执行文件名称（Windows 用 pandoc.exe，其他平台 pandoc）。</summary>
        private static string ExeName => OperatingSystem.IsWindows() ? "pandoc.exe" : "pandoc";

        /// <summary>获取 pandoc 路径：自定义路径 > 常见安装目录 > PATH 搜索 > null。</summary>
        public string? FindPandoc()
        {
            // 1) 自定义路径
            if (!string.IsNullOrEmpty(_customPath) && File.Exists(_customPath))
                return _customPath;

            // 2) 常见安装目录（pandoc 默认安装位置，未加入 PATH 时也能找到）
            foreach (var c in CommonPandocPaths())
            {
                if (File.Exists(c)) return c;
            }

            // 3) PATH 搜索
            try
            {
                // 在 Windows 上用 where，其他用 which
                var psi = new ProcessStartInfo
                {
                    FileName = OperatingSystem.IsWindows() ? "where" : "which",
                    Arguments = ExeName,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    var output = proc.StandardOutput.ReadToEnd().Trim();
                    proc.WaitForExit(2000);
                    if (proc.ExitCode == 0 && !string.IsNullOrEmpty(output))
                    {
                        // where 可能返回多行，取第一行
                        var firstLine = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
                        if (File.Exists(firstLine))
                            return firstLine;
                    }
                }
            }
            catch { /* pandoc 未安装 */ }

            return null;
        }

        /// <summary>枚举 pandoc 的常见安装位置（含用户约定的 D:\allll 与环境变量/配置扩展）。</summary>
        private IEnumerable<string> CommonPandocPaths()
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            yield return Path.Combine(pf, "Pandoc", "pandoc.exe");
            yield return Path.Combine(pf86, "Pandoc", "pandoc.exe");
            yield return Path.Combine(local, "Pandoc", "pandoc.exe");
            yield return Path.Combine(common, "Pandoc", "pandoc.exe");
            yield return Path.Combine("C:\\", "Pandoc", "pandoc.exe");
            // 用户约定：安装产物一律落在 D:\allll（保留为候选之一）
            yield return Path.Combine("D:\\allll", "pandoc", "pandoc.exe");

            // 环境变量自定义目录（跨机器可移植，不硬编码）
            var customDir = Environment.GetEnvironmentVariable("SEEME_PANDOC_DIR");
            if (!string.IsNullOrEmpty(customDir))
                yield return Path.Combine(customDir, "pandoc.exe");

            // 设置文件中用户自定义的额外搜索目录
            foreach (var extra in LoadExtraSearchDirs())
            {
                yield return Path.Combine(extra, "pandoc.exe");
            }
        }

        private List<string> _extraSearchDirs = new();

        private List<string> LoadExtraSearchDirs()
        {
            if (_extraSearchDirs.Count > 0) return _extraSearchDirs;
            try
            {
                if (!File.Exists(StoragePath)) return _extraSearchDirs;
                var json = File.ReadAllText(StoragePath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("extraSearchDirs", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in arr.EnumerateArray())
                    {
                        var s = item.GetString();
                        if (!string.IsNullOrEmpty(s))
                            _extraSearchDirs.Add(s);
                    }
                }
            }
            catch { }
            return _extraSearchDirs;
        }

        /// <summary>手动设置 pandoc 路径（持久化到设置文件）。</summary>
        public void SetCustomPath(string path)
        {
            _customPath = File.Exists(path) ? path : null;
            SaveSettings();
        }

        /// <summary>获取 pandoc 版本号，null 表示不可用。</summary>
        public string? GetVersion()
        {
            var pandoc = FindPandoc();
            if (pandoc == null) return null;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = pandoc,
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    var firstLine = proc.StandardOutput.ReadLine();
                    proc.WaitForExit(2000);
                    return firstLine;
                }
            }
            catch { }
            return null;
        }

        /// <summary>导出 Markdown 文件到目标格式。</summary>
        /// <param name="inputPath">源 .md 文件路径。</param>
        /// <param name="outputPath">输出路径（自动根据扩展名推断格式）。</param>
        /// <param name="format">pandoc 输出格式：docx、pdf、latex 等。为 null 则从 outputPath 扩展名推断。</param>
        /// <returns>(成功?, 错误信息或输出路径)</returns>
        public async Task<(bool Success, string Message)> ExportAsync(string inputPath, string? outputPath = null, string? format = null)
        {
            var pandoc = FindPandoc();
            if (pandoc == null)
                return (false, "pandoc 未安装。请先安装 pandoc (https://pandoc.org/installing.html) 或通过 SetCustomPath 指定路径。");

            if (string.IsNullOrEmpty(inputPath) || !File.Exists(inputPath))
                return (false, "源文件不存在: " + (inputPath ?? "(空)"));

            // 推断输出路径
            if (string.IsNullOrEmpty(outputPath))
            {
                var dir = Path.GetDirectoryName(inputPath) ?? ".";
                var name = Path.GetFileNameWithoutExtension(inputPath);
                format ??= "docx";
                var ext = format switch
                {
                    "docx" => ".docx",
                    "pdf" => ".pdf",
                    "latex" or "tex" => ".tex",
                    "html" => ".html",
                    "epub" => ".epub",
                    "rst" => ".rst",
                    "markdown" or "md" => ".md",
                    _ => "." + format
                };
                outputPath = Path.Combine(dir, name + "_exported" + ext);
            }

            // 从输出路径推断格式
            if (string.IsNullOrEmpty(format))
            {
                format = Path.GetExtension(outputPath).TrimStart('.').ToLowerInvariant();
                format = format switch
                {
                    "docx" => "docx",
                    "pdf" => "pdf",
                    "tex" => "latex",
                    "html" or "htm" => "html",
                    "epub" => "epub",
                    "rst" => "rst",
                    "md" or "markdown" => "markdown",
                    _ => format
                };
            }

            // 白名单校验：仅允许已知安全格式，防止 format 注入额外 pandoc 参数导致 RCE。
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "docx", "pdf", "latex", "html", "epub", "markdown", "rst" };
            if (!allowed.Contains(format))
                return (false, "不支持的导出格式: " + (format ?? "(空)"));

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = pandoc,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                // 使用 ArgumentList 逐参数传递，避免文件名含引号/空格导致参数解析错误或被注入
                psi.ArgumentList.Add(inputPath);
                psi.ArgumentList.Add("-o");
                psi.ArgumentList.Add(outputPath);
                psi.ArgumentList.Add("-f");
                psi.ArgumentList.Add("markdown");
                psi.ArgumentList.Add("-t");
                psi.ArgumentList.Add(format);
                psi.ArgumentList.Add("--wrap=none");

                var tcs = new TaskCompletionSource<(int, string, string)>();
                using var proc = new Process { StartInfo = psi };

                proc.Start();
                var stdout = await proc.StandardOutput.ReadToEndAsync();
                var stderr = await proc.StandardError.ReadToEndAsync();
                await proc.WaitForExitAsync();

                if (proc.ExitCode == 0)
                {
                    return (true, outputPath);
                }
                else
                {
                    var errMsg = !string.IsNullOrEmpty(stderr) ? stderr.Trim() : "未知错误 (exit code " + proc.ExitCode + ")";
                    return (false, "pandoc 导出失败: " + errMsg);
                }
            }
            catch (Exception ex)
            {
                return (false, "pandoc 调用异常: " + ex.Message);
            }
        }

        // ──────────────── 设置持久化 ────────────────

        private string StoragePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SeeMe", SettingsFile);

        public void LoadSettings()
        {
            try
            {
                if (!File.Exists(StoragePath)) return;
                var json = File.ReadAllText(StoragePath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("pandocPath", out var el) && el.ValueKind == JsonValueKind.String)
                {
                    var path = el.GetString();
                    if (!string.IsNullOrEmpty(path) && File.Exists(path))
                        _customPath = path;
                }
            }
            catch { }
        }

        private void SaveSettings()
        {
            try
            {
                var dir = Path.GetDirectoryName(StoragePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(StoragePath,
                    JsonSerializer.Serialize(new { pandocPath = _customPath ?? "", extraSearchDirs = _extraSearchDirs },
                        new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }
}
