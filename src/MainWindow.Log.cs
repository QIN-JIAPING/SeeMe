using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace SeeMe
{
    public partial class MainWindow
    {
        private static void LogErr(string msg) => WriteLog("ERROR", msg);

        private static void LogWarn(string msg) => WriteLog("WARN", msg);

        private static void LogInfo(string msg) => WriteLog("INFO", msg);


        private static void WriteLog(string level, string msg)
        {
            try
            {
                var logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SeeMe");
                if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);
                var logPath = Path.Combine(logDir, "error.log");
                // 日志滚动：超过 1MB 重命名为 error.old.log
                var fi = new FileInfo(logPath);
                if (fi.Exists && fi.Length > 1024 * 1024)
                {
                    var oldPath = Path.Combine(logDir, "error.old.log");
                    if (File.Exists(oldPath)) File.Delete(oldPath);
                    File.Move(logPath, oldPath);
                }
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {msg}{Environment.NewLine}";
                File.AppendAllText(logPath, line);
            }
            catch { }
        }
    }
}
