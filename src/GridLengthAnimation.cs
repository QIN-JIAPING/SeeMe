// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace SeeMe
{
    /// <summary>
    /// GridLength 平滑动画：用于右侧面板列宽 0↔190 的展开/收起过渡，
    /// 避免面板显隐时主内容区宽度瞬跳（WPF 无内置 GridLength 动画）。
    /// </summary>
    public sealed class GridLengthAnimation : AnimationTimeline
    {
        public override Type TargetPropertyType => typeof(GridLength);

        public static readonly DependencyProperty FromProperty =
            DependencyProperty.Register(nameof(From), typeof(GridLength), typeof(GridLengthAnimation));

        public GridLength From
        {
            get => (GridLength)GetValue(FromProperty);
            set => SetValue(FromProperty, value);
        }

        public static readonly DependencyProperty ToProperty =
            DependencyProperty.Register(nameof(To), typeof(GridLength), typeof(GridLengthAnimation));

        public GridLength To
        {
            get => (GridLength)GetValue(ToProperty);
            set => SetValue(ToProperty, value);
        }

        public static readonly DependencyProperty EasingFunctionProperty =
            DependencyProperty.Register(nameof(EasingFunction), typeof(IEasingFunction),
                typeof(GridLengthAnimation), new PropertyMetadata(null));

        public IEasingFunction? EasingFunction
        {
            get => (IEasingFunction?)GetValue(EasingFunctionProperty);
            set => SetValue(EasingFunctionProperty, value);
        }

        public override object GetCurrentValue(object defaultOriginValue, object defaultDestinationValue,
            AnimationClock animationClock)
        {
            if (animationClock.CurrentProgress == null) return From;
            var raw = animationClock.CurrentProgress.Value;
            var progress = EasingFunction != null ? EasingFunction.Ease(raw) : raw;
            var from = From.Value;
            var to = To.Value;
            return new GridLength(from + (to - from) * progress, GridUnitType.Pixel);
        }

        protected override Freezable CreateInstanceCore() => new GridLengthAnimation();
    }

    /// <summary>右侧面板列宽管理扩展：统一 190↔0 的显示逻辑与动画过渡。</summary>
    public static class RightPanelCol
    {
        public const double OpenWidth = 190;
        public const double CloseWidth = 0;
        public static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(240);

        /// <summary>
        /// 对目标列应用展开/收起动画（不启用动画或无需变化时直接赋值）。
        /// 返回 true 表示已启动动画（调用方应在动画完成后恢复 WebView 渲染，避免 resize 卡顿）。
        /// </summary>
        public static bool Apply(ColumnDefinition col, bool open, bool animate)
        {
            if (col == null) return false;
            var to = open ? OpenWidth : CloseWidth;
            if (!animate)
            {
                col.Width = new GridLength(to);
                return false;
            }
            var from = col.ActualWidth;
            if (Math.Abs(from - to) < 0.5)
            {
                col.Width = new GridLength(to);
                return false;
            }
            var anim = new GridLengthAnimation
            {
                From = new GridLength(from),
                To = new GridLength(to),
                Duration = Duration,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };
            col.BeginAnimation(ColumnDefinition.WidthProperty, anim);
            return true;
        }
    }
}
