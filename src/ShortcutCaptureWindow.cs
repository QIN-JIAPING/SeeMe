// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SeeMe
{
    /// <summary>
    /// 单条命令的快捷键录入窗口。
    ///
    /// <para><b>为什么用独立窗口而不是就地编辑</b>：录入需要"吃掉"用户按下的任意组合键
    /// （含 Tab / 方向键 / 空格）—— 放在设置页里，这些键会先被 ListBox / 按钮的默认键盘行为消费，
    /// 焦点还会在控件间跳。独立窗口只处理按键，行为可预测。</para>
    ///
    /// <para><b>Esc 固定为取消</b>：因此 Esc 本身不可被绑成命令快捷键（这是有意的取舍 ——
    /// 让"怎么退出这个对话框"永远有答案）。</para>
    ///
    /// <para><b>冲突提示</b>：同一手势绑到两条命令时命令表靠前者生效、后者静默失效，
    /// 所以这里在保存前就把冲突命令列出来，而不是让用户事后疑惑"我设的键没反应"。</para>
    /// </summary>
    public sealed class ShortcutCaptureWindow : Window
    {
        /// <summary>
        /// 用户确认后的结果：<c>null</c> = 未保存；空串 = 取消绑定；其余为手势字符串。
        /// </summary>
        public string? ResultGesture { get; private set; }

        private readonly ShortcutRegistry.Command _command;
        private readonly TextBlock _preview;
        private readonly TextBlock _warn;
        private string? _pending;

        public ShortcutCaptureWindow(ShortcutRegistry.Command command, string currentGesture)
        {
            _command = command;
            _pending = string.IsNullOrEmpty(currentGesture) ? null : currentGesture;

            Title = "修改快捷键";
            Width = 430;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            FontSize = 13;

            // 先合入主题字典，之后再取资源才拿得到画刷（与 MainWindow 里弹窗的做法一致）。
            try
            {
                if (App.GlobalTheme != null)
                {
                    App.GlobalTheme.ApplyToWindow(this, App.GlobalTheme.Current);
                    Background = (Brush)FindResource("WindowBackgroundBrush");
                }
            }
            catch { /* 主题资源缺失不阻断录入 */ }

            var fg = TryFindResource("TextBodyBrush") as Brush ?? Brushes.Black;
            var sec = TryFindResource("TextSecondaryBrush") as Brush ?? Brushes.Gray;
            var accent = TryFindResource("AccentBrush") as Brush ?? Brushes.SteelBlue;
            var danger = TryFindResource("DangerBrush") as Brush ?? Brushes.Firebrick;

            var root = new StackPanel { Margin = new Thickness(20, 18, 20, 18) };

            root.Children.Add(new TextBlock
            {
                Text = "为「" + command.Label + "」设置快捷键",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = fg,
                Margin = new Thickness(0, 0, 0, 4),
            });

            root.Children.Add(new TextBlock
            {
                Text = "直接按下想要的组合键（可与 Ctrl / Shift / Alt 组合）",
                FontSize = 11,
                Foreground = sec,
                Margin = new Thickness(0, 0, 0, 12),
            });

            _preview = new TextBlock
            {
                Text = _pending == null ? "未绑定" : ShortcutRegistry.ToDisplay(_pending),
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Foreground = _pending == null ? sec : accent,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 16, 0, 16),
            };
            root.Children.Add(new Border
            {
                Background = TryFindResource("InputBgBrush") as Brush,
                BorderBrush = TryFindResource("LightGrayBrush") as Brush,
                BorderThickness = new Thickness(0.5),
                CornerRadius = new CornerRadius(8),
                Child = _preview,
            });

            _warn = new TextBlock
            {
                FontSize = 11,
                Foreground = danger,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 0),
                Visibility = Visibility.Collapsed,
            };
            root.Children.Add(_warn);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 18, 0, 0),
            };
            var chip = TryFindResource("ChipBtn") as Style;
            buttons.Children.Add(MakeButton("保存", chip, true, OnSave));
            buttons.Children.Add(MakeButton("清除绑定", chip, false, OnClear));
            buttons.Children.Add(MakeButton("取消", chip, false, (_, _) => { DialogResult = false; }));
            root.Children.Add(buttons);

            Content = root;

            // 录入：用 PreviewKeyDown 抢在一切控件键盘处理之前拿到原始按键。
            PreviewKeyDown += OnCaptureKeyDown;
            Loaded += (_, _) => Focus();
        }

        private static Button MakeButton(string text, Style? style, bool primary, RoutedEventHandler onClick)
        {
            var b = new Button
            {
                Content = text,
                Padding = new Thickness(14, 5, 14, 5),
                FontSize = 11,
                Margin = new Thickness(6, 0, 0, 0),
                MinWidth = 76,
            };
            if (style != null) b.Style = style;
            if (primary) b.FontWeight = FontWeights.SemiBold;
            b.Click += onClick;
            return b;
        }

        private void OnCaptureKeyDown(object sender, KeyEventArgs e)
        {
            // Alt 组合键会以 Key.System 送达，真实键在 SystemKey（漏了它 Alt+F 之类录不进来）
            var key = e.Key == Key.System ? e.SystemKey : e.Key;

            // Esc 固定为取消（见类注释：Esc 因此不可被绑定）
            if (key == Key.Escape)
            {
                e.Handled = true;
                DialogResult = false;
                return;
            }

            // 只按下修饰键本身 → 还不构成一个完整手势，继续等
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.None)
            {
                e.Handled = true;
                return;
            }

            var gesture = ShortcutRegistry.Normalize(Keyboard.Modifiers, key);
            if (gesture == null) { e.Handled = true; return; }

            _pending = gesture;
            _preview.Text = ShortcutRegistry.ToDisplay(gesture);
            _preview.Foreground = TryFindResource("AccentBrush") as Brush ?? Brushes.SteelBlue;

            var conflicts = ShortcutRegistry.Conflicts(gesture, _command.Id);
            if (conflicts.Count > 0)
            {
                _warn.Text = "⚠ 该组合键已被「"
                             + string.Join("、", conflicts.Select(c => c.Label))
                             + "」占用。保存后本命令生效，那条将不再响应。";
                _warn.Visibility = Visibility.Visible;
            }
            else
            {
                _warn.Visibility = Visibility.Collapsed;
            }
            e.Handled = true;
        }

        private void OnSave(object sender, RoutedEventArgs e)
        {
            if (_pending == null)
            {
                // 没按任何键就点保存 → 视为取消，避免"误点保存把绑定清掉"
                DialogResult = false;
                return;
            }
            ResultGesture = _pending;
            DialogResult = true;
        }

        private void OnClear(object sender, RoutedEventArgs e)
        {
            ResultGesture = "";   // 空串 = 取消绑定（见 ShortcutRegistry.UnboundMarker 注释）
            DialogResult = true;
        }
    }
}
