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
