// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace SeeMe
{
    public partial class MainWindow : Window
    {
        private AppState _app = null!;
        private PanelState? _activePanel;
        // 服务实例：手写工厂在 OnLoaded 中创建（不引入 DI 容器）
        private IRenderService _render = null!;
        private IFileConverter _converter = null!;
        private IThemeManager _theme = null!;
        private PandocExportService _pandoc = null!;
        private IBookmarkStore _bookmarks = null!;
        private IHighlightStore _highlights = null!;
        private INoteStore _notes = null!;
        private ReadProgressStore _progress = null!;
        /// <summary>独立命令面板窗口（不挂在主窗内，避免遮住阅读区）。</summary>
        private CommandPaletteWindow? _paletteWindow;
        private string _searchFilter = "";
        /// <summary>用户自定义 CSS 文件路径（null=未启用）。读取失败时回退空。</summary>
        private string? _customCssPath;
        private const long MaxFileSize = 50L * 1024 * 1024;
        /// <summary>空页面：纯背景（跟随 WebView DefaultBackgroundColor），用于欢迎卡片关闭后。</summary>
        private const string EmptyPage = "<html><head><style>html,body{width:100%;height:100%;margin:0;background:transparent}</style></head><body></body></html>";
        // 实时刷新自适应防抖：首次触发后 100ms 执行；连续触发时每次递增 100ms，上限 800ms，
        // 避免记事本/IDE 高频保存时反复重渲染；刷新成功后回落到最小值
        private const int DebounceMinMs = 100;
        private const int DebounceMaxMs = 800;
        private const int DebounceStepMs = 100;

        public MainWindow()
        {
            InitializeComponent();
            SetWindowIcon();
            _paletteWindow = new CommandPaletteWindow(this);

            // 全局单例主题：App.OnStartup 已初始化，所有窗口共享（多窗口切换主题互相跟随）。
            // 窗口显示前首次合入目标主题字典——若放到 OnLoaded（窗口已显示），Window.Background 等
            // DynamicResource 会先解析到 App 级亮色兜底、合入暗色字典后再重解析 → 启动瞬间闪色。
            _theme = App.GlobalTheme;
            _theme.ApplyToWindow(this, _theme.Current == _theme.Dark ? _theme.Dark : _theme.Light);
            UpdateThemeButton();

            Loaded += OnLoaded;
            // PreviewKeyDown（隧道）而非 KeyDown（冒泡）：WebView2 有焦点时按键先经父窗口，
            // 保证 Ctrl+0/+/−、F5 等快捷键在文档页内也能被宿主拦截，不被浏览器/WebView2 吃掉。
            PreviewKeyDown += OnWindowKeyDown;
            SizeChanged += OnWindowSizeChanged;
            AllowDrop = true;
            Drop += OnDrop;
            DragOver += OnDragOver;
            SourceInitialized += OnSourceInitialized;
        }

        /// <summary>关闭拦截：按设置处理「直接退出 / 最小化到托盘 / 每次询问」。</summary>
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            base.OnClosing(e);
            var behavior = AppSettings.Get(AppSettings.CloseBehaviorKey, "exit");
            bool toTray = behavior == "tray";
            if (behavior == "ask")
            {
                var r = MessageBox.Show(this,
                    "关闭 SeeMe：\n\n是(Y)    → 最小化到系统托盘\n否(N)    → 直接退出\n取消     → 留在窗口",
                    "SeeMe", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
                if (r == MessageBoxResult.Yes) toTray = true;
                else if (r == MessageBoxResult.Cancel) { e.Cancel = true; return; }
            }
            if (toTray)
            {
                e.Cancel = true;
                Hide();
                if (Application.Current is App app) app.NotifyMinimizedToTray();
            }
        }

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            var saved = LoadWindowState();
            if (saved != null)
            {
                if (saved.Width > 0) Width = saved.Width;
                if (saved.Height > 0) Height = saved.Height;
                if (saved.Left >= 0) Left = saved.Left;
                if (saved.Top >= 0) Top = saved.Top;
                if (saved.Maximized) WindowState = WindowState.Maximized;
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                SaveWindowState();
                SaveAllReadProgress();   // 必须在 CleanupPanel 之前：Dispose 后滚动数据失效
                CleanupPanel(_app.Left);
                CleanupPanel(_app.Right);
                // 释放隐藏提取 WebView：ExtractView 独立于 PanelState，此前从未 Dispose，
                // 多窗口反复开关会导致 WebView2 渲染进程与事件订阅持续泄漏。
                try { ExtractView?.Dispose(); } catch (Exception ex) { LogErr("ExtractView dispose: " + ex.Message); }
            }
            catch (Exception ex) { LogErr("OnClosed cleanup: " + ex.Message); }
            base.OnClosed(e);
        }

        private static string WindowStatePath => StoragePaths.Combine("window.json");

        private record WindowStateData(double Width, double Height, double Left, double Top, bool Maximized);

        private void SaveWindowState()
        {
            try
            {
                var dir = Path.GetDirectoryName(WindowStatePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var data = new WindowStateData(
                    Width, Height, Left, Top,
                    WindowState == WindowState.Maximized);
                File.WriteAllText(WindowStatePath,
                    System.Text.Json.JsonSerializer.Serialize(data, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { LogErr("SaveWindowState: " + ex.Message); }
        }

        private WindowStateData? LoadWindowState()
        {
            try
            {
                if (!File.Exists(WindowStatePath)) return null;
                var json = File.ReadAllText(WindowStatePath);
                return System.Text.Json.JsonSerializer.Deserialize<WindowStateData>(json);
            }
            catch (Exception ex) { LogErr("LoadWindowState: " + ex.Message); return null; }
        }

        private static void CleanupPanel(PanelState? state)
        {
            if (state == null) return;
            try
            {
                state.Dispose();
            }
            catch (Exception ex) { LogErr("CleanupPanel: " + ex.Message); }
        }

        /// <summary>
        /// 退出前把两个面板的阅读进度落盘。
        /// 必须在 <see cref="CleanupPanel"/> **之前**调用 —— 后者会 Dispose 掉 WebView，
        /// 之后 state 上的滚动数据就没意义了。
        /// </summary>
        private void SaveAllReadProgress()
        {
            try { if (_app?.Left != null) SaveReadProgress(_app.Left); } catch (Exception ex) { LogErr("SaveAllReadProgress(L): " + ex.Message); }
            try { if (_app?.Right != null) SaveReadProgress(_app.Right); } catch (Exception ex) { LogErr("SaveAllReadProgress(R): " + ex.Message); }
        }

        // ──────────────── WebView2 缓存定期清理（保留设置/历史/书签） ────────────────

        /// <summary>上次清理标记文件；距今超过清理间隔（默认 7 天）才再次清理。</summary>
        private static string WebViewCacheCleanupMarker => StoragePaths.Combine("webview2-cache-cleaned.txt");
        private static readonly TimeSpan WebViewCacheCleanupInterval = TimeSpan.FromDays(7);

        private static bool IsWebViewCacheCleanupDue()
        {
            try
            {
                if (!File.Exists(WebViewCacheCleanupMarker)) return true;
                var last = DateTime.Parse(File.ReadAllText(WebViewCacheCleanupMarker).Trim());
                return DateTime.UtcNow - last >= WebViewCacheCleanupInterval;
            }
            catch { return false; } // 标记损坏 → 视为到期，下次清理会重写
        }

        /// <summary>到期则后台清理两个面板的 WebView2 浏览数据（缓存/存储），并更新标记。</summary>
        private void TryClearWebViewCacheIfDue()
        {
            try
            {
                if (!IsWebViewCacheCleanupDue()) return;
                // 先写标记再清，避免清理进行中重复触发
                File.WriteAllText(WebViewCacheCleanupMarker, DateTime.UtcNow.ToString("o"));

                var tasks = new List<Task>();
                foreach (var state in new[] { _app.Left, _app.Right })
                {
                    if (state?.WebView?.CoreWebView2 == null) continue;
                    tasks.Add(state.WebView.CoreWebView2.Profile.ClearBrowsingDataAsync(
                        CoreWebView2BrowsingDataKinds.AllProfile));
                }
                if (tasks.Count > 0)
                {
                    _ = Task.WhenAll(tasks).ContinueWith(t =>
                    {
                        if (t.IsFaulted) LogErr("ClearBrowsingData: " + t.Exception?.GetBaseException()?.Message);
                    });
                }
            }
            catch (Exception ex) { LogErr("ClearWebViewCache: " + ex.Message); }
        }

        private void SetWindowIcon()
        {
            try
            {
                Icon = new System.Windows.Media.Imaging.BitmapImage(
                    new Uri("pack://application:,,,/app_icon.png"));
            }
            catch (Exception ex) { LogErr("SetWindowIcon: " + ex.Message); }
        }

        private void OnThemeChanged()
        {
            UpdateThemeButton();
            try
            {
                UpdateActivePanelVisual();
                LogInfo("Theme -> " + (_theme.Current == _theme.Dark ? "dark" : "light"));
            }
            catch (Exception ex)
            {
                LogErr("Theme change failed: " + ex.Message);
            }
        }

        private void UpdateThemeButton()
        {
            try
            {
                if (ThemeToggleBtn != null)
                {
                    // 线性图标：暗色主题显示 Sun（点击切亮），亮色显示 Moon（点击切暗）
                    if (ThemeIconPath != null)
                        ThemeIconPath.Data = (Geometry)FindResource(_theme.Current == _theme.Dark ? "IconSun" : "IconMoon");
                    ThemeToggleBtn.ToolTip = _theme.Current == _theme.Dark
                        ? "切换到亮色主题 (Ctrl+Shift+D)"
                        : "切换到暗色主题 (Ctrl+Shift+D)";
                }
            }
            catch (Exception ex) { LogErr("UpdateThemeButton: " + ex.Message); }
        }

        /// <summary>无命令行参数且无历史可恢复时：扫描当前目录 Markdown 打开第一个（保持旧行为）。</summary>
        private void ScanCwdMarkdown()
        {
            try
            {
                var candidates = Directory.GetFiles(Environment.CurrentDirectory, "*.md")
                    .Concat(Directory.GetFiles(Environment.CurrentDirectory, "*.markdown"))
                    .Concat(Directory.GetFiles(Environment.CurrentDirectory, "*.mkd"))
                    .OrderBy(f => f)
                    .ToList();
                if (candidates.Count > 0)
                    OpenFileInternal(_app.Left, candidates[0]);
            }
            catch (Exception ex) { LogErr("Scan dir failed: " + ex.Message); }
        }

        /// <summary>打开设置对话框（左栏 ⚙ 按钮）。</summary>
        private void OnOpenSettings(object sender, RoutedEventArgs e) => OpenSettings();

        /// <summary>弹出分区设置窗口（通用/外观/阅读/快捷键/导出/高级/关于）。</summary>
        public void OpenSettings()
        {
            try
            {
                var dlg = new SettingsDialog(this) { Owner = this };
                dlg.ShowDialog();
            }
            catch (Exception ex) { LogErr("OpenSettings: " + ex.Message); }
        }

        /// <summary>应用主题模式（light/dark/system），持久化并立即生效（含 WebView 底色同步，避免闪切）。</summary>
        public void ApplyThemeMode(string mode)
        {
            AppSettings.Set(AppSettings.ThemeModeKey, mode);
            var resolved = mode == "system" ? AppSettings.ResolveSystemTheme() : mode;
            if (resolved != _theme.Light && resolved != _theme.Dark) return;

            // 与 OnThemeToggle 同一套 WebView 同步：先按目标态预改底色，再切 WPF，再推 JS
            var targetDark = resolved == _theme.Dark;
            var bg = ThemeWebViewBg(targetDark);
            try
            {
                WebViewL.DefaultBackgroundColor = bg;
                WebViewR.DefaultBackgroundColor = bg;
            }
            catch (Exception ex) { LogErr("Pre-set WebView bg: " + ex.Message); }

            _theme.ApplyToWindow(this, resolved);
            _ = PushThemeAsync(targetDark);
        }

        /// <summary>当前活动面板的文件名（导出对话框默认文件名）。</summary>
        public string? CurrentFileName
        {
            get
            {
                var p = _activePanel ?? _app?.Left;
                return p?.CurrentFile != null ? System.IO.Path.GetFileNameWithoutExtension(p.CurrentFile) : null;
            }
        }

        /// <summary>
        /// 导出当前活动面板为 PDF：注入 TOC / 页码 / 水印相关 CSS class 到页面 body，
        /// 调 WebView2.PrintToPdfAsync 静默输出，完成后移除注入样式（恢复浏览状态）。
        /// 失败抛异常由调用方提示。
        /// </summary>
        public async void ExportActivePanelToPdf(string filePath, bool includeToc, bool showPageNumber, bool enableWatermark, string watermarkText)
        {
            var panel = _activePanel ?? _app?.Left;
            var wb = panel?.WebView?.CoreWebView2;
            if (wb == null) throw new InvalidOperationException("没有可导出的活动文档");
            // 1. 构造开关脚本：按设置给 body 加 class + 注入水印文字（转义防注入）
            var escText = SecurityElement.Escape(watermarkText ?? "");
            var sb = new StringBuilder("(function(){try{");
            sb.Append("var b=document.body;if(!b)return;");
            sb.Append($"b.classList.toggle('pdf-toc',{includeToc.ToString().ToLowerInvariant()});");
            sb.Append($"b.classList.toggle('pdf-pageno',{showPageNumber.ToString().ToLowerInvariant()});");
            sb.Append($"b.classList.toggle('pdf-watermark',{enableWatermark.ToString().ToLowerInvariant()});");
            sb.Append($"var old=document.getElementById('__seeme_pdf_wm');if(old)old.remove();");
            if (enableWatermark && !string.IsNullOrEmpty(watermarkText))
            {
                sb.Append($"var s=document.createElement('style');s.id='__seeme_pdf_wm';s.textContent='@media print{{body.pdf-watermark::before{{content:\"{escText}\";position:fixed;top:50%;left:50%;transform:translate(-50%,-50%) rotate(-30deg);font-size:140px;color:rgba(0,0,0,.08);z-index:9999;pointer-events:none;}}}}';");
                sb.Append("document.head.appendChild(s);");
            }
            sb.Append("}catch(e){}})();");
            await wb.ExecuteScriptAsync(sb.ToString());
            // 2. 静默打印到 PDF
            try
            {
                await wb.PrintToPdfAsync(filePath, null);
            }
            finally
            {
                // 3. 恢复浏览态：清掉所有 class 和水印 style
                await wb.ExecuteScriptAsync("(function(){try{var b=document.body;if(b){b.classList.remove('pdf-toc');b.classList.remove('pdf-pageno');b.classList.remove('pdf-watermark');}var s=document.getElementById('__seeme_pdf_wm');if(s)s.remove();}catch(e){}})();");
            }
        }

        /// <summary>重渲染当前打开的两个面板（设置变更后同步预览）。</summary>
        private void ReloadOpenPanels()
        {
            if (_app?.Left != null && !string.IsNullOrEmpty(_app.Left.CurrentFile))
                _ = ReloadFileAsync(_app.Left, force: true);
            if (_app?.Right != null && !string.IsNullOrEmpty(_app.Right.CurrentFile))
                _ = ReloadFileAsync(_app.Right, force: true);
        }

        /// <summary>
        /// 把面板当前 FontScale 重新注入页面（导航完成后/设置变更时调用，保证缩放跨刷新保留）。
        /// 无条件注入：即使缩放为 1.0 也要覆盖此前注入的缩放，否则从 125% 改回 100% 无法生效。
        /// </summary>
        private void ApplyPanelZoom(PanelState? state)
        {
            if (state?.WebView?.CoreWebView2 == null) return;
            try
            {
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                state.WebView.ExecuteScriptAsync(
                    $"window.__seemeApplyZoom ? window.__seemeApplyZoom({state.FontScale.ToString(inv)}) : " +
                    $"(document.documentElement.style.fontSize=({state.FontScale * 14}).toString()+'px');");
            }
            catch (Exception ex) { LogErr("ApplyPanelZoom: " + ex.Message); }
        }

        private void OnThemeToggle(object sender, RoutedEventArgs e)
        {
            // Toggle 会翻转主题：先算目标态，保证 WebView 底色与 WPF 动画同步到位
            var targetDark = _theme.Current != _theme.Dark;
            AnimateThemeTransition(targetDark, () =>
            {
                _theme.Toggle(this);
                StatusText.Text = _theme.Current == _theme.Dark
                    ? "已切换到暗色主题"
                    : "已切换到亮色主题";
            });
        }

        /// <summary>
        /// 主题切换：先同步预改 WebView 底色（避免 WPF 已动、WebView 未动的中间帧露旧色/白底），
        /// 再执行 WPF 侧 Brush.Color 渲染线程动画（零主线程阻塞），最后 WebView 注入 setTheme JS。
        /// targetDark 由调用方明确传入目标态（Toggle 翻转 / ApplyThemeMode 指定值），不隐式推断。
        /// </summary>
        private void AnimateThemeTransition(bool targetDark, Action switchAction)
        {
            var bg = ThemeWebViewBg(targetDark);
            try
            {
                WebViewL.DefaultBackgroundColor = bg;
                WebViewR.DefaultBackgroundColor = bg;
            }
            catch (Exception ex) { LogErr("Pre-set WebView bg: " + ex.Message); }

            switchAction();
            _ = PushThemeAsync(targetDark);
        }

        /// <summary>
        /// 向所有 WebView 推送主题：更新控件底色 + 注入主题切换 JS（await + 错误捕获，杜绝静默失败）。
        /// 注入的 JS 自带兜底：页面 window.setTheme 存在则调用（保留 Prism/Mermaid 联动）；
        /// 不存在（内联被 CSP 拦 / 外链未加载 / NavigateToString opaque-origin 边缘）时，
        /// 由注入代码直接切 html 的 dark class —— 不依赖页面脚本环境，任何情况都必然生效。
        /// 任何导航完成后也会调用（NavigationCompleted 兜底），保证新加载页面必然收到主题。
        /// </summary>
        private async Task PushThemeAsync(bool isDark)
        {
            var bg = ThemeWebViewBg(isDark);
            var arg = isDark ? "true" : "false";
            var js = $"if(window.setTheme){{window.setTheme({arg});}}" +
                     $"else{{var h=document.documentElement;if({arg})h.classList.add('dark');else h.classList.remove('dark');}}";
            foreach (var v in new[] { WebViewL, WebViewR })
            {
                if (v == null) continue;
                try { v.DefaultBackgroundColor = bg; } catch (Exception ex) { LogErr("WebView bg: " + ex.Message); }
                try
                {
                    if (v.CoreWebView2 != null)
                    {
                        // 滚动条/空白区/表单控件默认走对应主题，避免系统白色暴露
                        v.CoreWebView2.Profile.PreferredColorScheme = isDark
                            ? CoreWebView2PreferredColorScheme.Dark
                            : CoreWebView2PreferredColorScheme.Light;
                        await v.CoreWebView2.ExecuteScriptAsync(js);
                    }
                }
                catch (Exception ex) { LogErr("WebView theme: " + ex.Message); }
            }
        }

        /// <summary>WebView2 控件底色（对应 WindowBackgroundBrush），按入参 isDark 返回，不依赖 _theme.Current 时机。</summary>
        private static System.Drawing.Color ThemeWebViewBg(bool isDark) =>
            isDark
                ? System.Drawing.Color.FromArgb(0x0F, 0x13, 0x1A)  // WindowBackgroundBrush 暗色
                : System.Drawing.Color.FromArgb(0xF1, 0xF5, 0xF9); // WindowBackgroundBrush 亮色

        /// <summary>显示空白页面的欢迎提示卡片；若用户已选"不再显示"则显示纯背景空页。</summary>
        private void ShowWelcome(PanelState state)
        {
            if (state?.WebView?.CoreWebView2 == null) return;
            var html = _theme.WelcomeDismissed ? EmptyPage : _render.BuildWelcomePage(this);
            state.WebView.NavigateToString(html);
            state.TitleText.Text = "未选择";
            state.TitleText.ToolTip = null;
            UpdateEditButtonVisibility();
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            // 手写工厂：先创建应用状态与服务实例（后续逻辑依赖它们，必须最先执行）
            _app = new AppState();
            _app.Left = new PanelState(TitleTextL, PathTextL, WebViewL, TitleBarL);
            _app.Right = new PanelState(TitleTextR, PathTextR, WebViewR, TitleBarR);

            // _theme 已在构造函数初始化并首次合入主题字典（窗口显示前，无启动闪色）
            _render = new RenderService();
            _render.ThemeManager = _theme;
            _converter = new FileConverter();
            _render.Converter = _converter;
            _theme.Changed += OnThemeChanged;

            _pandoc = new PandocExportService();
            _pandoc.LoadSettings();

            _app.History = FileHistory.Load();
            _app.History.SetMaxEntries(AppSettings.Get(AppSettings.HistorySizeKey, 15));
            _app.History.Changed += RefreshRecentFilesList;

            _bookmarks = BookmarkStore.Load();
            _bookmarks.Changed += RefreshRecentFilesList;

            _highlights = HighlightStore.Load();
            _highlights.Changed += OnHighlightsChanged;
            _render.HighlightStore = _highlights;

            _notes = NoteStore.Load();
            _notes.Changed += RefreshNotesPanel;

            // 阅读进度：跨会话持久化滚动位置与百分比（内存态的 PanelState.LastScrollY 只活一次运行）
            _progress = ReadProgressStore.Load();
            _progress.PruneMissing();   // 启动清一次僵尸条目（用户删掉的文档不该留在进度表里）
            _progress.Changed += RefreshRecentFilesList;

            // 先设置 WebView 默认背景避免闪白（跟随主题画布色）
            var initBg = ThemeWebViewBg(_theme.Current == _theme.Dark);
            WebViewL.DefaultBackgroundColor = initBg;
            WebViewR.DefaultBackgroundColor = initBg;

            try
            {
                // 显式指定 UserDataFolder（StoragePaths.Root\WebView2Data）：
                // 1) 安装到 Program Files 等只读目录时不再依赖 exe 旁的默认数据目录（SeeMe.exe.WebView2，不可写）；
                // 2) 缓存位置确定，便于定期清理；设置/历史/书签等同根下的 *.json 不受影响；
                // 3) 便携模式下随之落到 data\WebView2Data，整个数据树随程序目录一起搬迁。
                // ⚠️ 便携模式装在 Program Files 时 WebView2 会因写不进去而初始化失败 ——
                // 该场景由设置页的可写性探测提前警告（见 StoragePaths.ProbePortableWritable）。
                var webviewDataDir = Path.Combine(StoragePaths.Root, "WebView2Data");
                var env = await CoreWebView2Environment.CreateAsync(null, webviewDataDir);
                await WebViewL.EnsureCoreWebView2Async(env);
                await WebViewR.EnsureCoreWebView2Async(env);
                // 后台 PDF 文本层提取专用（隐藏）：与面板共享同一环境
                await ExtractView.EnsureCoreWebView2Async(env);

                // 定期清理 WebView2 浏览数据缓存（含打开过的 PDF/文档残留），默认 7 天一清；不影响设置/历史
                TryClearWebViewCacheIfDue();

                // 禁用浏览器级缩放（Ctrl+滚轮 / Ctrl++ / Ctrl+-）：
                // 否则焦点在 WebView2 内时按键被浏览器吃掉，走 ZoomFactor 整体缩放——整个页面
                // （含 PDF 工具条 #bar、Markdown 顶栏）一起放大，绕开 C# SetZoom 的内容级缩放链路。
                // 禁用后按键冒泡回 WPF KeyDown，由 SetZoom 统一走 __seemeApplyZoom / fontSize 注入。
                WebViewL.CoreWebView2.Settings.IsZoomControlEnabled = false;
                WebViewR.CoreWebView2.Settings.IsZoomControlEnabled = false;
                ExtractView.CoreWebView2.Settings.IsZoomControlEnabled = false;
                // 关闭开发者工具：应用渲染的是不可信文档，XSS 成功后 DevTools 会放大攻击面
                // （可读页面全部 JS/数据、执行任意表达式、辅助进一步渗透）。本地阅读器无需调试。
                WebViewL.CoreWebView2.Settings.AreDevToolsEnabled = false;
                WebViewR.CoreWebView2.Settings.AreDevToolsEnabled = false;
                ExtractView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            }
            catch (Exception ex)
            {
                StatusText.Text = "WebView2 初始化失败 " + ex.Message;
                LogErr("WebView2 init failed: " + ex);
                return;
            }

            // 注册虚拟主机映射：将 https://appassets.example 指向本地 Resources 目录，
            // 配合 RenderService 中收紧后的 CSP（不再允许 file: 协议），安全加载 KaTeX/Prism/Mermaid 等离线资源。
            var resRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources");
            WebViewL.CoreWebView2.SetVirtualHostNameToFolderMapping(RenderService.VirtualHost, resRoot, CoreWebView2HostResourceAccessKind.Allow);
            WebViewR.CoreWebView2.SetVirtualHostNameToFolderMapping(RenderService.VirtualHost, resRoot, CoreWebView2HostResourceAccessKind.Allow);
            ExtractView.CoreWebView2.SetVirtualHostNameToFolderMapping(RenderService.VirtualHost, resRoot, CoreWebView2HostResourceAccessKind.Allow);

            // 收窄文档虚拟主机暴露面（docfiles.example / pdffiles.example）：
            // SetVirtualHostNameToFolderMapping 会把整个文档目录映射出去，页面（尤其被注入 JS 的
            // 恶意文档）可 fetch 同目录任意文件。注册 WebResourceRequested 白名单——只放行与
            // 当前打开文件同名的请求，其余一律 403。
            GuardDocumentHost(WebViewL, () => _app.Left?.CurrentFile);
            GuardDocumentHost(WebViewR, () => _app.Right?.CurrentFile);
            GuardDocumentHost(ExtractView, () => _app.Left?.AnyDocPending?.File ?? _app.Right?.AnyDocPending?.File);

            // 初始 PreferredColorScheme：滚动条/空白区/表单控件跟随主题，避免系统默认白色
            try
            {
                WebViewL.CoreWebView2.Profile.PreferredColorScheme = _theme.Current == _theme.Dark
                    ? CoreWebView2PreferredColorScheme.Dark
                    : CoreWebView2PreferredColorScheme.Light;
                WebViewR.CoreWebView2.Profile.PreferredColorScheme = _theme.Current == _theme.Dark
                    ? CoreWebView2PreferredColorScheme.Dark
                    : CoreWebView2PreferredColorScheme.Light;
            }
            catch (Exception ex) { LogErr("Set preferred color scheme: " + ex.Message); }

            // 关闭表单自动填充与密码保存：本地阅读器无登录场景，防止表单数据/凭据残留缓存
            try
            {
                WebViewL.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
                WebViewR.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
                WebViewL.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
                WebViewR.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
            }
            catch (Exception ex) { LogErr("Disable autofill: " + ex.Message); }

            // 文件拖放注入：WebView2（HWND 宿主）会吃掉中央内容区的 OLE 拖放，WPF 窗口级 Drop 在
            // WebView 上方不触发。改为在每个页面注入全局 dragover/drop 拦截，用 File.path（WebView2
            // 特有扩展）postMessage 回宿主打开——官方 drag&drop 方案。
            const string dropJs = @"window.addEventListener('dragover',function(e){e.preventDefault();if(e.dataTransfer)e.dataTransfer.dropEffect='copy';});
window.addEventListener('drop',function(e){
  e.preventDefault();
  try{
    if(!e.dataTransfer||!e.dataTransfer.files||!e.dataTransfer.files.length) return;
    var paths=[];
    for(var i=0;i<e.dataTransfer.files.length;i++){var f=e.dataTransfer.files[i];if(f.path)paths.push(f.path);}
    if(paths.length) window.chrome.webview.postMessage(JSON.stringify({kind:'drop',paths:paths}));
  }catch(err){}
});";
            await WebViewL.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(dropJs);
            await WebViewR.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(dropJs);

            WebViewL.CoreWebView2.NavigationStarting += (_, ea) => OnNavigationStarting(WebViewL, ea);
            WebViewR.CoreWebView2.NavigationStarting += (_, ea) => OnNavigationStarting(WebViewR, ea);
            WebViewL.CoreWebView2.NewWindowRequested += (_, ea) => OnNewWindowRequested(ea);
            WebViewR.CoreWebView2.NewWindowRequested += (_, ea) => OnNewWindowRequested(ea);
            WebViewL.CoreWebView2.WebMessageReceived += (_, ea) => OnWebMessage(_app.Left!, ea);
            WebViewR.CoreWebView2.WebMessageReceived += (_, ea) => OnWebMessage(_app.Right!, ea);
            // 隐藏提取 WebView：anydoc-result 按令牌匹配对应面板（支持双栏并发提取）
            ExtractView.CoreWebView2.WebMessageReceived += (_, ea) => OnExtractMessage(ea);
            // 渲染进程崩溃恢复：记录 + 自动重载当前文件；连续崩溃 >= 3 次（5 秒窗口）停止重载并提示
            WebViewL.CoreWebView2.ProcessFailed += (_, ea) => OnProcessFailed(_app.Left!, ea);
            WebViewR.CoreWebView2.ProcessFailed += (_, ea) => OnProcessFailed(_app.Right!, ea);

            // 导航完成后补推一次主题：任何页面（含错误/加载/占位页）加载完都必然收到当前主题，
            // 即使切换发生在导航进行中，也不会出现"外壳变了 body 没变"的时序窗口。
            // 顺带探测页内是否有可导出图表（ECharts / markmap / Mermaid），据此显隐「导出图表」按钮 ——
            // 图表 DOM 只有渲染完成后才存在，无法在 C# 侧提前判断。
            WebViewL.NavigationCompleted += (_, _) => { _ = PushThemeAsync(_theme.Current == _theme.Dark); ApplyPanelZoom(_app.Left); ApplyAnnMode(_app.Left); SyncAnnBtns(); _ = UpdateChartExportAvailabilityAsync(_app.Left!); };
            WebViewR.NavigationCompleted += (_, _) => { _ = PushThemeAsync(_theme.Current == _theme.Dark); ApplyPanelZoom(_app.Right); ApplyAnnMode(_app.Right); SyncAnnBtns(); _ = UpdateChartExportAvailabilityAsync(_app.Right!); };

            // 面板宽度变化（窗口缩放/拖分隔条）时刷新标题栏按钮折叠状态
            WebViewL.SizeChanged += (_, _) => UpdateTitleBarOverflow();
            WebViewR.SizeChanged += (_, _) => UpdateTitleBarOverflow();

            // ── 应用设置：自定义 CSS / 默认缩放 / 信息面板默认态 / 跟随系统主题 / 主题色 / 渲染风格 / 护眼 / 动画 ──
            if (AppSettings.Get(AppSettings.ThemeModeKey, "light") == "system")
                ApplyThemeMode("system"); // 重启后仍按系统主题解析（Initialize 只读 theme 键）
            _customCssPath = string.IsNullOrEmpty(AppSettings.Get(AppSettings.CustomCssKey, ""))
                ? null : AppSettings.Get(AppSettings.CustomCssKey, "");
            _app.Left.FontScale = AppSettings.Get(AppSettings.DefaultZoomKey, 1.0);
            _app.Right.FontScale = AppSettings.Get(AppSettings.DefaultZoomKey, 1.0);
            try { SetInfoPanelVisibility(AppSettings.Get(AppSettings.InfoPanelVisibleKey, true)); }
            catch (Exception ex) { LogErr("InfoPanel default: " + ex.Message); }
            _theme.ApplyAccent(AppSettings.Get(AppSettings.AccentKey, "indigo"));
            if (_render is RenderService rs)
            {
                rs.MdStyle = AppSettings.Get(AppSettings.MdStyleKey, "default");
                rs.EyeCare = AppSettings.Get(AppSettings.EyeCareKey, false);
                rs.FontSize = AppSettings.Get(AppSettings.FontSizeKey, 14);
                rs.LineHeight = AppSettings.Get(AppSettings.LineHeightKey, 1.65);
            }
            _theme.AnimationsEnabled = AppSettings.Get(AppSettings.AnimationsKey, true);

            var args = Environment.GetCommandLineArgs();
            string? leftFromArgs = null;
            if (args.Length >= 2)
            {
                try
                {
                    var p = Path.GetFullPath(args[1]);
                    if (File.Exists(p))
                        leftFromArgs = p;
                }
                catch { LogErr("Invalid command line arg 1: " + Path.GetFileName(args[1])); }
            }

            if (leftFromArgs != null)
            {
                OpenFileInternal(_app.Left, leftFromArgs);
            }
            else if (AppSettings.Get(AppSettings.StartupOpenKey, "last") == "last")
            {
                // 「上次关闭的文件」：重开历史首条；开启双栏且历史有两条则分栏展示。
                var hist = _app.History.Entries;
                var file0 = hist.Count > 0 && File.Exists(hist[0]) ? hist[0] : null;
                if (file0 != null)
                {
                    if (AppSettings.Get(AppSettings.StartupSplitKey, false)
                        && hist.Count > 1 && File.Exists(hist[1]))
                    {
                        SetSplitMode(true);
                        OpenFileInternal(_app.Left, file0);
                        OpenFileInternal(_app.Right, hist[1]);
                    }
                    else
                    {
                        OpenFileInternal(_app.Left, file0);
                    }
                }
                else
                {
                    ScanCwdMarkdown();
                }
            }
            else
            {
                // 「空白页」：不自动打开任何文件
            }

            if (args.Length >= 3)
            {
                try
                {
                    var p = Path.GetFullPath(args[2]);
                    if (File.Exists(p))
                        OpenFileInternal(_app.Right, p);
                }
                catch { LogErr("Invalid command line arg 2: " + args[2]); }
            }

            RefreshRecentFilesList();

            var hasLeft = !string.IsNullOrEmpty(_app.Left!.CurrentFile);
            var hasRight = !string.IsNullOrEmpty(_app.Right!.CurrentFile);
            SetSplitMode(hasLeft && hasRight);

            _activePanel = _app.Left;
            UpdateActivePanelVisual();
            // 布局完成后刷新标题栏按钮折叠状态（面板实际宽度此时才可用）
            _ = Dispatcher.BeginInvoke(new Action(UpdateTitleBarOverflow));

            if (!hasLeft)
                ShowWelcome(_app.Left);

            if (hasLeft && hasRight)
                StatusText.Text = "双栏模式 ？点击标题栏选中中间面板";
            else if (hasLeft)
                StatusText.Text = "提示：拖入另一个文件到右栏可对比阅读";
            else
                StatusText.Text = "Ctrl+O 打开 ？直接拖入 md/pdf/Office/epub/csv 文件 ？Ctrl+T 分栏";
            UpdateWindowTitle();
            // 启动布局收尾：右栏无文件时不占空间（双栏灰色空区域防护）
            UpdateSplitAutoHide();
        }

        /// <summary>演示/专注模式（F11）：隐藏左右栏让当前文档占满视口，Esc/F11 还原。Sumatra 演示模式启发。</summary>
        private bool _presentationOn;
        private bool _presInfoVisible;
        private bool _presNotesVisible;

        private void TogglePresentation()
        {
            try
            {
                var hasFile = (ActiveOrLeft?.CurrentFile != null) || (_app?.Right?.CurrentFile != null);
                if (!_presentationOn && !hasFile)
                {
                    StatusText.Text = "没有打开的文档";
                    return;
                }
                _presentationOn = !_presentationOn;
                if (_presentationOn)
                {
                    _presInfoVisible = InfoPanel.Visibility == Visibility.Visible;
                    _presNotesVisible = NotesPanel != null && NotesPanel.Visibility == Visibility.Visible;
                    LeftNavCol.Width = new GridLength(0);
                    InfoPanelCol.Width = new GridLength(0);
                    InfoPanel.Visibility = Visibility.Collapsed;
                    if (NotesPanel != null) NotesPanel.Visibility = Visibility.Collapsed;
                    // 内容面板进入专注态：清空活动面板选择视觉
                    _activePanel = null;
                    UpdateActivePanelVisual();
                    StatusText.Text = "演示模式：文档占满视口 · F11 退出（Esc 退出文档区选择）";
                }
                else
                {
                    LeftNavCol.Width = new GridLength(195);
                    InfoPanelCol.Width = new GridLength(190);
                    InfoPanel.Visibility = _presInfoVisible ? Visibility.Visible : Visibility.Collapsed;
                    if (NotesPanel != null)
                        NotesPanel.Visibility = _presNotesVisible ? Visibility.Visible : Visibility.Collapsed;
                    UpdateRightPanelColWidth();
                    UpdateWindowTitle();
                    StatusText.Text = "已退出演示模式";
                }
            }
            catch (Exception ex) { LogErr("TogglePresentation: " + ex.Message); }
        }

        /// <summary>
        /// 键盘流是否可激活：无修饰键、焦点不在文本/列表等会消费方向键的控件、
        /// 当前面板打开了文件且不在编辑模式（编辑页 textarea 需要 ↑↓←→ 与 Enter）。
        /// </summary>
        private bool QuickNavAllowed()
        {
            var mods = Keyboard.Modifiers;
            if (mods.HasFlag(ModifierKeys.Control) || mods.HasFlag(ModifierKeys.Shift) || mods.HasFlag(ModifierKeys.Alt))
                return false;
            if (_paletteWindow != null && _paletteWindow.IsVisible) return false;
            if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase
                or System.Windows.Controls.ComboBox or ListBox or ListView or System.Windows.Controls.TreeView
                or System.Windows.Controls.DataGrid or System.Windows.Controls.DataGridCell)
                return false;
            var p = _activePanel ?? _app?.Left;
            return p != null && !p.EditMode && !string.IsNullOrEmpty(p.CurrentFile) && File.Exists(p.CurrentFile);
        }

        /// <summary>当前活动文件在最近文件（MRU，最新在前）中的浏览索引（无则 -1）。</summary>
        private int ActiveFileHistoryIndex()
        {
            var p = _activePanel ?? _app?.Left;
            if (p?.CurrentFile == null) return -1;
            var list = _app?.History.Entries;
            if (list == null || list.Count == 0) return -1;
            try
            {
                var cur = Path.GetFullPath(p.CurrentFile);
                for (var i = 0; i < list.Count; i++)
                {
                    if (string.Equals(Path.GetFullPath(list[i]), cur, StringComparison.OrdinalIgnoreCase)) return i;
                }
            }
            catch { }
            return -1;
        }

        /// <summary>键盘流 ←/→：沿最近文件列表前后切换（到头停止），QuickLook 方向键浏览文件语义。</summary>
        private void StepRecentFile(int dir)
        {
            try
            {
                var p = ActiveOrLeft;
                if (p == null || p.EditMode) return;
                var list = _app.History.Entries;
                if (list == null || list.Count == 0) return;
                var idx = ActiveFileHistoryIndex();
                if (idx < 0) idx = 0;
                else idx = Math.Clamp(idx + dir, 0, list.Count - 1);
                var next = list[idx];
                if (next == null || !File.Exists(next)) return;
                if (string.Equals(next, p.CurrentFile, StringComparison.OrdinalIgnoreCase)) return;
                p.LastScrollY = 0; // 切换目标文件时清除上一文件的滚动缓存，新文件从头显示
                OpenFileInternal(p, next);
                StatusText.Text = $"历史 {idx + 1}/{list.Count} · {Path.GetFileName(next)}（←→ 切换，Enter 系统打开）";
            }
            catch (Exception ex) { LogErr("StepRecentFile: " + ex.Message); }
        }

        /// <summary>键盘流 Enter：用系统默认程序打开当前预览的文件（快速转正式编辑/阅读）。</summary>
        private void OpenActiveWithDefaultApp()
        {
            try
            {
                var p = ActiveOrLeft;
                if (p?.CurrentFile == null || !File.Exists(p.CurrentFile)) return;
                Process.Start(new ProcessStartInfo { FileName = p.CurrentFile, UseShellExecute = true });
                StatusText.Text = "已在默认程序中打开: " + Path.GetFileName(p.CurrentFile);
            }
            catch (Exception ex) { LogErr("OpenActiveWithDefaultApp: " + ex.Message); }
        }

        public void ApplyCurrentThemeToWindow(Window w)
        {
            try { _theme?.ApplyToWindow(w, _theme.Current); }
            catch (Exception ex) { LogErrPublic("ApplyThemeToWindow: " + ex.Message); }
        }

        /// <summary>独立命令面板（Topmost 浮窗）开关入口。</summary>
        private void TogglePaletteWindow()
        {
            if (_paletteWindow == null) _paletteWindow = new CommandPaletteWindow(this);
            _paletteWindow.Toggle();
        }

        /// <summary>状态栏「命令」按钮入口（XAML Click）。</summary>
        private void OnPaletteButton(object sender, RoutedEventArgs e)
        {
            TogglePaletteWindow();
        }

        // ════════ 命令面板（CommandPaletteWindow）公开入口见 MainWindow.Palette.cs；以下为该窗口外的共用私有辅助 ════════

        /// <summary>确保右侧分栏存在（分栏模式关闭时先开启）——向右栏投递文件的公共前置。</summary>
        private void EnsureRightPanel()
        {
            if (!_app.IsSplitMode) SetSplitMode(true);
        }
        /// <summary>主题化弹窗外壳样板（唯一来源）：公共窗口属性 → 合入主题字典 → build 填充内容 → 模态显示。
        /// Title/Width/Height/Background 等差异项由调用方在 win 初始化器里自设。</summary>
        private void ShowThemedDialog(Window win, Action<Window> build)
        {
            win.Owner = this;
            win.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            win.ResizeMode = ResizeMode.NoResize;
            // 合入主题字典：独立 Window 的 DynamicResource 需经 ApplyToWindow 才能解析到主题资源
            _theme.ApplyToWindow(win, _theme.Current);
            build(win);
            win.ShowDialog();
        }

        private void OnWindowKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F5)
            {
                var target = ActiveOrLeft;
                if (target != null && !string.IsNullOrEmpty(target.CurrentFile))
                {
                    _ = ReloadFileAsync(target, true, target.ResetCts());
                    StatusText.Text = "已刷新 " + Path.GetFileName(target.CurrentFile);
                }
                else
                    StatusText.Text = "当前栏未打开文件";
                e.Handled = true;
                return;
            }

            var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

            // Ctrl+Shift+P：开关独立命令面板窗口（VS Code 同款——避开 WebView2 Chromium 抢 Ctrl+K 调搜索栏）
            if (ctrl && shift && e.Key == Key.P)
            {
                TogglePaletteWindow();
                e.Handled = true;
                return;
            }
            // 命令面板打开时：其余键交给窗口自身处理
            if (_paletteWindow != null && _paletteWindow.IsVisible) return;

            if (ctrl && e.Key == Key.O)
            {
                if (_activePanel != null) ShowOpenFor(_activePanel);
                else if (_app.Left != null) ShowOpenFor(_app.Left);
                e.Handled = true;
            }
            else if (ctrl && e.Key == Key.T)
            {
                SetSplitMode(!_app.IsSplitMode);
                e.Handled = true;
            }
            else if (ctrl && e.Key == Key.N)
            {
                if (Application.Current is App app) app.CreateWindow();
                e.Handled = true;
            }
            else if (ctrl && (e.Key == Key.W || e.Key == Key.F4))
            {
                Close();
                e.Handled = true;
            }
            else if (ctrl && shift && e.Key == Key.D)
            {
                OnThemeToggle(this, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (ctrl && e.Key == Key.F)
            {
                var tgt = ActiveOrLeft;
                if (tgt != null) ShowFind(tgt);
                e.Handled = true;
            }
            else if (ctrl && e.Key == Key.P)
            {
                // 打印：MD/Office/PDF 文本视图 → WebView2 系统打印对话框
                PrintActivePanel();
                e.Handled = true;
            }
            else if (ctrl && e.Key == Key.D0)
            {
                SetZoomActive(1.0);
                e.Handled = true;
            }
            else if (ctrl && (e.Key == Key.Add || e.Key == Key.OemPlus || e.Key == Key.Subtract || e.Key == Key.OemMinus))
            {
                var tgt = ActiveOrLeft;
                if (tgt != null)
                {
                    double step = 0.1;
                    if (e.Key == Key.Add || e.Key == Key.OemPlus) SetZoom(tgt, Math.Min(2.5, tgt.FontScale + step));
                    else SetZoom(tgt, Math.Max(0.5, tgt.FontScale - step));
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                _activePanel = null;
                UpdateActivePanelVisual();
                e.Handled = true;
            }
            // 键盘流（QuickLook 式）：无修饰键 ←/→ 在最近历史文件间切换预览，Enter 用系统默认程序打开当前文件
            else if (QuickNavAllowed() && e.Key == Key.Left)
            {
                StepRecentFile(-1);
                e.Handled = true;
            }
            else if (QuickNavAllowed() && e.Key == Key.Right)
            {
                StepRecentFile(1);
                e.Handled = true;
            }
            else if (QuickNavAllowed() && e.Key == Key.Enter)
            {
                OpenActiveWithDefaultApp();
                e.Handled = true;
            }
            else if (!ctrl && !shift && e.Key == Key.F11)
            {
                TogglePresentation();
                e.Handled = true;
            }
            else if (e.Key == Key.F12 && AppSettings.Get(AppSettings.DevToolsKey, false))
            {
                var tgt = ActiveOrLeft;
                if (tgt?.WebView?.CoreWebView2 != null)
                {
                    tgt.WebView.CoreWebView2.OpenDevToolsWindow();
                    e.Handled = true;
                }
            }
        }


        private void OnOpenL(object sender, RoutedEventArgs e)
        {
            _activePanel = _app.Left;
            UpdateActivePanelVisual();
            ShowOpenFor(_app.Left);
        }

        private void OnExportL(object sender, RoutedEventArgs e) => ShowExportDialog(_app.Left);

        private void OnOpenR(object sender, RoutedEventArgs e)
        {
            EnsureRightPanel();
            _activePanel = _app.Right;
            UpdateActivePanelVisual();
            ShowOpenFor(_app.Right);
        }

        private void OnExportR(object sender, RoutedEventArgs e)
        {
            EnsureRightPanel();
            ShowExportDialog(_app.Right);
        }

        /// <summary>显示 pandoc 导出菜单/对话框。</summary>
        private async void ShowExportDialog(PanelState state)
        {
            if (string.IsNullOrEmpty(state.CurrentFile))
            {
                StatusText.Text = "当前面板未打开文件，无法导出";
                return;
            }

            // 检查 pandoc 可用性
            var pandocPath = _pandoc.FindPandoc();
            if (pandocPath == null)
            {
                var action = ShowPandocMissingDialog();
                if (action == PandocMissingAction.SetPath)
                {
                    var dlg = new OpenFileDialog
                    {
                        Title = "选择 pandoc.exe",
                        Filter = "pandoc 可执行文件|pandoc.exe|所有文件|*.*",
                        CheckFileExists = true
                    };
                    if (dlg.ShowDialog() == true)
                    {
                        _pandoc.SetCustomPath(dlg.FileName);
                        pandocPath = _pandoc.FindPandoc();
                    }
                }
                else if (action == PandocMissingAction.Download)
                {
                    OpenInSystemBrowser("https://pandoc.org/installing.html");
                    StatusText.Text = "已打开 pandoc 下载页，安装后将 pandoc.exe 目录加入 PATH，或回到此处「选择 pandoc.exe」";
                    return;
                }

                if (pandocPath == null)
                {
                    LogWarn("Export failed: pandoc not found");
                    return;
                }
            }

            // 显示导出格式选择 + 自定义保存路径
            var export = await ShowExportPicker(state);
            if (export == null) return; // 用户取消

            StatusText.Text = $"正在导出为 {export.Format}...";

            var result = await _pandoc.ExportAsync(state.CurrentFile, outputPath: export.OutputPath, format: export.Format);
            if (result.Success)
            {
                StatusText.Text = $"导出成功: {result.Message}";
                LogInfo("Exported: " + Path.GetFileName(result.Message));
            }
            else
            {
                StatusText.Text = result.Message;
                LogErr("Export: " + result.Message);
            }
        }

        /// <summary>pandoc 缺失时用户的选择。</summary>
        private enum PandocMissingAction { None, SetPath, Download }

        /// <summary>弹出对话框提示 pandoc 未安装（主题化：背景/文字/按钮走主题字典），返回用户选择的操作。</summary>
        private PandocMissingAction ShowPandocMissingDialog()
        {
            var result = PandocMissingAction.None;
            var win = new Window
            {
                Title = "需要 pandoc",
                Width = 460, SizeToContent = System.Windows.SizeToContent.Height,
                Background = (System.Windows.Media.Brush)FindResource("WindowBackgroundBrush")
            };

            var panel = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(20) };
            var msg = new System.Windows.Controls.TextBlock
            {
                Text = "导出功能需要 pandoc，但当前未检测到。",
                FontWeight = System.Windows.FontWeights.SemiBold,
                FontSize = 14,
                Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush"),
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(0, 0, 0, 10)
            };
            var hint = new System.Windows.Controls.TextBlock
            {
                Text = "• 已安装 pandoc：点击「选择 pandoc.exe」指定其路径\n• 未安装：点击「下载 pandoc」打开官网，安装后将其目录加入 PATH 或回到此处指定",
                FontSize = 12,
                Foreground = (System.Windows.Media.Brush)FindResource("TextBodyBrush"),
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(0, 0, 0, 16)
            };
            panel.Children.Add(msg);
            panel.Children.Add(hint);

            var row = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right
            };
            var chooseBtn = new System.Windows.Controls.Button
            {
                Content = "选择 pandoc.exe",
                Style = (Style)FindResource("ChipBtn"),
                FontSize = 12,
                Padding = new System.Windows.Thickness(12, 6, 12, 6),
                Margin = new System.Windows.Thickness(0, 0, 8, 0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            chooseBtn.Click += (_, _) => { result = PandocMissingAction.SetPath; win.Close(); };
            var dlBtn = new System.Windows.Controls.Button
            {
                Content = "下载 pandoc",
                Style = (Style)FindResource("ChipBtn"),
                FontSize = 12,
                Padding = new System.Windows.Thickness(12, 6, 12, 6),
                Margin = new System.Windows.Thickness(0, 0, 8, 0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            dlBtn.Click += (_, _) => { result = PandocMissingAction.Download; win.Close(); };
            var cancelBtn = new System.Windows.Controls.Button
            {
                Content = "取消",
                Style = (Style)FindResource("ChipBtn"),
                FontSize = 12,
                Padding = new System.Windows.Thickness(12, 6, 12, 6),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            cancelBtn.Click += (_, _) => { result = PandocMissingAction.None; win.Close(); };
            row.Children.Add(chooseBtn);
            row.Children.Add(dlBtn);
            row.Children.Add(cancelBtn);
            panel.Children.Add(row);

            ShowThemedDialog(win, w => w.Content = panel);
            return result;
        }

        /// <summary>弹出导出对话框（独立 ExportDialog 窗口，颜色取自 ThemeColors）。用户取消返回 null。</summary>
        private Task<ExportDialogResult?> ShowExportPicker(PanelState state)
        {
            if (state?.CurrentFile == null) return Task.FromResult<ExportDialogResult?>(null);
            var dlg = new ExportDialog { Owner = this };
            dlg.SetSourceFile(state.CurrentFile);
            ShowThemedDialog(dlg, _ => { });
            return Task.FromResult(dlg.Result);
        }

    }
}
