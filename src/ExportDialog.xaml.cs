// Copyright (c) 2026 QIN-JIAPING
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace SeeMe
{
    /// <summary>导出对话框的返回值。</summary>
    public class ExportDialogResult
    {
        public string Format = "";
        public string OutputPath = "";
    }

    /// <summary>导出为... 对话框（独立窗口；打开前经 ThemeManager.ApplyToWindow 合入主题字典，颜色随主题）。</summary>
    public partial class ExportDialog : Window
    {
        private string _selectedFormat = "docx";
        private string _selectedExt = ".docx";
        private string _sourceName = "document";
        private string _sourceDir = "";
        private Button? _activeBtn;

        /// <summary>对话框结果；取消为 null。</summary>
        public ExportDialogResult? Result { get; private set; }

        public ExportDialog()
        {
            InitializeComponent();
        }

        /// <summary>设置源文件，初始化格式网格。</summary>
        /// <remarks>背景/卡片/文字/输入框/格式按钮全部走 XAML 主题资源（DynamicResource），
        /// 需在 ShowExportPicker 打开前调用 ThemeManager.ApplyToWindow 合入主题字典；
        /// 主题切换/accent 变化时 DynamicResource 自动跟随，无需 code-behind 手动取色。</remarks>
        public void SetSourceFile(string filePath)
        {
            _sourceName = Path.GetFileNameWithoutExtension(filePath);
            _sourceDir = Path.GetDirectoryName(filePath)
                ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            SourceInfo.Text = "源文件: " + Path.GetFileName(filePath);
            BuildFormatGrid();
            UpdatePath();
        }

        private void BuildFormatGrid()
        {
            FormatGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            FormatGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            FormatGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            for (int i = 0; i < SupportedExportFormats.DialogFormats.Count; i++)
            {
                var fmt = SupportedExportFormats.DialogFormats[i];
                var label = fmt.Label;
                var ext = fmt.Ext;
                var row = i / 2;
                var col = i % 2 * 2;

                if (FormatGrid.RowDefinitions.Count <= row)
                    FormatGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var btn = new Button
                {
                    Style = (Style)FindResource("FormatOptionBtn"),
                    Margin = new Thickness(0, 4, 0, 4),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Tag = (fmt.Format, ext),
                    Content = new StackPanel
                    {
                        Children =
                        {
                            new TextBlock { Text = label, FontWeight = FontWeights.SemiBold },
                            new TextBlock { Text = ext, FontSize = 11, Opacity = 0.65, Margin = new Thickness(0, 2, 0, 0) }
                        }
                    }
                };
                btn.Click += (_, _) =>
                {
                    var (f, e) = ((string, string))btn.Tag;
                    _selectedFormat = f;
                    _selectedExt = e;
                    UpdateActive(btn);
                    UpdatePath();
                };

                if (fmt.Format == _selectedFormat)
                    UpdateActive(btn);

                Grid.SetRow(btn, row);
                Grid.SetColumn(btn, col);
                FormatGrid.Children.Add(btn);
            }
        }

        /// <summary>切换选中格式按钮：选中项用主题 accent 底 + 白字，其余回到主题卡片态。</summary>
        private void UpdateActive(Button b)
        {
            var accent = (Brush)FindResource("AccentBrush");
            var bg = (Brush)FindResource("InputBgBrush");
            var border = (Brush)FindResource("LightGrayBrush");
            var text = (Brush)FindResource("TextBodyBrush");

            if (_activeBtn != null)
            {
                _activeBtn.Background = bg;
                _activeBtn.BorderBrush = border;
                _activeBtn.Foreground = text;
            }
            _activeBtn = b;
            _activeBtn.Background = accent;
            _activeBtn.BorderBrush = accent;
            _activeBtn.Foreground = Brushes.White;
        }

        private void UpdatePath()
        {
            var currentDir = Path.GetDirectoryName(PathBox.Text);
            PathBox.Text = Path.Combine(currentDir ?? _sourceDir, _sourceName + "_exported" + _selectedExt);
        }

        private void OnBrowse(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                FileName = Path.GetFileName(PathBox.Text),
                InitialDirectory = Path.GetDirectoryName(PathBox.Text) ?? _sourceDir,
                Filter = GetSaveFilter(_selectedFormat),
                Title = "选择导出位置"
            };
            if (dlg.ShowDialog() == true)
                PathBox.Text = dlg.FileName;
        }

        private static string GetSaveFilter(string format) =>
            SupportedExportFormats.FromFormat(format)?.Filter ?? "所有文件 (*.*)|*.*";

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            Result = null;
            Close();
        }

        private void OnExport(object sender, RoutedEventArgs e)
        {
            var output = PathBox.Text.Trim();
            if (string.IsNullOrEmpty(output))
            {
                MessageBox.Show("请选择保存位置", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            Result = new ExportDialogResult { Format = _selectedFormat, OutputPath = output };
            Close();
        }
    }
}
