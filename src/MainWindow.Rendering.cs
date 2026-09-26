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
            // 堆栈含文件路径与内部结构，仅 Debug 构建显示；Release 只给友好提示，避免截图/分享时泄露
#if DEBUG
            var stack = System.Security.SecurityElement.Escape(ex.StackTrace ?? "(无堆栈信息)");
            var stackHtml = "<div class='stack'>" + stack + "</div>";
#else
            var stackHtml = "";
#endif
            var css = RenderService.PageResetCss + @"
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
    " + stackHtml + @"
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
            var css = RenderService.PageResetCss + @"
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
            var css = RenderService.PageResetCss + @"
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
            var css = RenderService.PageResetCss + @"
  html,body { width:100%; height:100vh; }
  body { display:flex; align-items:center; justify-content:center; background:var(--bg); color:var(--text);
         font-family:'Segoe UI','Microsoft YaHei',sans-serif; font-size:15px;
         transition:background-color .3s ease,color .3s ease; }
  " + RenderService.SpinnerCss;
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
            // 图片：独立查看链路（原生 <img>，不走 Markdig/anydoc）
            if (FileTypes.IsImage(ext))
            {
                RenderImage(state);
                return;
            }
            switch (ext)
            {
                case ".pdf":
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
                case ".xls":
                    await ReloadOfficeAsync(state, ext, ct);
                    return;
                default:
                    // Markdown / 纯文本 / HTML 等其余格式走原始渲染链路
                    await ReloadMarkdownAsync(state, ct);
                    return;
            }
        }

        /// <summary>Office/PDF 系加载：PDF 文本视图、anydoc-wasm 12 种格式（含宏变体）、旧 .xls 走 FileConverter。</summary>
        private async Task ReloadOfficeAsync(PanelState state, string ext, CancellationToken ct)
        {
            try
            {
                switch (ext)
                {
                    case ".pdf":
                        // PDF 统一文本视图：图文重建由页面内 JS 完成，无 C# 侧大纲
                        RenderPdf(state); MarkLoaded(state); SetOutline(null);
                        return;
                    case ".xls":
                        // .xls（旧二进制 OLE）打包的 anydoc 不支持，直接走 FileConverter
                        await RenderExcel(state); MarkLoaded(state); SetOutline(null); return;
                    default:
                        // anydoc-wasm 覆盖的 12 种格式（docx/xlsx/pptx/doc/ppt/rtf/odt/ods/odp/epub/csv）及宏变体 docm/xlsm
                        await RenderOfficeAnyDoc(state, ext); return;
                }
            }
            catch (OperationCanceledException) { LogInfo("Reload cancelled (office): " + Path.GetFileName(state.CurrentFile)); }
            catch (Exception ex)
            {
                LogErr("Reload office: " + ex);
                if (!ct.IsCancellationRequested) RenderErrorPage(state, ex);
                StatusText.Text = "读取失败: " + ex.Message;
            }
        }

        /// <summary>Markdown 渲染链路：解析/后处理在后台线程执行，大文件先出加载占位。</summary>
        private async Task ReloadMarkdownAsync(PanelState state, CancellationToken ct)
        {
            try
            {
                ct.ThrowIfCancellationRequested();

                if (CheckFileSize(state, out var mdPath))
                {
                    MarkLoaded(state);
                    return;
                }

                var isDark = _theme.Current == _theme.Dark;
                // 大文件：先在 UI 线程显示加载占位，避免读取/解析阻塞界面导致假死
                const long LargeMdThreshold = 1L * 1024 * 1024;
                if (new FileInfo(mdPath).Length > LargeMdThreshold)
                    state.WebView.NavigateToString(BuildLoadingPage(isDark));

                ct.ThrowIfCancellationRequested();

                // 读取 + Markdig 解析 + 后处理统一在后台线程执行，避免大文件阻塞 UI
                var currentFile = state.CurrentFile;
                var (htmlRaw, fmCard, statsCard, tocCard, tocItems) = await Task.Run(() =>
                {
                    ct.ThrowIfCancellationRequested();
                    // 显式守卫：state.CurrentFile 是 string?，ReadAuto 形参非空。
                    // CheckFileSize 已校验过存在性，此处为可空性收窄（消除 CS8604）。
                    var md = string.IsNullOrEmpty(currentFile) ? "" : TextEncoding.ReadAuto(currentFile);
                    md = _render.StripBom(md);
                    md = _render.StripYamlFrontMatter(md, out var fmBlock);
                    var card = _render.BuildFrontMatterCard(fmBlock);
                    var parsed = Markdig.Markdown.ToHtml(md ?? "", _render.Pipeline);
                    parsed = _render.ProcessRelativePaths(parsed, state.FileDir);
                    parsed = _render.WrapTables(parsed);
                    parsed = _render.UpgradePreBlocks(parsed);
                    // 行号注入必须在 UpgradePreBlocks 之后（此时才形成 code-block 结构）、
                    // 在 Prism 高亮之前（页面加载时 Prism.highlightAll 才跑）—— 见方法注释。
                    parsed = _render.AddCodeLineNumbers(parsed);
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

        /// <summary>最近一次大纲条目（供思维导图视图重建标题树）。</summary>
        private List<TocItem> _lastTocItems = new();

        /// <summary>
        /// 大纲跳转防回环标志：点击大纲 → scrollIntoView → 页面 scroll 事件上报 →
        /// 若不加锁会再次改选中态，产生抖动/回弹。
        /// 用法：置 true → 执行跳转 → 延迟复位。
        /// </summary>
        private bool _isNavigatingFromOutline;

        /// <summary>
        /// 把大纲列表的选中态同步到「当前可见标题」。
        /// 由 MainWindow.Messages.cs 的 scroll 处理调用（已过滤 _isNavigatingFromOutline）。
        ///
        /// <para>左侧 Tab 的大纲与浮动大纲<b>共用本方法</b>——两者是同一份数据的平行视图，
        /// 分开写两份高亮代码必然漂移。</para>
        ///
        /// <para>PDF 场景下 id 是书签目标键，与 TocItem.Id 同一套令牌口径，可直接比对。</para>
        /// </summary>
        private void HighlightOutlineItem(string headingId)
        {
            if (string.IsNullOrEmpty(headingId)) return;
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => HighlightOutlineItem(headingId)));
                return;
            }
            try
            {
                // 只有活动面板的大纲才跟随（双栏模式下两个面板各有滚动，共用一份大纲 UI）
                var target = ActiveOrLeft;
                if (target == null || target.CurrentHeadingId != headingId) return;

                ApplyOutlineSelection(OutlineList, headingId);
                // 浮层未展开时跳过（Items 为空，天然无害，但省掉一次遍历）
                if (_outlineFloatOn) ApplyOutlineSelection(OutlineFloatList, headingId);
            }
            catch (Exception ex) { LogErr("HighlightOutlineItem: " + ex.Message); }
        }

        /// <summary>
        /// 在指定的 ListBox 中把 <paramref name="headingId"/> 对应的项设为选中并滚入视口。
        /// 抽成共用是因为左侧 Tab 与浮动大纲用的是同一套 <see cref="TocItem"/> 容器结构。
        /// </summary>
        private static void ApplyOutlineSelection(ListBox list, string headingId)
        {
            foreach (var obj in list.Items)
            {
                if (obj is ListBoxItem li && li.Tag is TocItem toc
                    && string.Equals(toc.Id, headingId, StringComparison.Ordinal))
                {
                    if (!ReferenceEquals(list.SelectedItem, li))
                    {
                        list.SelectedItem = li;
                        // 滚到可视区域（列表长时高亮项可能在视野外）。用 BringIntoView 而非
                        // ScrollIntoView —— 后者在某些 WPF 版本会抢焦点，导致键盘操作失焦。
                        try { li.BringIntoView(); } catch { }
                    }
                    return;
                }
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
            _lastTocItems = list;
            if (list.Count == 0)
            {
                OutlineList.Visibility = Visibility.Collapsed;
                OutlineHint.Visibility = Visibility.Visible;
                OutlineCount.Visibility = Visibility.Collapsed;
                // 切到没有标题的文档（图片/PDF 无书签等）时，浮层留着只会是个空壳 → 自动收回
                if (_outlineFloatOn) SetOutlineFloatVisible(false);
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
            // 浮动大纲镜像同一份数据（浮层未开时该方法内部直接返回）
            SyncOutlineFloatItems();
        }

        /// <summary>点击大纲条目：在当前聚焦面板（默认左栏）中平滑滚动到对应标题锚点。
        /// PDF 文本视图的大纲 id 是书签/标题目标键，走页面内 __pdfScrollTo（含书签按 y 比例定位）。
        /// 左侧 Tab 与浮动大纲共用本处理器（<c>sender</c> 即发起点击的那个 ListBox）。</summary>
        private void OnOutlineClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is not ListBox list) return;
            if (list.SelectedItem is ListBoxItem li && li.Tag is TocItem toc)
            {
                var target = ActiveOrLeft;
                if (target?.WebView?.CoreWebView2 == null) return;
                var isPdf = Path.GetExtension(target.CurrentFile ?? "")
                    .Equals(".pdf", StringComparison.OrdinalIgnoreCase);
                var js = isPdf
                    ? "if(window.__pdfScrollTo)window.__pdfScrollTo("
                      + System.Text.Json.JsonSerializer.Serialize(toc.Id) + ");"
                    : "var el=document.getElementById("
                      + System.Text.Json.JsonSerializer.Serialize(toc.Id)
                      + ");if(el)el.scrollIntoView({behavior:'smooth',block:'start'});";
                // 防回环：跳转触发的 scroll 上报会改选中态，导致抖动/回弹。
                // 上锁 → 执行 → 800ms 后解锁（慢于 smooth 滚动完成，避免中途又被打回）。
                _isNavigatingFromOutline = true;
                _ = target.WebView.CoreWebView2.ExecuteScriptAsync(js);
                _ = System.Threading.Tasks.Task.Delay(800).ContinueWith(_ =>
                    Dispatcher.BeginInvoke(new Action(() => _isNavigatingFromOutline = false)));
            }
        }

        /// <summary>读取用户自定义 CSS 内容（限量 256KB），失败返回空字符串。</summary>
        private string LoadCustomCss()
        {
            try
            {
                if (string.IsNullOrEmpty(_customCssPath) || !File.Exists(_customCssPath)) return "";
                var fi = new FileInfo(_customCssPath);
                if (fi.Length > Limits.MaxSearchBytes) return "";
                return File.ReadAllText(_customCssPath);
            }
            catch { return ""; }
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

                ShowThemedDialog(w, x => x.Content = panel);
            }
            catch (Exception ex) { LogErr("ShowShortcutsDialog: " + ex.Message); }
        }

        /// <summary>
        /// 状态栏「导出图表」按钮：把当前活动面板的页内图表导出为 PNG / SVG。
        /// 文档不含图表时该按钮保持隐藏（见 <see cref="UpdateChartExportAvailability"/>）。
        /// </summary>
        private async void OnExportChart(object sender, RoutedEventArgs e)
        {
            var state = ActiveOrLeft;
            if (state?.WebView?.CoreWebView2 == null) return;
            try
            {
                // 页面侧探明有哪些可导出的图表，弹选择框让用户决定导哪个、什么格式。
                // 用 ExecuteScriptAsync 拿 JSON 数组（页面侧已过滤掉不支持的图表类型）。
                var probe = await state.WebView.CoreWebView2.ExecuteScriptAsync(
                    "(function(){"
                    + "function n(s){return document.querySelectorAll(s).length;}"
                    + "return JSON.stringify({echarts:n('.seeme-chart'),"
                    + "markmap:n('.seeme-markmap'),mermaid:n('.mermaid')});})()");
                var json = System.Text.Json.JsonDocument.Parse(UnwrapJsonString(probe)).RootElement;
                var choices = new List<(string Kind, string Label, int Index)>();
                AddChartChoices(choices, json, "echarts", "ECharts 图表");
                AddChartChoices(choices, json, "markmap", "思维导图");
                AddChartChoices(choices, json, "mermaid", "Mermaid 图");
                if (choices.Count == 0)
                {
                    StatusText.Text = "当前文档没有可导出的图表";
                    return;
                }

                var picked = ShowChartPicker(choices);
                if (picked == null) return;
                var (kind, index, format) = picked.Value;
                await RequestChartExportAsync(state, kind, index, format);
            }
            catch (Exception ex)
            {
                LogErr("OnExportChart: " + ex.Message);
                StatusText.Text = "导出图表失败: " + ex.Message;
            }
        }

        private static void AddChartChoices(List<(string, string, int)> target,
            System.Text.Json.JsonElement counts, string kind, string label)
        {
            if (!counts.TryGetProperty(kind, out var cEl)) return;
            var n = cEl.GetInt32();
            for (var i = 0; i < n; i++) target.Add((kind, label, i));
        }

        /// <summary>
        /// ExecuteScriptAsync 返回的是「JSON 值的 JSON 字符串」（结果被再包一层引号并转义），
        /// 必须先去外层引号才能当 JSON 解析 —— 直接 Parse 会得到 JsonValueKind.String 而非对象。
        /// </summary>
        private static string UnwrapJsonString(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return "{}";
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(raw);
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.String)
                    return doc.RootElement.GetString() ?? "{}";
                return raw;
            }
            catch { return "{}"; }
        }

        /// <summary>将当前预览面板截图导出为图片（格式 PNG/JPG 与 1x/2x 分辨率可在设置中配置）。</summary>
        private async void OnExportImage(object sender, RoutedEventArgs e)
        {
            var state = ActiveOrLeft;
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
        /// <param name="state">面板状态。</param>
        /// <param name="verifiedPath">
        /// 返回 false 时为已确认非空的文件路径（返回 true 时其值不可用，调用方应直接 return）。
        /// 调用方用此参数即可获得编译器认可的非空路径，避免 CS8604 与 <c>!</c> 压制。
        /// </param>
        private bool CheckFileSize(PanelState state, out string verifiedPath)
        {
            verifiedPath = string.Empty;
            if (string.IsNullOrEmpty(state.CurrentFile)) return true;
            var fi = new FileInfo(state.CurrentFile);
            if (fi.Length > MaxFileSize)
            {
                state.WebView.NavigateToString(
                    BuildNoticePage(_theme.Current == _theme.Dark, "文件过大", "超过 50MB 限制，无法预览"));
                return true;
            }
            verifiedPath = state.CurrentFile;
            return false;
        }

        /// <summary>兼容旧调用点的重载（不关心路径时使用）。</summary>
        private bool CheckFileSize(PanelState state) => CheckFileSize(state, out _);

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
    }
}
