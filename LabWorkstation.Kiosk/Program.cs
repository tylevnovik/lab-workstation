using System.IO;
using LabWorkstation.Common.Configuration;
using LabWorkstation.Kiosk;

namespace LabWorkstation.Kiosk;

static class Program
{
    /// <summary>Kiosk 启动调试日志路径：%LocalAppData%\LabWorkstation\kiosk_debug.log</summary>
    private static readonly string DebugLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LabWorkstation", "kiosk_debug.log");

    [STAThread]
    static void Main()
    {
        // 全局异常捕获：任何未处理异常都写日志，避免黑屏无诊断信息
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteDebugLog($"[FATAL] AppDomain 未处理异常: {e.ExceptionObject}");

        Application.ThreadException += (_, e) =>
            WriteDebugLog($"[FATAL] UI 线程未处理异常: {e.Exception}");

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteDebugLog($"[FATAL] 未观察 Task 异常: {e.Exception}");
            e.SetObserved();
        };

        try
        {
            WriteDebugLog("========================================");
            WriteDebugLog($"[START] Kiosk 应用启动，调试模式={LabConfig.KioskDebugMode}");
            WriteDebugLog($"[START] 当前用户: {Environment.UserName}");
            WriteDebugLog($"[START] 工作目录: {Environment.CurrentDirectory}");
            WriteDebugLog($"[START] exe 路径: {Environment.ProcessPath}");
            WriteDebugLog($"[START] OS: {Environment.OSVersion}, .NET: {Environment.Version}");

            ApplicationConfiguration.Initialize();

            WriteDebugLog("[START] WinForms 初始化完成，准备创建 KioskForm");

            using var form = new KioskForm();

            WriteDebugLog("[START] KioskForm 构造完成，进入消息循环");

            Application.Run(form);

            WriteDebugLog("[END] Kiosk 应用正常退出");
        }
        catch (Exception ex)
        {
            WriteDebugLog($"[FATAL] Main 异常: {ex}");
            throw;
        }
    }

    /// <summary>写调试日志（追加模式）。即使调试模式关闭也写，便于排查黑屏。</summary>
    internal static void WriteDebugLog(string message)
    {
        try
        {
            var dir = Path.GetDirectoryName(DebugLogPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(DebugLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch { /* 日志失败不应阻断主流程 */ }
    }
}
