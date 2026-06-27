using System.Diagnostics;
using System.IO;
using LabWorkstation.Common.Configuration;
using LabWorkstation.Common.Security;

namespace LabWorkstation.Admin;

static class Program
{
    /// <summary>
    ///  The main entry point for the application.
    ///  传入 --test 参数进入测试模式：所有操作仅作用于内存，不修改真实系统。
    ///  传入 --deployed 表示已从 C:\Scripts 自部署目录启动，跳过自部署检查。
    /// </summary>
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Contains("--test", StringComparer.OrdinalIgnoreCase))
        {
            LabConfig.TestMode = true;
            Console.WriteLine("[测试模式] 已启用，所有操作仅作用于内存模拟状态。");
        }

        // 管理员权限检查：非管理员禁止运行 Admin 面板
        if (!LabConfig.TestMode && !AdminCheck.IsCurrentUserAdmin())
        {
            MessageBox.Show(
                "此管理面板仅管理员可用。\n请使用管理员账户登录后运行。",
                "权限不足", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // 自部署：不在 C:\Scripts 时自动复制并从该目录重启
        if (!LabConfig.TestMode && !args.Contains("--deployed", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (TrySelfDeploy(out var newExePath))
                {
                    // 从新位置重启自身，传递 --deployed 防止循环
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = newExePath,
                        Arguments = "--deployed",
                        UseShellExecute = false,
                        WorkingDirectory = Path.GetDirectoryName(newExePath)!
                    });
                    return;
                }
            }
            catch (Exception ex)
            {
                // 自部署失败不阻断启动，继续从当前位置运行
                Console.WriteLine($"[自部署] 失败，将从当前位置启动: {ex.Message}");
            }
        }

        // 已部署到 C:\Scripts：确保管理员桌面有快捷方式
        if (!LabConfig.TestMode && args.Contains("--deployed", StringComparer.OrdinalIgnoreCase))
        {
            try { EnsureDesktopShortcut(); }
            catch (Exception ex) { Console.WriteLine($"[快捷方式] 创建失败: {ex.Message}"); }
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }

    /// <summary>
    /// 检查当前是否已从 C:\Scripts\LabWorkstation.Admin 运行。
    /// 若不是，将整个构建产物目录复制到该位置，返回 true 并输出新 exe 路径。
    /// </summary>
    private static bool TrySelfDeploy(out string newExePath)
    {
        newExePath = "";
        var currentDir = AppContext.BaseDirectory;
        var targetDir = Path.Combine(LabConfig.ScriptsDir, "LabWorkstation.Admin");

        // 已经在目标目录运行
        if (string.Equals(currentDir.TrimEnd('\\', '/'),
            Path.GetFullPath(targetDir).TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase))
            return false;

        var currentExe = Environment.ProcessPath ?? Path.Combine(currentDir, "LabWorkstation.Admin.exe");
        newExePath = Path.Combine(targetDir, "LabWorkstation.Admin.exe");

        // 已存在目标文件且与当前相同则跳过
        if (File.Exists(newExePath))
        {
            try
            {
                var srcInfo = new FileInfo(currentExe);
                var dstInfo = new FileInfo(newExePath);
                if (srcInfo.Length == dstInfo.Length &&
                    srcInfo.LastWriteTime <= dstInfo.LastWriteTime)
                    return false; // 目标已是最新
            }
            catch { /* 比较失败则继续覆盖 */ }
        }

        Directory.CreateDirectory(LabConfig.ScriptsDir);
        Directory.CreateDirectory(targetDir);

        // 整目录复制（覆盖已存在文件）
        CopyDirectory(currentDir, targetDir);

        Console.WriteLine($"[自部署] 已复制到 {targetDir}");
        return true;
    }

    /// <summary>
    /// 在 Administrator 账户的桌面创建 Admin 面板快捷方式。
    /// 快捷方式指向 C:\Scripts\LabWorkstation.Admin\LabWorkstation.Admin.exe，
    /// WorkingDirectory 设为 C:\Scripts\LabWorkstation.Admin。
    /// </summary>
    private static void EnsureDesktopShortcut()
    {
        var exePath = Path.Combine(LabConfig.ScriptsDir, "LabWorkstation.Admin", "LabWorkstation.Admin.exe");
        var workingDir = Path.Combine(LabConfig.ScriptsDir, "LabWorkstation.Admin");
        if (!File.Exists(exePath)) return;

        // Administrator 桌面路径
        var adminDesktop = Path.Combine(@"C:\Users\Administrator\Desktop");
        var shortcutPath = Path.Combine(adminDesktop, "工作站管理面板.lnk");

        // 使用 PowerShell WScript.Shell COM 对象创建快捷方式
        var psScript = $@"
$ws = New-Object -ComObject WScript.Shell
$sc = $ws.CreateShortcut('{shortcutPath}')
$sc.TargetPath = '{exePath}'
$sc.WorkingDirectory = '{workingDir}'
$sc.Description = '课题组工作站管理面板'
$sc.IconLocation = '{exePath},0'
$sc.Save()
";
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -Command \"{psScript.Replace("\"", "\\\"")}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        var proc = Process.Start(psi);
        proc?.WaitForExit(5000);
        Console.WriteLine($"[快捷方式] 已创建: {shortcutPath}");
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var fileName = Path.GetFileName(file);
            File.Copy(file, Path.Combine(dest, fileName), overwrite: true);
        }
        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            var dirName = Path.GetFileName(dir);
            // 跳过 obj 目录减少体积
            if (dirName == "obj") continue;
            CopyDirectory(dir, Path.Combine(dest, dirName));
        }
    }
}
