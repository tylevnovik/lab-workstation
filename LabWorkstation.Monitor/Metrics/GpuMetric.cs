using System.Diagnostics;
using LabWorkstation.Common.Logging;

namespace LabWorkstation.Monitor.Metrics;

/// <summary>
/// GPU 使用率指标。对应原 PowerShell 脚本 Get-GpuUsage。
/// 通过 nvidia-smi 查询 utilization.gpu / memory.used / memory.total。
/// 首次调用时探测 nvidia-smi 路径并缓存；不存在则返回 null（跳过 GPU 监控）。
/// </summary>
public sealed class GpuMetric
{
    public int Usage { get; init; }
    public int MemoryUsedMB { get; init; }
    public int MemoryTotalMB { get; init; }
}

public sealed class GpuMetricCollector
{
    private string? _nvidiaSmiPath;
    private bool _discovered;
    private readonly object _gate = new();

    /// <summary>采集 GPU 指标。无 NVIDIA GPU 时返回 null。</summary>
    public GpuMetric? Collect(MonitorLogger logger)
    {
        lock (_gate)
        {
            if (!_discovered)
            {
                DiscoverNvidiaSmi(logger);
                _discovered = true;
            }
        }

        if (string.IsNullOrEmpty(_nvidiaSmiPath)) return null;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _nvidiaSmiPath!,
                Arguments = "--query-gpu=utilization.gpu,memory.used,memory.total --format=csv,noheader,nounits",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return null;

            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();

            if (proc.ExitCode != 0)
            {
                logger.LogWarn($"nvidia-smi 返回非零退出码 ({proc.ExitCode})");
                return null;
            }

            // Take the first GPU (index 0) in multi-GPU systems
            var firstLine = output
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()?.Trim();

            if (string.IsNullOrEmpty(firstLine))
            {
                logger.LogWarn($"nvidia-smi 输出格式异常: {firstLine}");
                return null;
            }

            var parts = firstLine.Split(',').Select(p => p.Trim()).ToArray();
            if (parts.Length < 3)
            {
                logger.LogWarn($"nvidia-smi 输出格式异常: {firstLine}");
                return null;
            }

            int gpuUtil = ExtractInt(parts[0]);
            int memUsed = ExtractInt(parts[1]);
            int memTotal = ExtractInt(parts[2]);

            return new GpuMetric
            {
                Usage = gpuUtil,
                MemoryUsedMB = memUsed,
                MemoryTotalMB = memTotal
            };
        }
        catch (Exception ex)
        {
            logger.LogWarn($"GPU 指标采集失败: {ex.Message}");
            return null;
        }
    }

    private static int ExtractInt(string s)
    {
        var digits = new string(s.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out int v) ? v : 0;
    }

    private void DiscoverNvidiaSmi(MonitorLogger logger)
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"),
            Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe")
        };

        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate))
            {
                _nvidiaSmiPath = candidate;
                logger.LogInfo($"已检测到 nvidia-smi: {candidate}");
                return;
            }
        }

        // PATH fallback
        var pathHit = SearchPathFor("nvidia-smi.exe");
        if (pathHit != null)
        {
            _nvidiaSmiPath = pathHit;
            logger.LogInfo($"已检测到 nvidia-smi (via PATH): {pathHit}");
            return;
        }

        _nvidiaSmiPath = "";
        logger.LogInfo("未检测到 NVIDIA GPU (nvidia-smi)，跳过 GPU 监控");
    }

    private static string? SearchPathFor(string fileName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return null;

        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                var full = Path.Combine(dir.Trim('"'), fileName);
                if (File.Exists(full)) return full;
            }
            catch { }
        }
        return null;
    }
}
