using System.Diagnostics;
using System.Runtime.Versioning;
using LabWorkstation.Common.Configuration;
using LabWorkstation.Common.Desktop;

namespace LabWorkstation.Common.System;

/// <summary>
/// 部署 LabWorkstation.Monitor 到 C:\Scripts 并注册开机自启计划任务。
/// 对应原 PS Setup-Maintenance.ps1 第 6 段（资源监控守护程序部署）。
/// 用 dotnet.exe 跑 dll，避免 apphost 在 SYSTEM 非交互会话下探测 runtime 失败。
/// 测试模式（LabConfig.TestMode）下跳过。
/// </summary>
[SupportedOSPlatform("windows")]
public static class MonitorDeployer
{
    /// <summary>计划任务名（与原 PS 一致）。</summary>
    public const string TaskName = "LabResourceMonitor";

    /// <summary>dotnet 主机路径（与原 PS 一致）。</summary>
    private const string DotnetExe = @"C:\Program Files\dotnet\dotnet.exe";

    /// <summary>
    /// 部署 Monitor：复制构建产物到 C:\Scripts\LabWorkstation.Monitor，
    /// 注册开机自启计划任务（SYSTEM 身份、最高权限），并立即启动一次。
    /// 部署前强制停止正在运行的任务实例并杀掉占用 dll 的 dotnet 进程，
    /// 确保旧目录可被删除、新文件可覆盖。
    /// </summary>
    public static void Deploy()
    {
        if (LabConfig.TestMode)
        {
            Console.WriteLine("[测试模式] 跳过部署 Monitor");
            return;
        }

        // 强制停止正在运行的 Monitor：停止计划任务 + 杀掉运行 Monitor.dll 的 dotnet 进程
        StopRunningMonitor();

        var sourceDir = BuildArtifactLocator.ResolveProjectBinDir("LabWorkstation.Monitor");
        var targetDir = Path.Combine(LabConfig.ScriptsDir, "LabWorkstation.Monitor");

        // 清理旧部署目录（进程已停止，可安全删除）
        if (Directory.Exists(targetDir))
        {
            try { Directory.Delete(targetDir, recursive: true); }
            catch { /* 删除失败不阻断，后续 CopyDirectory 会覆盖 */ }
        }
        CopyDirectory(sourceDir, targetDir);

        var monitorDll = Path.Combine(targetDir, "LabWorkstation.Monitor.dll");
        if (!File.Exists(monitorDll))
            throw new LabOperationException("DEPLOY_MONITOR", targetDir,
                detail: $"缺少 {monitorDll}",
                message: "部署目录中未找到 LabWorkstation.Monitor.dll");

        // 尝试注册开机自启计划任务（失败不阻断，改为直接启动）
        Exception? startException = null;
        try
        {
            var exePath = $"\"{DotnetExe}\" \"{monitorDll}\"";
            TaskSchedulerHelper.CreateStartupTask(
                TaskName,
                exePath: exePath,
                workingDir: targetDir,
                description: "课题组工作站 · 资源监控守护程序（CPU/内存/GPU/磁盘）",
                runAsSystem: true);

            // 补设进程崩溃自动重启策略：schtasks /create 不支持 RestartInterval，
            // 必须用 Task Scheduler COM API 设置。
            // 设置间隔 1 分钟、上限 999 次，保证 Monitor 进程崩溃后能自动恢复。
            // CreateStartupTask 已重新启用任务（先 delete 再 create），此处仅需补设策略。
            try
            {
                TaskSchedulerHelper.SetRestartPolicy(TaskName, intervalMin: 1, count: 999);
                Console.WriteLine("[MonitorDeployer] 已设置进程崩溃自动重启策略（1分钟/999次）");
            }
            catch (Exception exPolicy)
            {
                // 设置重启策略失败不阻断部署：任务仍可正常启动，只是崩溃后不会自动恢复
                Console.WriteLine($"[MonitorDeployer] 设置重启策略失败（不阻断，仅影响崩溃恢复）: {exPolicy.Message}");
            }

            TaskSchedulerHelper.StartTask(TaskName);
            Console.WriteLine("[MonitorDeployer] 计划任务已注册并启动");
        }
        catch (Exception ex)
        {
            startException = ex;
            Console.WriteLine($"[MonitorDeployer] 计划任务注册失败（不影响部署）: {ex.Message}");
            // 直接启动 Monitor 进程作为 fallback
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = DotnetExe,
                    Arguments = $"\"{monitorDll}\"",
                    WorkingDirectory = targetDir,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                Process.Start(psi);
                Console.WriteLine("[MonitorDeployer] Monitor 进程已直接启动（fallback）");
            }
            catch (Exception ex2)
            {
                Console.WriteLine($"[MonitorDeployer] 直接启动也失败: {ex2.Message}");
            }
        }

        // 部署后验证：等待 Monitor 启动并确认心跳文件已更新
        VerifyMonitorRunning(startException);
    }

    /// <summary>
    /// 部署后验证：等待最多 30 秒，确认 Monitor 进程在运行且心跳文件已更新。
    /// 验证失败抛 LabOperationException，提示用户检查事件日志或手动启动。
    /// </summary>
    /// <param name="startException">启动阶段已捕获的异常（用于错误信息合并）。</param>
    private static void VerifyMonitorRunning(Exception? startException)
    {
        // Monitor 每 5 秒轮询一次并写心跳，等待最多 30 秒（6 次检查）
        const int verifyTimeoutSeconds = 30;
        const int checkIntervalMs = 3000;
        var deadline = DateTime.Now.AddSeconds(verifyTimeoutSeconds);

        while (DateTime.Now < deadline)
        {
            if (IsMonitorProcessRunning() && IsHeartbeatFresh())
            {
                Console.WriteLine("[MonitorDeployer] 验证通过：Monitor 进程运行中且心跳已更新");
                return;
            }
            global::System.Threading.Thread.Sleep(checkIntervalMs);
        }

        // 验证失败：收集诊断信息并抛异常
        var processRunning = IsMonitorProcessRunning();
        var heartbeatPath = Path.Combine(LabConfig.KioskResponsesPath, "monitor_heartbeat.json");
        var heartbeatExists = File.Exists(heartbeatPath);
        var heartbeatAge = heartbeatExists
            ? (DateTime.Now - new FileInfo(heartbeatPath).LastWriteTime).TotalSeconds
            : -1;

        var detail = $"进程运行={processRunning}, 心跳文件存在={heartbeatExists}, 心跳年龄={heartbeatAge:F0}s";
        var message = "Monitor 部署后验证失败：进程未运行或心跳未更新";
        if (startException != null)
            message += $"；启动阶段异常: {startException.Message}";
        message += "。请检查系统事件日志或手动运行 dotnet LabWorkstation.Monitor.dll 查看错误";

        throw new LabOperationException("DEPLOY_MONITOR_VERIFY", TaskName,
            detail: detail, message: message);
    }

    /// <summary>
    /// 检查是否有 dotnet 进程加载了 LabWorkstation.Monitor.dll。
    /// 通过 tasklist /FI 查询，比 PerformanceCounter 更可靠且无需额外权限。
    /// </summary>
    private static bool IsMonitorProcessRunning()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "tasklist.exe",
                Arguments = "/FI \"MODULES eq LabWorkstation.Monitor.dll\" /NH /FO CSV",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(5000);
            var output = p?.StandardOutput.ReadToEnd() ?? "";
            // tasklist 无匹配时输出 "信息: 没有运行的任务匹配指定标准。" 或英文类似信息
            // 有匹配时输出进程行（CSV 格式，以 " 开头）
            return output.Contains("dotnet", StringComparison.OrdinalIgnoreCase)
                || output.Contains("LabWorkstation.Monitor", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 检查心跳文件是否已更新（最近 60 秒内写入）。
    /// Monitor 每 5 秒写一次心跳，60 秒阈值容忍启动延迟。
    /// </summary>
    private static bool IsHeartbeatFresh()
    {
        try
        {
            var path = Path.Combine(LabConfig.KioskResponsesPath, "monitor_heartbeat.json");
            if (!File.Exists(path)) return false;
            var age = (DateTime.Now - new FileInfo(path).LastWriteTime).TotalSeconds;
            return age <= 60;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>递归复制目录（覆盖已存在文件）。</summary>
    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }

    /// <summary>
    /// 强制停止正在运行的 Monitor，确保后续文件替换不被占用、也不被任务自动重启打断。
    /// 顺序：① 禁用计划任务（防止杀进程后 1 分钟内自动重启抢占 dll 句柄）
    ///      ② 停止计划任务运行实例 ③ 杀掉加载 Monitor.dll 的 dotnet 进程。
    /// 三步均失败不抛异常（任务可能尚未注册/进程可能未运行）。
    /// 注意：禁用后必须由调用方在部署完成后重新注册任务（CreateStartupTask 会自动重新启用）。
    /// </summary>
    private static void StopRunningMonitor()
    {
        // 1. 先禁用任务触发器：防止杀进程后重启策略在文件替换中途自动拉起任务
        try
        {
            TaskSchedulerHelper.DisableTask(TaskName);
            Console.WriteLine("[MonitorDeployer] 已禁用计划任务触发器（防止杀进程后自动重启）");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MonitorDeployer] 禁用计划任务失败（可忽略，可能未注册）: {ex.Message}");
        }

        // 2. 停止计划任务运行实例
        try
        {
            TaskSchedulerHelper.StopTask(TaskName);
            Console.WriteLine("[MonitorDeployer] 已停止计划任务运行实例");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MonitorDeployer] 停止计划任务失败（可忽略）: {ex.Message}");
        }

        // 3. 杀掉运行 Monitor.dll 的 dotnet 进程
        //    用 taskkill /T /F 配合模块过滤器，杀掉加载了 LabWorkstation.Monitor.dll 的进程及其子进程
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "taskkill.exe",
                // /FI "MODULES eq xxx.dll" 过滤加载了指定模块的进程；/F 强制；/T 连同子进程
                Arguments = "/F /T /FI \"MODULES eq LabWorkstation.Monitor.dll\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(5000);
            Console.WriteLine("[MonitorDeployer] 已尝试杀掉运行 Monitor 的 dotnet 进程");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MonitorDeployer] 杀进程失败（可忽略）: {ex.Message}");
        }

        // 给文件句柄释放留一点时间
        global::System.Threading.Thread.Sleep(500);
    }
}
