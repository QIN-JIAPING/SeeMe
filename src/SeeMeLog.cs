// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Text;

namespace SeeMe
{
    /// <summary>
    /// 统一日志 helper：<b>全项目唯一的日志落点</b>（静默 catch、异常路径、安全拦截都走这里）。
    ///
    /// <para><b>三通道设计（有意为之，勿"统一"成一条路径）：</b></para>
    /// <list type="bullet">
    /// <item><description>
    /// <see cref="Info"/> —— <b>仅 Debug 构建</b>输出到调试器（<c>Debug.WriteLine</c> + <c>[Conditional("DEBUG")]</c>）。
    /// Release 下调用点被编译器整个移除，<b>零开销</b>。用于常规流程记录。
    /// </description></item>
    /// <item><description>
    /// <see cref="Warn"/> —— <b>Release 也落盘</b>。用于<b>安全事件</b>
    /// （拦截导航、拦截越权资源请求、危险伪协议等）：这类事件量小但对事后审计有价值，
    /// 且必须保证 Release 版可见，故<b>刻意不带</b> <c>[Conditional]</c>。
    /// </description></item>
    /// <item><description>
    /// <see cref="Error"/> —— <b>Release 也落盘</b>。用于「本应处理但静默吞掉」的异常路径：
    /// 这类失败不 crash，但会让功能悄悄失效，用户报障时若无日志将完全无法定位。
    /// </description></item>
    /// </list>
    ///
    /// <para><b>日志文件</b>：<c>%LOCALAPPDATA%\SeeMe\logs\seeme.log</c>，
    /// 单文件上限 <see cref="MaxLogBytes"/>，超出后轮转为 <c>seeme.log.1</c>（只保留一代）。
    /// 路径与 <c>AppSettings</c> 使用同一 LocalApplicationData 根，便于用户一并打包上交。</para>
    ///
    /// <para><b>失败自愈</b>：日志写入自身的任何异常都被吞掉——记录日志绝不能让程序崩溃，
    /// 也不能因日志失败而掩盖真正的业务异常。</para>
    ///
    /// <para><b>历史</b>：2026-09-10 之前项目存在并行的第二套落盘实现
    /// （<c>MainWindow.WriteLog</c> 写 <c>error.log</c>，且其 <c>LogInfo</c> 在 Release 无条件写盘）。
    /// 现已全部转发到本类，<c>MainWindow.Log.cs</c> 仅保留薄壳。</para>
    ///
    /// <para><b>可见性</b>：<c>public</c>（与 <c>AppSettings</c> / <c>PdfTextCache</c> 等一致）——
    /// 本项目未配置 <c>InternalsVisibleTo</c>，测试程序集只能访问 public 成员，
    /// 而本类的落盘/轮转逻辑必须有测试覆盖。</para>
    /// </summary>
    public static class SeeMeLog
    {
        /// <summary>单文件上限 512KB，超出即轮转。取值偏小是因为这里只记异常与安全事件，正常不会增长。</summary>
        private const long MaxLogBytes = 512 * 1024;

        /// <summary>日志文件路径（懒加载并缓存，避免每次写入都拼路径）。</summary>
        private static string? _logPath;

        /// <summary>写入锁：Error/Warn 可能从任意线程调用（含 WPF 派发线程、FileSystemWatcher 回调）。</summary>
        private static readonly object _gate = new();

        /// <summary>测试隔离：覆盖日志路径并重置缓存（仅测试使用）。</summary>
        public static void ResetForTest(string? logPath = null)
        {
            lock (_gate)
            {
                _logPath = logPath;
                _wroteBanner = false;
            }
        }

        /// <summary>测试用：当前生效的日志文件路径（只读）。</summary>
        public static string CurrentLogPath => LogPath;

        /// <summary>测试用：单文件轮转上限（字节）。</summary>
        public const long MaxBytesForTest = MaxLogBytes;

        /// <summary>是否已写入本次进程的启动分隔行（只写一次，避免高频 Error 刷屏）。</summary>
        private static bool _wroteBanner;

        private static string LogPath
        {
            get
            {
                if (_logPath != null) return _logPath;
                var dir = StoragePaths.LogsDir;
                _logPath = Path.Combine(dir, "seeme.log");
                return _logPath;
            }
        }

        // ─────────────────────────── Info：Debug 专用，Release 零开销 ───────────────────────────

        /// <summary>带来源标签记录，如 SeeMeLog.Info("FileHistory.Load", ex.Message)。<b>仅 Debug 构建生效。</b></summary>
        [System.Diagnostics.Conditional("DEBUG")]
        public static void Info(string tag, string message)
            => System.Diagnostics.Debug.WriteLine("[SeeMe] " + tag + ": " + message);

        /// <summary>无标签直接记录。<b>仅 Debug 构建生效。</b></summary>
        [System.Diagnostics.Conditional("DEBUG")]
        public static void Info(string message)
            => System.Diagnostics.Debug.WriteLine("[SeeMe] " + message);

        // ─────────────────────────── Warn：Release 也落盘（安全事件） ───────────────────────────

        /// <summary>
        /// 记录一条警告并<b>落盘</b>（Release 也生效）。用于<b>安全事件</b>——
        /// 被拦截的导航 / 越权资源请求 / 危险伪协议等。这类事件量小、事后审计价值高，
        /// 故<b>刻意不加</b> <c>[Conditional]</c>。
        /// </summary>
        public static void Warn(string tag, string message)
        {
            System.Diagnostics.Debug.WriteLine("[SeeMe][WARN] " + tag + ": " + message);
            WriteToFile("WARN", tag, message);
        }

        /// <summary>无标签警告并落盘（安全事件常用：调用点没有天然的 tag）。</summary>
        public static void Warn(string message) => Warn(DefaultTag, message);

        // ─────────────────────────── Error：Release 也落盘 ───────────────────────────

        /// <summary>
        /// 记录一条错误并<b>落盘</b>（Debug 下同时输出到调试器）。
        /// 用于本该处理、但静默吞掉的异常——这类失败不影响程序运行，但没有日志就无法排查。
        /// </summary>
        public static void Error(string tag, string message)
        {
            System.Diagnostics.Debug.WriteLine("[SeeMe][ERROR] " + tag + ": " + message);
            WriteToFile("ERROR", tag, message);
        }

        /// <summary>记录异常并落盘，自动附带异常类型与堆栈（多条异常链只取最外层，内层由调用方展开）。</summary>
        public static void Error(string tag, Exception ex)
        {
            var detail = ex.GetType().Name + ": " + ex.Message;
            System.Diagnostics.Debug.WriteLine("[SeeMe][ERROR] " + tag + ": " + detail);
            WriteToFile("ERROR", tag, detail, ex.StackTrace);
        }

        /// <summary>无标签错误并落盘（<c>MainWindow.Log.cs</c> 的 <c>LogErr</c> 转发目标）。</summary>
        public static void Error(string message) => Error(DefaultTag, message);

        /// <summary>无标签重载使用的默认来源标签。</summary>
        private const string DefaultTag = "App";

        // ─────────────────────────── 落盘实现 ───────────────────────────

        /// <param name="stackTrace">
        /// 可选的异常堆栈。**单独传参而非拼进 message，是为了让它走不同的格式化路径**：
        /// 堆栈的行结构对排障至关重要（见 <see cref="AppendStack"/>），
        /// 若与 message 一起被 <see cref="SanitizeInline"/> 处理，20 帧会被压成一行超长文本。
        /// </param>
        private static void WriteToFile(string level, string tag, string message, string? stackTrace = null)
        {
            // 记录日志绝不能让程序崩溃：从路径解析到 IO 的每一步都在守卫内。
            try
            {
                lock (_gate)
                {
                    var path = LogPath;
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                    RotateIfNeeded(path);

                    var sb = new StringBuilder();
                    if (!_wroteBanner)
                    {
                        // 每次进程启动写一行分隔，便于从日志中切分"哪次运行"。
                        sb.Append("===== SeeMe ").Append(AppVersion).Append(" @ ")
                          .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
                          .Append(" =====").Append(Environment.NewLine);
                        _wroteBanner = true;
                    }
                    sb.Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("] ")
                      .Append(level).Append(' ').Append(tag).Append(": ")
                      .Append(SanitizeInline(message));
                    AppendStack(sb, stackTrace);
                    sb.Append(Environment.NewLine);

                    File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
                }
            }
            catch
            {
                // 静默：日志子系统故障不得影响主流程，也不得掩盖原始异常。
            }
        }

        /// <summary>
        /// 堆栈格式化：<b>保留真实换行并缩进</b>，而不是压成一行。
        ///
        /// <para><b>为什么不能复用 <see cref="SanitizeInline"/></b>：异常堆栈的每一帧独占一行是
        /// 排障的基本前提。若整段转义，一个 20 帧的堆栈会变成超长单行（换行全成字面量 <c>\r\n</c>），
        /// 在编辑器里几乎无法阅读 —— 这是 2026-09-10 实测发现的问题。</para>
        ///
        /// <para><b>如何与"一行一条"的解析约定共存</b>：续行统一以 4 个空格开头，
        /// 而正式日志行以 <c>[yyyy-MM-dd</c> 开头。两者不会混淆，
        /// 解析器用「行首是否为 <c>[</c>」即可切分记录。</para>
        ///
        /// <para><b>安全</b>：每一帧内部**仍做转义**。堆栈里可能出现外部数据
        /// （用户提供的路径、文件名），不转义则可被用来伪造后续行。</para>
        /// </summary>
        private static void AppendStack(StringBuilder sb, string? stackTrace)
        {
            if (string.IsNullOrWhiteSpace(stackTrace)) return;

            // 统一按 \n 切分（.NET 的 StackTrace 用 Environment.NewLine，
            // 但手工构造的异常字符串可能是 \n）。
            var frames = stackTrace!.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            foreach (var frame in frames)
            {
                if (frame.Length == 0) continue;
                sb.Append(Environment.NewLine)
                  .Append("    ")                       // 缩进标记续行
                  .Append(SanitizeInline(frame.Trim()));
            }
        }

        /// <summary>
        /// 日志伪造防护（**仅用于单行内容**）：文件名 / URL / 异常消息等外部来源字符串可能含 CR/LF，
        /// 原样写入会让一条日志跨多行，破坏"一行一条"结构（可被用来伪造日志行）。
        /// 统一转义为字面量 <c>\r</c> / <c>\n</c>。
        ///
        /// <para><b>不要用它处理异常堆栈</b> —— 堆栈需要保留行结构，
        /// 见 <see cref="AppendStack"/>（内含每帧的转义调用）。</para>
        /// </summary>
        private static string SanitizeInline(string message)
            => string.IsNullOrEmpty(message)
               ? message
               : message.Replace("\r", "\\r").Replace("\n", "\\n");

        /// <summary>超出上限则把现有日志改名为 .1（覆盖旧的一代）。</summary>
        private static void RotateIfNeeded(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length < MaxLogBytes) return;
                var bak = path + ".1";
                if (File.Exists(bak)) File.Delete(bak);
                File.Move(path, bak);
            }
            catch
            {
                // 轮转失败不阻断写入：File.AppendAllText 会继续追加到原文件。
            }
        }

        /// <summary>版本号，用于日志分隔行。取自程序集版本，取不到则 "?"。</summary>
        private static string AppVersion
        {
            get
            {
                try
                {
                    var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                    return v?.ToString() ?? "?";
                }
                catch { return "?"; }
            }
        }
    }
}
