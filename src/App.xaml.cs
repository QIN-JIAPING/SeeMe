// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Windows;

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
                System.Diagnostics.Debug.WriteLine("[SeeMe] InitTray: " + ex.Message);
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
