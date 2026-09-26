// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using Sentry;
using Velopack;
using Velopack.Sources;

namespace SeeMe
{
    public partial class App : Application
    {
        private int _windowCount;

        private System.Windows.Forms.NotifyIcon? _trayIcon;

        /// <summary>全局单例主题管理器：App 启动时初始化，所有窗口共享同一实例（多窗口主题同步）。</summary>
        internal static ThemeManager GlobalTheme = null!;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            // 崩溃日志必须最先注册：越早越能覆盖启动阶段的异常。
            RegisterCrashLogging();
            InitSentry();
            InitAutoUpdate();
            // 应用级单例主题：所有窗口共享同一 ThemeManager，任一窗口切换主题，
            // 其余窗口通过订阅同一实例的 Changed 事件跟随（见 MainWindow.OnLoaded）。
            GlobalTheme = new ThemeManager();
            GlobalTheme.Initialize();

            InitTray();

            // 清理过期的 PDF 文本层缓存（防磁盘无限增长 + 残留敏感 PDF 明文）
            try { PdfTextCache.CleanupExpired(); } catch { }

            var file = e.Args.Length > 0 && File.Exists(e.Args[0]) ? e.Args[0] : null;
            CreateWindow(file);
        }

        /// <summary>
        /// 自动更新（Velopack）：VELOPACK_FEED 环境变量指向更新源（如 GitHub Releases 目录），
        /// 未配置则跳过；后台检查，发现新版本弹窗询问，下载完成后应用并重启。
        /// VelopackApp.Build().Run() 处理安装/更新后的首次启动钩子（必须最先调用）。
        /// </summary>
        private void InitAutoUpdate()
        {
            try { VelopackApp.Build().Run(); } catch { }

            var feed = Environment.GetEnvironmentVariable("VELOPACK_FEED");
            if (string.IsNullOrWhiteSpace(feed)) return;

            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    var mgr = new UpdateManager(new SimpleWebSource(feed));
                    if (!mgr.IsInstalled) return;
                    var cur = mgr.CurrentVersion;
                    if (cur == null) return;
                    var idx = await mgr.CheckForUpdatesAsync();
                    var target = idx?.TargetFullRelease;
                    if (target == null || target.Version <= cur) return;

                    var ver = target.Version.ToString();
                    var yes = await Dispatcher.InvokeAsync(() =>
                        MessageBox.Show($"发现新版本 SeeMe {ver}\n\n当前版本：{cur}\n是否立即下载更新？",
                            "SeeMe 更新", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);
                    if (!yes) return;

                    await mgr.DownloadUpdatesAsync(idx!, null);
                    mgr.ApplyUpdatesAndExit(target);
                }
                catch { /* 更新失败静默，不影响使用 */ }
            });
        }

        /// <summary>
        /// 崩溃上报（Sentry）：仅在环境变量 SEEME_SENTRY_DSN 存在时启用，
        /// 不硬编码 DSN，未配置则零开销零网络。捕获 WPF 派发线程未处理异常。
        /// </summary>
        /// <remarks>
        /// 注意：本地崩溃落盘（<see cref="SeeMeLog.Error(string, Exception)"/>）**不在此方法内**注册，
        /// 而是无条件注册于 <see cref="RegisterCrashLogging"/>——否则未配置 Sentry 的用户
        /// 崩溃时将不留任何本地痕迹，无法排查。
        /// </remarks>
        private void InitSentry()
        {
            var dsn = Environment.GetEnvironmentVariable("SEEME_SENTRY_DSN");
            if (string.IsNullOrWhiteSpace(dsn)) return;
            try
            {
                var ver = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
                SentrySdk.Init(o =>
                {
                    o.Dsn = dsn;
                    o.Environment = "production";
                    o.Release = $"seeme@{ver}";
                    o.TracesSampleRate = 0.0;   // 只上报错误，不采集性能轨迹
                    o.SendDefaultPii = false;   // 不上报个人信息
                    o.MaxBreadcrumbs = 50;
                });
            }
            catch
            {
                // Sentry 初始化失败不影响主程序
            }
        }

        /// <summary>
        /// 本地崩溃日志：<b>无条件注册</b>（与 Sentry 无关）。
        /// 未捕获异常落盘到 %LOCALAPPDATA%\SeeMe\logs\seeme.log，
        /// 保证即使没有配置 Sentry，用户报障时也有据可查。
        /// 覆盖三条路径：UI 线程（DispatcherUnhandledException）、后台任务
        /// （TaskScheduler.UnobservedTaskException）、非 UI 线程未捕获（AppDomain.UnhandledException）。
        /// </summary>
        private void RegisterCrashLogging()
        {
            DispatcherUnhandledException += (_, args) =>
            {
                try { SeeMeLog.Error("Unhandled(UI)", args.Exception); } catch { }
                try { SentrySdk.CaptureException(args.Exception); } catch { }   // 未启用 Sentry 时为 no-op
            };

            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                try { SeeMeLog.Error("Unhandled(Task)", args.Exception); } catch { }
                args.SetObserved();   // 标记已观察，避免进程被终结
            };

            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                if (args.ExceptionObject is Exception ex)
                {
                    try { SeeMeLog.Error("Unhandled(AppDomain)", ex); } catch { }
                }
            };
        }

        protected override void OnExit(ExitEventArgs e)
        {
            DisposeTray();
            base.OnExit(e);
        }

        // ──────────────── 系统托盘 ────────────────

        private void InitTray()
        {
            try
            {
                _trayIcon = new System.Windows.Forms.NotifyIcon
                {
                    Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? ""),
                    Text = "SeeMe",
                    Visible = true,
                };
                _trayIcon.DoubleClick += (_, _) => ShowMainWindow();

                var menu = new System.Windows.Forms.ContextMenuStrip();
                menu.Items.Add("显示主窗口", null, (_, _) => ShowMainWindow());
                menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                menu.Items.Add("退出 SeeMe", null, (_, _) => Shutdown());
                _trayIcon.ContextMenuStrip = menu;
            }
            catch (Exception ex)
            {
                // 托盘初始化失败 → 最小化到托盘后窗口可能找不回来。留档。
                SeeMeLog.Error("InitTray", ex);
            }
        }

        private void DisposeTray()
        {
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                // ExtractAssociatedIcon 返回的是新的 GDI+ Icon 对象，须显式释放，
                // 否则每个窗口生命周期泄漏一个图标句柄。
                var icon = _trayIcon.Icon;
                _trayIcon.Icon = null;
                try { icon?.Dispose(); } catch { }
                _trayIcon.Dispose();
                _trayIcon = null;
            }
        }

        private void ShowMainWindow()
        {
            foreach (Window w in Windows)
            {
                w.Show();
                if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
                w.Activate();
            }
        }

        /// <summary>窗口最小化到托盘后提示（仅当设置开启托盘行为时调用）。</summary>
        public void NotifyMinimizedToTray()
        {
            try
            {
                _trayIcon?.ShowBalloonTip(2000, "SeeMe", "已最小化到系统托盘，双击图标恢复窗口",
                    System.Windows.Forms.ToolTipIcon.Info);
            }
            catch { }
        }

        public void CreateWindow(string? filePath = null)
        {
            _windowCount++;
            var win = new MainWindow();
            win.Closed += (_, _) =>
            {
                _windowCount--;
                if (_windowCount <= 0)
                    Shutdown();
            };
            win.Show();
            // 不要在这里调用 win.OpenFile(filePath)：
            // _app 字段在 MainWindow.OnLoaded 中才初始化，而 Show() 返回时 OnLoaded 尚未执行
            // （WPF 的 Loaded 事件异步派发），此时调用会触发 NullReferenceException —— 双击 .md 启动即崩溃。
            // 命令行参数的文件已由 OnLoaded 读取 Environment.GetCommandLineArgs() 自行打开，这里无需重复处理。
        }
    }
}
