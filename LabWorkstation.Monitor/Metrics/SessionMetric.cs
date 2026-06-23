using System.Diagnostics;

namespace LabWorkstation.Monitor.Metrics;

/// <summary>
/// 活跃用户会话指标。对应原 PowerShell 脚本 Get-ActiveSessions。
/// 解析 `query user`（回退 quser）输出，提取用户名列表。
/// </summary>
public sealed class SessionMetric
{
    public int Count { get; init; }
    public List<string> Users { get; init; } = new();

    public static SessionMetric Collect()
    {
        try
        {
            var output = RunQueryCommand("query user");
            if (string.IsNullOrWhiteSpace(output))
            {
                output = RunQueryCommand("quser");
            }

            if (string.IsNullOrWhiteSpace(output))
            {
                return new SessionMetric();
            }

            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var users = new List<string>();

            // Skip header row, first column is username (may have leading '>' for current session)
            foreach (var line in lines.Skip(1))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                var username = line.Trim()
                    .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault()?
                    .TrimStart('>');

                if (!string.IsNullOrEmpty(username))
                {
                    users.Add(username);
                }
            }

            return new SessionMetric { Count = users.Count, Users = users };
        }
        catch
        {
            return new SessionMetric();
        }
    }

    private static string RunQueryCommand(string command)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {command} 2>nul",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return "";

            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();

            return proc.ExitCode == 0 ? output : "";
        }
        catch
        {
            return "";
        }
    }
}
