using System;
using System.Text;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;
using System.Collections.Generic;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace SeeMe
{
    public partial class MainWindow
    {
        private async void OnManualRefresh(object sender, RoutedEventArgs e)
        {
            if (_app == null) return;
            RefreshBtn.Content = "⟳";
            try
            {
                var targets = _app.IsSplitMode
                    ? new[] { _app.Left, _app.Right }
                    : new[] { _app.Left };
                foreach (var state in targets.Where(s => s != null && !string.IsNullOrEmpty(s.CurrentFile)))
                    await ReloadFileAsync(state, force: true);
                StatusText.Text = "刷新完成";
            }
            catch (Exception ex)
            {
                LogErr("Manual refresh: " + ex);
                StatusText.Text = "刷新失败";
            }
            finally
            {
                RefreshBtn.Content = "🔄";
            }
        }


        private void RenderErrorPage(PanelState state, Exception ex)
        {
            if (state?.WebView?.CoreWebView2 == null) return;
            var msg = System.Security.SecurityElement.Escape(ex.Message);
            var stack = System.Security.SecurityElement.Escape(ex.StackTrace ?? "(无堆栈信息)");
            var css = @"
  * { margin:0; padding:0; box-sizing:border-box; }
  html,body { width:100%; height:100vh; }
  body { background:var(--bg); color:var(--text); padding:40px; font-family:'Microsoft YaHei','PingFang SC',sans-serif; line-height:1.6; transition:background-color .3s ease,color .3s ease; }
  h2 { font-size:20px; color:var(--danger); margin-bottom:14px; }
  .box { background:var(--card); border:1px solid var(--border); border-radius:10px; padding:16px 18px; }
  .msg { font-size:14px; word-break:break-all; white-space:pre-wrap; }
  .stack { margin-top:14px; font-size:11px; color:var(--secondary); white-space:pre-wrap; max-height:40vh; overflow:auto; }
  .hint { margin-top:16px; font-size:12px; color:var(--secondary); }
";
            var html = RenderService.WrapPage(_theme.Current == _theme.Dark, "预览失败", css, @"
  <h2>⚠ 预览失败</h2>
  <div class='box'>
    <div class='msg'>" + msg + @"</div>
    <div class='stack'>" + stack + @"</div>
  </div>
  <div class='hint'>可尝试重新打开文件，或按 F5 手动刷新。详细日志见错误日志文件。</div>");
            try { state.WebView.NavigateToString(html); } catch (Exception ex2) { LogErr("RenderErrorPage: " + ex2.Message); }
            SetOutline(null);
        }

        /// <summary>文件被删除/移动后显示的占位页（统一外壳，支持 setTheme 切换）。</summary>

        private void RenderFileGonePage(PanelState state)
        {
            if (state?.WebView?.CoreWebView2 == null) return;
            var name = System.Security.SecurityElement.Escape(Path.GetFileName(state.CurrentFile ?? ""));
            var css = @"
  * { margin:0; padding:0; box-sizing:border-box; }
  html,body { width:100%; height:100vh; }
  body { background:var(--bg); color:var(--text); padding:48px; font-family:'Microsoft YaHei','PingFang SC',sans-serif; text-align:center; transition:background-color .3s ease,color .3s ease; }
  .icon { font-size:42px; margin-bottom:12px; }
  h2 { font-size:18px; color:var(--heading); margin-bottom:8px; }
  p { font-size:13px; }
";
            var html = RenderService.WrapPage(_theme.Current == _theme.Dark, "文件已删除或移动", css, @"
  <div class='icon'>🗑</div>
  <h2>文件已删除或移动</h2>
  <p>" + name + @"</p>
  <p style='margin-top:8px;opacity:.7'>重新打开文件以继续预览</p>");
            try { state.WebView.NavigateToString(html); } catch (Exception ex2) { LogErr("RenderFileGonePage: " + ex2.Message); }
            SetOutline(null);
        }

        /// <summary>生成主题一致的提示页（大文件等），统一外壳 + setTheme。</summary>

        private static string BuildNoticePage(bool isDark, string title, string msg)
        {
            var css = @"
* { margin:0; padding:0; box-sizing:border-box; }
html,body { width:100%; height:100vh; }
body { display:flex; align-items:center; justify-content:center; background:var(--bg); color:var(--text);
       font-family:'Segoe UI','Microsoft YaHei',sans-serif; font-size:14px; text-align:center;
       transition:background-color .3s ease,color .3s ease; }
.wrap { max-width:480px; padding:32px; }
h2 { font-size:20px; color:var(--heading); margin-bottom:10px; }
p { font-size:13px; color:var(--secondary); }
";
            return RenderService.WrapPage(isDark, title, css, $@"<div class='wrap'>
  <h2>{System.Security.SecurityElement.Escape(title)}</h2>
  <p>{System.Security.SecurityElement.Escape(msg)}</p>
</div>");
        }

        /// <summary>生成一个主题一致的"加载中"占位页（旋转指示器），统一外壳 + setTheme。</summary>

        private static string BuildLoadingPage(bool isDark)
        {
            var css = @"
  * { margin:0; padding:0; box-sizing:border-box; }
  html,body { width:100%; height:100vh; }
  body { display:flex; align-items:center; justify-content:center; background:var(--bg); color:var(--text);
         font-family:'Segoe UI','Microsoft YaHei',sans-serif; font-size:15px;
         transition:background-color .3s ease,color .3s ease; }
  .spin { width:18px; height:18px; margin-right:10px; border:2px solid var(--border); border-top-color:var(--accent);
           border-radius:50%; display:inline-block; animation:spin .8s linear infinite; vertical-align:middle; }
  @keyframes spin { to { transform:rotate(360deg); } }
";
            return RenderService.WrapPage(isDark, "加载中", css, "<span class='spin'></span>加载中…");
        }


        private async Task ReloadFileAsync(PanelState state, bool force = false, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(state.CurrentFile) || !File.Exists(state.CurrentFile)) return;
            // 内容感知：文件时间戳与大小都没变则跳过刷新，避免编辑器快速保存产生重复重渲染
            if (!force && !ContentChanged(state)) return;
            // 检查取消
            if (ct.IsCancellationRequested) return;

            var ext = Path.GetExtension(state.CurrentFile).ToLowerInvariant();
            try
            {
                switch (ext)
                {
                    case ".pdf":
                        await RenderPdf(state); MarkLoaded(state); SetOutline(null); return;
                    case ".xlsx":
                    case ".xls":
                        await RenderExcel(state); MarkLoaded(state); SetOutline(null); return;
                    case ".pptx":
                        await RenderPpt(state); MarkLoaded(state); SetOutline(null); return;
                    case ".docx":
                        await RenderDocx(state); MarkLoaded(state); SetOutline(null); return;
                }
            }
            catch (OperationCanceledException) { LogInfo("Reload cancelled (office): " + Path.GetFileName(state.CurrentFile)); return; }
            catch (Exception ex)
            {
                LogErr("Reload office: " + ex);
                if (!ct.IsCancellationRequested) RenderErrorPage(state, ex);
                StatusText.Text = "读取失败: " + ex.Message;
                return;
            }

            if (ct.IsCancellationRequested) return;

// ═══════════════ 以下为原始 Markdown 渲染逻辑 ═══════════════
            try
            {
                ct.ThrowIfCancellationRequested();

                if (CheckFileSize(state))
                {
                    MarkLoaded(state);
                    return;
                }

                var isDark = _theme.Current == _theme.Dark;
                // 大文件：先在 UI 线程显示加载占位，避免读取/解析阻塞界面导致假死
                const long LargeMdThreshold = 1L * 1024 * 1024;
                if (new FileInfo(state.CurrentFile).Length > LargeMdThreshold)
                    state.WebView.NavigateToString(BuildLoadingPage(isDark));

                ct.ThrowIfCancellationRequested();

                // 读取 + Markdig 解析 + 后处理统一在后台线程执行，避免大文件阻塞 UI
                var (htmlRaw, fmCard, statsCard, tocCard, tocItems) = await Task.Run(() =>
                {
                    ct.ThrowIfCancellationRequested();
                    var md = ReadTextAuto(state.CurrentFile);
                    md = _render.StripBom(md);
                    md = _render.StripYamlFrontMatter(md, out var fmBlock);
                    var card = _render.BuildFrontMatterCard(fmBlock);
                    var parsed = Markdig.Markdown.ToHtml(md ?? "", _render.Pipeline);
                    parsed = _render.ProcessRelativePaths(parsed, state.FileDir);
                    parsed = _render.WrapTables(parsed);
                    parsed = _render.UpgradePreBlocks(parsed);
                    parsed = _render.SanitizeLinkHrefs(parsed);
                    return (parsed, card, BuildStatsCard(md ?? ""), _render.BuildTocCard(parsed), _render.BuildTocItems(parsed));
                }, ct);

                ct.ThrowIfCancellationRequested();

                SetOutline(tocItems);
                var html = _render.Render(htmlRaw, state, fmCard + tocCard + statsCard, this, null, LoadCustomCss());
                state.WebView.NavigateToString(html);
                MarkLoaded(state);
            }
            catch (OperationCanceledException) { LogInfo("Reload cancelled (md): " + Path.GetFileName(state.CurrentFile)); }
            catch (Exception ex)
            {
                LogErr("Reload: " + ex);
                if (!ct.IsCancellationRequested) RenderErrorPage(state, ex);
                StatusText.Text = "读取失败: " + ex.Message;
            }
        }

        /// <summary>填充左侧导航栏大纲列表；null/空 → 显示占位提示。跨线程安全。</summary>
        private void SetOutline(List<TocItem>? items)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => SetOutline(items));
                return;
            }
            OutlineList.Items.Clear();
            var list = items ?? new List<TocItem>();
            if (list.Count == 0)
            {
                OutlineList.Visibility = Visibility.Collapsed;
                OutlineHint.Visibility = Visibility.Visible;
                OutlineCount.Visibility = Visibility.Collapsed;
                return;
            }
            foreach (var it in list)
            {
                var tb = new TextBlock
                {
                    Text = it.Title,
                    FontSize = 11,
                    Margin = new Thickness((it.Level - 1) * 10, 0, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    ToolTip = it.Title
                };
                OutlineList.Items.Add(new ListBoxItem { Content = tb, Tag = it });
            }
            OutlineList.Visibility = Visibility.Visible;
            OutlineHint.Visibility = Visibility.Collapsed;
            OutlineCount.Visibility = Visibility.Visible;
            OutlineCount.Text = list.Count + " 项";
        }

        /// <summary>点击大纲条目：在当前聚焦面板（默认左栏）中平滑滚动到对应标题锚点。</summary>
        private void OnOutlineClick(object sender, MouseButtonEventArgs e)
        {
            if (OutlineList.SelectedItem is ListBoxItem li && li.Tag is TocItem toc)
            {
                var target = _activePanel ?? _app.Left;
                if (target?.WebView?.CoreWebView2 == null) return;
                var js = "var el=document.getElementById("
                         + System.Text.Json.JsonSerializer.Serialize(toc.Id)
                         + ");if(el)el.scrollIntoView({behavior:'smooth',block:'start'});";
                _ = target.WebView.CoreWebView2.ExecuteScriptAsync(js);
            }
        }

        /// <summary>读取用户自定义 CSS 内容（限量 256KB），失败返回空字符串。</summary>
        private string LoadCustomCss()
        {
            try
            {
                if (string.IsNullOrEmpty(_customCssPath) || !File.Exists(_customCssPath)) return "";
                var fi = new FileInfo(_customCssPath);
                if (fi.Length > 256 * 1024) return "";
                return File.ReadAllText(_customCssPath);
            }
            catch { return ""; }
        }

        private void OnPickCustomCss(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = "CSS 文件 (*.css)|*.css|所有文件 (*.*)|*.*",
                    Title = "选择自定义样式文件"
                };
                if (dlg.ShowDialog(this) == true)
                {
                    _customCssPath = dlg.FileName;
                    AppSettings.Set(AppSettings.CustomCssKey, dlg.FileName);
                    StatusText.Text = "已启用自定义样式: " + Path.GetFileName(dlg.FileName);
                    // 立即重渲染当前文件使样式生效
                    if (_app.Left != null && !string.IsNullOrEmpty(_app.Left.CurrentFile))
                        _ = ReloadFileAsync(_app.Left, force: true);
                    if (_app.IsSplitMode && _app.Right != null && !string.IsNullOrEmpty(_app.Right.CurrentFile))
                        _ = ReloadFileAsync(_app.Right, force: true);
                }
            }
            catch (Exception ex) { LogErr("OnPickCustomCss: " + ex.Message); }
        }

        /// <summary>状态栏「? 快捷键」按钮事件：弹出快捷键速查窗口。</summary>
        private void OnShowShortcuts(object sender, RoutedEventArgs e) => ShowShortcutsDialog();

        /// <summary>状态栏「? 快捷键」：弹出主题化快捷键速查窗口（分组展示，替代 tooltip）。</summary>
        private void ShowShortcutsDialog()
        {
            try
            {
                var w = new Window
                {
                    Title = "快捷键",
                    Width = 400,
                    SizeToContent = SizeToContent.Height,
                    Owner = this,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    ResizeMode = ResizeMode.NoResize,
                    ShowInTaskbar = false,
                    Background = (Brush)TryFindResource("CardBackgroundBrush") ?? Brushes.White
                };
                var panel = new StackPanel { Margin = new Thickness(20, 18, 20, 16) };
                panel.Children.Add(new TextBlock
                {
                    Text = "⌨  快捷键",
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)TryFindResource("TextPrimaryBrush") ?? Brushes.Black,
                    Margin = new Thickness(0, 0, 0, 6)
                });

                void AddGroup(string group)
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = group,
                        FontSize = 11,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = (Brush)TryFindResource("AccentBrush") ?? Brushes.Indigo,
                        Margin = new Thickness(0, 10, 0, 2)
                    });
                }
                void AddItem(string key, string desc)
                {
                    var row = new Grid { Margin = new Thickness(0, 3, 0, 0) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    var k = new TextBlock
                    {
                        Text = key, FontSize = 12, FontWeight = FontWeights.SemiBold,
                        Foreground = (Brush)TryFindResource("TextBodyBrush") ?? Brushes.DimGray,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    var d = new TextBlock
                    {
                        Text = desc, FontSize = 12,
                        Foreground = (Brush)TryFindResource("TextSecondaryBrush") ?? Brushes.Gray,
                        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0)
                    };
                    Grid.SetColumn(k, 0);
                    Grid.SetColumn(d, 1);
                    row.Children.Add(k);
                    row.Children.Add(d);
                    panel.Children.Add(row);
                }

                AddGroup("文件操作");
                AddItem("Ctrl+O", "打开文件");
                AddItem("Ctrl+T", "分栏 / 取消分栏");
                AddItem("Ctrl+N", "新建窗口");
                AddItem("Ctrl+W", "关闭窗口");
                AddItem("Ctrl+F", "页内查找");
                AddGroup("视图操作");
                AddItem("F5", "刷新当前文档");
                AddItem("Ctrl+Shift+D", "切换主题");
                AddItem("Ctrl+0", "缩放复位 100%");
                AddItem("Ctrl+= / Ctrl+-", "放大 / 缩小");
                AddItem("Esc", "取消面板选中");
                if (AppSettings.Get(AppSettings.DevToolsKey, false))
                {
                    AddGroup("调试");
                    AddItem("F12", "开发者工具");
                }

                var closeBtn = new Button
                {
                    Content = "关闭",
                    Style = (Style)TryFindResource("ChipBtn"),
                    FontSize = 12,
                    Padding = new Thickness(16, 6, 16, 6),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 14, 0, 0),
                    Cursor = Cursors.Hand,
                    IsDefault = true
                };
                closeBtn.Click += (_, _) => w.Close();
                panel.Children.Add(closeBtn);

                w.Content = panel;
                w.ShowDialog();
            }
            catch (Exception ex) { LogErr("ShowShortcutsDialog: " + ex.Message); }
        }

        /// <summary>将当前预览面板截图导出为图片（格式 PNG/JPG 与 1x/2x 分辨率可在设置中配置）。</summary>
        private async void OnExportImage(object sender, RoutedEventArgs e)
        {
            var state = _activePanel ?? _app.Left;
            if (state?.WebView?.CoreWebView2 == null || state.WebView.ActualWidth < 10 || state.WebView.ActualHeight < 10)
            {
                StatusText.Text = "无可导出的预览内容";
                return;
            }
            try
            {
                var isJpg = AppSettings.Get(AppSettings.ImageFormatKey, "png") == "jpg";
                var scale = AppSettings.Get(AppSettings.ImageScaleKey, 1);
                var baseName = string.IsNullOrEmpty(state.CurrentFile)
                    ? "preview" : Path.GetFileNameWithoutExtension(state.CurrentFile);
                var ext = isJpg ? "jpg" : "png";

                // 固定文件夹模式：跳过保存对话框（文件夹不存在时回退询问）
                var fixedDir = AppSettings.Get(AppSettings.ExportPathModeKey, "ask") == "fixed"
                    ? AppSettings.Get(AppSettings.ExportPathKey, "") : "";
                string targetPath;
                if (!string.IsNullOrEmpty(fixedDir) && Directory.Exists(fixedDir))
                {
                    targetPath = Path.Combine(fixedDir, baseName + "." + ext);
                }
                else
                {
                    var dlg = new Microsoft.Win32.SaveFileDialog
                    {
                        FileName = baseName + "." + ext,
                        Filter = isJpg ? "JPG 图片 (*.jpg)|*.jpg" : "PNG 图片 (*.png)|*.png",
                        Title = "导出当前预览为图片"
                    };
                    if (dlg.ShowDialog(this) != true) return;
                    targetPath = dlg.FileName;
                }

                var stream = new System.IO.MemoryStream();
                await state.WebView.CoreWebView2.CapturePreviewAsync(
                    Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, stream);
                stream.Position = 0;
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.StreamSource = stream;
                bmp.EndInit();

                System.Windows.Media.Imaging.BitmapSource src = bmp;
                if (scale >= 2)
                    src = new System.Windows.Media.Imaging.TransformedBitmap(bmp, new ScaleTransform(2, 2));

                var bytes = EncodeBitmap(src, isJpg);
                await System.IO.File.WriteAllBytesAsync(targetPath, bytes);
                StatusText.Text = "已导出图片: " + Path.GetFileName(targetPath);
            }
            catch (Exception ex) { LogErr("OnExportImage: " + ex.Message); StatusText.Text = "导出图片失败: " + ex.Message; }
        }

        /// <summary>
        /// 按格式编码位图：PNG（无损）或 JPG（质量 90，透明区域铺白底，避免 JPEG 变黑）。
        /// 注：CapturePreviewAsync 无缩放参数，2x 为事后上采样而非真实更高分辨率捕获。
        /// </summary>
        private static byte[] EncodeBitmap(System.Windows.Media.Imaging.BitmapSource src, bool jpg)
        {
            System.Windows.Media.Imaging.BitmapEncoder enc;
            if (jpg)
            {
                // 透明 → 白底合成，再编码 JPEG
                var white = new System.Windows.Media.DrawingVisual();
                using (var dc = white.RenderOpen())
                {
                    dc.DrawRectangle(System.Windows.Media.Brushes.White, null, new Rect(0, 0, src.PixelWidth, src.PixelHeight));
                    dc.DrawImage(src, new Rect(0, 0, src.PixelWidth, src.PixelHeight));
                }
                var flat = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    src.PixelWidth, src.PixelHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                flat.Render(white);
                flat.Freeze();
                enc = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 90 };
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(flat));
            }
            else
            {
                enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(src));
            }
            using var ms = new System.IO.MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }

        /// <summary>判断文件自上次成功加载后内容是否变化（时间戳或大小任一不同即视为变化）。</summary>

        private static bool ContentChanged(PanelState state)
        {
            try
            {
                if (state.CurrentFile == null || !File.Exists(state.CurrentFile)) return true;
                var fi = new FileInfo(state.CurrentFile);
                if (state.LastWrite != fi.LastWriteTimeUtc) return true;
                if (state.LastLen != fi.Length) return true;
                return false;
            }
            catch { return true; }
        }

        /// <summary>
        /// 统计文本字数：中文按字符数、英文按单词数（代码块/行内代码剔除后数英文）。
        /// 信息面板「文件统计」卡与渲染页统计卡共用，保证数字一致。
        /// </summary>
        internal static (int Cjk, int Words) CountTextStats(string text)
        {
            int cjk = 0;
            foreach (var ch in text)
            {
                if (ch >= 0x4E00 && ch <= 0x9FFF || ch >= 0x3400 && ch <= 0x4DBF || ch >= 0x3040 && ch <= 0x30FF
                    || ch >= 0xAC00 && ch <= 0xD7AF || ch >= 0x3000 && ch <= 0x303F)
                    cjk++;
            }
            // 剔除代码块/行内代码干扰后数英文单词（简单处理：去掉反引号包裹的段落）
            var noCode = System.Text.RegularExpressions.Regex.Replace(text, "`[^`]*`", " ");
            noCode = System.Text.RegularExpressions.Regex.Replace(noCode, @"```[\s\S]*?```", " ");
            return (cjk, System.Text.RegularExpressions.Regex.Matches(noCode, @"[A-Za-z0-9]+(?:['\-][A-Za-z0-9]+)*").Count);
        }

        /// <summary>统计 markdown 字数与预估阅读时间，生成统计卡片（复用 fm-card 主题样式）。</summary>
        private static string BuildStatsCard(string md)
        {
            if (string.IsNullOrWhiteSpace(md)) return "";
            try
            {
                var (cjk, words) = CountTextStats(md);

                var total = cjk + words;
                if (total == 0) return "";
                // 中文阅读速度 ~400字/分钟，英文 ~200词/分钟，取加权估算
                var minutes = (int)Math.Max(1, Math.Round(cjk / 400.0 + words / 200.0));
                var secs = minutes * 60;
                string time = secs < 90 ? $"{secs} 秒" : $"{minutes} 分钟";
                var escCjk = System.Security.SecurityElement.Escape(cjk.ToString());
                var escWords = System.Security.SecurityElement.Escape(words.ToString());
                var escTime = System.Security.SecurityElement.Escape(time);
                return $"<div class=\"fm-card\" style=\"font-size:11px;color:var(--quote-text);\">" +
                       $"📊 字数 {escCjk} · 词数 {escWords} · 约 {escTime} 阅读</div>";
            }
            catch { return ""; }
        }

        /// <summary>记录当前文件的时间戳与大小，供内容感知防抖对比。</summary>

        private static void MarkLoaded(PanelState state)
        {
            try
            {
                if (state.CurrentFile == null || !File.Exists(state.CurrentFile)) return;
                var fi = new FileInfo(state.CurrentFile);
                state.LastWrite = fi.LastWriteTimeUtc;
                state.LastLen = fi.Length;
            }
            catch { }
        }

        /// <summary>统一文件大小检查：超限时渲染提示页并返回 true（调用方应直接 return）。</summary>

        private bool CheckFileSize(PanelState state)
        {
            if (string.IsNullOrEmpty(state.CurrentFile)) return true;
            var fi = new FileInfo(state.CurrentFile);
            if (fi.Length > MaxFileSize)
            {
                state.WebView.NavigateToString(
                    BuildNoticePage(_theme.Current == _theme.Dark, "文件过大", "超过 50MB 限制，无法预览"));
                return true;
            }
            return false;
        }

        /// <summary>
        /// 按 BOM → UTF-8 → GB18030/系统默认 顺序探测读取文本文件，避免 GBK/ANSI 编码的
        /// 中文 .md 文件被按 UTF-8 解码成乱码。GB18030 是 GBK 的超集（含 GB2312），
        /// 且现代 .NET 内置支持，无需注册代码页。
        /// </summary>

        private static string ReadTextAuto(string path)
        {
            var bytes = File.ReadAllBytes(path);
            // 1) BOM 优先
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            // 2) 无 BOM：先按 UTF-8 严格解析，出现替换字符说明不是 UTF-8
            var asUtf8 = Encoding.UTF8.GetString(bytes);
            if (asUtf8.IndexOf('\uFFFD') < 0) return asUtf8;
            // 3) 回退到 GB18030（GBK 超集，覆盖更广；.NET Core 内置无需注册）
            try { return Encoding.GetEncoding("GB18030").GetString(bytes); }
            catch { return Encoding.Default.GetString(bytes); }
        }

// ═══════════════ PDF / Excel / PPT 渲染 ═══════════════

        private async Task RenderPdf(PanelState state)
        {
            if (string.IsNullOrEmpty(state.CurrentFile)) return;
            if (CheckFileSize(state)) return;

            try
            {
                // 用 WebView2 虚拟主机映射 PDF 所在目录，不再生成临时 HTML 文件：
                // ① 避免 5 秒延迟删除导致加载慢时误删正在使用的文件（竞态）
                // ② 摆脱 file:// 协议（与收紧后的 CSP 冲突）
                var dir = Path.GetDirectoryName(state.CurrentFile);
                var name = Path.GetFileName(state.CurrentFile);
                if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name)) return;

                var hostName = "pdffiles.example";
                try
                {
                    state.WebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                        hostName, dir, CoreWebView2HostResourceAccessKind.Allow);
                }
                catch { /* 同一目录重复映射会抛异常，忽略（映射已存在） */ }

                // PDF.js 自绘渲染（替代原生 embed）：解锁 适合宽度/适合页面/100% 缩放、
                // 滚动/分页翻页、双击放大；背景用 var(--bg) 兜底防闪白，setTheme 切 html.dark 跟随主题。
                var isDarkPdf = _theme.Current == _theme.Dark;
                var htmlClass = isDarkPdf ? " class='dark'" : "";
                var pdfUrl = $"https://{hostName}/{System.Uri.EscapeDataString(name)}";
                var zoomMode = AppSettings.Get(AppSettings.PdfZoomModeKey, "fitWidth");
                var pageMode = AppSettings.Get(AppSettings.PdfPageModeKey, "scroll");
                var dblClick = AppSettings.Get(AppSettings.PdfDblClickZoomKey, true);

                var jsPdfUrl = System.Text.Json.JsonSerializer.Serialize(pdfUrl);
                var jsZoom = System.Text.Json.JsonSerializer.Serialize(zoomMode);
                var jsPageMode = System.Text.Json.JsonSerializer.Serialize(pageMode);

                var htmlContent = $@"<!DOCTYPE html>
<html{htmlClass}><head><meta charset='utf-8'/>
<meta name='viewport' content='width=device-width,initial-scale=1'/>
<meta http-equiv='Content-Security-Policy' content=""default-src 'self' https://appassets.example https://{hostName}; script-src 'self' https://appassets.example 'unsafe-inline'; style-src 'unsafe-inline'; img-src data: blob:; worker-src 'self' https://appassets.example blob:; connect-src 'self' https://appassets.example https://{hostName};"">
<style>
{RenderService.ThemeCss()}
  * {{ margin:0; padding:0; box-sizing:border-box; }}
  html,body {{ width:100%; height:100vh; overflow:hidden; background:var(--bg); transition:background-color .3s ease; }}
  #bar {{ position:fixed; top:0; left:0; right:0; z-index:10; display:flex; align-items:center; gap:10px;
    padding:6px 12px; background:var(--card); border-bottom:1px solid var(--border); font-size:11px; color:var(--secondary); }}
  #bar button {{ border:1px solid var(--border); background:transparent; color:var(--secondary);
    font-size:11px; padding:3px 10px; border-radius:6px; cursor:pointer; transition:all .15s ease; }}
  #bar button:hover {{ border-color:var(--accent); color:var(--heading); }}
  #bar button.active {{ background:var(--accent); border-color:var(--accent); color:#fff; }}
  #bar button:disabled {{ opacity:.4; cursor:default; }}
  #bar .spacer {{ flex:1; }}
  #pgNav {{ display:none; align-items:center; gap:8px; }}
  #status {{ max-width:340px; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }}
  #viewer {{ position:absolute; top:34px; left:0; right:0; bottom:0; overflow:auto; padding:12px; }}
  .pdf-page {{ margin:0 auto 18px; width:fit-content; }}
  .pdf-page canvas {{ display:block; background:#fff; box-shadow:0 1px 6px rgba(0,0,0,.25); border-radius:2px; }}
  html.dark .pdf-page canvas {{ box-shadow:0 2px 14px rgba(0,0,0,.5); }}
  .pg-num {{ text-align:center; font-size:10px; color:var(--secondary); margin-top:4px; }}
</style>
<script>
window.__PDF_CONF = {{ pdf: {jsPdfUrl}, zoom: {jsZoom}, pageMode: {jsPageMode}, dbl: {dblClick.ToString().ToLowerInvariant()} }};
function setTheme(dark){{var h=document.documentElement;if(dark)h.classList.add('dark');else h.classList.remove('dark');}}
</script>
</head><body>
  <div id='bar'>
    <button id='btnFW' class='active'>适合宽度</button>
    <button id='btnFP'>适合页面</button>
    <button id='btnActual'>100%</button>
    <span class='spacer'></span>
    <span id='status'></span>
    <span id='pgNav'>
      <button id='btnPrev'>◀ 上一页</button>
      <span id='pgInfo'>1 / 1</span>
      <button id='btnNext'>下一页 ▶</button>
    </span>
  </div>
  <div id='viewer'></div>
<script src='https://appassets.example/pdfjs/pdf.min.js'></script>
<script src='https://appassets.example/pdfjs/viewer.js'></script>
</body></html>";
                state.WebView.NavigateToString(htmlContent);
            }
            catch (Exception ex)
            {
                LogErr("RenderPdf: " + ex.Message);
                // 降级到文本提取（后台线程执行，避免大 PDF 卡 UI）
                var isDark = _theme.Current == _theme.Dark;
                state.WebView.NavigateToString(BuildLoadingPage(isDark));
                var content = await _converter.PdfToHtml(state.CurrentFile);
                var html = _render.BuildOfficePage(content, state, this);
                state.WebView.NavigateToString(html);
            }
        }


        private async Task RenderDocx(PanelState state)
        {
            if (string.IsNullOrEmpty(state.CurrentFile)) return;
            if (CheckFileSize(state)) return;
            var isDark = _theme.Current == _theme.Dark;
            state.WebView.NavigateToString(BuildLoadingPage(isDark));
            var content = await _converter.DocxToHtml(state.CurrentFile);
            var html = _render.BuildOfficePage(content, state, this);
            state.WebView.NavigateToString(html);
        }


        private async Task RenderExcel(PanelState state)
        {
            if (string.IsNullOrEmpty(state.CurrentFile)) return;
            if (CheckFileSize(state)) return;
            var isDark = _theme.Current == _theme.Dark;
            state.WebView.NavigateToString(BuildLoadingPage(isDark));
            var content = await _converter.ExcelToHtml(state.CurrentFile);
            var html = _render.BuildOfficePage(content, state, this);
            state.WebView.NavigateToString(html);
        }


        private async Task RenderPpt(PanelState state)
        {
            if (string.IsNullOrEmpty(state.CurrentFile)) return;
            if (CheckFileSize(state)) return;
            var isDark = _theme.Current == _theme.Dark;
            state.WebView.NavigateToString(BuildLoadingPage(isDark));
            var content = await _converter.PptToHtml(state.CurrentFile);
            var html = _render.BuildOfficePage(content, state, this);
            state.WebView.NavigateToString(html);
        }


        private void ShowFind(PanelState state)
        {
            try
            {
                // 原实现 window.find() 未传搜索词，形同虚设。改为页内高亮搜索：
                // 宿主建悬浮搜索栏 → 注入 __seemeSearch(query) → 页面高亮并回传 search-result。
                var w = new Window
                {
                    Title = "查找",
                    Width = 340,
                    SizeToContent = SizeToContent.Height,
                    Owner = this,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    ResizeMode = ResizeMode.NoResize,
                    ShowInTaskbar = false,
                    Background = TryFindResource("SidebarBackgroundBrush") as System.Windows.Media.Brush
                                 ?? System.Windows.Media.Brushes.White,
                };
                var panel = new StackPanel { Margin = new Thickness(12) };
                var inputRow = new DockPanel();
                var nextBtn = new Button { Content = "下一个", Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(6, 0, 0, 0) };
                var prevBtn = new Button { Content = "上一个", Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(6, 0, 0, 0) };
                var closeBtn = new Button { Content = "✕", Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(6, 0, 0, 0) };
                DockPanel.SetDock(closeBtn, Dock.Right);
                DockPanel.SetDock(nextBtn, Dock.Right);
                DockPanel.SetDock(prevBtn, Dock.Right);
                inputRow.Children.Add(closeBtn);
                inputRow.Children.Add(nextBtn);
                inputRow.Children.Add(prevBtn);
                var input = new TextBox
                {
                    Padding = new Thickness(6, 4, 6, 4),
                    VerticalContentAlignment = VerticalAlignment.Center,
                    FontSize = 13,
                };
                inputRow.Children.Add(input);
                panel.Children.Add(inputRow);

                var hint = new TextBlock
                {
                    Text = "输入后按 Enter 搜索 · Enter/Shift+Enter 切换结果",
                    FontSize = 11,
                    Margin = new Thickness(0, 6, 0, 0),
                    Foreground = TryFindResource("TextSecondaryBrush") as System.Windows.Media.Brush
                                 ?? System.Windows.Media.Brushes.Gray,
                };
                panel.Children.Add(hint);

                var result = new TextBlock { FontSize = 11, Margin = new Thickness(0, 4, 0, 0), Text = "" };
                panel.Children.Add(result);

                void RunSearch(string query)
                {
                    // JsonSerializer 生成合法 JS 字符串字面量（转义引号/反斜杠），不可用 SecurityElement.Escape（HTML 实体 JS 不识别）
                    var q = System.Text.Json.JsonSerializer.Serialize(query ?? "");
                    try { state.WebView.CoreWebView2.ExecuteScriptAsync($"window.__seemeSearch && window.__seemeSearch({q})"); }
                    catch (Exception ex) { LogErr("Find: " + ex.Message); }
                }

                input.KeyDown += (_, ke) =>
                {
                    if (ke.Key == Key.Enter)
                    {
                        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                        {
                            try { state.WebView.CoreWebView2.ExecuteScriptAsync("window.__seemeSearchPrev && window.__seemeSearchPrev()"); }
                            catch (Exception ex) { LogErr("Find prev: " + ex.Message); }
                        }
                        else
                        {
                            RunSearch(input.Text);
                        }
                        ke.Handled = true;
                    }
                    else if (ke.Key == Key.Escape) { w.Close(); }
                };
                nextBtn.Click += (_, _) =>
                {
                    if (string.IsNullOrEmpty(input.Text)) RunSearch(input.Text);
                    try { state.WebView.CoreWebView2.ExecuteScriptAsync("window.__seemeSearchNext && window.__seemeSearchNext()"); }
                    catch (Exception ex) { LogErr("Find next: " + ex.Message); }
                };
                prevBtn.Click += (_, _) =>
                {
                    try { state.WebView.CoreWebView2.ExecuteScriptAsync("window.__seemeSearchPrev && window.__seemeSearchPrev()"); }
                    catch (Exception ex) { LogErr("Find prev: " + ex.Message); }
                };
                closeBtn.Click += (_, _) => w.Close();

                w.Content = panel;
                w.Show();
                input.Focus();
            }
            catch (Exception ex) { LogErr("ShowFind: " + ex.Message); }
        }


        private void SetZoom(PanelState state, double scale)
        {
            state.FontScale = Math.Clamp(scale, 0.5, 2.5);
            try
            {
                var js = $"window.__seemeApplyZoom ? window.__seemeApplyZoom({state.FontScale}) : " +
                         $"(document.documentElement.style.fontSize=({state.FontScale*14})+'px');";
                state.WebView.ExecuteScriptAsync(js);
            }
            catch (Exception ex) { LogErr("Zoom script: " + ex.Message); }
            if (state.CurrentFile != null)
                StatusText.Text = $"缩放: {(int)(state.FontScale * 100)}%";
        }


        private void OnZoomL(object sender, RoutedEventArgs e) => SetZoomDialog(_app.Left);

        private void OnZoomR(object sender, RoutedEventArgs e)
        {
            if (!_app.IsSplitMode) SetSplitMode(true);
            SetZoomDialog(_app.Right);
        }


        private void OnFindL(object sender, RoutedEventArgs e) => ShowFind(_app.Left);

        private void OnFindR(object sender, RoutedEventArgs e)
        {
            if (!_app.IsSplitMode) SetSplitMode(true);
            ShowFind(_app.Right);
        }


        private void SetZoomDialog(PanelState state)
        {
            var w = new Window
            {
                Title = "设置字号",
                Width = 280, Height = 120,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = System.Windows.ResizeMode.NoResize,
                Background = new System.Windows.Media.SolidColorBrush(
                    _render.IsDarkTheme(_theme) ? System.Windows.Media.Color.FromRgb(30, 35, 46)
                                  : System.Windows.Media.Colors.White)
            };
            var stack = new StackPanel { Margin = new Thickness(16) };
            var header = new TextBlock { Text = "字号比例 (50% - 250%)", FontSize = 12, Margin = new Thickness(0, 0, 0, 6) };
            var tb = new TextBox
            {
                Text = ((int)(state.FontScale * 100)).ToString(),
                FontSize = 14,
                Padding = new Thickness(6, 4, 6, 4)
            };
            var btn = new Button
            {
                Content = "应用",
                Margin = new Thickness(0, 10, 0, 0),
                Padding = new Thickness(20, 6, 20, 6),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            btn.Click += (_, _) =>
            {
                if (int.TryParse(tb.Text, out var pct))
                {
                    SetZoom(state, Math.Clamp(pct / 100.0, 0.5, 2.5));
                    w.DialogResult = true;
                    w.Close();
                }
                else
                    tb.Focus();
            };
            stack.Children.Add(header);
            stack.Children.Add(tb);
            stack.Children.Add(btn);
            w.Content = stack;
            w.ShowDialog();
        }


    }
}
