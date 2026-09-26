// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace SeeMe
{
    public partial class MainWindow
    {
        // ═══════ 文件职责：WebView2 导航守卫 / 进程崩溃恢复 / 外链安全 ═══════
        // 由 MainWindow.xaml.cs 拆出（2026-09-10）。安全关键路径集中于此：
        // · OnNavigationStarting —— 取消一切非 about:/data: 导航，file: 一律拒绝（防本地文件泄露）
        // · GuardDocumentHost   —— 文档虚拟主机白名单：只放行与当前文件同名的请求，其余 403（fail-closed）
        // · OpenInSystemBrowser —— scheme 白名单（https/http/mailto/ftp），其余拒绝（含 javascript:）
        // 改这些方法前请先读注释里记录的威胁模型。


        private void OnNavigationStarting(WebView2 view, CoreWebView2NavigationStartingEventArgs e)
        {
            var uri = e.Uri;
            if (string.IsNullOrEmpty(uri)) return;
            // NavigateToString 内部导航（about:blank）与 data: 页面放行
            if (uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
                || uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return;
            if (uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                // 收窄：本地文件导航一律取消。应用内渲染全部走 NavigateToString / 虚拟主机，
                // 不依赖 file: 导航；放行会让恶意文档借 WebView 显示任意本地文件内容。
                e.Cancel = true;
                LogWarn("Blocked file: navigation: " + uri);
                return;
            }
            e.Cancel = true;
            OpenInSystemBrowser(uri);
        }

        /// <summary>
        /// 收窄文档虚拟主机（docfiles.example / pdffiles.example）的暴露面：
        /// 目录映射本身无法限制到单文件，这里用 WebResourceRequested 做白名单——
        /// 仅放行与当前打开文件同名的请求，其余一律 403。
        /// 这样即使页面（恶意文档注入的 JS）发起 fetch，也只能取到当前文档本身，
        /// 无法读取同目录的其他文件。
        /// </summary>
        private void GuardDocumentHost(WebView2 view, Func<string?> allowedFile)
        {
            var core = view.CoreWebView2;
            core.AddWebResourceRequestedFilter("https://docfiles.example/*", CoreWebView2WebResourceContext.All);
            core.AddWebResourceRequestedFilter("https://pdffiles.example/*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, ea) =>
            {
                try
                {
                    var uri = new Uri(ea.Request.Uri);
                    // AbsolutePath 对中文/空格文件名是 %XX 编码形式，先解码再取文件名，
                    // 否则中文文件名请求会被误杀（403）。
                    var reqName = Path.GetFileName(System.Uri.UnescapeDataString(uri.AbsolutePath));
                    var allow = allowedFile();
                    var allowName = string.IsNullOrEmpty(allow) ? "" : Path.GetFileName(allow);
                    if (string.IsNullOrEmpty(reqName)
                        || !string.Equals(reqName, allowName, StringComparison.OrdinalIgnoreCase))
                    {
                        ea.Response = core.Environment.CreateWebResourceResponse(
                            null, 403, "Forbidden", "Content-Type: text/plain");
                        LogWarn("Blocked doc-host resource: " + ea.Request.Uri);
                    }
                }
                catch (Exception ex)
                {
                    // fail-closed：URI 解析失败视为不可信请求一律 403。
                    // 正常渲染的资源请求恒为良构绝对 URL（应用自己拼的虚拟主机地址），
                    // 走到这里只会是畸形/伪造请求，拒绝不会误伤真实渲染。
                    ea.Response = core.Environment.CreateWebResourceResponse(
                        null, 403, "Forbidden", "Content-Type: text/plain");
                    LogWarn("Blocked malformed doc-host request: " + ex.Message);
                }
            };
        }

        /// <summary>WebView2 渲染进程崩溃恢复：记录 + 自动重载当前文件；连续崩溃 >= 3 次（5 秒窗口）停止重载。</summary>
        private void OnProcessFailed(PanelState state, CoreWebView2ProcessFailedEventArgs e)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnProcessFailed(state, e)));
                return;
            }
            try
            {
                LogErr($"[WebView] process failed: kind={e.ProcessFailedKind} reason={e.Reason} exit={e.ExitCode}");
                var file = state.CurrentFile;
                if (string.IsNullOrEmpty(file)) return;

                // 节流：同一面板 5 秒内连续崩溃才累计，跨时段自动清零
                var now = DateTime.UtcNow;
                state.CrashCount = now - state.LastCrashUtc <= TimeSpan.FromSeconds(5)
                    ? state.CrashCount + 1 : 1;
                state.LastCrashUtc = now;

                if (state.CrashCount >= 3)
                {
                    LogWarn("WebView crashed " + state.CrashCount + " times, auto-reload stopped: " + Path.GetFileName(file));
                    StatusText.Text = "预览多次崩溃，已停止自动恢复，请重新打开文件或重启应用";
                    return;
                }

                // 渲染进程重建需要时间，延迟到下一帧再重载
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (string.IsNullOrEmpty(state.CurrentFile)) return;
                        _ = ReloadFileAsync(state, true, state.ResetCts());
                        StatusText.Text = "预览已崩溃，正在自动恢复: " + Path.GetFileName(state.CurrentFile);
                    }
                    catch (Exception ex) { LogErr("Auto-reload after crash: " + ex.Message); }
                }), DispatcherPriority.Background);
            }
            catch (Exception ex) { LogErr("OnProcessFailed: " + ex.Message); }
        }

        private void OnNewWindowRequested(CoreWebView2NewWindowRequestedEventArgs e)
        {
            e.Handled = true;
            if (!string.IsNullOrEmpty(e.Uri))
                OpenInSystemBrowser(e.Uri);
        }

        private static void OpenInSystemBrowser(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            try
            {
                var uri = new Uri(url);
                var scheme = uri.Scheme.ToLowerInvariant();
                var allowed = new[] { "https", "http", "mailto", "ftp" };
                if (!allowed.Contains(scheme))
                {
                    LogErr("Blocked URL with disallowed scheme: " + scheme);
                    return;
                }
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                LogErr("Open link failed: " + ex.Message);
            }
        }
    }
}
