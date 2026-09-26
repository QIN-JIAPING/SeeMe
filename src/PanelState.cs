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
    /// <summary>anydoc-wasm 转换请求状态（docx/pptx/xlsx → Markdown，PDF → 文本层提取）。</summary>
    public sealed class AnyDocPending
    {
        public string Token = "";   // 每请求唯一令牌，桥接页回传校验
        public string File = "";    // 发起请求时的文件路径（防跨文件过期结果）
        public string Ext = "";     // 文件扩展名（回退时决定走哪个 FileConverter 方法）
        public bool PdfExtract;      // PDF 文本层提取模式：结果只写缓存，不渲染
        public System.Threading.Tasks.TaskCompletionSource<bool>? ExtractTcs; // 提取完成信号（RenderPdf 等待用）
    }

    public class PanelState : IDisposable
    {
        public string? CurrentFile;
        public string? FileDir;
        public FileSystemWatcher? Watcher;
        public DispatcherTimer? Debounce;
        public double LastScrollY;
        /// <summary>
        /// 最近一次上报的阅读百分比 0–100（前端按 scrollHeight 计算）。
        /// 与 <see cref="LastScrollY"/> 的区别：Y 是还原用的像素值，本字段是给人看的进度。
        /// 内容不足一屏时前端上报 100。
        /// </summary>
        public double LastReadPercent;
        /// <summary>
        /// 当前可见的最上方标题 id（前端 IntersectionObserver 上报）。
        /// 用途：大纲面板/浮动大纲的高亮跟随。
        /// </summary>
        public string CurrentHeadingId = "";
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
        // anydoc-wasm 转换请求：令牌 + 源文件，防止过期/跨文件消息误渲染（OnWebMessage 校验用）
        public AnyDocPending? AnyDocPending;
        // 内联编辑：编辑模式标记、textarea 当前内容、docx 编辑用的 anydoc 原始 Markdown、原文件编码标志
        public bool EditMode;
        public string EditSource = "";
        public string? DocxMarkdown;
        public int EditEncoding;   // 0=UTF-8无BOM 1=UTF-8-BOM 2=UTF-16LE 3=UTF-16BE 4=GB18030
        // PDF 文本层：anydoc 提取结果（供内容搜索/统计），null=未提取/未知
        public string PdfText = "";
        public bool? PdfTextLayer;
        // PDF 文本视图渲染令牌：页面重建完成后上报大纲时回传，防止过期页面的大纲覆盖新文件
        public string? PdfOutlineToken;
        // 远程图片授权：true=允许本面板加载文档中的远程图片（CSP img-src 放行 https:）
        // 由 MainWindow 在渲染前按 AppSettings 持久化授权 + 用户确认结果写入
        public bool AllowRemoteImages;
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
