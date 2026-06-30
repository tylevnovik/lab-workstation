using System.Diagnostics;
using System.Runtime.Versioning;
using LabWorkstation.Common.Configuration;

namespace LabWorkstation.Common.System;

/// <summary>
/// 计划任务封装。对应原 PS 的 Register-ScheduledTask / Unregister-ScheduledTask。
/// 通过 Process.Start 调用 schtasks.exe 实现，避免引入 COM 互操作 Task Scheduler 类型库。
/// 测试模式（LabConfig.TestMode）下跳过，仅记录。
/// </summary>
[SupportedOSPlatform("windows")]
public static class TaskSchedulerHelper
{
    /// <summary>
    /// 创建开机自启计划任务。
    /// 先查询若已存在则删除，再 /create /sc onstart 注册。
    /// </summary>
    /// <param name="taskName">任务名。</param>
    /// <param name="exePath">/tr 内容（目标命令；可含参数，调用方自行加引号）。</param>
    /// <param name="workingDir">工作目录（schtasks.exe /create 不支持单独设置工作目录，调用方应在 exePath 中使用绝对路径）。</param>
    /// <param name="description">任务描述。</param>
    /// <param name="runAsSystem">true 以 SYSTEM 身份运行并提权；false 以当前用户运行。</param>
    public static void CreateStartupTask(string taskName, string exePath, string workingDir,
        string description, bool runAsSystem)
    {
        if (LabConfig.TestMode)
        {
            Console.WriteLine($"[测试模式] 跳过创建计划任务: {taskName} -> {exePath}");
            return;
        }

        // 已存在则先删除
        if (TaskExists(taskName))
            RunSchtasks($"/delete /tn \"{QuoteName(taskName)}\" /f");

        var tr = QuoteTr(exePath);
        var ru = runAsSystem ? "/ru SYSTEM" : "";
        var exitCode = RunSchtasks(
            $"/create /tn \"{QuoteName(taskName)}\" /tr {tr} /sc onstart {ru} /rl HIGHEST /f");

        if (exitCode != 0)
            throw new LabOperationException("CREATE_TASK", taskName,
                detail: $"exe={exePath}, workingDir={workingDir}, runAsSystem={runAsSystem}",
                message: $"schtasks 创建任务失败，退出码: {exitCode}");
    }

    /// <summary>
    /// 创建每周定时计划任务（SYSTEM 身份、最高权限）。
    /// </summary>
    /// <param name="taskName">任务名。</param>
    /// <param name="command">/tr 内容（命令行）。</param>
    /// <param name="day">星期，如 SUN / 周日（schtasks /d 接受的值）。</param>
    /// <param name="time">时间，如 03:00。</param>
    public static void CreateWeeklyTask(string taskName, string command, string day, string time)
    {
        if (LabConfig.TestMode)
        {
            Console.WriteLine($"[测试模式] 跳过创建每周任务: {taskName} -> {command} @ {day} {time}");
            return;
        }

        if (TaskExists(taskName))
            RunSchtasks($"/delete /tn \"{QuoteName(taskName)}\" /f");

        var tr = QuoteTr(command);
        var exitCode = RunSchtasks(
            $"/create /tn \"{QuoteName(taskName)}\" /tr {tr} /sc weekly /d {day} /st {time} /ru SYSTEM /rl HIGHEST /f");

        if (exitCode != 0)
            throw new LabOperationException("CREATE_TASK", taskName,
                detail: $"cmd={command}, day={day}, time={time}",
                message: $"schtasks 创建每周任务失败，退出码: {exitCode}");
    }

    /// <summary>删除计划任务。</summary>
    public static void DeleteTask(string taskName)
    {
        if (LabConfig.TestMode)
        {
            Console.WriteLine($"[测试模式] 跳过删除计划任务: {taskName}");
            return;
        }
        RunSchtasks($"/delete /tn \"{QuoteName(taskName)}\" /f");
    }

    /// <summary>立即运行计划任务一次。</summary>
    public static void StartTask(string taskName)
    {
        if (LabConfig.TestMode)
        {
            Console.WriteLine($"[测试模式] 跳过启动计划任务: {taskName}");
            return;
        }
        RunSchtasks($"/run /tn \"{QuoteName(taskName)}\"");
    }

    /// <summary>
    /// 停止正在运行的计划任务实例（schtasks /end）。
    /// 任务不存在或未在运行时静默忽略，不抛异常。
    /// </summary>
    public static void StopTask(string taskName)
    {
        if (LabConfig.TestMode)
        {
            Console.WriteLine($"[测试模式] 跳过停止计划任务: {taskName}");
            return;
        }
        // /end 对未运行的任务返回非零退出码，这里忽略
        RunSchtasks($"/end /tn \"{QuoteName(taskName)}\"");
    }

    /// <summary>
    /// 禁用计划任务。schtasks /change /disable 仅阻止触发器激活，
    /// 不会停止已在运行的实例（如需停止请先调用 StopTask）。
    /// 用于部署流程中"防止杀进程后任务自动重启"：先 Disable → Stop → Kill → 替换文件。
    /// 任务不存在时静默忽略。
    /// </summary>
    public static void DisableTask(string taskName)
    {
        if (LabConfig.TestMode)
        {
            Console.WriteLine($"[测试模式] 跳过禁用计划任务: {taskName}");
            return;
        }
        RunSchtasks($"/change /tn \"{QuoteName(taskName)}\" /disable");
    }

    /// <summary>
    /// 启用计划任务（撤销 DisableTask）。任务不存在时静默忽略。
    /// </summary>
    public static void EnableTask(string taskName)
    {
        if (LabConfig.TestMode)
        {
            Console.WriteLine($"[测试模式] 跳过启用计划任务: {taskName}");
            return;
        }
        RunSchtasks($"/change /tn \"{QuoteName(taskName)}\" /enable");
    }

    /// <summary>
    /// 为已存在的计划任务补设"进程退出后自动重启"策略。
    /// schtasks.exe 不支持设置 RestartInterval/RestartCount，必须通过 Task Scheduler COM API。
    /// 用于守护进程任务：进程崩溃后 1 分钟自动重启，最多 999 次（实际等于无限）。
    /// 同时取消单次执行时长限制（ExecutionTimeLimit=PT0S），保证守护进程可常驻。
    /// </summary>
    /// <param name="taskName">任务名。</param>
    /// <param name="intervalMin">重启间隔（分钟），默认 1。</param>
    /// <param name="count">重启次数上限，默认 999。</param>
    /// <remarks>
    /// 调用前提：任务已通过 CreateStartupTask 创建。本方法读取现有任务定义，
    /// 仅修改 Settings.RestartInterval / RestartCount / ExecutionTimeLimit，
    /// 其他字段（触发器/动作/身份）保持不变，以 SYSTEM 身份重新注册（TASK_CREATE_OR_UPDATE=6）。
    /// </remarks>
    public static void SetRestartPolicy(string taskName, int intervalMin = 1, int count = 999)
    {
        if (LabConfig.TestMode)
        {
            Console.WriteLine($"[测试模式] 跳过设置重启策略: {taskName}");
            return;
        }

        // 用 PowerShell 调用 Task Scheduler COM API
        // RestartInterval/Count 用 ISO 8601 持续时间格式：PT1M = 1 分钟
        var intervalIso = $"PT{intervalMin}M";
        // 用单引号字符串避免 PowerShell 变量插值；任务名插入时已确保不含单引号
        var psScript = "$ErrorActionPreference = 'Stop'\n" +
            "try {\n" +
            "    $ts = New-Object -ComObject Schedule.Service\n" +
            "    $ts.Connect()\n" +
            "    $folder = $ts.GetFolder('\\')\n" +
            $"    $task = $folder.GetTask('{taskName}')\n" +
            "    $def = $task.Definition\n" +
            $"    $def.Settings.RestartInterval = '{intervalIso}'\n" +
            $"    $def.Settings.RestartCount = {count}\n" +
            "    $def.Settings.ExecutionTimeLimit = 'PT0S'\n" +
            "    # TASK_CREATE_OR_UPDATE=6, SYSTEM 账户登录类型=5（无需密码）\n" +
            $"    $folder.RegisterTaskDefinition('{taskName}', $def, 6, 'SYSTEM', $null, 5) | Out-Null\n" +
            "    Write-Output 'OK'\n" +
            "} catch {\n" +
            "    Write-Output ('ERR:' + $_.Exception.Message)\n" +
            "    exit 1\n" +
            "}\n";

        // 用 -EncodedCommand 传递 Base64(UTF16LE) 脚本，彻底避免引号/特殊字符转义问题
        var encoded = Convert.ToBase64String(global::System.Text.Encoding.Unicode.GetBytes(psScript));
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var p = Process.Start(psi);
        p?.WaitForExit(15000);
        var output = p?.StandardOutput.ReadToEnd()?.Trim() ?? "";

        if (p?.ExitCode != 0 || !output.Equals("OK", StringComparison.OrdinalIgnoreCase))
        {
            var err = p?.StandardError.ReadToEnd()?.Trim() ?? output;
            throw new LabOperationException("SET_RESTART_POLICY", taskName,
                detail: $"interval={intervalIso}, count={count}, output={output}, err={err}",
                message: $"设置任务重启策略失败: {err}");
        }

        Console.WriteLine($"[TaskSchedulerHelper] 任务 '{taskName}' 重启策略已设置: 间隔={intervalMin}分钟, 次数={count}");
    }

    /// <summary>查询计划任务是否存在。</summary>
    private static bool TaskExists(string taskName)
    {
        return RunSchtasks($"/query /tn \"{QuoteName(taskName)}\"") == 0;
    }

    /// <summary>
    /// 执行 schtasks.exe 并等待退出，返回退出码。
    /// </summary>
    private static int RunSchtasks(string args)
    {
        using var p = new Process();
        p.StartInfo.FileName = "schtasks.exe";
        p.StartInfo.Arguments = args;
        p.StartInfo.UseShellExecute = false;
        p.StartInfo.CreateNoWindow = true;
        p.StartInfo.RedirectStandardOutput = true;
        p.StartInfo.RedirectStandardError = true;
        p.Start();
        p.WaitForExit();
        return p.ExitCode;
    }

    /// <summary>转义任务名中的双引号（schtasks /tn 的值）。</summary>
    private static string QuoteName(string name) => name.Replace("\"", "\"\"");

    /// <summary>
    /// 构造 /tr 的引号包裹值。/tr 接受一个完整的命令行字符串，整体用双引号包裹，
    /// 内部双引号用 "" 转义。
    /// </summary>
    private static string QuoteTr(string command)
    {
        return "\"" + command.Replace("\"", "\"\"") + "\"";
    }
}
