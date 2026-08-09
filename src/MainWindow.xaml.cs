using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Microsoft.Win32;
using Markdig;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Web.WebView2.Core;

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
        // 双栏滚动同步防重入标志
        private bool _isSyncingScroll;

        public MainWindow()
        {
            InitializeComponent();
            SetWindowIcon();

            // 全局单例主题：App.OnStartup 已初始化，所有窗口共享（多窗口切换主题互相跟随）。
            // 窗口显示前首次合入目标主题字典——若放到 OnLoaded（窗口已显示），Window.Background 等
            // DynamicResource 会先解析到 App 级亮色兜底、合入暗色字典后再重解析 → 启动瞬间闪色。
            _theme = App.GlobalTheme;
            _theme.ApplyToWindow(this, _theme.Current == _theme.Dark ? _theme.Dark : _theme.Light);
            UpdateThemeButton();

            Loaded += OnLoaded;
            KeyDown += OnWindowKeyDown;
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
                CleanupPanel(_app.Left);
                CleanupPanel(_app.Right);
            }
            catch (Exception ex) { LogErr("OnClosed cleanup: " + ex.Message); }
            base.OnClosed(e);
        }

        private static string WindowStatePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SeeMe", "window.json");

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

        // ──────────────── WebView2 缓存定期清理（保留设置/历史/书签） ────────────────

        /// <summary>上次清理标记文件；距今超过清理间隔（默认 7 天）才再次清理。</summary>
        private static string WebViewCacheCleanupMarker => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SeeMe", "webview2-cache-cleaned.txt");
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

        /// <summary>应用历史上限，立即裁剪并持久化。</summary>
        public void ApplyHistorySize(int n)
        {
            AppSettings.Set(AppSettings.HistorySizeKey, n);
            if (_app?.History != null) _app.History.SetMaxEntries(n);
        }

        /// <summary>将默认缩放应用到当前两个面板并持久化。</summary>
        public void ApplyDefaultZoomNow()
        {
            var z = AppSettings.Get(AppSettings.DefaultZoomKey, 1.0);
            if (_app?.Left != null) _app.Left.FontScale = z;
            if (_app?.Right != null) _app.Right.FontScale = z;
            ApplyPanelZoom(_app?.Left);
            ApplyPanelZoom(_app?.Right);
        }

        /// <summary>按设置应用信息面板默认展开/收起。</summary>
        public void ApplyInfoPanelDefault()
        {
            if (_app == null) return;
            SetInfoPanelVisibility(AppSettings.Get(AppSettings.InfoPanelVisibleKey, true));
        }

        /// <summary>设置自定义 CSS 路径（空=清除），持久化并重渲染。</summary>
        public void ApplyCustomCssNow(string path)
        {
            AppSettings.Set(AppSettings.CustomCssKey, path ?? "");
            _customCssPath = string.IsNullOrEmpty(path) ? null : path;
            ReloadOpenPanels();
        }

        /// <summary>主题色切换（indigo/blue/green），持久化 + WPF 资源动画 + 预览页重渲染。</summary>
        public void ApplyAccentNow(string accent)
        {
            AppSettings.Set(AppSettings.AccentKey, accent);
            _theme.ApplyAccent(accent);
            ReloadOpenPanels(); // CSS 变量跟随 ActiveAccent，重渲染让预览同步换色
        }

        /// <summary>Markdown 渲染风格（default/github/simple），持久化并重渲染。</summary>
        public void ApplyMdStyleNow(string style)
        {
            AppSettings.Set(AppSettings.MdStyleKey, style);
            if (_render is RenderService rs) rs.MdStyle = style;
            ReloadOpenPanels();
        }

        /// <summary>护眼模式开关，持久化并重渲染（暖色滤镜作用于所有 HTML 预览页）。</summary>
        public void ApplyEyeCareNow(bool on)
        {
            AppSettings.Set(AppSettings.EyeCareKey, on);
            if (_render is RenderService rs) rs.EyeCare = on;
            ReloadOpenPanels();
        }

        /// <summary>动画效果开关（主题切换过渡动画）。</summary>
        public void ApplyAnimationsNow(bool on)
        {
            AppSettings.Set(AppSettings.AnimationsKey, on);
            _theme.AnimationsEnabled = on;
        }

        /// <summary>字号/行高变更：持久化并重渲染（缩放倍数以新字号为基础）。</summary>
        public void ApplyFontSettingsNow()
        {
            if (_render is RenderService rs)
            {
                rs.FontSize = AppSettings.Get(AppSettings.FontSizeKey, 14);
                rs.LineHeight = AppSettings.Get(AppSettings.LineHeightKey, 1.65);
            }
            ReloadOpenPanels();
        }

        /// <summary>当前动画开关（设置对话框读回用）。</summary>
        public bool ThemeAnimationsEnabled => _theme.AnimationsEnabled;

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

            // 先设置 WebView 默认背景避免闪白（跟随主题画布色）
            var initBg = ThemeWebViewBg(_theme.Current == _theme.Dark);
            WebViewL.DefaultBackgroundColor = initBg;
            WebViewR.DefaultBackgroundColor = initBg;

            try
            {
                // 显式指定 UserDataFolder（%LOCALAPPDATA%\SeeMe\WebView2Data）：
                // 1) 安装到 Program Files 等只读目录时不再依赖 exe 旁的默认数据目录（SeeMe.exe.WebView2，不可写）；
                // 2) 缓存位置确定，便于定期清理；设置/历史/书签（%LOCALAPPDATA%\SeeMe\*.json）不受影响。
                var webviewDataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SeeMe", "WebView2Data");
                var env = await CoreWebView2Environment.CreateAsync(null, webviewDataDir);
                await WebViewL.EnsureCoreWebView2Async(env);
                await WebViewR.EnsureCoreWebView2Async(env);

                // 定期清理 WebView2 浏览数据缓存（含打开过的 PDF/文档残留），默认 7 天一清；不影响设置/历史
                TryClearWebViewCacheIfDue();
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
            // 渲染进程崩溃恢复：记录 + 自动重载当前文件；连续崩溃 >= 3 次（5 秒窗口）停止重载并提示
            WebViewL.CoreWebView2.ProcessFailed += (_, ea) => OnProcessFailed(_app.Left!, ea);
            WebViewR.CoreWebView2.ProcessFailed += (_, ea) => OnProcessFailed(_app.Right!, ea);

            // 注：PDF 虚拟主机（SetVirtualHostNameToFolderMapping）的请求不经 WebResourceRequested，
            // 无法用网络层事件观察 Range；已在 viewer.js 的 blob worker 里注入统计探针（kind:pdf 上报）。

            // 导航完成后补推一次主题：任何页面（含错误/加载/占位页）加载完都必然收到当前主题，
            // 即使切换发生在导航进行中，也不会出现"外壳变了 body 没变"的时序窗口。
            WebViewL.NavigationCompleted += (_, _) => { _ = PushThemeAsync(_theme.Current == _theme.Dark); ApplyPanelZoom(_app.Left); };
            WebViewR.NavigationCompleted += (_, _) => { _ = PushThemeAsync(_theme.Current == _theme.Dark); ApplyPanelZoom(_app.Right); };
            // 调试：WebView 控制台消息（含 JS 错误/CSP 拦截/import 失败）转发到日志，便于诊断 PDF.js 等页面问题
            // 注：CoreWebView2 在本 SDK 版本无 ConsoleMessage 事件，改用 viewer.js 内 postMessage 上报（kind:pdf）。

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
                StatusText.Text = "Ctrl+O 打开 ？直接拖入 md/pdf/xlsx/pptx/docx 文件 ？Ctrl+T 分栏";
            UpdateWindowTitle();
        }

        private void OnWindowKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F5)
            {
                var target = _activePanel ?? _app.Left;
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
                var tgt = _activePanel ?? _app.Left;
                if (tgt != null) ShowFind(tgt);
                e.Handled = true;
            }
            else if (ctrl && e.Key == Key.D0)
            {
                SetZoom(_activePanel ?? _app.Left, 1.0);
                e.Handled = true;
            }
            else if (ctrl && (e.Key == Key.Add || e.Key == Key.OemPlus || e.Key == Key.Subtract || e.Key == Key.OemMinus))
            {
                var tgt = _activePanel ?? _app.Left;
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
            else if (e.Key == Key.F12 && AppSettings.Get(AppSettings.DevToolsKey, false))
            {
                var tgt = _activePanel ?? _app.Left;
                if (tgt?.WebView?.CoreWebView2 != null)
                {
                    tgt.WebView.CoreWebView2.OpenDevToolsWindow();
                    e.Handled = true;
                }
            }
        }

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

        private void OnWebMessage(PanelState state, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string? raw = null;
            try
            {
                raw = e.TryGetWebMessageAsString();
                if (string.IsNullOrEmpty(raw)) return;
                using var doc = System.Text.Json.JsonDocument.Parse(raw);
                var root = doc.RootElement;
                if (!root.TryGetProperty("kind", out var kindEl)) return;
                var kind = kindEl.GetString();
                if (kind == "pdf")
                {
                    // PDF.js 查看器状态上报。仅记录错误路径（避免每次打开 PDF 刷屏日志）；
                    // NavigateToString 页 Source 为 about:blank，因此仅对空/空白 Source 放行，其余仍走来源校验。
                    var pdfSrcOk = string.IsNullOrEmpty(e.Source) || e.Source == "about:blank";
                    if (pdfSrcOk && root.TryGetProperty("msg", out var pdfEl))
                    {
                        var pdfMsg = pdfEl.GetString();
                        // 仅记录错误、成功摘要与加载模式（每文档一条），跳过 worker/进度等详细步骤日志
                        if (!string.IsNullOrEmpty(pdfMsg) && (pdfMsg.Contains("load error") || pdfMsg.Contains("loaded ") || pdfMsg.Contains("stream mode") || pdfMsg.Contains("buffer mode")))
                            LogErr("[PDF] " + pdfMsg);
                    }
                    return;
                }
                // 来源校验：仅接受来自当前 WebView 已加载页面的消息，防止伪造 postMessage 干扰 UI 或触发外链。
                var webSrc = state.WebView?.CoreWebView2?.Source;
                if (!string.IsNullOrEmpty(e.Source) && !string.IsNullOrEmpty(webSrc) && e.Source != webSrc)
                    return;
                if (kind == "scroll" && root.TryGetProperty("y", out var yEl))
                {
                    state.LastScrollY = yEl.GetDouble();
                    // 双栏滚动同步：两侧都是文档类（均含 .md / 均含 .html 页面）时联动机滚动
                    if (_app.IsSplitMode && !_isSyncingScroll)
                    {
                        var other = state == _app.Left ? _app.Right : _app.Left;
                        if (other?.WebView?.CoreWebView2 != null
                            && !string.IsNullOrEmpty(other.CurrentFile)
                            && !string.IsNullOrEmpty(state.CurrentFile))
                        {
                            // 限制为同类型文件之间同步（md ↔ md；其他类型不强制同步）
                            var ext = Path.GetExtension(state.CurrentFile).ToLowerInvariant();
                            var otherExt = Path.GetExtension(other.CurrentFile).ToLowerInvariant();
                            if (ext == otherExt)
                            {
                                _isSyncingScroll = true;
                                var y = yEl.GetDouble();
                                var js = $"window.scrollTo(0, {y.ToString(System.Globalization.CultureInfo.InvariantCulture)})";
                                try { other.WebView.CoreWebView2.ExecuteScriptAsync(js); } catch { }
                                // 50ms 后释放防重入锁，避免同步触发的 scroll 事件传回本侧
                                _ = System.Threading.Tasks.Task.Delay(50).ContinueWith(_ =>
                                    Dispatcher.BeginInvoke(new Action(() => _isSyncingScroll = false)));
                            }
                        }
                    }
                }
                else if (kind == "drop" && root.TryGetProperty("paths", out var pathsEl)
                         && pathsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    // WebView 页面内拖入文件（File.path 经 postMessage 传回），与窗口级 WPF 拖放共用打开逻辑
                    var paths = new List<string>();
                    foreach (var item in pathsEl.EnumerateArray())
                    {
                        var p = item.GetString();
                        if (!string.IsNullOrEmpty(p)) paths.Add(p);
                    }
                    if (paths.Count > 0)
                    {
                        var total = paths.Count;
                        Dispatcher.BeginInvoke(new Action(() =>
                            OpenDroppedFiles(FilterSupportedFiles(paths), total)));
                    }
                }
                else if (kind == "link" && root.TryGetProperty("href", out var hrefEl))
                {
                    var href = hrefEl.GetString();
                    if (!string.IsNullOrEmpty(href))
                    {
                        // 纵深防御：显式拒绝危险伪协议（XSS 载体），即使 CSP 与 OpenInSystemBrowser 已拦截
                        var lower = href.ToLowerInvariant();
                        if (lower.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
                            || lower.StartsWith("vbscript:", StringComparison.OrdinalIgnoreCase))
                        {
                            LogErr("Blocked dangerous link scheme: " + lower);
                            return;
                        }
                        if (!(lower.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                             || lower.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                             || lower.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
                             || lower.StartsWith("#", StringComparison.Ordinal)))
                        {
                            OpenInSystemBrowser(href);
                        }
                    }
                }
                else if (kind == "dismiss-welcome")
                {
                    // 欢迎提示卡片"不再显示"：持久化开关 + 清空为透明背景页
                    _theme.SetWelcomeDismissed(true);
                    state.WebView?.NavigateToString(EmptyPage);
                }
                else if (kind == "search-result")
                {
                    // 页内搜索回报：更新打开中的搜索栏结果文案
                    var count = root.TryGetProperty("count", out var cEl) ? cEl.GetInt32() : 0;
                    var current = root.TryGetProperty("current", out var iEl) ? iEl.GetInt32() : 0;
                    StatusText.Text = count > 0 ? $"找到 {count} 处，当前第 {current} 处" : "未找到匹配内容";
                }
                else if (kind == "open-file")
                {
                    // 点击欢迎提示卡片 → 打开文件
                    _activePanel = state;
                    UpdateActivePanelVisual();
                    ShowOpenFor(state);
                }
            }
            catch (Exception ex) { LogErr("Web msg parse: " + ex.Message + " | raw=" + (raw ?? "<null>")); }
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
            if (!_app.IsSplitMode) SetSplitMode(true);
            _activePanel = _app.Right;
            UpdateActivePanelVisual();
            ShowOpenFor(_app.Right);
        }

        private void OnExportR(object sender, RoutedEventArgs e)
        {
            if (!_app.IsSplitMode) SetSplitMode(true);
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
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = System.Windows.ResizeMode.NoResize,
                Background = (System.Windows.Media.Brush)FindResource("WindowBackgroundBrush")
            };
            // 合入主题字典：独立 Window 的 DynamicResource 需要经 ApplyToWindow 才能解析到主题资源
            _theme.ApplyToWindow(win, _theme.Current);

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

            win.Content = panel;
            win.ShowDialog();
            return result;
        }

        /// <summary>弹出导出对话框（独立 ExportDialog 窗口，颜色取自 ThemeColors）。用户取消返回 null。</summary>
        private Task<ExportDialogResult?> ShowExportPicker(PanelState state)
        {
            if (state?.CurrentFile == null) return Task.FromResult<ExportDialogResult?>(null);
            var dlg = new ExportDialog { Owner = this };
            // 合入主题字典：ExportDialog 是独立 Window，DynamicResource 需经 ApplyToWindow 才能取到主题资源
            _theme.ApplyToWindow(dlg, _theme.Current);
            dlg.SetSourceFile(state.CurrentFile);
            dlg.ShowDialog();
            return Task.FromResult(dlg.Result);
        }

        public void OpenFile(string path)
        {
            if (_app.Left != null && File.Exists(path))
                OpenFileInternal(_app.Left, path);
        }

    }
}
