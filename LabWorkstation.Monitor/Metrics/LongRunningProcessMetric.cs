using System.Management;
using System.Text.RegularExpressions;
using LabWorkstation.Common.Logging;

namespace LabWorkstation.Monitor.Metrics;

/// <summary>
/// 长时间运行进程指标。对应原 PowerShell 脚本 Get-LongRunningProcesses。
/// 筛选运行超过阈值的用户进程，排除系统账户与 dwm-* 桌面管理进程。
/// </summary>
public sealed class LongRunningProcessMetric
{
    public string Name { get; init; } = "";
    public int PID { get; init; }
    public string User { get; init; } = "";
    public double RunningHours { get; init; }

    private static readonly string[] SystemAccounts =
    {
        "NT AUTHORITY\\SYSTEM",
        "NT AUTHORITY\\LOCAL SERVICE",
        "NT AUTHORITY\\NETWORK SERVICE",
        "SYSTEM",
        "LOCAL SERVICE",
        "NETWORK SERVICE"
    };

    private static readonly Regex DwmPattern = new(@"^dwm-\d+", RegexOptions.Compiled);

    public static List<LongRunningProcessMetric> Collect(MonitorLogger logger, int longRunningHours)
    {
        var results = new List<LongRunningProcessMetric>();

        try
        {
            var now = DateTime.Now;
            var threshold = now.AddHours(-longRunningHours);

            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, ProcessId, CreationDate FROM Win32_Process");

            foreach (ManagementObject proc in searcher.Get())
            {
                var creationDateRaw = proc["CreationDate"];
                if (creationDateRaw == null) continue;

                DateTime creationTime;
                try
                {
                    creationTime = ManagementDateTimeConverter.ToDateTime(creationDateRaw.ToString());
                }
                catch { continue; }

                if (creationTime > threshold) continue;

                // Resolve owner
                string? fullUser = null;
                try
                {
                    var owner = proc.InvokeMethod("GetOwner", new object[] { }) as ManagementBaseObject;
                    if (owner == null) continue;

                    var ret = owner["ReturnValue"];
                    if (ret == null || Convert.ToInt32(ret) != 0) continue;

                    var user = owner["User"]?.ToString();
                    if (string.IsNullOrEmpty(user)) continue;

                    var domain = owner["Domain"]?.ToString();
                    fullUser = string.IsNullOrEmpty(domain) ? user : $"{domain}\\{user}";
                }
                catch { continue; }

                if (fullUser == null) continue;

                // Skip system accounts
                bool isSystem = false;
                foreach (var sysAcct in SystemAccounts)
                {
                    if (fullUser.Contains(sysAcct, StringComparison.Ordinal))
                    {
                        isSystem = true;
                        break;
                    }
                }
                if (isSystem) continue;

                // Skip dwm-* desktop manager processes
                var name = proc["Name"]?.ToString() ?? "";
                if (DwmPattern.IsMatch(name)) continue;

                var runningHours = Math.Round((now - creationTime).TotalHours, 1);
                var pid = Convert.ToInt32(proc["ProcessId"]);

                results.Add(new LongRunningProcessMetric
                {
                    Name = name,
                    PID = pid,
                    User = fullUser,
                    RunningHours = runningHours
                });
            }
        }
        catch (Exception ex)
        {
            logger.LogWarn($"长时间运行进程检测失败: {ex.Message}");
        }

        return results;
    }
}
