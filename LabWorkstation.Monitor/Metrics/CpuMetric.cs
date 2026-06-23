using System.Diagnostics;
using System.Management;
using LabWorkstation.Common.Logging;

namespace LabWorkstation.Monitor.Metrics;

/// <summary>
/// CPU 使用率指标。对应原 PowerShell 脚本 Get-CpuUsage。
/// 系统整体：Win32_Processor.LoadPercentage（多 CPU 取平均）。
/// 高占用进程：采样 Process.TotalProcessorTime 差值，取 Top 3，解析 GetOwner。
/// </summary>
public sealed class CpuMetric
{
    public int Usage { get; init; }
    public List<TopProcess> TopProcesses { get; init; } = new();

    public sealed class TopProcess
    {
        public string Name { get; set; } = "";
        public int PID { get; set; }
        public string User { get; set; } = "N/A";
        public double CpuPct { get; set; }
    }

    public static CpuMetric Collect(MonitorLogger logger, int processSampleSeconds)
    {
        try
        {
            // ── Overall CPU (Win32_Processor.LoadPercentage) ─────────
            int avgLoad = 0;
            using (var procSearcher = new ManagementObjectSearcher("SELECT LoadPercentage FROM Win32_Processor"))
            {
                var loads = new List<int>();
                foreach (var obj in procSearcher.Get())
                {
                    var val = obj["LoadPercentage"];
                    if (val != null && int.TryParse(val.ToString(), out int load))
                        loads.Add(load);
                }
                if (loads.Count > 0)
                    avgLoad = (int)Math.Round(loads.Average());
            }

            // ── Per-process CPU (sampled delta) ──────────────────────
            var snap1 = new Dictionary<int, (string Name, double Cpu)>();
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id == 0) continue;
                    snap1[p.Id] = (p.ProcessName, p.TotalProcessorTime.TotalSeconds);
                }
                catch { }
            }

            Thread.Sleep(processSampleSeconds * 1000);

            int logicalCores = Environment.ProcessorCount;
            var processList = new List<TopProcess>();

            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (!snap1.TryGetValue(p.Id, out var s1)) continue;
                    double cpu2 = p.TotalProcessorTime.TotalSeconds;
                    double delta = cpu2 - s1.Cpu;
                    if (delta <= 0) continue;

                    double cpuPct = Math.Round(delta / (processSampleSeconds * logicalCores) * 100, 1);
                    processList.Add(new TopProcess
                    {
                        Name = p.ProcessName,
                        PID = p.Id,
                        CpuPct = cpuPct
                    });
                }
                catch { }
            }

            var top = processList.OrderByDescending(p => p.CpuPct).Take(3).ToList();

            // Resolve owning user for each top process
            foreach (var tp in top)
            {
                tp.User = ResolveProcessOwner(tp.PID);
            }

            return new CpuMetric { Usage = avgLoad, TopProcesses = top };
        }
        catch (Exception ex)
        {
            logger.LogWarn($"CPU 指标采集失败: {ex.Message}");
            return new CpuMetric { Usage = 0, TopProcesses = new() };
        }
    }

    private static string ResolveProcessOwner(int pid)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT * FROM Win32_Process WHERE ProcessId = {pid}");

            foreach (ManagementObject obj in searcher.Get())
            {
                var owner = obj.InvokeMethod("GetOwner", new object[] { }) as ManagementBaseObject;
                if (owner != null)
                {
                    var ret = owner["ReturnValue"];
                    if (ret != null && Convert.ToInt32(ret) == 0)
                    {
                        var user = owner["User"]?.ToString();
                        if (!string.IsNullOrEmpty(user))
                            return user;
                    }
                }
            }
        }
        catch { }
        return "N/A";
    }
}
