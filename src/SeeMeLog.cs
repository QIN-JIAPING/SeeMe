// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

using System;

namespace SeeMe
{
    /// <summary>
    /// 统一日志 helper：静默 catch 的唯一落点。Debug 构建输出到调试器，Release 零开销。
    /// 替代散落各处的 System.Diagnostics.Debug.WriteLine("[SeeMe] ...")。
    /// </summary>
    internal static class SeeMeLog
    {
        /// <summary>带来源标签记录，如 SeeMeLog.Info("FileHistory.Load", ex.Message)。</summary>
        internal static void Info(string tag, string message)
            => System.Diagnostics.Debug.WriteLine("[SeeMe] " + tag + ": " + message);

        /// <summary>无标签直接记录。</summary>
        internal static void Info(string message)
            => System.Diagnostics.Debug.WriteLine("[SeeMe] " + message);
    }
}
