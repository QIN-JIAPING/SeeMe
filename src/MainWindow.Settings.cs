// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

// MainWindow 分部类 —— 设置即时应用
//
// 本文件承载「设置对话框改一项 → 立即作用到当前窗口」的一组方法：
//   Apply*Now              单项设置的即时生效（缩放/主题色/渲染风格/护眼/动画/自动保存/字号）
//   ApplyHistorySize       历史上限的即时裁剪
//   ApplyInfoPanelDefault  信息面板默认展开状态
//   ThemeAnimationsEnabled 供设置对话框读回的只读属性
//
// 这些方法**全部是 public**：由 SettingsWindow / CommandPaletteWindow 直接调用，
// 名称中的 Now 后缀表示「立即生效」（区别于启动时的一次性初始化）。
//
// 依赖的 _app / _theme / _render / _customCssPath 等字段与
// ReloadOpenPanels / ApplyPanelZoom / SetInfoPanelVisibility 等私有方法
// 均定义于本 partial 类的其他文件（主要是 MainWindow.xaml.cs），跨文件可见。

using System;

namespace SeeMe
{
    public partial class MainWindow
    {
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
            if (StatusText != null)
                StatusText.Text = "已应用 Markdown 渲染风格：" + style;
        }

        /// <summary>护眼模式开关，持久化并重渲染（暖色滤镜作用于所有 HTML 预览页）。</summary>
        public void ApplyEyeCareNow(bool on)
        {
            AppSettings.Set(AppSettings.EyeCareKey, on);
            if (_render is RenderService rs) rs.EyeCare = on;
            ReloadOpenPanels();
            if (StatusText != null)
                StatusText.Text = on ? "已开启护眼模式" : "已关闭护眼模式";
        }

        /// <summary>动画效果开关（主题切换过渡动画）。</summary>
        public void ApplyAnimationsNow(bool on)
        {
            AppSettings.Set(AppSettings.AnimationsKey, on);
            _theme.AnimationsEnabled = on;
        }

        /// <summary>
        /// 自动保存开关/间隔变更时对正在编辑的面板即时生效：向编辑页注入 __setAutoSave，
        /// 不重建页面、不丢失正在编辑的内容（未打开编辑页则下次进入编辑时生效）。
        /// </summary>
        public void ApplyAutoSaveSettingsNow()
        {
            if (_app == null) return;
            var on = AppSettings.Get(AppSettings.AutoSaveKey, true) ? "true" : "false";
            var delayMs = AppSettings.Get(AppSettings.AutoSaveDelayKey, 10) * 1000;
            foreach (var st in new[] { _app.Left, _app.Right })
            {
                if (st?.EditMode != true || st.WebView?.CoreWebView2 == null) continue;
                try
                {
                    st.WebView.CoreWebView2.ExecuteScriptAsync(
                        $"window.__setAutoSave ? window.__setAutoSave({on}, {delayMs}) : ''");
                }
                catch (Exception ex) { LogErr("ApplyAutoSaveSettingsNow: " + ex.Message); }
            }
        }

        /// <summary>PDF 查看器顶栏状态徽标：显示文本层已提取 / 扫描版无文本层（提取完成且面板为 PDF 时）。</summary>
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
    }
}
