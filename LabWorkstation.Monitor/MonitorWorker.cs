using LabWorkstation.Common.Configuration;
using LabWorkstation.Monitor.Metrics;

namespace LabWorkstation.Monitor;

/// <summary>
/// 资源监控后台服务。对应原 PowerShell 脚本 Lab-Monitor.ps1 的主循环。
/// 每 60 秒采集 CPU / 内存 / 磁盘 / GPU / 长时进程指标，超阈值时记录告警，
/// 并定期输出状态摘要。主循环异常不中断，写 ERROR 日志后继续。
/// </summary>
public sealed class MonitorWorker : BackgroundService
{
    // ── 配置常量（与原 PS 脚本 1:1 对齐）──────────────────────
    private const int CheckIntervalSeconds = 60;
    private const int AlertCooldownMinutes = 10;
    private const int StatusInterval = 10;          // 每 N 轮写一次状态摘要
    private const int ProcessSampleSeconds = 2;     // 进程 CPU 采样窗口

    // ── 阈值 ──────────────────────────────────────────────────
    private const int CpuThreshold = 90;            // CPU 使用率 %
    private const int MemoryThreshold = 90;         // 内存使用率 %
    private const int DiskThreshold = 90;           // 磁盘使用率 %
    private const int GpuThreshold = 95;            // GPU 使用率 %
    private const int LongRunningHours = 48;        // 长时进程阈值（小时）

    private readonly MonitorLogger _logger;
    private readonly AlertCooldown _cooldown;
    private readonly GpuMetricCollector _gpuCollector;
    private int _cycleCount;

    public MonitorWorker(MonitorLogger logger, AlertCooldown cooldown, GpuMetricCollector gpuCollector)
    {
        _logger = logger;
        _cooldown = cooldown;
        _gpuCollector = gpuCollector;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // ── 启动横幅 ──────────────────────────────────────────
        _logger.LogInfo("════════════════════════════════════════════");
        _logger.LogInfo("资源监控服务已启动");
        _logger.LogInfo($"  检查间隔   : {CheckIntervalSeconds}s (进程采样: {ProcessSampleSeconds}s)");
        _logger.LogInfo($"  告警冷却   : {AlertCooldownMinutes} min");
        _logger.LogInfo($"  日志上限   : {LabConfig.MonitorLogMaxSizeBytes / (1024 * 1024)} MB → 自动轮转");
        _logger.LogInfo($"  阈值 → CPU: {CpuThreshold}% | 内存: {MemoryThreshold}% | 磁盘: {DiskThreshold}% | GPU: {GpuThreshold}%");
        _logger.LogInfo($"  长时进程   : > {LongRunningHours}h");
        _logger.LogInfo("════════════════════════════════════════════");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _cycleCount++;

                // ── 1. 采集所有指标 ──────────────────────────────
                var cpu = CpuMetric.Collect(_logger, ProcessSampleSeconds);
                var mem = MemoryMetric.Collect(_logger);
                var disks = DiskMetric.Collect(_logger);
                var gpu = _gpuCollector.Collect(_logger);

                // ── 2. CPU 告警 ──────────────────────────────────
                if (cpu.Usage >= CpuThreshold && !_cooldown.IsOnCooldown("cpu_high"))
                {
                    var topInfo = cpu.TopProcesses.Count > 0
                        ? string.Join("; ", cpu.TopProcesses.Select(p =>
                            $"{p.Name}(PID {p.PID}, 用户 {p.User}, {p.CpuPct}%)"))
                        : "(无法获取进程详情)";

                    _logger.LogAlert($"CPU 使用率 {cpu.Usage}% 超过阈值 {CpuThreshold}% | 高占用进程: {topInfo}");
                    _cooldown.RecordAlert("cpu_high");
                }

                // ── 3. 内存告警 ──────────────────────────────────
                if (mem.Usage >= MemoryThreshold && !_cooldown.IsOnCooldown("memory_high"))
                {
                    _logger.LogAlert($"内存使用率 {mem.Usage}% ({mem.UsedGB}/{mem.TotalGB} GB) 超过阈值 {MemoryThreshold}%");
                    _cooldown.RecordAlert("memory_high");
                }

                // ── 4. 磁盘告警（逐盘）──────────────────────────
                foreach (var disk in disks)
                {
                    var diskKey = "disk_" + new string(disk.Drive.Where(char.IsLetter).ToArray());

                    if (disk.Usage >= DiskThreshold && !_cooldown.IsOnCooldown(diskKey))
                    {
                        _logger.LogAlert($"磁盘 {disk.Drive} 使用率 {disk.Usage}% (剩余 {disk.FreeGB}/{disk.TotalGB} GB) 超过阈值 {DiskThreshold}%");
                        _cooldown.RecordAlert(diskKey);
                    }
                }

                // ── 5. GPU 告警（无 GPU 时跳过）──────────────────
                if (gpu != null)
                {
                    if (gpu.Usage >= GpuThreshold && !_cooldown.IsOnCooldown("gpu_high"))
                    {
                        _logger.LogAlert($"GPU 使用率 {gpu.Usage}% (显存 {gpu.MemoryUsedMB}/{gpu.MemoryTotalMB} MB) 超过阈值 {GpuThreshold}%");
                        _cooldown.RecordAlert("gpu_high");
                    }
                }

                // ── 6. 长时间运行进程 ────────────────────────────
                var longRunning = LongRunningProcessMetric.Collect(_logger, LongRunningHours);
                if (longRunning.Count > 0 && !_cooldown.IsOnCooldown("long_running_procs"))
                {
                    var procInfo = string.Join("; ", longRunning.Select(p =>
                        $"{p.Name}(PID {p.PID}, 用户 {p.User}, 已运行 {p.RunningHours}h)"));

                    _logger.LogWarn($"检测到 {longRunning.Count} 个长时间运行进程 (>{LongRunningHours}h): {procInfo}");
                    _cooldown.RecordAlert("long_running_procs");
                }

                // ── 7. 定期状态摘要 ──────────────────────────────
                if (_cycleCount % StatusInterval == 0)
                {
                    var sessions = SessionMetric.Collect();

                    var gpuFrag = gpu != null
                        ? $"GPU: {gpu.Usage}% (显存 {gpu.MemoryUsedMB}/{gpu.MemoryTotalMB}MB)"
                        : "GPU: N/A";

                    var diskFrag = disks.Count > 0
                        ? string.Join(", ", disks.Select(d => $"{d.Drive} {d.Usage}%"))
                        : "N/A";

                    var sessFrag = sessions.Count > 0
                        ? $"{sessions.Count} 人 ({string.Join(", ", sessions.Users)})"
                        : "无活跃会话";

                    _logger.LogInfo($"状态摘要 | CPU: {cpu.Usage}% | 内存: {mem.Usage}% ({mem.UsedGB}/{mem.TotalGB}GB) | 磁盘: {diskFrag} | {gpuFrag} | 会话: {sessFrag} | 长时进程: {longRunning.Count}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"检查循环异常: {ex.Message}");
            }

            try
            {
                await Task.Delay(CheckIntervalSeconds * 1000, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                // 服务正在停止，正常退出
            }
        }
    }
}
