// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
                RefreshAssocStatus();
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

        private void FillKeymap()
        {
            KeymapList.Items.Clear();
            var fg = TryFindResource("TextBodyBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gray;
            string[] items =
            {
                "Ctrl+O            打开文件",
                "Ctrl+T            切换双栏",
                "Ctrl+F            查找",
                "F5                刷新",
                "Ctrl+0            缩放复位",
                "Ctrl+= / Ctrl+-   放大 / 缩小",
                "Ctrl+Shift+D      切换主题",
                "Ctrl+N            新建窗口",
                "Ctrl+W            关闭窗口",
            };
            foreach (var it in items)
                KeymapList.Items.Add(new ListBoxItem { Content = new TextBlock { Text = it, FontSize = 12, Foreground = fg, Margin = new Thickness(0, 2, 0, 2) } });
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

        // ──────────────── 文件关联 ────────────────

        /// <summary>刷新关联状态文本（Markdown 默认查看器 + 右键菜单两项）。</summary>
        private void RefreshAssocStatus()
        {
            if (AssocStatus == null) return;
            var parts = new List<string>
            {
                "Markdown 默认查看器：" + (FileAssoc.IsMdDefault() ? "已设置" : "未设置"),
                "右键菜单：" + (FileAssoc.IsContextMenuRegistered() ? "已注册" : "未注册")
            };
            AssocStatus.Text = string.Join("　·　", parts);
        }

        private void OnAssocSetMd(object sender, RoutedEventArgs e)
        {
            var err = FileAssoc.RegisterMdDefault();
            RefreshAssocStatus();
            if (err != null)
            {
                MessageBox.Show(this, "设置失败：" + err, "SeeMe 文件关联",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            else
            {
                MessageBox.Show(this,
                    "已将 .md 的默认打开方式设为 SeeMe。\n\n若系统默认应用仍指向其他程序，请在「设置 → 默认应用 → 按文件类型指定默认应用」中选择 SeeMe。",
                    "SeeMe 文件关联", MessageBoxButton.OK, MessageBoxImage.Information);
            }
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
            RefreshAssocStatus();
            MessageBox.Show(this, "已取消全部文件关联（.md 关联仅在仍指向 SeeMe 时移除）。",
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
