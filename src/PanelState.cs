// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;

namespace SeeMe
{
    public class PanelState : IDisposable
    {
        public string? CurrentFile;
        public string? FileDir;
        public FileSystemWatcher? Watcher;
        public DispatcherTimer? Debounce;
        public double LastScrollY;
        public double FontScale = 1.0;
        // 自适应防抖：当前等待间隔(ms)，连发时递增至上限，刷新成功后回落到最小值
        public int DebounceMs = 100;
        // 内容感知防抖：记录上次成功加载的文件时间戳与大小，用于判断是否真的需要刷新
        public System.DateTime LastWrite;
        public long LastLen;
        // 取消令牌：切换文件或关闭面板时取消正在进行的异步操作
        public CancellationTokenSource? Cts;
        // 渲染进程崩溃恢复：连续崩溃计数与最近崩溃时间（ProcessFailed 自动重载节流用）
        public int CrashCount;
        public DateTime LastCrashUtc;
        public TextBlock TitleText { get; }
        public TextBlock PathText { get; }
        public WebView2 WebView { get; }
        public Border TitleBar { get; }

        public PanelState(TextBlock titleText, TextBlock pathText, WebView2 webView, Border titleBar)
        {
            TitleText = titleText;
            PathText = pathText;
            WebView = webView;
            TitleBar = titleBar;
        }

        /// <summary>取消当前加载操作并创建新的 CancellationTokenSource。</summary>
        public CancellationToken ResetCts()
        {
            Cts?.Cancel();
            Cts?.Dispose();
            Cts = new CancellationTokenSource();
            return Cts.Token;
        }

        /// <summary>释放资源：停止文件监视、防抖计时器，并取消正在进行的异步加载。</summary>
        public void Dispose()
        {
            try { Watcher?.Dispose(); } catch { }
            try { Debounce?.Stop(); } catch { }
            try { Cts?.Cancel(); Cts?.Dispose(); } catch { }
            // 显式释放 WebView2 控件（关闭 CoreWebView2，事件源随之一并释放），
            // 避免托盘常驻/反复开窗时残留 WebView2 环境实例与事件订阅
            try { WebView.Dispose(); } catch { }
            Watcher = null;
            Debounce = null;
            Cts = null;
        }
    }
}
