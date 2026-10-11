// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SeeMe
{
    /// <summary>
    /// 诊断信息预览窗口：**先把要复制的内容原样显示出来，再让用户决定是否复制**。
    ///
    /// <para><b>为什么不能"点一下直接进剪贴板"</b>：诊断信息里有数据目录、安装位置与日志尾部，
    /// 天然带着本机用户名与用户文件路径。用户在看不见内容的情况下点"复制"，
    /// 等于把"发什么出去"的决定权完全交给了程序。
    /// 这对应 <c>securing-agentic-ai-tool-invocation</c> 里的 human-in-the-loop 原则 ——
    /// 高影响动作（此处是"数据离开本机"）必须由人看过再确认。</para>
    ///
    /// <para>脱敏（<see cref="DiagnosticInfo.Redact"/>）已经把默认文本降了一档敏感度，
    /// 但它只是纵深的一层；真正的护栏是这个"看得见"的步骤。</para>
    /// </summary>
    public sealed class DiagnosticPreviewWindow : Window
    {
        /// <summary>用户是否点了「复制到剪贴板」。</summary>
        public bool Copied { get; private set; }

        private readonly TextBox _box;

        /// <param name="content">已脱敏、即将被复制的完整文本。</param>
        /// <param name="includedLogLines">文本里包含的日志行数；0 表示不含日志。</param>
        public DiagnosticPreviewWindow(string content, int includedLogLines)
        {
            Title = "诊断信息预览";
            Width = 720;
            Height = 520;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            FontSize = 13;

            try
            {
                if (App.GlobalTheme != null)
                {
                    App.GlobalTheme.ApplyToWindow(this, App.GlobalTheme.Current);
                    Background = (Brush)FindResource("WindowBackgroundBrush");
                }
            }
            catch { /* 主题资源缺失不阻断预览 */ }

            var fg = TryFindResource("TextBodyBrush") as Brush ?? Brushes.Black;
            var sec = TryFindResource("TextSecondaryBrush") as Brush ?? Brushes.Gray;

            var root = new DockPanel { Margin = new Thickness(16) };

            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            header.Children.Add(new TextBlock
            {
                Text = "以下是将要复制的内容（已把用户主目录替换为 %USERPROFILE%）",
                FontSize = 12,
                Foreground = fg,
                TextWrapping = TextWrapping.Wrap,
            });
            header.Children.Add(new TextBlock
            {
                Text = includedLogLines > 0
                    ? $"⚠ 含最近 {includedLogLines} 行日志 —— 日志里会出现你打开过的文件路径。请确认无误后再复制。"
                    : "不含日志尾部，仅版本/系统/目录等摘要信息。",
                FontSize = 11,
                Margin = new Thickness(0, 4, 0, 0),
                Foreground = sec,
                TextWrapping = TextWrapping.Wrap,
            });
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0),
            };
            var chip = TryFindResource("ChipBtn") as Style;
            buttons.Children.Add(MakeButton("复制到剪贴板", chip, true, OnCopy));
            buttons.Children.Add(MakeButton("取消", chip, false, (_, _) => { DialogResult = false; }));
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);

            _box = new TextBox
            {
                Text = content,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new FontFamily("Consolas, 'Cascadia Mono', monospace"),
                FontSize = 12,
                Padding = new Thickness(10),
                Background = TryFindResource("InputBgBrush") as Brush,
                Foreground = fg,
                BorderBrush = TryFindResource("LightGrayBrush") as Brush,
                BorderThickness = new Thickness(0.5),
            };
            root.Children.Add(_box);

            Content = root;
            Loaded += (_, _) => { _box.Focus(); _box.CaretIndex = 0; };
        }

        private static Button MakeButton(string text, Style? style, bool primary, RoutedEventHandler onClick)
        {
            var b = new Button
            {
                Content = text,
                Padding = new Thickness(14, 5, 14, 5),
                FontSize = 11,
                Margin = new Thickness(6, 0, 0, 0),
                MinWidth = 96,
            };
            if (style != null) b.Style = style;
            if (primary) b.FontWeight = FontWeights.SemiBold;
            b.Click += onClick;
            return b;
        }

        private void OnCopy(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(_box.Text);
                Copied = true;
                DialogResult = true;
            }
            catch (Exception ex)
            {
                SeeMeLog.Error("DiagnosticPreview.Clipboard", ex);
                MessageBox.Show(this, "写入剪贴板失败：" + ex.Message, "SeeMe 诊断",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
