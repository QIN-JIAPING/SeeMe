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
            Sec2.Visibility = idx == 2 ? Visibility.Visible : Visibility.Collapsed;
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
                AnimationsBox.IsChecked = _owner.ThemeAnimationsEnabled;
                var fs = AppSettings.Get(AppSettings.FontSizeKey, 14);
                FontSizeBox.SelectedIndex = fs switch { 13 => 0, 16 => 2, 18 => 3, _ => 1 };
                var lh = AppSettings.Get(AppSettings.LineHeightKey, 1.65);
                RowHeightBox.SelectedIndex = lh >= 2.0 ? 2 : lh <= 1.4 ? 0 : 1;

                // 阅读
                CssPathBox.Text = AppSettings.Get(AppSettings.CustomCssKey, "");
                var mdStyle = AppSettings.Get(AppSettings.MdStyleKey, "default");
                if (mdStyle == "github") StyleGithub.IsChecked = true;
                else if (mdStyle == "simple") StyleSimple.IsChecked = true;
                else StyleDefault.IsChecked = true;
                EyeCareBox.IsChecked = AppSettings.Get(AppSettings.EyeCareKey, false);
                var pz = AppSettings.Get(AppSettings.PdfZoomModeKey, "fitWidth");
                PdfZoomBox.SelectedIndex = pz == "fitPage" ? 1 : pz == "actual" ? 2 : 0;
                PdfPageModeBox.SelectedIndex = AppSettings.Get(AppSettings.PdfPageModeKey, "scroll") == "paged" ? 1 : 0;
                PdfDblClickBox.IsChecked = AppSettings.Get(AppSettings.PdfDblClickZoomKey, true);

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
                DevToolsBox.IsChecked = AppSettings.Get(AppSettings.DevToolsKey, false);

                // 高级 / 关于
                var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SeeMe");
                DataDirBox.Text = dataDir;
                AboutDataDir.Text = dataDir;
                AboutInstallDir.Text = AppDomain.CurrentDomain.BaseDirectory;
                var ver = typeof(SettingsDialog).Assembly.GetName().Version;
                AboutVersion.Text = "SeeMe " + (ver?.ToString(3) ?? "1.0.1") + " · 多格式文档查看器";
                RefreshCacheInfo();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[SeeMe] SettingsDialog.LoadValues: " + ex.Message);
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

        private void OnMdStyleChanged(object sender, RoutedEventArgs e)
        {
            if (_loading || sender is not RadioButton rb || rb.IsChecked != true) return;
            var style = rb == StyleGithub ? "github" : rb == StyleSimple ? "simple" : "default";
            AppSettings.Set(AppSettings.MdStyleKey, style);
            _owner.ApplyMdStyleNow(style);
        }

        private void OnEyeCareChanged(object sender, RoutedEventArgs e)
        {
            if (_loading || sender is not CheckBox cb) return;
            AppSettings.Set(AppSettings.EyeCareKey, cb.IsChecked == true);
            _owner.ApplyEyeCareNow(cb.IsChecked == true);
        }

        private void OnAnimationsChanged(object sender, RoutedEventArgs e)
        {
            if (_loading || sender is not CheckBox cb) return;
            AppSettings.Set(AppSettings.AnimationsKey, cb.IsChecked == true);
            _owner.ApplyAnimationsNow(cb.IsChecked == true);
        }

        private void OnDevToolsChanged(object sender, RoutedEventArgs e)
        {
            if (_loading || sender is not CheckBox cb) return;
            AppSettings.Set(AppSettings.DevToolsKey, cb.IsChecked == true);
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
            var cache = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SeeMe.exe.WebView2");
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
            var cache = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SeeMe.exe.WebView2");
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

        private void StatusBarHint(string msg) => System.Diagnostics.Debug.WriteLine("[SeeMe] " + msg);

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

        // ──────────────── 外观 ────────────────

        private void OnThemeModeChanged(object sender, RoutedEventArgs e)
        {
            if (_loading || sender is not RadioButton rb || rb.IsChecked != true) return;
            var mode = rb == ThemeDark ? "dark" : rb == ThemeSystem ? "system" : "light";
            _owner.ApplyThemeMode(mode);
        }

        private void OnPdfZoomChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || PdfZoomBox.SelectedIndex < 0) return;
            var mode = PdfZoomBox.SelectedIndex switch { 1 => "fitPage", 2 => "actual", _ => "fitWidth" };
            AppSettings.Set(AppSettings.PdfZoomModeKey, mode);
        }

        private void OnPdfPageModeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || PdfPageModeBox.SelectedIndex < 0) return;
            AppSettings.Set(AppSettings.PdfPageModeKey, PdfPageModeBox.SelectedIndex == 1 ? "paged" : "scroll");
        }

        private void OnPdfDblClickChanged(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            AppSettings.Set(AppSettings.PdfDblClickZoomKey, PdfDblClickBox.IsChecked == true);
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

        // ──────────────── 阅读 ────────────────

        private void OnPickCss(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "CSS 文件 (*.css)|*.css|所有文件 (*.*)|*.*",
                Title = "选择自定义样式文件"
            };
            if (dlg.ShowDialog(this) == true)
            {
                CssPathBox.Text = dlg.FileName;
                _owner.ApplyCustomCssNow(dlg.FileName);
            }
        }

        private void OnClearCss(object sender, RoutedEventArgs e)
        {
            CssPathBox.Text = "";
            _owner.ApplyCustomCssNow("");
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

    }
}
