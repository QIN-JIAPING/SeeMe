// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

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
            // 编辑模式：watcher 触发的自动刷新一律跳过，避免把用户的编辑器踢回预览（保存由 edit-save 自己处理）
            if (state.EditMode && !force) return;
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
                        // PDF 统一文本视图：图文重建由页面内 JS 完成，无 C# 侧大纲
                        RenderPdf(state); MarkLoaded(state); SetOutline(null);
                        return;
                    case ".xlsx":
                    case ".docx":
                    case ".pptx":
                    case ".doc":
                    case ".docm": // 宏文档变体，anydoc 映射到 docx 解析器
                    case ".ppt":
                    case ".rtf":
                    case ".odt":
                    case ".ods":
                    case ".odp":
                    case ".epub":
                    case ".csv":
                    case ".xlsm": // 宏工作簿变体，anydoc 映射到 xlsx 解析器
                        // anydoc-wasm 覆盖的 12 种格式（docx/xlsx/pptx/doc/ppt/rtf/odt/ods/odp/epub/csv）及宏变体 docm/xlsm
                        await RenderOfficeAnyDoc(state, ext); return;
                    case ".xls":
                        // .xls（旧二进制 OLE）打包的 anydoc 不支持，直接走 FileConverter
                        await RenderExcel(state); MarkLoaded(state); SetOutline(null); return;
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
                EnsureRemoteImagePolicy(state, htmlRaw);
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

        /// <summary>点击大纲条目：在当前聚焦面板（默认左栏）中平滑滚动到对应标题锚点。
        /// PDF 文本视图的大纲 id 是书签/标题目标键，走页面内 __pdfScrollTo（含书签按 y 比例定位）。</summary>
        private void OnOutlineClick(object sender, MouseButtonEventArgs e)
        {
            if (OutlineList.SelectedItem is ListBoxItem li && li.Tag is TocItem toc)
            {
                var target = _activePanel ?? _app.Left;
                if (target?.WebView?.CoreWebView2 == null) return;
                var isPdf = Path.GetExtension(target.CurrentFile ?? "")
                    .Equals(".pdf", StringComparison.OrdinalIgnoreCase);
                var js = isPdf
                    ? "if(window.__pdfScrollTo)window.__pdfScrollTo("
                      + System.Text.Json.JsonSerializer.Serialize(toc.Id) + ");"
                    : "var el=document.getElementById("
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
        /// 远程图片授权检查：默认拦截（CSP img-src 不含外网），文档含 https:// 图片时
        /// 弹窗询问用户是否放行，授权结果写入 AppSettings（按文档路径持久化）。
        /// 放行后本面板 AllowRemoteImages=true，RenderService 渲染时给 img-src 追加 https:。
        /// </summary>
        private static readonly System.Text.RegularExpressions.Regex RemoteImgRegex = new(
            @"<img\b[^>]*\bsrc\s*=\s*[""']https?://",
            System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        private void EnsureRemoteImagePolicy(PanelState state, string htmlRaw)
        {
            if (state.AllowRemoteImages) return;                     // 本面板已放行（本次会话）
            if (string.IsNullOrEmpty(htmlRaw)) return;
            if (string.IsNullOrEmpty(state.CurrentFile)) return;
            if (!RemoteImgRegex.IsMatch(htmlRaw)) return;            // 无远程图片
            if (AppSettings.IsRemoteImageAllowed(state.CurrentFile)) // 该文档历史已授权
            {
                state.AllowRemoteImages = true;
                return;
            }
            var r = MessageBox.Show(
                "此文档包含远程图片（https:// 链接）。加载远程图片会向图片服务器暴露你的 IP 和阅读行为。\n\n是否允许加载？",
                "SeeMe - 远程图片", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r == MessageBoxResult.Yes)
            {
                state.AllowRemoteImages = true;
                AppSettings.SetRemoteImageAllowed(state.CurrentFile, true);
            }
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

        private void RenderPdf(PanelState state)
        {
            if (string.IsNullOrEmpty(state.CurrentFile)) return;
            if (CheckFileSize(state)) return;
            // PDF 统一走文本视图：浏览器端重建图文混排（PDF.js 提取文本 + 内联图片）
            RenderPdfTextView(state);
        }

        /// <summary>
        /// PDF 文本视图：浏览器端用 PDF.js 重建图文混排——按 y 坐标交错文本行与图片
        /// （图片内联在原文位置，点击放大），扫描版（无文本层）则仅显示各页图片。
        /// 主题/缩放/搜索与 Markdown 页一致；anydoc 文本层仍在后台提取（供内容搜索与统计卡）。
        /// </summary>
        private void RenderPdfTextView(PanelState state)
        {
            if (string.IsNullOrEmpty(state.CurrentFile)) return;
            try
            {
                // PDF 文件目录映射虚拟主机（页面需 fetch 字节重建图文）
                var pdfUrl = MapPdfVirtualHost(state);
                if (string.IsNullOrEmpty(pdfUrl))
                {
                    RenderErrorPage(state, new Exception("无法映射 PDF 目录"));
                    return;
                }
                // 后台提取文本层（供内容搜索 >关键词 / 统计卡），不阻塞图文重建
                _ = TryExtractPdfTextAsync(state);
                state.PdfOutlineToken = Guid.NewGuid().ToString("N");
                state.WebView.NavigateToString(BuildPdfReadingPage(state, pdfUrl));
                MarkLoaded(state);
                LogInfo("Pdf text view: " + Path.GetFileName(state.CurrentFile));
            }
            catch (Exception ex)
            {
                LogErr("Pdf text view: " + ex.Message);
                if (state.WebView?.CoreWebView2 != null)
                    RenderErrorPage(state, ex);
            }
        }

        /// <summary>把当前 PDF 所在目录映射到 pdffiles.example 虚拟主机，返回可 fetch 的 PDF URL；失败返回 null。</summary>
        private string? MapPdfVirtualHost(PanelState state)
        {
            var dir = Path.GetDirectoryName(state.CurrentFile);
            var name = Path.GetFileName(state.CurrentFile);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name)) return null;
            const string hostName = "pdffiles.example";
            try
            {
                state.WebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    hostName, dir, CoreWebView2HostResourceAccessKind.Allow);
            }
            catch { /* 同一目录重复映射会抛异常，忽略（映射已存在） */ }
            return $"https://{hostName}/{System.Uri.EscapeDataString(name)}";
        }

        /// <summary>
        /// PDF 图文混排页外壳：主题 CSS + 状态栏 + 文档容器 + PDF.js（appassets）+ 重建脚本。
        /// CSP 放行 pdffiles.example（fetch PDF 字节）与 PDF.js worker；字号缩放/搜索与 Markdown 页一致。
        /// </summary>
        private string BuildPdfReadingPage(PanelState state, string pdfUrl)
        {
            var isDark = _theme.Current == _theme.Dark;
            var htmlClass = isDark ? " class='dark'" : "";
            var jsPdfUrl = System.Text.Json.JsonSerializer.Serialize(pdfUrl);
            var jsRestoreY = state.LastScrollY.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var jsToken = System.Text.Json.JsonSerializer.Serialize(state.PdfOutlineToken ?? "");
            var nonce = RenderService.NewNonce();
            return $@"<!DOCTYPE html>
<html{htmlClass}><head><meta charset='utf-8'/>
<meta name='viewport' content='width=device-width,initial-scale=1'/>
<meta name='referrer' content='no-referrer'/>
<meta http-equiv='Content-Security-Policy' content=""default-src 'self' https://appassets.example; script-src 'nonce-{nonce}' https://appassets.example; style-src 'unsafe-inline'; img-src 'self' data: blob:; connect-src 'self' https://appassets.example https://pdffiles.example; worker-src 'self' https://appassets.example blob:;"">
<style>
{RenderService.ThemeCss()}
  * {{ margin:0; padding:0; box-sizing:border-box; }}
  html {{ font-size:14px; }}
  html,body {{ width:100%; height:100vh; }}
  body {{ background:var(--bg); color:var(--text); transition:background-color .3s ease,color .3s ease; }}
  {RenderService.MdDocumentCss}
  #pdf-doc {{ max-width:860px; margin:0 auto; padding:4px 20px 60px; font-size:1rem; line-height:1.75; }}
  .pdf-page .pdf-pg-title {{ font-size:.85rem; font-weight:600; color:var(--secondary); margin:26px 0 10px;
                  padding-bottom:6px; border-bottom:1px dashed var(--h2-border); }}
  .pdf-fig {{ margin:16px auto; text-align:center; }}
  .pdf-fig img {{ max-width:100%; border-radius:4px; cursor:zoom-in;
                 box-shadow:0 1px 3px rgba(0,0,0,.25),0 6px 18px rgba(0,0,0,.08); }}
  html.dark .pdf-fig img {{ box-shadow:0 1px 3px rgba(0,0,0,.5),0 6px 18px rgba(0,0,0,.35); }}
  .pdf-fig figcaption {{ font-size:.72rem; color:var(--secondary); margin-top:5px; }}
  .pdf-fig-ph {{ font-size:.75rem; color:var(--quote-text); padding:10px; }}
  .pdf-note {{ margin:14px 0; padding:10px 14px; border-left:3px solid var(--quote); background:var(--quote-bg);
              color:var(--quote-text); font-size:.8rem; border-radius:0 4px 4px 0; }}
  mark.seeme-hl {{ background:#FBBF24; color:#1F2937; border-radius:2px; padding:1px 2px; }}
  mark.seeme-cur {{ background:#3B82F6; color:#fff; }}
  ::-webkit-scrollbar {{ width:8px; height:8px; }}
  ::-webkit-scrollbar-thumb {{ background:var(--h1-border); border-radius:4px; }}
</style>
</head><body>
  <div id='pdf-doc' class='content'></div>
<script nonce='{nonce}'>
window.__PDF_READ_CONF = {{ pdf: {jsPdfUrl}, restoreY: {jsRestoreY}, tok: {jsToken} }};
function setTheme(dark){{var h=document.documentElement;if(dark)h.classList.add('dark');else h.classList.remove('dark');}}
</script>
<script src='https://appassets.example/pdfjs/pdf.min.js?v=11'></script>
<script nonce='{nonce}'>{RenderService.SearchScript}</script>
<script nonce='{nonce}'>
{PdfReadingScript}
</script>
</body></html>";
        }

        /// <summary>
        /// 图文重建脚本：逐页 getTextContent（文本行）+ getOperatorList（图片框），按 y 坐标交错排版；
        /// 图片手动构造裁剪视口渲染（懒加载，按原生分辨率），点击后超采样二次渲染放大。
        /// 扫描版（无文本）→ 仅显示各页图片 + 顶部提示。
        /// </summary>
        private const string PdfReadingScript = @"
(function () {
  var conf = window.__PDF_READ_CONF || {};
  var pdfUrl = conf.pdf || '';
  var restoreY = conf.restoreY || 0;
  var docEl = document.getElementById('pdf-doc');
  if (!pdfUrl || !window.pdfjsLib) { return; }

  pdfjsLib.GlobalWorkerOptions.workerSrc = 'https://appassets.example/pdfjs/pdf.worker.min.js';
  fetch(pdfUrl)
    .then(function (r) { if (!r.ok) throw new Error('fetch ' + r.status); return r.arrayBuffer(); })
    .then(function (buf) { return pdfjsLib.getDocument({ data: new Uint8Array(buf), disableAutoFetch: true, disableStream: true }).promise; })
    .then(buildDoc)
    .catch(function (err) { console.error('[pdf-read] load failed:', err); });

  // 诊断日志 → 宿主 error.log（kind:pdf-read-log，C# 侧记录）
  function dbg(msg) {
    try { window.chrome.webview.postMessage(JSON.stringify({ kind: 'pdf-read-log', msg: String(msg) })); } catch (e) {}
  }

  // 字号缩放（宿主 ApplyPanelZoom 调用）：与 Markdown 页一致，改 html 字号（CSS 用 rem 继承）
  window.__seemeApplyZoom = function (z) {
    document.documentElement.style.fontSize = (z * 14) + 'px';
  };

  // HTML 转义（PDF 文本可能含 < > &）
  function esc(s) {
    return String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
  }
  // 智能拼接 + 行内样式：CJK 间不加空格，拉丁词间加空格；粗体/斜体包 <strong>/<em>
  function joinPartsHtml(parts) {
    var html = '', lastCh = '';
    for (var i = 0; i < parts.length; i++) {
      var p = parts[i];
      if (!p || !p.s) continue;
      var seg = esc(p.s);
      if (p.b) seg = '<strong>' + seg + '</strong>';
      if (p.i) seg = '<em>' + seg + '</em>';
      if (html.length) {
        var lastLatin = /[A-Za-z0-9]/.test(lastCh);
        var firstLatin = /[A-Za-z0-9]/.test(p.s.charAt(0));
        if (lastLatin && firstLatin) html += ' ';
      }
      html += seg;
      lastCh = p.s.charAt(p.s.length - 1);
    }
    return html;
  }

  var totalTextChars = 0;
  var total = 0;

  function buildDoc(doc) {
    total = doc.numPages;
    var cur = 0;
    function next() {
      cur++;
      if (cur > total) {
        if (totalTextChars === 0) showNoTextNote();
        if (restoreY > 0) window.scrollTo(0, restoreY);
        dbg('done: pages=' + total + ' textChars=' + totalTextChars);
        buildOutline(doc);
        return;
      }
      buildPage(doc, cur).then(next);
    }
    next();
  }

  async function buildPage(doc, n) {
    var page = await doc.getPage(n);
    var tc = await page.getTextContent();
    var opList = await page.getOperatorList();
    var vp1 = page.getViewport({ scale: 1 });
    var O = pdfjsLib.OPS;

    // ── 图片框：跟踪 opList 的变换状态（save/restore/transform）。
    //    PDF 语义：图片画在单位正方形内，Do 时的 CTM 就是它在用户空间的
    //    位置与尺寸（实测 opList：transform [w,0,0,h,x,y] → paintImageXObject
    //    [objId, 像素宽, 像素高]，像素尺寸只影响解码不影响布局）。
    //    所以用当前矩阵映射单位正方形四角求轴对齐包围盒。
    var hasText = false;
    for (var ti = 0; ti < tc.items.length; ti++) {
      if (tc.items[ti] && tc.items[ti].str && tc.items[ti].str.trim()) { hasText = true; break; }
    }
    var imgs = [];
    var stack = [];
    var cur = [1, 0, 0, 1, 0, 0];
    function mul(T, M) {
      return [
        T[0] * M[0] + T[2] * M[1], T[1] * M[0] + T[3] * M[1],
        T[0] * M[2] + T[2] * M[3], T[1] * M[2] + T[3] * M[3],
        T[0] * M[4] + T[2] * M[5] + T[4], T[1] * M[4] + T[3] * M[5] + T[5]
      ];
    }
    function mapPt(x, y) {
      return [cur[0] * x + cur[2] * y + cur[4], cur[1] * x + cur[3] * y + cur[5]];
    }
    function pushImg(pxW, pxH) {
      var p00 = mapPt(0, 0), p10 = mapPt(1, 0), p01 = mapPt(0, 1), p11 = mapPt(1, 1);
      var xs = [p00[0], p10[0], p01[0], p11[0]];
      var ys = [p00[1], p10[1], p01[1], p11[1]];
      var x = Math.min.apply(null, xs), y = Math.min.apply(null, ys);
      var bw = Math.max.apply(null, xs) - x, bh = Math.max.apply(null, ys) - y;
      if (!isFinite(bw) || !isFinite(bh) || bw < 8 || bh < 8) return; // 装饰性小图
      // 有文本时跳过几乎整页的背景图（水印/底图）；无文本的扫描页保留（图即内容）
      if (hasText && bw > vp1.width * 0.99 && bh > vp1.height * 0.99) return;
      imgs.push({ x: x, y: y, w: bw, h: bh, pxW: pxW, pxH: pxH }); // pxW/pxH = 源图像素尺寸
    }
    for (var i = 0; i < opList.fnArray.length; i++) {
      var fn = opList.fnArray[i];
      var a = opList.argsArray[i];
      if (fn === O.save) {
        stack.push(cur.slice());
      } else if (fn === O.restore) {
        cur = stack.pop() || cur;
      } else if (fn === O.transform && a && a.length >= 6) {
        cur = mul(cur, a);
      } else if (fn === O.paintImageXObject && a && a.length >= 1) {
        pushImg(a.length >= 3 ? +a[1] : 0, a.length >= 3 ? +a[2] : 0);
      } else if (fn === O.paintInlineImageXObject && a && a[0]) {
        pushImg(+a[0].width || 0, +a[0].height || 0);
      }
      // paintImageMaskXObject（透明度蒙版/阴影模板）不视为正文图片，跳过
    }

    // ── 文本行：按基线 y 分桶（±3 容差），记录字号与字体。
    //    Word 导出的 PDF 用字号表达标题层级、字体名带 Bold/Italic 表达强调。
    var lines = [];
    var items = tc.items || [];
    var sizeCount = {};
    for (var k = 0; k < items.length; k++) {
      var it = items[k];
      if (!it || !it.str) continue;
      var yy = (it.transform && it.transform.length >= 6) ? it.transform[5] : 0;
      var line = null;
      for (var L = 0; L < lines.length; L++) {
        if (Math.abs(lines[L].y - yy) < 3) { line = lines[L]; break; }
      }
      if (!line) { line = { y: yy, parts: [], sz: 0, bold: false, italic: false }; lines.push(line); }
      var sz = Math.abs(it.height || (it.transform && it.transform[0]) || 0);
      var fn = '';
      try {
        var fObj = it.fontName ? page.commonObjs.get(it.fontName) : null;
        fn = (fObj && (fObj.name || fObj.fallbackName)) || '';
      } catch (e) { }
      var bold = /Bold|Heavy|Black|Demibold/i.test(fn);
      var italic = /Italic|Oblique/i.test(fn);
      line.parts.push({ s: it.str, sz: sz, b: bold, i: italic });
      if (sz > line.sz) line.sz = sz;
      if (bold) line.bold = true;
      if (italic) line.italic = true;
      sizeCount[sz] = (sizeCount[sz] || 0) + it.str.length;
    }
    // 正文基准字号 = 按字符数最多的字号
    var bodySz = 0, bestN = 0;
    for (var bz in sizeCount) {
      if (sizeCount[bz] > bestN) { bestN = sizeCount[bz]; bodySz = +bz; }
    }
    if (!bodySz) bodySz = 11;
    function headingLevel(sz) {
      if (sz >= bodySz * 1.4) return 1;
      if (sz >= bodySz * 1.2) return 2;
      if (sz >= bodySz * 1.1) return 3;
      return 0;
    }

    // 文档顺序：PDF y 向上增长 → y 大者在前
    lines.sort(function (A, B) { return B.y - A.y; });
    imgs.sort(function (A, B) { return B.y - A.y; });
    dbg('p' + n + ': textItems=' + items.length + ' textLines=' + lines.length + ' images=' + imgs.length + ' bodySz=' + bodySz);

    var sec = document.createElement('section');
    sec.className = 'pdf-page';
    sec.setAttribute('data-pg', n);
    sec.setAttribute('data-vph', vp1.height);
    var h = document.createElement('h3');
    h.className = 'pdf-pg-title';
    h.id = 'pdf-pg-' + n;
    h.textContent = '第 ' + n + ' 页';
    sec.appendChild(h);

    var paraParts = null;
    var prevY = null;
    var headingSeq = 0;
    var gapTh = vp1.height * 0.03; // 段间距阈值（页面高度的 3%）

    function flushPara() {
      if (paraParts) {
        var html = joinPartsHtml(paraParts);
        var txt = html.replace(/<[^>]+>/g, '');
        if (txt.trim()) {
          var p = document.createElement('p');
          p.innerHTML = html;
          sec.appendChild(p);
          totalTextChars += txt.replace(/\s/g, '').length;
        }
        paraParts = null;
      }
    }
    // 大字号行 → MD 风格标题（h1/h2/h3），正文行进段落
    function addHeading(ln) {
      flushPara();
      var lv = headingLevel(ln.sz);
      var el = document.createElement('h' + lv);
      el.id = 'pdf-h' + (++headingSeq);
      el.innerHTML = joinPartsHtml(ln.parts);
      sec.appendChild(el);
      totalTextChars += el.textContent.replace(/\s/g, '').length;
    }
    function addImg(box) {
      flushPara();
      var fig = document.createElement('figure');
      fig.className = 'pdf-fig';
      fig.__box = box;
      fig.__page = page;
      var ph = document.createElement('div');
      ph.className = 'pdf-fig-ph';
      ph.textContent = '图片加载中…';
      var img = document.createElement('img');
      img.alt = '第 ' + n + ' 页图片';
      img.style.visibility = 'hidden';
      var cap = document.createElement('figcaption');
      cap.textContent = '第 ' + n + ' 页 · 图片（点击放大）';
      fig.appendChild(ph);
      fig.appendChild(img);
      fig.appendChild(cap);
      sec.appendChild(fig);
      var rendered = false;
      function tryRender() {
        if (rendered) return;
        rendered = true;
        renderCrop(page, box, img, ph, null);
      }
      if ('IntersectionObserver' in window) {
        var io = new IntersectionObserver(function (entries) {
          entries.forEach(function (en) {
            if (en.isIntersecting) { tryRender(); io.disconnect(); }
          });
        }, { rootMargin: '500px 0px' });
        io.observe(img);
      } else {
        tryRender();
      }
    }

    // 交错合并：lines 与 imgs 均按 y 降序
    var li = 0, ii = 0;
    while (li < lines.length || ii < imgs.length) {
      var lineY = li < lines.length ? lines[li].y : -Infinity;
      var imgY = ii < imgs.length ? imgs[ii].y : -Infinity;
      if (ii < imgs.length && imgY >= lineY) {
        addImg(imgs[ii]);
        ii++;
      } else if (li < lines.length) {
        var ln = lines[li];
        li++;
        if (headingLevel(ln.sz) > 0) { addHeading(ln); prevY = ln.y; continue; }
        var htxt = joinPartsHtml(ln.parts).replace(/<[^>]+>/g, '');
        if (!htxt.trim()) continue;
        if (prevY === null || (prevY - ln.y) > gapTh) flushPara();
        if (!paraParts) paraParts = [];
        paraParts.push.apply(paraParts, ln.parts);
        prevY = ln.y;
      }
    }
    flushPara();
    docEl.appendChild(sec);
  }

  function showNoTextNote() {
    var note = document.createElement('div');
    note.className = 'pdf-note';
    note.textContent = '🖨 该 PDF 没有可提取的文本层（扫描版），仅显示页面图像。';
    docEl.insertBefore(note, docEl.firstChild);
  }

  // ── 大纲：优先 PDF 书签（Word 导出自带），其次重建标题（h1-h3），最后各页标记 ──
  var outlineTargets = {};
  window.__pdfOutlineTargets = outlineTargets;
  window.__pdfScrollTo = function (id) {
    var t = outlineTargets[id];
    if (!t) return;
    if (t.el) {
      var el = document.getElementById(t.el);
      if (el) el.scrollIntoView({ behavior: 'smooth', block: 'start' });
      return;
    }
    // 书签：按页面内 y 比例定位（PDF y 向上 → 距顶比例 = (页高 - top)/页高）
    var sec = docEl.querySelector('.pdf-page[data-pg=""' + t.pg + '""]');
    if (!sec) return;
    var vpH = +sec.getAttribute('data-vph') || 1;
    var ratio = Math.min(1, Math.max(0, (vpH - t.top) / vpH));
    window.scrollTo({ top: sec.offsetTop + sec.offsetHeight * ratio - 30, behavior: 'smooth' });
  };
  function postOutline(items) {
    try {
      window.chrome.webview.postMessage(JSON.stringify({ kind: 'pdf-outline', token: conf.tok || '', items: items }));
    } catch (e) { }
  }
  function buildOutline(doc) {
    doc.getOutline().then(function (bm) {
      if (bm && bm.length) {
        // 书签树 → 扁平化（level 递增），逐项解析目标页与 y 坐标
        var flat = [];
        (function walk(list, lv) {
          for (var i = 0; i < list.length; i++) {
            var it = list[i];
            flat.push({ t: it.title || '', l: lv, d: it.dest });
            if (it.items && it.items.length) walk(it.items, lv + 1);
          }
        })(bm, 1);
        var items = [], tasks = [];
        for (var i = 0; i < flat.length; i++) {
          var f = flat[i], id = 'pdf-bm' + i;
          (function (f, id) {
            var push = function (pg, top) {
              outlineTargets[id] = { pg: pg, top: top };
              items.push({ t: f.t, l: f.l, id: id });
            };
            if (f.d && f.d.length && f.d[0]) {
              tasks.push(doc.getPageIndex(f.d[0]).then(function (pi) {
                var top = (f.d.length > 3 && f.d[1] && f.d[1].name === 'XYZ' && isFinite(f.d[3])) ? f.d[3] : 0;
                push(pi + 1, top);
              }).catch(function () { push(1, 0); }));
            } else {
              push(1, 0);
            }
          })(f, id);
        }
        Promise.all(tasks).then(function () { postOutline(items); });
        return;
      }
      // 无书签 → 用重建出的标题
      var hs = docEl.querySelectorAll('.pdf-page h1, .pdf-page h2, .pdf-page h3');
      var headingItems = [];
      for (var i = 0; i < hs.length; i++) {
        var el = hs[i];
        if (el.classList.contains('pdf-pg-title')) continue;
        outlineTargets[el.id] = { el: el.id };
        headingItems.push({ t: el.textContent.trim(), l: +el.tagName.charAt(1), id: el.id });
      }
      if (headingItems.length) { postOutline(headingItems); return; }
      // 再没有 → 各页标记
      var pgs = docEl.querySelectorAll('.pdf-page');
      var pgItems = [];
      for (var j = 0; j < pgs.length; j++) {
        var s = pgs[j];
        var id = (s.querySelector('.pdf-pg-title') || {}).id;
        if (!id) continue;
        outlineTargets[id] = { el: id };
        pgItems.push({ t: '第 ' + s.getAttribute('data-pg') + ' 页', l: 1, id: id });
      }
      if (pgItems.length) postOutline(pgItems);
    }).catch(function () { });
  }

  // 渲染图片区域 → dataURL（thumbW 为 null 用默认缩略宽度；指定宽度用于 lightbox 高分辨率二次渲染）
  // 本 bundle 未导出 PageViewport → 手动构造裁剪视口（PDF 用户空间 → canvas，
  // y 翻转 + 平移到原点），只渲染图片区域，不浪费整页。
  function cropVp(box, scale, dpr) {
    var s = scale * dpr;
    return {
      width: box.w * scale, height: box.h * scale, rotation: 0, scale: scale,
      transform: [s, 0, 0, -s, -s * box.x, s * (box.y + box.h)]
    };
  }
  function renderCrop(page, box, img, ph, thumbW) {
    try {
      var maxW = Math.min(1500, Math.max(400, (docEl.clientWidth || 800) - 60));
      var displayW = thumbW || Math.min(460, maxW);
      var displayScale = displayW / box.w;
      // 源图原生分辨率：渲染倍率不低于它（源图多清晰就多清晰）
      var natScale = (box.pxW && box.pxW > 0) ? box.pxW / box.w : displayScale;
      // 缩略图：最多 2× 显示宽度防内存膨胀；lightbox：1.5× 原生超采样，长边 ≤ 2400
      var want = thumbW
        ? Math.min(natScale * 1.5, 2400 / Math.max(box.w, box.h))
        : Math.min(natScale, displayScale * 2);
      var scale = Math.max(displayScale, want);
      var dpr = window.devicePixelRatio || 1;
      var vp = cropVp(box, scale, dpr);
      var canvas = document.createElement('canvas');
      canvas.width = Math.max(1, Math.floor(vp.width * dpr));
      canvas.height = Math.max(1, Math.floor(vp.height * dpr));
      var ctx = canvas.getContext('2d');
      return page.render({ canvasContext: ctx, viewport: vp }).promise
        .then(function () {
          var url = canvas.toDataURL('image/jpeg', 0.95);
          if (img) { img.src = url; img.style.visibility = 'visible'; }
          if (ph) ph.style.display = 'none';
          canvas.width = 0; canvas.height = 0;
          return url;
        })
        .catch(function (err) {
          if (ph) ph.textContent = '图片渲染失败';
          console.error('[pdf-read] renderCrop failed:', err);
          return null;
        });
    } catch (err) {
      if (ph) ph.textContent = '图片渲染失败';
      console.error('[pdf-read] renderCrop throw:', err);
      return Promise.resolve(null);
    }
  }

  // 点击图片 → 按更高分辨率二次渲染并打开 lightbox
  var ov = null;
  function openLightbox(src, cap) {
    closeLightbox();
    ov = document.createElement('div');
    ov.style.cssText = 'position:fixed;inset:0;background:rgba(0,0,0,.88);display:flex;align-items:center;justify-content:center;z-index:9999;cursor:zoom-out;flex-direction:column;';
    var img = document.createElement('img');
    img.src = src;
    img.style.cssText = 'max-width:94%;max-height:86%;border-radius:6px;box-shadow:0 8px 40px rgba(0,0,0,.5);';
    var capEl = document.createElement('div');
    capEl.textContent = cap || '';
    capEl.style.cssText = 'color:#ccc;font-size:12px;margin-top:10px;';
    ov.appendChild(img);
    ov.appendChild(capEl);
    ov.addEventListener('click', closeLightbox);
    document.body.appendChild(ov);
  }
  function closeLightbox() {
    if (ov) { document.body.removeChild(ov); ov = null; }
  }
  docEl.addEventListener('click', function (e) {
    var t = e.target;
    var fig = t && t.closest ? t.closest('.pdf-fig') : null;
    if (!fig) return;
    var img = fig.querySelector('img');
    if (!img || !img.src || img.style.visibility === 'hidden') return;
    var cap = fig.querySelector('figcaption');
    if (fig.__page && fig.__box) {
      renderCrop(fig.__page, fig.__box, null, null, 1100).then(function (url) {
        if (url) openLightbox(url, cap ? cap.textContent : '');
      });
    } else {
      openLightbox(img.src, cap ? cap.textContent : '');
    }
  });
  document.addEventListener('keydown', function (e) { if (e.key === 'Escape') closeLightbox(); });
})();
";

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


// ═══════════════ anydoc-wasm（WebAssembly 转 Markdown）渲染 ═══════════════
// 方案 B：docx/pptx/xlsx 通过 WebView2 页内 anydoc-wasm 转 Markdown，再走 Markdig 渲染。
// 链路：文件目录映射虚拟主机（docfiles.example，与 PDF 同款模式）→ 桥接页 fetch 字节 →
// 经典 blob worker（initSync 字节初始化，绕开 module worker）→ Markdown 回传 → Markdig 渲染。
// 失败 / 超时 / 资源缺失 → 回退 FileConverter（原 OpenXML/PdfPig 解析）。

        /// <summary>anydoc-wasm 离线资产是否齐全（worker + 经典胶水 + wasm 主体）。</summary>
        private static bool AnyDocAssetsPresent()
        {
            try
            {
                var root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "anydoc");
                return File.Exists(Path.Combine(root, "worker.js"))
                    && File.Exists(Path.Combine(root, "anydoc_wasm_classic.js"))
                    && File.Exists(Path.Combine(root, "anydoc_wasm_bg.wasm"));
            }
            catch { return false; }
        }

        /// <summary>docx/pptx/xlsx 渲染入口：优先 anydoc-wasm，缺失/失败自动回退 FileConverter。</summary>
        private async Task RenderOfficeAnyDoc(PanelState state, string ext)
        {
            if (string.IsNullOrEmpty(state.CurrentFile)) return;
            if (CheckFileSize(state)) return;
            if (!AnyDocAssetsPresent()) { await FallbackOfficeAsync(state, ext); return; }
            var isDark = _theme.Current == _theme.Dark;
            // 小文档（≤1MB）跳过首个加载占位，直接进桥接页（自带 spinner），更快呈现；大文档保留占位防空白
            if (new FileInfo(state.CurrentFile).Length > 1L * 1024 * 1024)
                state.WebView.NavigateToString(BuildLoadingPage(isDark));

            var dir = Path.GetDirectoryName(state.CurrentFile);
            var name = Path.GetFileName(state.CurrentFile);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name)) { await FallbackOfficeAsync(state, ext); return; }

            // 与 RenderPdf 同款：把文件所在目录映射为虚拟主机，页面直接 fetch 字节（免 base64 / 大字符串注入）
            const string hostName = "docfiles.example";
            try
            {
                state.WebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    hostName, dir, CoreWebView2HostResourceAccessKind.Allow);
            }
            catch { /* 同一目录重复映射会抛异常，忽略（映射已存在） */ }

            var docUrl = $"https://{hostName}/{System.Uri.EscapeDataString(name)}";
            var token = Guid.NewGuid().ToString("N");
            state.AnyDocPending = new AnyDocPending { Token = token, File = state.CurrentFile, Ext = ext };
            state.WebView.NavigateToString(BuildAnyDocBridgePage(isDark, docUrl, token, ext));
            _ = AnyDocWatchdogAsync(state, token);
        }

        /// <summary>超时保护：桥接页/worker 5 秒未回结果则回退 FileConverter，避免永远停在加载页。</summary>
        private async Task AnyDocWatchdogAsync(PanelState state, string token)
        {
            await Task.Delay(5000);
            var pending = state.AnyDocPending;
            if (pending != null && pending.Token == token)
            {
                state.AnyDocPending = null;
                LogErr("AnyDoc timeout -> fallback: " + Path.GetFileName(state.CurrentFile));
                try { await FallbackOfficeAsync(state, pending.Ext); }
                catch (Exception ex) { LogErr("AnyDoc fallback: " + ex); }
            }
        }

        /// <summary>
        /// PDF 文本层提取：用 anydoc-wasm 的 pdf 能力（内嵌 pdf-inspector）把文本型 PDF 转成
        /// Markdown 并写缓存，供内容搜索；扫描版（无文本层）写 .no 标记。
        /// 复用 Office 桥接页 + worker 链路（AnyDocPending.PdfExtract 模式：结果只缓存不渲染）。
        /// 任何失败/超时都静默返回，不阻断 PDF.js 渲染。
        /// </summary>
        /// <summary>
        /// PDF 文本层提取：在隐藏的 ExtractView 里跑 anydoc 桥接页（fetch 字节 → worker → Markdown），
        /// 写缓存供内容搜索；扫描版写 .no 标记。结果经 OnExtractMessage 按令牌匹配面板回写。
        /// 后台执行，不打断面板当前视图；失败/超时静默，不阻断 PDF.js 渲染。
        /// </summary>
        private async Task TryExtractPdfTextAsync(PanelState state)
        {
            try
            {
                if (string.IsNullOrEmpty(state.CurrentFile)) return;
                if (!AnyDocAssetsPresent()) return;
                if (ExtractView?.CoreWebView2 == null) return; // 未初始化
                // 有文本缓存直接应用，不重跑桥接页。
                // 注意：.no 标记（上次判定扫描版）不短路——提取可能瞬时失败被误判（曾致文本型 PDF
                // 永远进不了文本视图），每次会话至少重试一次（anydoc 毫秒级，成本可忽略）。
                if (PdfTextCache.TryRead(state.CurrentFile, out var cachedText, out var cachedHasText) && cachedHasText)
                {
                    state.PdfText = cachedText ?? "";
                    state.PdfTextLayer = true;
                    _ = Dispatcher.BeginInvoke(new Action(() => RefreshStats(state)));
                    return;
                }
                var dir = Path.GetDirectoryName(state.CurrentFile);
                var name = Path.GetFileName(state.CurrentFile);
                if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name)) return;

                const string hostName = "docfiles.example";
                try
                {
                    ExtractView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                        hostName, dir, CoreWebView2HostResourceAccessKind.Allow);
                }
                catch { /* 同一目录重复映射会抛异常，忽略 */ }

                var docUrl = $"https://{hostName}/{System.Uri.EscapeDataString(name)}";
                var token = Guid.NewGuid().ToString("N");
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                state.AnyDocPending = new AnyDocPending
                {
                    Token = token, File = state.CurrentFile, Ext = ".pdf",
                    PdfExtract = true, ExtractTcs = tcs
                };
                ExtractView.NavigateToString(BuildAnyDocBridgePage(_theme.Current == _theme.Dark, docUrl, token, ".pdf"));
                // 等回传（OnExtractMessage 写缓存 + SetResult），后台超时 8s 静默放弃
                await Task.WhenAny(tcs.Task, Task.Delay(8000));
            }
            catch (Exception ex) { LogErr("Pdf text extract: " + ex.Message); }
        }

        /// <summary>anydoc 桥接页：加载经典 worker 胶水，fetch 文档字节转 Markdown，回传宿主。</summary>
        private string BuildAnyDocBridgePage(bool isDark, string docUrl, string token, string ext)
        {
            var cls = isDark ? " class='dark'" : "";
            var jsDocUrl = System.Text.Json.JsonSerializer.Serialize(docUrl);
            var jsToken = System.Text.Json.JsonSerializer.Serialize(token);
            var jsExt = System.Text.Json.JsonSerializer.Serialize(ext);
            var nonce = RenderService.NewNonce();
            return $@"<!DOCTYPE html>
<html{cls}><head><meta charset='utf-8'/>
<meta name='viewport' content='width=device-width,initial-scale=1'/>
<meta name='referrer' content='no-referrer'/>
<meta http-equiv='Content-Security-Policy' content=""default-src 'self' https://appassets.example https://docfiles.example; script-src 'nonce-{nonce}' https://appassets.example 'unsafe-eval'; style-src 'unsafe-inline'; img-src 'self' data: https://appassets.example; worker-src 'self' blob: https://appassets.example; connect-src 'self' https://appassets.example https://docfiles.example;"">
<style>
{RenderService.ThemeCss()}
  * {{ margin:0; padding:0; box-sizing:border-box; }}
  html,body {{ width:100%; height:100vh; }}
  body {{ display:flex; align-items:center; justify-content:center; background:var(--bg); color:var(--text);
         font-family:'Segoe UI','Microsoft YaHei',sans-serif; font-size:15px; }}
  .spin {{ width:18px; height:18px; margin-right:10px; border:2px solid var(--border); border-top-color:var(--accent);
          border-radius:50%; display:inline-block; animation:spin .8s linear infinite; vertical-align:middle; }}
  @keyframes spin {{ to {{ transform:rotate(360deg); }} }}
</style>
</head><body><span class='spin'></span>解析文档…</body>
<script nonce='{nonce}'>
window.__ANYDOC_CONF = {{ url: {jsDocUrl}, id: {jsToken}, ext: {jsExt} }};
(function(){{
  var conf = window.__ANYDOC_CONF;
  function post(obj){{ try {{ window.chrome.webview.postMessage(JSON.stringify(obj)); }} catch(e){{}} }}
  function fail(err){{ post({{kind:'anydoc-result', id: conf.id, ok:false, error: String(err)}}); }}
  function start(workerText){{
    var w = null;
    try {{ w = new Worker(URL.createObjectURL(new Blob([workerText], {{type:'text/javascript'}}))); }}
    catch(err){{ fail(err); return; }}
    w.onmessage = function(e){{
      if(e.data && e.data.id === conf.id){{
        w.terminate();
        post({{kind:'anydoc-result', id: conf.id, ok: !!e.data.ok, markdown: e.data.markdown || '', error: e.data.error || ''}});
      }}
    }};
    w.onerror = function(e){{ try{{ w.terminate(); }}catch(_){{}} fail(e.message || 'worker error'); }};
    w.postMessage({{ id: conf.id, url: conf.url, ext: conf.ext }});
  }}
  fetch('https://appassets.example/anydoc/worker.js')
    .then(function(r){{ if(!r.ok) throw new Error('worker fetch ' + r.status); return r.text(); }})
    .then(start)
    .catch(fail);
}})();
</script>
</body></html>";
        }

        /// <summary>
        /// anydoc Markdown → 主题化 HTML 页面（Markdig 管线 + 消毒/后处理 + 卡片 + Render 外壳）。
        /// Office 渲染与 PDF 文本视图共用；PDF 文本视图通过 extraCsp/bodySuffix 注入「页面图像」图库。
        /// </summary>
        private async Task<(string Html, List<TocItem> TocItems)> BuildAnyDocMarkdownPageAsync(PanelState state, string md)
        {
            var htmlRaw = await Task.Run(() =>
            {
                var parsed = Markdig.Markdown.ToHtml(md ?? "", _render.Pipeline);
                parsed = _render.ProcessRelativePaths(parsed, state.FileDir);
                parsed = _render.WrapTables(parsed);
                parsed = _render.UpgradePreBlocks(parsed);
                parsed = _render.SanitizeLinkHrefs(parsed);
                return parsed;
            });
            var fmCard = _render.BuildFrontMatterCard(null);
            var statsCard = BuildStatsCard(md ?? "");
            var tocCard = _render.BuildTocCard(htmlRaw);
            var tocItems = _render.BuildTocItems(htmlRaw);
            EnsureRemoteImagePolicy(state, htmlRaw);
            var html = _render.Render(htmlRaw, state, fmCard + tocCard + statsCard, this, null, LoadCustomCss());
            return (html, tocItems);
        }

        /// <summary>把 anydoc 产出的 Markdown 走 Markdig 管线渲染（与 md 文件同链路，含全部消毒/后处理）。</summary>
        private async Task RenderAnyDocMarkdownAsync(PanelState state, string md)
        {
            if (string.IsNullOrEmpty(state.CurrentFile)) return;
            try
            {
                var (html, tocItems) = await BuildAnyDocMarkdownPageAsync(state, md);
                SetOutline(tocItems);
                // 存储 anydoc 原始 Markdown，供 docx 内联编辑使用（编辑模式直接编辑这份内容）
                state.DocxMarkdown = md;
                _ = Dispatcher.BeginInvoke(new Action(() => RefreshStats(state))); // anydoc 转换完成 → 统计卡刷新
                state.WebView.NavigateToString(html);
                MarkLoaded(state);
                LogInfo("AnyDoc ok: " + Path.GetFileName(state.CurrentFile) + " (" + (md?.Length ?? 0) + " chars)");
            }
            catch (Exception ex)
            {
                LogErr("AnyDoc render: " + ex);
                RenderErrorPage(state, ex);
            }
        }

        /// <summary>回退到 FileConverter（原 OpenXML/PdfPig 解析）。仅 docx/xlsx/pptx 有本地回退解析器；
        /// 新增的 anydoc 格式（doc/ppt/rtf/odt/ods/odp/epub/csv）无回退解析器，直接抛错走错误页。</summary>
        private async Task FallbackOfficeAsync(PanelState state, string ext)
        {
            if (string.IsNullOrEmpty(state.CurrentFile)) return;
            try
            {
                var isDark = _theme.Current == _theme.Dark;
                state.WebView.NavigateToString(BuildLoadingPage(isDark));
                string content = ext switch
                {
                    ".xlsx" => await _converter.ExcelToHtml(state.CurrentFile),
                    ".pptx" => await _converter.PptToHtml(state.CurrentFile),
                    ".docx" => await _converter.DocxToHtml(state.CurrentFile),
                    _ => throw new NotSupportedException("该格式无本地回退解析器（需 anydoc-wasm）：" + ext)
                };
                var html = _render.BuildOfficePage(content, state, this);
                state.WebView.NavigateToString(html);
                MarkLoaded(state);
                SetOutline(null);
            }
            catch (Exception ex)
            {
                LogErr("Office fallback: " + ex);
                RenderErrorPage(state, ex);
            }
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
