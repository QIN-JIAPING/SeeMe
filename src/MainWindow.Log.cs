// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

namespace SeeMe
{
    public partial class MainWindow
    {
        // ═══════ 文件职责：MainWindow 内的日志快捷方式（薄壳，全部转发到 SeeMeLog） ═══════
        //
        // 历史上这里有一套独立的落盘实现（WriteLog → %LOCALAPPDATA%\SeeMe\error.log，
        // 1MB 轮转为 error.old.log），与 SeeMeLog 的 seeme.log 并行存在，导致：
        //   1) 排查故障要翻两个目录两个文件，两套日志无时间戳对齐关系；
        //   2) 本文件的 LogInfo 没有 [Conditional("DEBUG")]，Release 下 18 个调用点
        //      无条件执行「建目录 → 查长度 → 拼串 → 追加」，与 SeeMeLog 的零开销设计矛盾。
        //
        // 2026-09-10 已合并：本文件不再有任何 IO，只做方法名转发，保留是为了不改动
        // 111 个既有调用点（`LogErr("...")` 的写法在 MainWindow 各处高频出现）。
        //
        // ⚠ 级别语义（改动前先读）：
        //   LogErr  → SeeMeLog.Error  —— Release 落盘
        //   LogWarn → SeeMeLog.Warn   —— Release 落盘（安全事件，刻意保留可见）
        //   LogInfo → SeeMeLog.Info   —— 仅 Debug，Release 零开销

        private static void LogErr(string msg) => SeeMeLog.Error(msg);

        private static void LogWarn(string msg) => SeeMeLog.Warn(msg);

        private static void LogInfo(string msg) => SeeMeLog.Info(msg);
    }
}
