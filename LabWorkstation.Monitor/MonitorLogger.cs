using LabWorkstation.Common.Configuration;
using LabWorkstation.Common.Logging;

namespace LabWorkstation.Monitor;

/// <summary>
/// 监控日志封装。格式与原 PowerShell 脚本一致：
/// [yyyy-MM-dd HH:mm:ss] [LEVEL] message
/// 底层轮转由 RotatingFileWriter 处理（按大小切分，保留月度归档）。
/// </summary>
public sealed class MonitorLogger
{
    private const int MaxArchives = 12;
    private readonly RotatingFileWriter _writer;

    public MonitorLogger()
    {
        _writer = new RotatingFileWriter(
            LabConfig.MonitorLogPath,
            LabConfig.MonitorLogMaxSizeBytes,
            MaxArchives);
    }

    public void Log(string level, string message)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        _writer.AppendLine($"[{timestamp}] [{level}] {message}");
    }

    public void LogInfo(string message) => Log("INFO", message);
    public void LogWarn(string message) => Log("WARN", message);
    public void LogAlert(string message) => Log("ALERT", message);
    public void LogError(string message) => Log("ERROR", message);
}
