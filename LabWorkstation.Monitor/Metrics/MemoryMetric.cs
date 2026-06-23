using System.Management;
using LabWorkstation.Common.Logging;

namespace LabWorkstation.Monitor.Metrics;

/// <summary>
/// 内存使用率指标。对应原 PowerShell 脚本 Get-MemoryUsage。
/// 数据源：Win32_OperatingSystem (TotalVisibleMemorySize / FreePhysicalMemory)。
/// </summary>
public sealed class MemoryMetric
{
    public double Usage { get; init; }
    public double UsedGB { get; init; }
    public double TotalGB { get; init; }

    public static MemoryMetric Collect(MonitorLogger logger)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem");

            foreach (var os in searcher.Get())
            {
                double totalKB = Convert.ToDouble(os["TotalVisibleMemorySize"]);
                double freeKB = Convert.ToDouble(os["FreePhysicalMemory"]);
                double usedKB = totalKB - freeKB;

                return new MemoryMetric
                {
                    Usage = Math.Round(usedKB / totalKB * 100, 1),
                    UsedGB = Math.Round(usedKB / (1024.0 * 1024), 1),
                    TotalGB = Math.Round(totalKB / (1024.0 * 1024), 1)
                };
            }
        }
        catch (Exception ex)
        {
            logger.LogWarn($"内存指标采集失败: {ex.Message}");
        }

        return new MemoryMetric();
    }
}
