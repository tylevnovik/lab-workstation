using System.Diagnostics;
using System.Runtime.Versioning;
using LabWorkstation.Common.Configuration;
using LabWorkstation.Common.Desktop;

namespace LabWorkstation.Common.System;

/// <summary>
/// 部署 LabWorkstation.TrayApp 到 C:\Scripts 并在公共启动目录创建快捷方式。
/// 对应原 PS Deploy-TrayApp.ps1（C# 版）。
/// 所有用户登录时自动启动悬浮导航。
/// 测试模式（LabConfig.TestMode）下跳过。
/// </summary>
[SupportedOSPlatform("windows")]
public static class TrayAppDeployer
{
    /// <summary>开机启动快捷方式名（与原 PS 一致）。</summary>
    public const string ShortcutName = "Lab-TrayApp.lnk";

    /// <summary>
    /// 部署 TrayApp：复制构建产物到 C:\Scripts\LabWorkstation.TrayApp，
    /// 在公共启动目录创建 Lab-TrayApp.lnk 快捷方式，并启动一次。
    /// 部署前强制关闭正在运行的 TrayApp 进程，确保旧目录可删除、新文件可覆盖。
    /// </summary>
    public static void Deploy()
    {
        if (LabConfig.TestMode)
        {
            Console.WriteLine("[测试模式] 跳过部署 TrayApp");
            return;
        }

        // 强制关闭正在运行的 TrayApp 进程，避免文件被占用导致删除/复制失败
        KillRunningProcess("LabWorkstation.TrayApp");

        var sourceDir = BuildArtifactLocator.ResolveProjectBinDir("LabWorkstation.TrayApp");
        var targetDir = Path.Combine(LabConfig.ScriptsDir, "LabWorkstation.TrayApp");

        // 清理旧部署目录（进程已关闭，可安全删除）
        if (Directory.Exists(targetDir))
        {
            try { Directory.Delete(targetDir, recursive: true); }
            catch { /* 删除失败不阻断，后续 CopyDirectory 会覆盖 */ }
        }
        CopyDirectory(sourceDir, targetDir);

        var trayExe = Path.Combine(targetDir, "LabWorkstation.TrayApp.exe");
        if (!File.Exists(trayExe))
            throw new LabOperationException("DEPLOY_TRAYAPP", targetDir,
                detail: $"缺少 {trayExe}",
                message: "部署目录中未找到 LabWorkstation.TrayApp.exe");

        // 在公共启动目录创建快捷方式（所有用户开机自启）
        var startupDir = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
        var shortcutPath = Path.Combine(startupDir, ShortcutName);

        ShortcutHelper.CreateShortcut(
            shortcutPath,
            targetPath: trayExe,
            arguments: "",
            workingDirectory: targetDir,
            iconLocation: "",
            description: "课题组工作站 · 悬浮导航工具");

        // 启动一次（启动 + 验证：进程必须运行起来才算部署成功）
        Exception? startException = null;
        try
        {
            using var p = new Process();
            p.StartInfo.FileName = trayExe;
            p.StartInfo.WorkingDirectory = targetDir;
            p.StartInfo.UseShellExecute = true;
            p.Start();
        }
        catch (Exception ex)
        {
            startException = ex;
            Console.WriteLine($"[TrayAppDeployer] 启动 TrayApp 失败: {ex.Message}");
        }

        // 部署后验证：exe 文件已到位 + 进程已启动运行
        VerifyTrayAppRunning(trayExe, startException);
    }

    /// <summary>
    /// 部署后验证：确认 exe 文件存在且进程已启动。
    /// 等待最多 10 秒（TrayApp 是 GUI 程序，启动较快）。
    /// 验证失败抛 LabOperationException，提示用户检查。
    /// </summary>
    /// <param name="trayExe">TrayApp.exe 完整路径。</param>
    /// <param name="startException">启动阶段已捕获的异常。</param>
    private static void VerifyTrayAppRunning(string trayExe, Exception? startException)
    {
        // 1. 验证文件到位
        if (!File.Exists(trayExe))
        {
            throw new LabOperationException("DEPLOY_TRAYAPP_VERIFY", trayExe,
                detail: $"exe 路径不存在: {trayExe}",
                message: "TrayApp 部署后验证失败：LabWorkstation.TrayApp.exe 未到位");
        }

        // 2. 验证进程已启动（等待最多 10 秒）
        const int verifyTimeoutSeconds = 10;
        const int checkIntervalMs = 1000;
        var deadline = DateTime.Now.AddSeconds(verifyTimeoutSeconds);
        while (DateTime.Now < deadline)
        {
            if (IsProcessRunning("LabWorkstation.TrayApp"))
            {
                Console.WriteLine("[TrayAppDeployer] 验证通过：TrayApp 进程已启动");
                return;
            }
            global::System.Threading.Thread.Sleep(checkIntervalMs);
        }

        // 验证失败
        var detail = $"exe存在=true, 进程运行=false";
        var message = "TrayApp 部署后验证失败：进程未启动";
        if (startException != null)
            message += $"；启动异常: {startException.Message}";
        message += "。请手动运行 TrayApp.exe 检查错误，或查看事件日志";

        throw new LabOperationException("DEPLOY_TRAYAPP_VERIFY", trayExe,
            detail: detail, message: message);
    }

    /// <summary>检查指定名称的进程是否在运行（通过 tasklist 查询）。</summary>
    private static bool IsProcessRunning(string processName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "tasklist.exe",
                Arguments = $"/FI \"IMAGENAME eq {processName}.exe\" /NH /FO CSV",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(5000);
            var output = p?.StandardOutput.ReadToEnd() ?? "";
            // 无匹配时 tasklist 输出"信息: 没有运行的任务..."，有匹配时输出 CSV 行
            return output.Contains(processName, StringComparison.OrdinalIgnoreCase);
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
    /// 强制关闭指定名称的运行中进程，避免部署时文件被占用。
    /// 使用 taskkill /F /IM，进程不存在时静默忽略。
    /// </summary>
    /// <param name="processName">进程名（不含 .exe 后缀）。</param>
    private static void KillRunningProcess(string processName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "taskkill.exe",
                Arguments = $"/F /IM {processName}.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(5000);
            Console.WriteLine($"[TrayAppDeployer] 已尝试关闭运行中的 {processName}.exe");
        }
        catch
        {
            // 进程不存在或关闭失败，忽略
        }
    }
}
