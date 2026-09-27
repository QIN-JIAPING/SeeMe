// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Media;

namespace SeeMe
{
    /// <summary>
    /// MainWindow 分部类：页内图表（ECharts / markmap / Mermaid）导出为 PNG / SVG。
    ///
    /// <para><b>职责切分</b>：页面侧（<see cref="RenderService.ChartExportScript"/>）只负责
    /// 「把图表序列化成 data URL / SVG 文本 → postMessage 回传」；本文件负责
    /// 「接收 → 弹保存对话框 → 落盘 → 状态栏反馈」。</para>
    ///
    /// <para><b>为什么不在页面里直接下载</b>：WebView2 页面在收紧的 CSP 下没有 file: 写权限，
    /// 且「保存到哪」必须由宿主决定。走 <c>a[download]</c> 会落到 WebView2 的下载目录，
    /// 用户既找不到也无法与应用的「固定导出目录」设置联动。</para>
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>
        /// 待导出图表的上下文：发起导出时记录，等页面回传数据时用于命名与保存。
        ///
        /// <para><b>为什么需要令牌</b>：导出是异步的（页面序列化 → postMessage → 宿主落盘），
        /// 期间用户可能切换文件或再次点击导出。没有令牌就无法区分
        /// 「这次回传对应哪次请求」，会写出文件名与实际内容不符的文件。</para>
        /// </summary>
        private sealed class ChartExportPending
        {
            public string Token = "";
            public PanelState? State;
            public string ChartKind = "";
            public int Index;
            public string Format = "png";
            public string SourceName = "";
        }

        private ChartExportPending? _chartExport;

        /// <summary>
        /// 请求把当前活动面板的第 index 个指定类型图表导出为 fmt（png/svg）。
        /// 保存对话框在**数据回传后**才弹 —— 因为 PNG 可能转换失败（SVG 引用外部资源导致
        /// canvas 污染），先弹框会让用户白选一次路径。
        /// </summary>
        private async Task RequestChartExportAsync(PanelState state, string chartKind, int index, string format)
        {
            try
            {
                if (state?.WebView?.CoreWebView2 == null)
                {
                    StatusText.Text = "无可导出的图表";
                    return;
                }
                var token = Guid.NewGuid().ToString("N");
                _chartExport = new ChartExportPending
                {
                    Token = token,
                    State = state,
                    ChartKind = chartKind,
                    Index = index,
                    Format = format,
                    SourceName = string.IsNullOrEmpty(state.CurrentFile)
                        ? "chart" : Path.GetFileNameWithoutExtension(state.CurrentFile)
                };
                // 页面侧按 (kind, index) 定位图表；kind 是固定枚举值，index 是数字，无注入面。
                // token 原样传给页面，页面回传后由 HandleChartExportAsync 校验，丢弃过期/伪造回传。
                var js = "if(window.__seemeExportChart)window.__seemeExportChart("
                         + System.Text.Json.JsonSerializer.Serialize(chartKind) + ","
                         + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ","
                         + System.Text.Json.JsonSerializer.Serialize(format) + ","
                         + System.Text.Json.JsonSerializer.Serialize(token) + ");";
                StatusText.Text = "正在导出图表…";
                await state.WebView.CoreWebView2.ExecuteScriptAsync(js);
            }
            catch (Exception ex)
            {
                _chartExport = null;
                LogErr("RequestChartExport: " + ex.Message);
                StatusText.Text = "导出图表失败: " + ex.Message;
            }
        }

        /// <summary>
        /// 处理页面回传的图表数据（由 MainWindow.Messages.cs 的 chart-export 分支调用）。
        /// data 是 data URL（PNG）或 SVG 文本；本方法解码后弹保存框并写盘。
        /// </summary>
        private async Task HandleChartExportAsync(System.Text.Json.JsonElement root)
        {
            var pending = _chartExport;
            _chartExport = null;
            try
            {
                if (pending == null) return;
                // 令牌校验：过期/伪造的回传一律丢弃（与 anydoc-result 同一口径）。
                // ⚠️ 必须要求令牌**存在且完全相等**，不能写成「空令牌放行」——
                // 页面侧 __seemeExportChart 从不回传 token 字段，用 `!string.IsNullOrEmpty(tok) && ...`
                // 会让校验恒等于没做：任何页面只要 postMessage 一个 {kind:'chart-export', data:...}
                // 就能顶替用户真正选中的那次导出。当前会话令牌是实现里的唯一来源，缺令牌即视为伪造。
                var tok = root.TryGetProperty("token", out var tokEl) ? tokEl.GetString() : null;
                if (tok != pending.Token) return;

                if (!root.TryGetProperty("data", out var dataEl)
                    || dataEl.ValueKind != System.Text.Json.JsonValueKind.String)
                {
                    StatusText.Text = "导出失败：页面未返回图表数据";
                    return;
                }
                var data = dataEl.GetString() ?? "";
                if (data.Length == 0)
                {
                    StatusText.Text = "导出失败：图表数据为空";
                    return;
                }
                // 体积上限：data URL 是 base64，膨胀约 1.33 倍；超限多半意味着页面出了问题
                if (data.Length > Limits.MaxChartExportChars)
                {
                    LogWarn("chart export too large: " + data.Length + " chars");
                    StatusText.Text = "导出失败：图表数据过大（可能渲染异常）";
                    return;
                }

                var fmt = root.TryGetProperty("format", out var fEl) ? fEl.GetString() ?? "png" : "png";
                var (bytes, ext, filter) = ChartExportCodec.Decode(data, fmt);
                if (bytes == null)
                {
                    StatusText.Text = "导出失败：图表数据格式无法识别";
                    return;
                }

                var label = pending.ChartKind switch
                {
                    "echarts" => "图表",
                    "markmap" => "思维导图",
                    _ => "流程图"
                };
                var baseName = $"{pending.SourceName}-{label}{pending.Index + 1}";

                string targetPath;
                var fixedDir = AppSettings.Get(AppSettings.ExportPathModeKey, "ask") == "fixed"
                    ? AppSettings.Get(AppSettings.ExportPathKey, "") : "";
                if (!string.IsNullOrEmpty(fixedDir) && Directory.Exists(fixedDir))
                {
                    targetPath = Path.Combine(fixedDir, baseName + ext);
                }
                else
                {
                    var dlg = new Microsoft.Win32.SaveFileDialog
                    {
                        FileName = baseName + ext,
                        Filter = filter,
                        Title = "导出" + label
                    };
                    if (dlg.ShowDialog(this) != true) { StatusText.Text = "已取消导出"; return; }
                    targetPath = dlg.FileName;
                }

                await File.WriteAllBytesAsync(targetPath, bytes);
                StatusText.Text = "已导出" + label + ": " + Path.GetFileName(targetPath);
                LogInfo("Chart exported: " + Path.GetFileName(targetPath) + " (" + bytes.Length + " bytes)");
            }
            catch (Exception ex)
            {
                LogErr("HandleChartExport: " + ex.Message);
                StatusText.Text = "导出图表失败: " + ex.Message;
            }
        }

        /// <summary>
        /// 图表选择对话框：列出文档内所有可导出图表，每项提供 PNG / SVG 两个按钮。
        /// 返回 (类型, 序号, 格式)；用户取消返回 null。
        ///
        /// <para><b>为什么让用户选而不是「导出全部」</b>：一张文档可能含多张图，
        /// 且 ECharts 只能出 PNG（canvas renderer）、SVG 图种只能出 PNG/SVG ——
        /// 统一「导出全部为某格式」会在部分图表上静默失败。逐个选择语义更明确。</para>
        /// </summary>
        private (string Kind, int Index, string Format)? ShowChartPicker(
            List<(string Kind, string Label, int Index)> choices)
        {
            (string, int, string)? picked = null;
            try
            {
                var w = new Window { Title = "导出图表", Width = 420, SizeToContent = SizeToContent.Height };
                var panel = new StackPanel { Margin = new Thickness(20, 18, 20, 16) };
                panel.Children.Add(new TextBlock
                {
                    Text = "选择要导出的图表",
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)TryFindResource("TextPrimaryBrush") ?? Brushes.Black,
                    Margin = new Thickness(0, 0, 0, 4)
                });
                panel.Children.Add(new TextBlock
                {
                    Text = "ECharts 图表仅支持 PNG（画布渲染）；思维导图 / Mermaid 可导出矢量 SVG。",
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Brush)TryFindResource("TextSecondaryBrush") ?? Brushes.Gray,
                    Margin = new Thickness(0, 0, 0, 10)
                });

                for (var i = 0; i < choices.Count; i++)
                {
                    var (kind, label, index) = choices[i];
                    var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var name = new TextBlock
                    {
                        Text = $"{label} {index + 1}",
                        VerticalAlignment = VerticalAlignment.Center,
                        FontSize = 12,
                        Foreground = (Brush)TryFindResource("TextPrimaryBrush") ?? Brushes.Black
                    };
                    Grid.SetColumn(name, 0);
                    row.Children.Add(name);

                    var canSvg = kind != "echarts";
                    row.Children.Add(MakeChartFmtButton("PNG", kind, index, "png", 1, row,
                        () => picked = (kind, index, "png")));
                    if (canSvg)
                        row.Children.Add(MakeChartFmtButton("SVG", kind, index, "svg", 2, row,
                            () => picked = (kind, index, "svg")));
                    panel.Children.Add(row);
                }

                var cancel = new Button
                {
                    Content = "取消",
                    Style = (Style)TryFindResource("StatusBtn"),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Padding = new Thickness(16, 6, 16, 6),
                    Margin = new Thickness(0, 12, 0, 0)
                };
                cancel.Click += (_, _) => w.Close();
                panel.Children.Add(cancel);

                ShowThemedDialog(w, x => x.Content = panel);
            }
            catch (Exception ex) { LogErr("ShowChartPicker: " + ex.Message); }
            return picked;
        }

        /// <summary>构造图表格式按钮（列位置由 column 决定）；点击即记录选择并关窗。</summary>
        private Button MakeChartFmtButton(string text, string kind, int index, string format, int column,
            Grid row, Action onPick)
        {
            var b = new Button
            {
                Content = text,
                Style = (Style)TryFindResource("StatusBtn"),
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(6, 0, 0, 0)
            };
            b.Click += (_, _) =>
            {
                onPick();
                // 关掉承载该按钮的窗口（向上找到 Window 而非依赖闭包捕获的局部变量）
                if (row.Parent is FrameworkElement fe && Window.GetWindow(fe) is { } win) win.Close();
            };
            Grid.SetColumn(b, column);
            return b;
        }

        /// <summary>
        /// 按当前面板是否含可导出图表，切换状态栏「导出图表」按钮的可见性。
        ///
        /// <para>在页面消息处理里探测 —— 页面 DOM 只有到渲染后才知道有多少张图。
        /// 不做这一步的话按钮会一直显示，点下去才发现「没有可导出的图表」。</para>
        /// </summary>
        private async Task UpdateChartExportAvailabilityAsync(PanelState state)
        {
            try
            {
                if (ChartExportBtn == null) return;
                if (state?.WebView?.CoreWebView2 == null)
                {
                    ChartExportBtn.Visibility = Visibility.Collapsed;
                    return;
                }
                var probe = await state.WebView.CoreWebView2.ExecuteScriptAsync(
                    "(function(){function n(s){return document.querySelectorAll(s).length;}"
                    + "return (n('.seeme-chart')+n('.seeme-markmap')+n('.mermaid'))>0;})()");
                var has = string.Equals(probe?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
                // 只对当前活动面板生效：非活动面板探测完不得覆盖活动面板的按钮状态
                if (!ReferenceEquals(state, ActiveOrLeft)) return;
                ChartExportBtn.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex) { LogInfo("Chart availability probe: " + ex.Message); }
        }
    }
}

