using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace SeeMe
{
    public partial class MainWindow
    {
        private void SetupWatcher(PanelState state)
        {
            state.Watcher?.Dispose();
            state.Watcher = null;
            state.Debounce?.Stop();
            state.Debounce = null;

            if (string.IsNullOrEmpty(state.CurrentFile))
                return;

            try
            {
                var dir = Path.GetDirectoryName(state.CurrentFile);
                var name = Path.GetFileName(state.CurrentFile);
                if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name)) return;

                state.Watcher = new FileSystemWatcher(dir, name)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    EnableRaisingEvents = false
                };
                state.Debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(state.DebounceMs) };

                state.Debounce.Tick += (_, _) =>
                {
                    state.Debounce!.Stop();
                    state.DebounceMs = DebounceMinMs; // 刷新成功后回落到最小间隔
                    _ = ReloadFileAsync(state, false, state.ResetCts());
                };

                state.Watcher.Changed += (_, _) =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (state.Debounce!.IsEnabled)
                        {
                            // 连发：逐步拉长间隔（100→800ms），给高频保存留出缓冲
                            state.Debounce.Stop();
                            state.DebounceMs = Math.Min(state.DebounceMs + DebounceStepMs, DebounceMaxMs);
                        }
                        else
                        {
                            state.DebounceMs = DebounceMinMs;
                        }
                        state.Debounce.Interval = TimeSpan.FromMilliseconds(state.DebounceMs);
                        state.Debounce.Start();
                    }));
                };

                state.Watcher.Renamed += (_, e) =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            state.CurrentFile = Path.GetFullPath(e.FullPath);
                            state.FileDir = Path.GetDirectoryName(state.CurrentFile);
                            state.TitleText.Text = Path.GetFileName(state.CurrentFile);
                            state.TitleText.ToolTip = state.FileDir ?? "";
                            UpdateWindowTitle();
                            _ = ReloadFileAsync(state, true, state.ResetCts());
                            // 重命名后原 watcher 仍监听旧文件名，后续实时刷新会静默失效；
                            // 重新建立 watcher 以监听新文件名（SetupWatcher 会先释放旧 watcher）。
                            SetupWatcher(state);
                        }
                        catch (Exception ex) { LogErr("Watcher renamed: " + ex.Message); }
                    }));
                };

                state.Watcher.Deleted += (_, e) =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        // 延迟 300ms 确认，避开"先删后建"型保存（vim 等编辑器）
                        System.Threading.Tasks.Task.Delay(300).ContinueWith(_ => Dispatcher.BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                if (state.CurrentFile != null && File.Exists(state.CurrentFile)) return; // 文件又出现了，忽略
                                if (state.Watcher != null) state.Watcher.EnableRaisingEvents = false;
                                RenderFileGonePage(state);
                                StatusText.Text = "文件已被删除或移动: " + Path.GetFileName(state.CurrentFile ?? "");
                                LogInfo("File gone: " + Path.GetFileName(state.CurrentFile ?? ""));
                            }
                            catch (Exception ex) { LogErr("Watcher deleted: " + ex.Message); }
                        })));
                    }));
                };

                state.Watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) { LogErr("Watcher setup: " + ex.Message); }
        }


    }
}
