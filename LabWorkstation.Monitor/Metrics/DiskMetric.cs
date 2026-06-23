using System.Management;
using LabWorkstation.Common.Logging;

namespace LabWorkstation.Monitor.Metrics;

/// <summary>
/// 磁盘使用率指标。对应原 PowerShell 脚本 Get-DiskUsage。
/// 数据源：Win32_LogicalDisk WHERE DriveType=3（固定本地盘）。
/// </summary>
public sealed class DiskMetric
{
    public string Drive { get; init; } = "";
    public double Usage { get; init; }
    public double FreeGB { get; init; }
    public double TotalGB { get; init; }

    public static List<DiskMetric> Collect(MonitorLogger logger)
    {
        var results = new List<DiskMetric>();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, Size, FreeSpace FROM Win32_LogicalDisk WHERE DriveType = 3");

            foreach (var disk in searcher.Get())
            {
                double size = Convert.ToDouble(disk["Size"]);
                if (size <= 0) continue;

                double free = Convert.ToDouble(disk["FreeSpace"]);

                results.Add(new DiskMetric
                {
                    Drive = disk["DeviceID"]?.ToString() ?? "",
                    Usage = Math.Round((size - free) / size * 100, 1),
                    FreeGB = Math.Round(free / (1024.0 * 1024 * 1024), 1),
                    TotalGB = Math.Round(size / (1024.0 * 1024 * 1024), 1)
                });
            }
        }
        catch (Exception ex)
        {
            logger.LogWarn($"磁盘指标采集失败: {ex.Message}");
        }

        return results;
    }
}
