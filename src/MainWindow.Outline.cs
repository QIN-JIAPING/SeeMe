// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SeeMe
{
    /// <summary>
    /// MainWindow 分部类：浮动大纲（项 6）。
    ///
    /// <para><b>为什么是"浮层"而不是"独立窗口"</b>：项目里已有 <see cref="CommandPaletteWindow"/>
    /// 这种独立 Window 的先例，但独立窗口要自己处理生命周期——主窗最小化到托盘时它不会跟着隐藏
    /// （见 <c>MainWindow.OnClosing</c> 的 tray 分支：主窗只是 <c>Hide()</c>，不放 <c>Close()</c>，
    /// 独立子窗会留在屏幕上）。浮层放在 <c>ContentGrid</c> 内则天然继承主窗的显示/隐藏/最小化/裁剪，
    /// 零生命周期代码。代价是不能拖出主窗边界——对本功能可以接受。</para>
    ///
    /// <para><b>与三态 Tab 的关系</b>：不推翻 <c>OutlinePanel</c> 那套左侧 Tab（大纲/标注/全部标注），
    /// 本浮层是<b>平行入口</b>，两者共用同一批数据与同一个
    /// <see cref="HighlightOutlineItem"/> 选中态同步逻辑。浮层开着时左侧 Tab 可以随便切，
    /// 互不干扰。</para>
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>浮动大纲是否展开。与左侧 Tab 的选择无关（两者可同时存在）。</summary>
        private bool _outlineFloatOn;

        // ──────────────── 开合 ────────────────

        /// <summary>左侧 Tab 内「⇱ 浮动」按钮 与 浮层「×」共用入口。</summary>
        private void OnOutlineFloatToggle(object sender, RoutedEventArgs e)
        {
            SetOutlineFloatVisible(!_outlineFloatOn);
        }

        /// <summary>
        /// 显示 / 隐藏浮动大纲。隐藏时把按钮置回未选中态，
        /// 并把浮层位置重置回默认锚点——下次打开不会停在用户上次拖到的奇怪角落。
        /// </summary>
        private void SetOutlineFloatVisible(bool on)
        {
            try
            {
                _outlineFloatOn = on;
                if (on)
                {
                    // 先开可见性再灌数据：SyncOutlineFloatItems 的守卫要求 Visibility==Visible
                    // （否则从关闭态开浮层会得到一个空壳）
                    OutlineFloat.Visibility = Visibility.Visible;
                    // 没有任何标题时开浮层只会是个空壳 → 拒绝并回退
                    if (_lastTocItems.Count == 0)
                    {
                        OutlineFloat.Visibility = Visibility.Collapsed;
                        _outlineFloatOn = false;
                        StatusText.Text = "当前文档没有标题，无法显示大纲";
                        UpdateOutlineFloatButtonVisual();
                        return;
                    }
                    SyncOutlineFloatItems();
                    ResetOutlineFloatPosition();
                    StatusText.Text = "浮动大纲：拖动标题栏移动 · 点击条目跳转 · 再点「⇱ 浮动」收回";
                }
                else
                {
                    OutlineFloat.Visibility = Visibility.Collapsed;
                    OutlineFloatList.Items.Clear();
                }
                UpdateOutlineFloatButtonVisual();
            }
            catch (Exception ex)
            {
                LogErr("SetOutlineFloatVisible: " + ex.Message);
                _outlineFloatOn = false;
            }
        }

        /// <summary>
        /// 「⇱ 浮动」按钮的选中态视觉：开着时前景/边框换 Accent 色。
        /// 沿用 <c>MainWindow.Panels.cs</c> 里 <c>SetResourceReference</c>（等价 XAML DynamicResource）
        /// 的写法，键名一律显式写资源名；属性一律全限定，避免分部类内 foreach 式歧义。
        /// </summary>
        private void UpdateOutlineFloatButtonVisual()
        {
            try
            {
                var fg = _outlineFloatOn ? "AccentBrush" : "TextSecondaryBrush";
                var border = _outlineFloatOn ? "AccentBrush" : "LightGrayBrush";
                OutlineFloatBtn.SetResourceReference(Control.ForegroundProperty, fg);
                OutlineFloatBtn.SetResourceReference(Control.BorderBrushProperty, border);
            }
            catch (Exception ex) { LogErr("UpdateOutlineFloatButtonVisual: " + ex.Message); }
        }

        /// <summary>把浮层放回默认位置（内容区左上、标题栏下方）。</summary>
        private void ResetOutlineFloatPosition()
        {
            OutlineFloat.Margin = new Thickness(14, 64, 0, 0);
        }

        // ──────────────── 拖动 ────────────────

        /// <summary>
        /// 拖动标题栏移动浮层。
        ///
        /// <para>不能用 <c>DragMove()</c>——那是 <see cref="Window"/> 的成员，<see cref="Border"/>
        /// 没有。改用鼠标捕获 + 位移换算 Margin 的手工拖动。</para>
        ///
        /// <para>钳制在内容区边界内：浮层不能拖到 <c>ContentGrid</c> 之外（拖出去就够不着了）。</para>
        /// </summary>
        private void OnOutlineFloatDrag(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            try
            {
                var startMargin = OutlineFloat.Margin;
                var startLocal = e.GetPosition(ContentGrid);        // 光标在内容区内的落点

                OutlineFloatBar.CaptureMouse();
                MouseEventHandler move = (_, me) =>
                {
                    var now = me.GetPosition(ContentGrid);
                    var dx = now.X - startLocal.X;
                    var dy = now.Y - startLocal.Y;

                    var w = OutlineFloat.ActualWidth > 0 ? OutlineFloat.ActualWidth : OutlineFloat.Width;
                    var h = OutlineFloat.ActualHeight > 0 ? OutlineFloat.ActualHeight : 200;

                    // 目标左上角 = 起始边距 + 位移；再钳进内容区
                    var left = Math.Max(0, Math.Min(ContentGrid.ActualWidth - w, startMargin.Left + dx));
                    var top = Math.Max(0, Math.Min(ContentGrid.ActualHeight - h, startMargin.Top + dy));
                    OutlineFloat.Margin = new Thickness(left, top, 0, 0);
                };
                MouseButtonEventHandler? up = null;
                up = (_, ue) =>
                {
                    OutlineFloatBar.ReleaseMouseCapture();
                    OutlineFloatBar.MouseMove -= move;
                    if (up is not null) OutlineFloatBar.MouseLeftButtonUp -= up;
                };
                OutlineFloatBar.MouseMove += move;
                OutlineFloatBar.MouseLeftButtonUp += up;
                e.Handled = true;
            }
            catch (Exception ex) { LogErr("OnOutlineFloatDrag: " + ex.Message); }
        }

        // ──────────────── 内容同步 ────────────────

        /// <summary>
        /// 用 <see cref="_lastTocItems"/> 重建浮层列表。
        /// 由 <see cref="SetOutline"/> 在填充左侧列表之后调用——**单一数据源**，
        /// 不在 <see cref="SetOutline"/> 里重复解析一遍 <see cref="TocItem"/>。
        /// </summary>
        private void SyncOutlineFloatItems()
        {
            if (!_outlineFloatOn || OutlineFloat.Visibility != Visibility.Visible) return;
            OutlineFloatList.Items.Clear();
            foreach (var it in _lastTocItems)
            {
                var tb = new TextBlock
                {
                    Text = it.Title,
                    FontSize = 11,
                    Margin = new Thickness((it.Level - 1) * 10, 0, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    ToolTip = it.Title
                };
                OutlineFloatList.Items.Add(new ListBoxItem { Content = tb, Tag = it });
            }
            OutlineFloatCount.Text = _lastTocItems.Count + " 项";
        }
    }
}
