// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace SeeMe
{
    public partial class SettingsDialog : Window
    {
        private readonly MainWindow _owner;
        private bool _loading = true;

        public SettingsDialog(MainWindow owner)
        {
            InitializeComponent();
            _owner = owner;
            Loaded += (_, _) =>
            {
                LoadValues();
                ShowSection(0); // 显式保证默认分区可见（初始化期的早退不会留下空白）
            };
        }

        private void ShowSection(int idx)
        {
            Sec0.Visibility = idx == 0 ? Visibility.Visible : Visibility.Collapsed;
            Sec1.Visibility = idx == 1 ? Visibility.Visible : Visibility.Collapsed;
            Sec3.Visibility = idx == 3 ? Visibility.Visible : Visibility.Collapsed;
            Sec4.Visibility = idx == 4 ? Visibility.Visible : Visibility.Collapsed;
            Sec6.Visibility = idx == 6 ? Visibility.Visible : Visibility.Collapsed;
            Sec7.Visibility = idx == 7 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ──────────────── 导航切换 ────────────────

        private void OnNavChanged(object sender, SelectionChangedEventArgs e)
        {
            // XAML 文档顺序中 Sec0-Sec7 在 NavList 之后创建，ListBox 初始化期 IsSelected 触发的事件
            // 可能早于字段赋值 → 空引用守卫（此时不切换，Loaded 里会显式显示默认分区）。
            if (Sec0 == null) return;
            if (NavList.SelectedItem is ListBoxItem li && int.TryParse(li.Tag?.ToString(), out var idx))
            {
                ShowSection(idx);
            }
        }

        // ──────────────── 加载当前值 ────────────────

        private void LoadValues()
        {
            _loading = true;
            try
            {
                // 通用
                StartupOpenBox.SelectedIndex = AppSettings.Get(AppSettings.StartupOpenKey, "last") == "last" ? 0 : 1;
                StartupSplitBox.SelectedIndex = AppSettings.Get(AppSettings.StartupSplitKey, false) ? 1 : 0;
                var hs = AppSettings.Get(AppSettings.HistorySizeKey, 15);
                HistorySizeBox.SelectedIndex = hs switch { 10 => 0, 20 => 2, 50 => 3, _ => 1 };
                var closeB = AppSettings.Get(AppSettings.CloseBehaviorKey, "exit");
                CloseBehaviorBox.SelectedIndex = closeB == "tray" ? 1 : closeB == "ask" ? 2 : 0;
                var autoOn = AppSettings.Get(AppSettings.AutoSaveKey, true);
                var autoDelay = AppSettings.Get(AppSettings.AutoSaveDelayKey, 10);
                AutoSaveBehaviorBox.SelectedIndex = !autoOn ? 0 : autoDelay switch { 5 => 1, 30 => 3, _ => 2 };
                SingleInstanceBox.SelectedIndex = AppSettings.Get(AppSettings.SingleInstanceKey, true) ? 0 : 1;
                RelayOpenModeBox.SelectedIndex = AppSettings.Get(AppSettings.RelayOpenModeKey, "window") == "current" ? 1 : 0;

                // 外观
                var mode = AppSettings.Get(AppSettings.ThemeModeKey, "light");
                if (mode == "dark") ThemeDark.IsChecked = true;
                else if (mode == "system") ThemeSystem.IsChecked = true;
                else ThemeLight.IsChecked = true;
                var zoom = AppSettings.Get(AppSettings.DefaultZoomKey, 1.0);
                DefaultZoomBox.SelectedIndex = zoom >= 1.5 ? 2 : zoom >= 1.25 ? 1 : 0;
                InfoPanelBox.SelectedIndex = AppSettings.Get(AppSettings.InfoPanelVisibleKey, true) ? 0 : 1;
                var accent = AppSettings.Get(AppSettings.AccentKey, "indigo");
                if (accent == "blue") AccentBlue.IsChecked = true;
                else if (accent == "green") AccentGreen.IsChecked = true;
                else AccentIndigo.IsChecked = true;
                AnimationsBox.SelectedIndex = _owner.ThemeAnimationsEnabled ? 1 : 0;
                var fs = AppSettings.Get(AppSettings.FontSizeKey, 14);
                FontSizeBox.SelectedIndex = fs switch { 13 => 0, 16 => 2, 18 => 3, _ => 1 };
                var lh = AppSettings.Get(AppSettings.LineHeightKey, 1.65);
                RowHeightBox.SelectedIndex = lh >= 2.0 ? 2 : lh <= 1.4 ? 0 : 1;

                // PDF 导出
                PdfIncludeTocBox.SelectedIndex = AppSettings.Get(AppSettings.PdfIncludeTocKey, true) ? 1 : 0;
                PdfPageNumberBox.SelectedIndex = AppSettings.Get(AppSettings.PdfPageNumberKey, true) ? 1 : 0;
                PdfWatermarkBox.SelectedIndex = AppSettings.Get(AppSettings.PdfWatermarkKey, false) ? 1 : 0;
                PdfWatermarkTextBox.Text = AppSettings.Get(AppSettings.PdfWatermarkTextKey, "");

                // 快捷键
                FillKeymap();

                // 导出
                ImageFormatBox.SelectedIndex = AppSettings.Get(AppSettings.ImageFormatKey, "png") == "jpg" ? 1 : 0;
                ImageScaleBox.SelectedIndex = AppSettings.Get(AppSettings.ImageScaleKey, 1) >= 2 ? 1 : 0;
                var fixedMode = AppSettings.Get(AppSettings.ExportPathModeKey, "ask") == "fixed";
                ExportPathModeBox.SelectedIndex = fixedMode ? 1 : 0;
                ExportPathBox.Text = AppSettings.Get(AppSettings.ExportPathKey, "");
                ExportPathBox.IsEnabled = fixedMode;

                // 高级
                DevToolsBox.SelectedIndex = AppSettings.Get(AppSettings.DevToolsKey, false) ? 1 : 0;

                // 高级 / 关于：数据目录经 StoragePaths 解析（便携模式下指向程序目录下的 data\，
                // 而非 AppData）—— 这里显示的是**真实生效**的目录，与各存储类一致。
                var dataDir = StoragePaths.Root;
                DataDirBox.Text = dataDir;
                AboutDataDir.Text = dataDir;
                AboutInstallDir.Text = AppDomain.CurrentDomain.BaseDirectory;
                RefreshPortableUi();
                var ver = typeof(SettingsDialog).Assembly.GetName().Version;
                AboutVersion.Text = "SeeMe " + (ver?.ToString(3) ?? "1.0.1") + " · 多格式文档查看器";
                RefreshCacheInfo();
                RefreshSearchIndexInfo();
                FillAssocGroups();
                RefreshAssocStatus();
                RefreshDiagnostics();
            }
            catch (Exception ex)
            {
                SeeMeLog.Info("SettingsDialog.LoadValues", ex.Message);
            }
            finally
            {
                _loading = false;
            }
        }

        /// <summary>
        /// 生成快捷键列表：每条命令一行「分类 · 命令名 ……… 当前手势」。
        ///
        /// <para><b>列表来自 <see cref="ShortcutRegistry.All"/>，不再是写死的字符串数组</b> ——
        /// 旧实现硬编码 9 条文本，与 <c>OnWindowKeyDown</c> 里的实际分支各写各的，
        /// 一旦不一致就会"设置页说的和程序做的不一样"。现在四处（列表 / 分派 / 默认值 / 存储）
        /// 共用同一张命令表。已自定义的条目用强调色 + 半粗体标出。</para>
        /// </summary>
        private void FillKeymap()
        {
            if (KeymapList == null) return;
            KeymapList.Items.Clear();

            var fg = TryFindResource("TextBodyBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gray;
            var sec = TryFindResource("TextSecondaryBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gray;
            var accent = TryFindResource("AccentBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.SteelBlue;

            foreach (var cmd in ShortcutRegistry.All)
            {
                var gesture = ShortcutRegistry.GetGesture(cmd.Id);
                var isCustom = gesture != cmd.DefaultGesture;

                var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var label = new TextBlock
                {
                    Text = cmd.Category + " · " + cmd.Label,
                    FontSize = 12,
                    Foreground = fg,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(label, 0);
                grid.Children.Add(label);

                var gestureText = new TextBlock
                {
                    Text = string.IsNullOrEmpty(gesture) ? "未绑定" : ShortcutRegistry.ToDisplay(gesture),
                    FontSize = 12,
                    FontWeight = isCustom ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = isCustom ? accent : sec,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(12, 0, 0, 0),
                };
                Grid.SetColumn(gestureText, 1);
                grid.Children.Add(gestureText);

                KeymapList.Items.Add(new ListBoxItem
                {
                    Content = grid,
                    Tag = cmd.Id,
                    Padding = new Thickness(4, 2, 4, 2),
                    ToolTip = isCustom
                        ? "已自定义（默认 " + ShortcutRegistry.ToDisplay(cmd.DefaultGesture) + "）"
                        : "当前为默认值",
                });
            }
        }

        private void OnKeymapEdit(object sender, RoutedEventArgs e) => EditSelectedShortcut();

        private void OnKeymapDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
            => EditSelectedShortcut();

        /// <summary>为列表中选中的命令打开录入窗口并落盘。</summary>
        private void EditSelectedShortcut()
        {
            if (KeymapList?.SelectedItem is not ListBoxItem { Tag: string id }) return;
            var cmd = ShortcutRegistry.Find(id);
            if (cmd == null) return;

            var dlg = new ShortcutCaptureWindow(cmd, ShortcutRegistry.GetGesture(id)) { Owner = this };
            if (dlg.ShowDialog() != true) return;

            // 空串 = 用户点了「清除绑定」→ 存哨兵值（与"恢复默认"语义不同，见 ShortcutRegistry）。
            ShortcutRegistry.SetGesture(id,
                string.IsNullOrEmpty(dlg.ResultGesture) ? ShortcutRegistry.UnboundMarker : dlg.ResultGesture);

            FillKeymap();
            KeymapHint.Text = string.IsNullOrEmpty(dlg.ResultGesture)
                ? "已取消「" + cmd.Label + "」的快捷键绑定"
                : "「" + cmd.Label + "」→ " + ShortcutRegistry.ToDisplay(dlg.ResultGesture);
        }

        private void OnKeymapResetAll(object sender, RoutedEventArgs e)
        {
            ShortcutRegistry.ResetAll();
            FillKeymap();
            KeymapHint.Text = "已恢复全部默认快捷键";
        }

        // ──────────────── 通用 ────────────────

        private void OnAccentChanged(object sender, RoutedEventArgs e)
        {
            if (_loading || sender is not RadioButton rb || rb.IsChecked != true) return;
            var accent = rb == AccentIndigo ? "indigo" : rb == AccentBlue ? "blue" : "green";
            AppSettings.Set(AppSettings.AccentKey, accent);
            _owner.ApplyAccentNow(accent);
        }

        private void OnAnimationsChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || AnimationsBox.SelectedIndex < 0) return;
            var on = AnimationsBox.SelectedIndex == 1;
            AppSettings.Set(AppSettings.AnimationsKey, on);
            _owner.ApplyAnimationsNow(on);
        }

        private void OnDevToolsChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || DevToolsBox.SelectedIndex < 0) return;
            AppSettings.Set(AppSettings.DevToolsKey, DevToolsBox.SelectedIndex == 1);
        }

        // ──────────────── 便携模式 ────────────────

        /// <summary>
        /// 刷新便携模式区块：下拉选中态 + 不安全的场景警告。
        ///
        /// <para><b>为什么要做可写性探测并在 UI 上警告</b>：程序装在 <c>C:\Program Files\</c> 时
        /// <c>portable.flag</c> 能建，但写 <c>data\</c> 会被 UAC 虚化重定向到
        /// <c>%LOCALAPPDATA%\VirtualStore\</c> —— 用户以为"便携"了，实际数据仍散落在系统盘，
        /// 换台机器就全丢。这种"静默失败"必须在设置页明说。</para>
        /// </summary>
        private void RefreshPortableUi()
        {
            PortableBox.SelectedIndex = StoragePaths.IsPortable ? 1 : 0;
            UpdatePortableWarning();
        }

        private void UpdatePortableWarning()
        {
            if (PortableWarnText == null) return;
            if (!StoragePaths.IsPortable)
            {
                PortableWarnText.Visibility = Visibility.Collapsed;
                return;
            }
            if (StoragePaths.ProbePortableWritable())
            {
                PortableWarnText.Visibility = Visibility.Collapsed;
                return;
            }
            PortableWarnText.Text = "⚠ 程序目录不可写，便携数据将无法保存。"
                + "请把程序移到用户可写的目录（如 D 盘），或改回关闭便携模式。";
            PortableWarnText.Visibility = Visibility.Visible;
        }

        private void OnPortableChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || PortableBox.SelectedIndex < 0) return;
            var enable = PortableBox.SelectedIndex == 1;

            // 开启前先确认目标目录真的可写，避免用户开了却存不下数据
            if (enable)
            {
                var probeDir = Path.Combine(StoragePaths.AppDirectory, StoragePaths.PortableDataDirName);
                if (!IsDirectoryWritable(probeDir))
                {
                    MessageBox.Show(
                        "程序所在目录不可写（常见于安装在 C:\\Program Files 的情况）。\n\n"
                        + "便携模式的数据需要保存在程序目录下的 data\\ 里，请先把程序移动到"
                        + "用户可写的位置（例如 D 盘），或改用普通模式。",
                        "无法开启便携模式", MessageBoxButton.OK, MessageBoxImage.Warning);
                    _loading = true;
                    PortableBox.SelectedIndex = 0;
                    _loading = false;
                    return;
                }
            }

            if (!StoragePaths.SetPortable(enable))
            {
                MessageBox.Show("切换便携模式失败：无法写入程序目录。", "操作失败",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                _loading = true;
                PortableBox.SelectedIndex = StoragePaths.IsPortable ? 1 : 0;
                _loading = false;
                return;
            }

            var dataDir = StoragePaths.Root;
            DataDirBox.Text = dataDir;
            AboutDataDir.Text = dataDir;
            UpdatePortableWarning();

            var tip = enable
                ? "已开启便携模式，数据目录：" + dataDir
                  + "\n\n⚠ 原有数据不会自动迁移。若需要保留，请点「迁移现有数据」。\n重启应用后完全生效。"
                : "已关闭便携模式，数据将存回：" + dataDir + "\n重启应用后完全生效。";
            MessageBox.Show(tip, "便携模式", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>把 AppData 里的既有数据复制进便携目录（只复制不删除源）。</summary>
        private void OnPortableMigrate(object sender, RoutedEventArgs e)
        {
            if (!StoragePaths.IsPortable)
            {
                MessageBox.Show("请先开启便携模式，再执行迁移。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var n = StoragePaths.CopyAppDataToPortable();
            MessageBox.Show(n > 0
                    ? $"已迁移 {n} 个文件到：\n{Path.Combine(StoragePaths.AppDirectory, StoragePaths.PortableDataDirName)}"
                      + "\n\n原 AppData 目录中的文件仍然保留，确认无误后可自行删除。"
                    : "没有可迁移的文件（AppData 中未找到 SeeMe 数据，或便携目录中已存在同名文件）。",
                "迁移完成", MessageBoxButton.OK, MessageBoxImage.Information);
            RefreshPortableUi();
        }

        /// <summary>
        /// 探测目录能否真实写入（先建目录再试写一个临时文件）。
        /// 不用 <c>Directory.Exists</c> + 权限位判断 —— UAC 虚化会让"看起来能写"的目录实际写进 VirtualStore。
        /// </summary>
        private static bool IsDirectoryWritable(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                var probe = Path.Combine(dir, ".write-probe");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        private void OnFontSizeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || FontSizeBox.SelectedIndex < 0) return;
            var v = FontSizeBox.SelectedIndex switch { 0 => 13, 2 => 16, 3 => 18, _ => 14 };
            AppSettings.Set(AppSettings.FontSizeKey, v);
            _owner.ApplyFontSettingsNow();
        }

        private void OnRowHeightChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || RowHeightBox.SelectedIndex < 0) return;
            var v = RowHeightBox.SelectedIndex switch { 0 => 1.4, 2 => 2.0, _ => 1.65 };
            AppSettings.Set(AppSettings.LineHeightKey, v);
            _owner.ApplyFontSettingsNow();
        }

        /// <summary>计算并显示 WebView2 缓存目录大小（后台线程，避免大目录卡 UI）。</summary>
        private void RefreshCacheInfo()
        {
            // ⚠️ 必须用 StoragePaths.WebView2DataDir（= MainWindow 传给 CoreWebView2Environment 的那个目录）。
            // 曾硬编码 exe 旁的 "SeeMe.exe.WebView2"：该目录在显式指定 UserDataFolder 后根本不会被创建，
            // 于是这里永远显示"未生成缓存"，而真实缓存（数百 MB）从未被清掉。
            var cache = StoragePaths.WebView2DataDir;
            if (!Directory.Exists(cache))
            {
                CacheBox.Text = "（未生成 WebView2 缓存）";
                return;
            }
            CacheBox.Text = cache;
            System.Threading.Tasks.Task.Run(() =>
            {
                long bytes = 0;
                try
                {
                    foreach (var f in Directory.EnumerateFiles(cache, "*", SearchOption.AllDirectories))
                    {
                        try { bytes += new FileInfo(f).Length; } catch { }
                    }
                }
                catch { }
                var text = bytes >= 1 << 20 ? (bytes / 1048576.0).ToString("0.0") + " MB"
                         : bytes >= 1024 ? (bytes / 1024.0).ToString("0") + " KB" : bytes + " B";
                Dispatcher.BeginInvoke(() => CacheBox.Text = cache + "（" + text + "）");
            });
        }

        private void OnClearCache(object sender, RoutedEventArgs e)
        {
            // 与 RefreshCacheInfo 同源，见其注释：不能用 exe 旁的旧路径
            var cache = StoragePaths.WebView2DataDir;
            if (!Directory.Exists(cache))
            {
                CacheBox.Text = "（未生成 WebView2 缓存）";
                return;
            }
            try
            {
                foreach (var f in Directory.EnumerateFiles(cache, "*", SearchOption.AllDirectories))
                {
                    try { File.Delete(f); } catch { }
                }
                foreach (var d in Directory.EnumerateDirectories(cache, "*", SearchOption.AllDirectories)
                                 .OrderByDescending(x => x.Length))
                {
                    try { if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch { }
                }
                StatusBarHint("缓存已清理（部分占用文件需重启后彻底删除）");
            }
            catch (Exception ex)
            {
                StatusBarHint("清理失败: " + ex.Message);
            }
            RefreshCacheInfo();
        }

        private void StatusBarHint(string msg) => SeeMeLog.Info(msg);

        // ──────────────── 诊断 ────────────────

        /// <summary>
        /// 刷新诊断区块：把日志目录（<see cref="StoragePaths.LogsDir"/>）显示出来。
        ///
        /// <para><b>为什么需要这一块</b>：日志是全项目唯一的排障入口，但此前**没有任何界面**
        /// 告诉用户它在哪里 —— 用户报障时开发者只能远程猜路径，或让用户手打
        /// <c>%LOCALAPPDATA%\SeeMe\logs</c>。这里把"路径 + 目录入口 + 一键复制"集中起来。</para>
        /// </summary>
        private void RefreshDiagnostics()
        {
            if (LogDirBox == null) return;
            LogDirBox.Text = StoragePaths.LogsDir;
        }

        private void OnOpenLogDir(object sender, RoutedEventArgs e) => OpenDirectory(StoragePaths.LogsDir);

        private void OnOpenDataDir(object sender, RoutedEventArgs e) => OpenDirectory(StoragePaths.Root);

        /// <summary>用资源管理器打开目录（目录不存在则先建，避免打开失败无反馈）。</summary>
        private static void OpenDirectory(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
            }
            catch (Exception ex) { SeeMeLog.Error("OpenDirectory", ex); }
        }

        /// <summary>WebView2 运行时版本；未安装运行时返回原因（这正是常见的"白屏"根因）。</summary>
        private static string WebView2Version()
        {
            try
            {
                return Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString() ?? "未知";
            }
            catch (Exception ex) { return "不可用（" + ex.GetType().Name + "）"; }
        }

        /// <summary>
        /// 组装诊断信息 → **脱敏** → 弹出预览让用户确认 → 确认后才写剪贴板。
        ///
        /// <para><b>为什么改成"先预览"</b>：原先点一下就直接进剪贴板，用户看不到自己要发出去什么。
        /// 文本里有数据目录、安装位置与日志尾部，天然带本机用户名与用户文件路径。
        /// 「看得见再决定」才是真正的护栏，脱敏（<see cref="DiagnosticInfo.Redact"/>）只是把默认
        /// 文本降一档敏感度的纵深措施。</para>
        ///
        /// <para><b>日志改为可选</b>：摘要部分（版本/系统/WebView2）无身份信息，日志尾部才有。
        /// 让用户能只发摘要。</para>
        /// </summary>
        private void OnCopyDiagnostics(object sender, RoutedEventArgs e)
        {
            try
            {
                var includeLog = DiagIncludeLogBox?.IsChecked != false;
                var sb = new StringBuilder();
                var ver = typeof(SettingsDialog).Assembly.GetName().Version?.ToString() ?? "?";
                sb.AppendLine("===== SeeMe 诊断信息 =====");
                sb.AppendLine("生成时间   : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("版本       : " + ver);
                sb.AppendLine("系统       : " + Environment.OSVersion.VersionString
                              + "（" + (Environment.Is64BitProcess ? "64 位" : "32 位") + " 进程）");
                sb.AppendLine("运行时     : .NET " + Environment.Version);
                sb.AppendLine("数据目录   : " + StoragePaths.Root);
                sb.AppendLine("便携模式   : " + (StoragePaths.IsPortable ? "开启" : "关闭"));
                sb.AppendLine("安装位置   : " + AppDomain.CurrentDomain.BaseDirectory);
                sb.AppendLine("日志文件   : " + SeeMeLog.CurrentLogPath);
                sb.AppendLine("WebView2   : " + WebView2Version());
                if (includeLog)
                {
                    sb.AppendLine();
                    sb.AppendLine("----- 日志尾部（最近 " + DiagnosticInfo.LogTailLines + " 行）-----");
                    sb.AppendLine(DiagnosticInfo.ReadLogTail(SeeMeLog.CurrentLogPath, DiagnosticInfo.LogTailLines));
                }
                else
                {
                    sb.AppendLine();
                    sb.AppendLine("（已按用户选择略去日志尾部）");
                }

                // 脱敏：主目录 → %USERPROFILE%，Bearer / key=value 形态的凭据 → <redacted>
                var text = DiagnosticInfo.Redact(sb.ToString(),
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

                var dlg = new DiagnosticPreviewWindow(text, includeLog ? DiagnosticInfo.LogTailLines : 0)
                {
                    Owner = this
                };
                dlg.ShowDialog();

                if (DiagHint != null)
                    DiagHint.Text = dlg.Copied ? "已复制到剪贴板" : "已取消（未复制任何内容）";
            }
            catch (Exception ex)
            {
                if (DiagHint != null) DiagHint.Text = "复制失败：" + ex.Message;
                SeeMeLog.Error("CopyDiagnostics", ex);
            }
        }

        private void OnAutoSaveBehaviorChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || AutoSaveBehaviorBox.SelectedIndex < 0) return;
            var idx = AutoSaveBehaviorBox.SelectedIndex;
            AppSettings.Set(AppSettings.AutoSaveKey, idx > 0);
            var delay = idx switch { 1 => 5, 3 => 30, _ => 10 };
            AppSettings.Set(AppSettings.AutoSaveDelayKey, delay);
            _owner.ApplyAutoSaveSettingsNow(); // 正在编辑的面板即时生效
        }

        private void OnCloseBehaviorChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || CloseBehaviorBox.SelectedIndex < 0) return;
            var v = CloseBehaviorBox.SelectedIndex switch { 1 => "tray", 2 => "ask", _ => "exit" };
            AppSettings.Set(AppSettings.CloseBehaviorKey, v);
        }

        // ──────────────── 多开与文件接力 ────────────────

        private void OnSingleInstanceChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || SingleInstanceBox.SelectedIndex < 0) return;
            AppSettings.Set(AppSettings.SingleInstanceKey, SingleInstanceBox.SelectedIndex == 0);
        }

        private void OnRelayOpenModeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || RelayOpenModeBox.SelectedIndex < 0) return;
            AppSettings.Set(AppSettings.RelayOpenModeKey,
                RelayOpenModeBox.SelectedIndex == 1 ? "current" : "window");
        }

        private void OnStartupOpenChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || StartupOpenBox.SelectedIndex < 0) return;
            AppSettings.Set(AppSettings.StartupOpenKey, StartupOpenBox.SelectedIndex == 0 ? "last" : "none");
        }

        private void OnStartupSplitChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || StartupSplitBox.SelectedIndex < 0) return;
            AppSettings.Set(AppSettings.StartupSplitKey, StartupSplitBox.SelectedIndex == 1);
        }

        private void OnHistorySizeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || HistorySizeBox.SelectedIndex < 0) return;
            var n = HistorySizeBox.SelectedIndex switch { 0 => 10, 2 => 20, 3 => 50, _ => 15 };
            _owner.ApplyHistorySize(n);
        }

        // ──────────────── 全文检索索引 ────────────────

        /// <summary>
        /// 刷新索引区块：显示索引目录、已索引篇数与磁盘占用。
        ///
        /// <para><b>为什么要让用户看见规模</b>：内容搜索此前是个完全的黑盒 ——
        /// 索引建在哪、建了多少、占多大、坏了怎么恢复，界面上一个字都没有。
        /// 搜索"没结果"时用户无法区分「确实没有」与「索引坏了」。</para>
        /// </summary>
        private void RefreshSearchIndexInfo()
        {
            if (SearchIndexBox == null) return;
            try
            {
                var idx = _owner.SearchIndex;
                SearchIndexBox.Text = idx.IndexDirectory;
                var docs = idx.DocumentCount();
                var size = FormatBytes(idx.SizeBytes());
                SetSearchIndexHint(docs == 0
                    ? "索引尚未建立（首次在搜索框输入「>关键词」时自动建立）。"
                    : $"已索引 {docs} 篇 · 占用 {size}。索引按文件变化增量维护，一般无需手工重建。");
            }
            catch (Exception ex)
            {
                SetSearchIndexHint("读取索引信息失败：" + ex.Message);
                SeeMeLog.Error("RefreshSearchIndexInfo", ex);
            }
        }

        private static string FormatBytes(long bytes)
            => bytes >= 1 << 20 ? (bytes / 1048576.0).ToString("0.0") + " MB"
             : bytes >= 1024 ? (bytes / 1024.0).ToString("0") + " KB"
             : bytes + " B";

        /// <summary>
        /// 写索引区块的提示文本。
        ///
        /// <para><b>为什么要有这个小壳</b>：<see cref="OnRebuildSearchIndex"/> 在 <c>await</c> 之后写提示，
        /// 而编译器在 await 之后会重置字段的可空流状态（期间别的代码可能改动它）→ CS8602。
        /// 本项目的约定是**用显式守卫而不是 <c>!</c> 压制**（压制只关警告，不增加运行时保证）。</para>
        /// </summary>
        private void SetSearchIndexHint(string text)
        {
            if (SearchIndexHint != null) SearchIndexHint.Text = text;
        }

        private void OnClearSearchIndex(object sender, RoutedEventArgs e)
        {
            var ok = _owner.SearchIndex.Clear();
            RefreshSearchIndexInfo();
            SetSearchIndexHint(ok
                ? "索引已清空，下次内容搜索会重新建立。"
                : "清空失败：索引文件可能正被占用，请稍后重试。");
        }

        /// <summary>重建索引（清空 + 立即按当前历史全量建立）。建索引可能很慢，故是 async void 事件处理器。</summary>
        private async void OnRebuildSearchIndex(object sender, RoutedEventArgs e)
        {
            SetSearchIndexHint("正在重建索引…");
            try
            {
                var outcome = await _owner.RebuildSearchIndexAsync();
                RefreshSearchIndexInfo();
                SetSearchIndexHint($"重建完成：已索引 {outcome.IndexedFiles} 篇"
                                   + (outcome.Updated > 0 ? $"（本次写入 {outcome.Updated} 篇）" : "")
                                   + "。未提取到文本层的 PDF 会被跳过，打开一次后即可纳入。");
            }
            catch (Exception ex)
            {
                SetSearchIndexHint("重建失败：" + ex.Message);
                SeeMeLog.Error("RebuildSearchIndex", ex);
            }
        }

        // ──────────────── 文件关联 ────────────────

        /// <summary>
        /// 生成格式分组的勾选列表（勾选态来自注册表的**实际**状态，不是记忆中的状态）。
        /// 每次应用后重建，保证 UI 与注册表一致。
        /// </summary>
        private void FillAssocGroups()
        {
            if (AssocGroupsPanel == null) return;
            AssocGroupsPanel.Children.Clear();
            var fg = TryFindResource("TextBodyBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gray;
            foreach (var g in FileAssoc.Groups)
            {
                AssocGroupsPanel.Children.Add(new CheckBox
                {
                    Content = new TextBlock
                    {
                        Text = g.Label + "　" + string.Join(" ", g.Exts),
                        FontSize = 11,
                        Foreground = fg,
                    },
                    IsChecked = FileAssoc.IsDefault(g),
                    Margin = new Thickness(0, 2, 0, 2),
                    Tag = g,
                });
            }
        }

        /// <summary>刷新关联状态文本（已接管的格式 + 右键菜单两项）。</summary>
        private void RefreshAssocStatus()
        {
            if (AssocStatus == null) return;
            var labels = FileAssoc.DefaultGroupLabels();
            var parts = new List<string>
            {
                "已接管格式：" + (labels.Count == 0 ? "未设置" : string.Join(" / ", labels)),
                "右键菜单：" + (FileAssoc.IsContextMenuRegistered() ? "已注册" : "未注册")
            };
            AssocStatus.Text = string.Join("　·　", parts);
        }

        private void OnAssocApply(object sender, RoutedEventArgs e)
        {
            var selected = new List<FileAssoc.Group>();
            foreach (var child in AssocGroupsPanel.Children)
            {
                if (child is CheckBox { IsChecked: true, Tag: FileAssoc.Group g })
                    selected.Add(g);
            }

            var err = FileAssoc.ApplySelection(selected);
            FillAssocGroups();
            RefreshAssocStatus();
            if (err != null)
            {
                MessageBox.Show(this, "设置失败：" + err, "SeeMe 文件关联",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            MessageBox.Show(this,
                selected.Count == 0
                    ? "已取消勾选格式的默认关联（仅移除仍指向 SeeMe 的项，不影响其他程序）。"
                    : $"已将 {selected.Count} 类格式的默认打开方式设为 SeeMe。\n\n"
                      + "若系统默认应用仍指向其他程序，请在「设置 → 默认应用 → 按文件类型指定默认应用」中选择 SeeMe。",
                "SeeMe 文件关联", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void OnAssocSetMenu(object sender, RoutedEventArgs e)
        {
            var err = FileAssoc.RegisterContextMenu();
            RefreshAssocStatus();
            if (err != null)
            {
                MessageBox.Show(this, "注册失败：" + err, "SeeMe 文件关联",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            else
            {
                MessageBox.Show(this,
                    "已在资源管理器右键菜单注册「用 SeeMe 打开」。\n\n对 SeeMe 支持的所有格式（md / docx / xlsx / pptx / pdf / epub…）右键即可看到。",
                    "SeeMe 文件关联", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void OnAssocClear(object sender, RoutedEventArgs e)
        {
            FileAssoc.UnregisterAll();
            FillAssocGroups();
            RefreshAssocStatus();
            MessageBox.Show(this, "已取消全部文件关联（默认关联仅在仍指向 SeeMe 时移除）。",
                "SeeMe 文件关联", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ──────────────── 外观 ────────────────

        private void OnThemeModeChanged(object sender, RoutedEventArgs e)
        {
            if (_loading || sender is not RadioButton rb || rb.IsChecked != true) return;
            var mode = rb == ThemeDark ? "dark" : rb == ThemeSystem ? "system" : "light";
            _owner.ApplyThemeMode(mode);
        }

        private void OnDefaultZoomChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || DefaultZoomBox.SelectedIndex < 0) return;
            var z = DefaultZoomBox.SelectedIndex switch { 1 => 1.25, 2 => 1.5, _ => 1.0 };
            AppSettings.Set(AppSettings.DefaultZoomKey, z);
            _owner.ApplyDefaultZoomNow();
        }

        private void OnInfoPanelDefaultChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || InfoPanelBox.SelectedIndex < 0) return;
            AppSettings.Set(AppSettings.InfoPanelVisibleKey, InfoPanelBox.SelectedIndex == 0);
            _owner.ApplyInfoPanelDefault();
        }

        // ──────────────── 导出 ────────────────

        private void OnImageFormatChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || ImageFormatBox.SelectedIndex < 0) return;
            AppSettings.Set(AppSettings.ImageFormatKey, ImageFormatBox.SelectedIndex == 1 ? "jpg" : "png");
        }

        private void OnImageScaleChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || ImageScaleBox.SelectedIndex < 0) return;
            AppSettings.Set(AppSettings.ImageScaleKey, ImageScaleBox.SelectedIndex == 1 ? 2 : 1);
        }

        private void OnExportPathModeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || ExportPathModeBox.SelectedIndex < 0) return;
            var fixedMode = ExportPathModeBox.SelectedIndex == 1;
            AppSettings.Set(AppSettings.ExportPathModeKey, fixedMode ? "fixed" : "ask");
            ExportPathBox.IsEnabled = fixedMode;
        }

        private void OnPickExportPath(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择默认导出文件夹" };
            if (dlg.ShowDialog(this) == true)
            {
                ExportPathBox.Text = dlg.FolderName;
                AppSettings.Set(AppSettings.ExportPathKey, dlg.FolderName);
            }
        }

        // ──────────────── PDF 导出 ────────────────

        private void OnPdfIncludeTocChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || PdfIncludeTocBox.SelectedIndex < 0) return;
            AppSettings.Set(AppSettings.PdfIncludeTocKey, PdfIncludeTocBox.SelectedIndex == 1);
        }

        private void OnPdfPageNumberChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || PdfPageNumberBox.SelectedIndex < 0) return;
            AppSettings.Set(AppSettings.PdfPageNumberKey, PdfPageNumberBox.SelectedIndex == 1);
        }

        private void OnPdfWatermarkChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || PdfWatermarkBox.SelectedIndex < 0) return;
            AppSettings.Set(AppSettings.PdfWatermarkKey, PdfWatermarkBox.SelectedIndex == 1);
        }

        private void OnPdfWatermarkTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading) return;
            AppSettings.Set(AppSettings.PdfWatermarkTextKey, PdfWatermarkTextBox.Text ?? "");
        }

        private async void OnExportPdf(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "PDF 文件 (*.pdf)|*.pdf",
                Title = "导出为 PDF",
                FileName = (_owner.CurrentFileName ?? "untitled") + ".pdf"
            };
            if (dlg.ShowDialog(this) != true) return;
            PdfExportHint.Text = "正在导出…";
            try
            {
                // await：导出方法现在返回 Task，异常能精确回到这里 —— 见其 XML 注释里
                // 关于 async void 会吞掉 await 之后异常的说明。
                await _owner.ExportActivePanelToPdf(dlg.FileName,
                    includeToc: AppSettings.Get(AppSettings.PdfIncludeTocKey, true),
                    showPageNumber: AppSettings.Get(AppSettings.PdfPageNumberKey, true),
                    enableWatermark: AppSettings.Get(AppSettings.PdfWatermarkKey, false),
                    watermarkText: AppSettings.Get(AppSettings.PdfWatermarkTextKey, ""));
                PdfExportHint.Text = "导出完成：" + dlg.FileName;
            }
            catch (Exception ex)
            {
                PdfExportHint.Text = "导出失败：" + ex.Message;
                // UI 已提示，但仍落盘：用户关掉对话框后 Message 不足以定位根因。
                SeeMeLog.Error("PDF 导出", ex);
            }
        }

    }
}
