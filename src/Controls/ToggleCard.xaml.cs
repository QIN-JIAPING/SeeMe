// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SeeMe.Controls
{
    /// <summary>
    /// 侧边栏刷新+面板切换卡片：视觉与原「实时刷新」卡片一致（InputBgBrush / 10px 圆角 / 12,10 padding）。
    /// 功能：
    ///   ① 整卡点击 → CardClicked（手动刷新，带旋转加载动画）
    ///   ② 右侧按钮点击 → ToggleClicked（信息面板切换）
    /// IsRefreshing=true 时右侧图标旋转 + 文字变「刷新中…」；按压有缩放回弹反馈。
    /// </summary>
    public partial class ToggleCard : UserControl
    {
        public static readonly RoutedEvent ToggleClickedEvent =
            EventManager.RegisterRoutedEvent(nameof(ToggleClicked), RoutingStrategy.Bubble,
                typeof(RoutedEventHandler), typeof(ToggleCard));

        public static readonly RoutedEvent CardClickedEvent =
            EventManager.RegisterRoutedEvent(nameof(CardClicked), RoutingStrategy.Bubble,
                typeof(RoutedEventHandler), typeof(ToggleCard));

        public static readonly DependencyProperty HeaderProperty =
            DependencyProperty.Register(nameof(Header), typeof(string), typeof(ToggleCard),
                new FrameworkPropertyMetadata("刷新"));

        public static readonly DependencyProperty IconDataProperty =
            DependencyProperty.Register(nameof(IconData), typeof(Geometry), typeof(ToggleCard),
                new FrameworkPropertyMetadata(null, OnIconDataChanged));

        public static readonly DependencyProperty ButtonToolTipProperty =
            DependencyProperty.Register(nameof(ButtonToolTip), typeof(string), typeof(ToggleCard),
                new FrameworkPropertyMetadata(""));

        public static readonly DependencyProperty IsRefreshingProperty =
            DependencyProperty.Register(nameof(IsRefreshing), typeof(bool), typeof(ToggleCard),
                new FrameworkPropertyMetadata(false, OnRefreshingChanged));

        public static readonly DependencyProperty SpinDurationProperty =
            DependencyProperty.Register(nameof(SpinDuration), typeof(double), typeof(ToggleCard),
                new FrameworkPropertyMetadata(800.0));

        private Storyboard? _spinStory;
        private string? _originalHeader;

        public ToggleCard()
        {
            InitializeComponent();
            MouseLeftButtonDown += OnCardClick;
        }

        public string Header
        {
            get => (string)GetValue(HeaderProperty);
            set => SetValue(HeaderProperty, value);
        }

        public Geometry? IconData
        {
            get => (Geometry?)GetValue(IconDataProperty);
            set => SetValue(IconDataProperty, value);
        }

        public string ButtonToolTip
        {
            get => (string)GetValue(ButtonToolTipProperty);
            set => SetValue(ButtonToolTipProperty, value);
        }

        /// <summary>是否正在刷新（true 时图标旋转 + 文字变「刷新中…」）。</summary>
        public bool IsRefreshing
        {
            get => (bool)GetValue(IsRefreshingProperty);
            set => SetValue(IsRefreshingProperty, value);
        }

        /// <summary>旋转一圈持续时间（ms），默认 800。</summary>
        public double SpinDuration
        {
            get => (double)GetValue(SpinDurationProperty);
            set => SetValue(SpinDurationProperty, value);
        }

        // ── 路由事件 ──

        /// <summary>右侧按钮点击（信息面板切换）。</summary>
        public event RoutedEventHandler ToggleClicked
        {
            add => AddHandler(ToggleClickedEvent, value);
            remove => RemoveHandler(ToggleClickedEvent, value);
        }

        /// <summary>整卡点击（手动刷新）。</summary>
        public event RoutedEventHandler CardClicked
        {
            add => AddHandler(CardClickedEvent, value);
            remove => RemoveHandler(CardClickedEvent, value);
        }

        // ── DP 回调 ──

        private static void OnIconDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ToggleCard card && card.PartIcon != null)
                card.PartIcon.Data = e.NewValue as Geometry;
        }

        private static void OnRefreshingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not ToggleCard card) return;
            if ((bool)e.NewValue)
                card.StartSpin();
            else
                card.StopSpin();
        }

        // ── 交互处理 ──

        private void OnToggleClick(object sender, RoutedEventArgs e)
            => RaiseEvent(new RoutedEventArgs(ToggleClickedEvent));

        private void OnCardClick(object sender, MouseButtonEventArgs e)
        {
            if (IsRefreshing || !IsEnabled) return;
            // 点在按钮上不重复触发 CardClicked（按钮有自己的 Click → ToggleClicked）
            if (e.OriginalSource is Button) return;
            RaiseEvent(new RoutedEventArgs(CardClickedEvent));
        }

        // ── 旋转动画 ──

        private void StartSpin()
        {
            _originalHeader ??= Header;
            PartLabel.Text = "刷新中\u2026";
            PartIcon.Opacity = 0.7;

            var duration = TimeSpan.FromMilliseconds(Math.Max(200, SpinDuration));
            _spinStory = new Storyboard();
            _spinStory.RepeatBehavior = RepeatBehavior.Forever;

            var anim = new DoubleAnimation(360, duration)
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            Storyboard.SetTarget(anim, IconSpin);
            Storyboard.SetTargetProperty(anim, new PropertyPath("Angle"));
            _spinStory.Children.Add(anim);

            _spinStory.Begin(this);
        }

        private void StopSpin()
        {
            _spinStory?.Stop(this);
            _spinStory = null;

            var snapBack = new Storyboard();
            var toZero = new DoubleAnimation(0, TimeSpan.FromMilliseconds(300))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(toZero, IconSpin);
            Storyboard.SetTargetProperty(toZero, new PropertyPath("Angle"));
            snapBack.Children.Add(toZero);

            snapBack.Completed += (_, _) =>
            {
                if (_originalHeader != null)
                    PartLabel.Text = _originalHeader;
                PartIcon.Opacity = 1.0;
            };

            snapBack.Begin(this);
        }
    }
}
