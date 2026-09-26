// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;

namespace SeeMe
{
    public partial class MainWindow
    {
        // ═══════ 文件职责：页内交互（查找 / 缩放对话框） ═══════
        // 由 MainWindow.Rendering.cs 拆出（2026-09-10）。此前这些方法被错置于 anydoc 区块的分隔
        // 注释之下（原注释只覆盖渲染管线，交互方法属另一职责），本次按语义归位。
        // 公开入口见 MainWindow.Palette.cs（ShowFindFor / SetZoomActive / SetZoomActiveDelta）。

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
            EnsureRightPanel();
            SetZoomDialog(_app.Right);
        }


        private void OnFindL(object sender, RoutedEventArgs e) => ShowFind(_app.Left);

        private void OnFindR(object sender, RoutedEventArgs e)
        {
            EnsureRightPanel();
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
