using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using LabWorkstation.Common.Configuration;

namespace LabWorkstation.Common.Desktop;

/// <summary>
/// 创建 Windows 快捷方式（.lnk）。对应原 PS 的 New-DesktopShortcut。
/// 优先尝试 COM Interop（IShellLinkW），失败时回退到 PowerShell WScript.Shell。
/// 测试模式（LabConfig.TestMode）下所有方法仅记录日志，不修改真实系统。
/// </summary>
[SupportedOSPlatform("windows")]
public static class ShortcutHelper
{
    // ── COM 接口内联声明 ──────────────────────────────────────

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [Guid("0000010B-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    [ClassInterface(ClassInterfaceType.None)]
    private class ShellLink { }

    /// <summary>
    /// 创建快捷方式文件。优先 COM Interop，失败回退 PowerShell WScript.Shell。
    /// </summary>
    public static void CreateShortcut(string shortcutPath, string targetPath,
        string arguments = "", string workingDirectory = "", string iconLocation = "", string description = "")
    {
        if (LabConfig.TestMode)
        {
            Console.WriteLine($"[测试模式] 跳过创建快捷方式: {shortcutPath} -> {targetPath} {arguments}");
            return;
        }

        var dir = Path.GetDirectoryName(shortcutPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // 先尝试 COM Interop
        try
        {
            CreateShortcutViaCom(shortcutPath, targetPath, arguments, workingDirectory, iconLocation, description);
            return;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ShortcutHelper] COM Interop 失败，回退 PowerShell: {ex.Message}");
        }

        // 回退：PowerShell WScript.Shell
        CreateShortcutViaPowerShell(shortcutPath, targetPath, arguments, workingDirectory, iconLocation, description);
    }

    private static void CreateShortcutViaCom(string shortcutPath, string targetPath,
        string arguments, string workingDirectory, string iconLocation, string description)
    {
        var shellLink = new ShellLink();
        var slw = (IShellLinkW)shellLink;

        slw.SetPath(targetPath);
        if (!string.IsNullOrEmpty(arguments)) slw.SetArguments(arguments);
        if (!string.IsNullOrEmpty(workingDirectory)) slw.SetWorkingDirectory(workingDirectory);
        if (!string.IsNullOrEmpty(description)) slw.SetDescription(description);

        if (!string.IsNullOrEmpty(iconLocation))
        {
            var parts = iconLocation.Split(',', StringSplitOptions.TrimEntries);
            var iconPath = parts.Length > 0 ? parts[0] : "";
            var iconIndex = parts.Length > 1 && int.TryParse(parts[1], out var idx) ? idx : 0;
            if (!string.IsNullOrEmpty(iconPath))
                slw.SetIconLocation(iconPath, iconIndex);
        }

        var persistFile = (IPersistFile)shellLink;
        persistFile.Save(shortcutPath, fRemember: false);
        Marshal.ReleaseComObject(shellLink);
    }

    private static void CreateShortcutViaPowerShell(string shortcutPath, string targetPath,
        string arguments, string workingDirectory, string iconLocation, string description)
    {
        // 用 PowerShell WScript.Shell COM 创建快捷方式（最可靠的方式）
        var ps = new StringBuilder();
        ps.AppendLine("$shell = New-Object -ComObject WScript.Shell");
        ps.AppendLine($"$sc = $shell.CreateShortcut('{shortcutPath.Replace("'", "''")}')");
        ps.AppendLine($"$sc.TargetPath = '{targetPath.Replace("'", "''")}'");
        if (!string.IsNullOrEmpty(arguments))
            ps.AppendLine($"$sc.Arguments = '{arguments.Replace("'", "''")}'");
        if (!string.IsNullOrEmpty(workingDirectory))
            ps.AppendLine($"$sc.WorkingDirectory = '{workingDirectory.Replace("'", "''")}'");
        if (!string.IsNullOrEmpty(description))
            ps.AppendLine($"$sc.Description = '{description.Replace("'", "''")}'");
        if (!string.IsNullOrEmpty(iconLocation))
            ps.AppendLine($"$sc.IconLocation = '{iconLocation.Replace("'", "''")}'");
        ps.AppendLine("$sc.Save()");

        using var p = new Process();
        p.StartInfo.FileName = "powershell.exe";
        p.StartInfo.Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{ps.ToString().Replace("\"", "\\\"")}\"";
        p.StartInfo.UseShellExecute = false;
        p.StartInfo.CreateNoWindow = true;
        p.StartInfo.RedirectStandardError = true;
        p.Start();
        p.WaitForExit(10000); // 10 秒超时
        if (p.ExitCode != 0)
        {
            var stderr = p.StandardError.ReadToEnd();
            throw new LabOperationException(
                "CREATE_SHORTCUT", shortcutPath,
                detail: $"PowerShell exit={p.ExitCode}, stderr={stderr}",
                message: "COM Interop 和 PowerShell 两种方式均失败");
        }
    }

    /// <summary>
    /// 在公共桌面创建 4 个快捷方式（所有用户可见）。对应原 PS 第 3 段。
    /// 1. 我的个人文件夹 → D:\Users\%USERNAME%
    /// 2. 组内共享文件夹 → 动态打开当前用户所属导师组的目录
    /// 3. 全局公共文件夹 → D:\GroupData\_公共
    /// 4. 工作站使用手册 → notepad 打开手册 md
    /// </summary>
    public static void CreateCommonDesktopShortcuts()
    {
        if (LabConfig.TestMode)
        {
            Console.WriteLine("[测试模式] 跳过创建公共桌面快捷方式");
            return;
        }

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);

        // 1. 我的个人文件夹（用 cmd start 以便用 explorer 打开，%USERNAME% 运行时展开）
        CreateShortcut(
            Path.Combine(desktop, "我的个人文件夹.lnk"),
            targetPath: @"C:\Windows\System32\cmd.exe",
            arguments: @"/c start """" ""D:\Users\%USERNAME%""",
            iconLocation: "shell32.dll,126",
            description: "你的私人存储空间（仅自己可见）");

        // 2. 组内共享文件夹（动态打开当前用户所属导师组目录）
        // 部署一个 PowerShell 脚本，运行时查询当前用户所属的 Lab_ 组并打开对应目录
        var scriptPath = Path.Combine(LabConfig.ScriptsDir, "OpenGroupFolder.ps1");
        File.WriteAllText(scriptPath, OpenGroupFolderScript, new UTF8Encoding(true));
        CreateShortcut(
            Path.Combine(desktop, "组内共享文件夹.lnk"),
            targetPath: @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
            arguments: $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            iconLocation: "shell32.dll,27",
            description: "你所在导师组的共享数据区");

        // 3. 全局公共文件夹
        CreateShortcut(
            Path.Combine(desktop, "全局公共文件夹.lnk"),
            targetPath: @"C:\Windows\explorer.exe",
            arguments: LabConfig.PublicPath,
            iconLocation: "shell32.dll,162",
            description: "所有导师组共享的数据和工具");

        // 4. 工作站使用手册
        var handbookPath = Path.Combine(LabConfig.PublicPath, "_使用手册", "工作站使用手册.md");
        CreateShortcut(
            Path.Combine(desktop, "工作站使用手册.lnk"),
            targetPath: @"C:\Windows\System32\notepad.exe",
            arguments: handbookPath,
            iconLocation: "shell32.dll,23",
            description: "工作站使用规范（必读）");
    }

    /// <summary>
    /// PowerShell 脚本：查找当前用户所属的 Lab_ 开头导师组，打开对应的 D:\GroupData\导师名 目录。
    /// 如果用户不属于任何导师组，回退到打开 D:\GroupData\_公共。
    /// </summary>
    private const string OpenGroupFolderScript = @"
# 查找当前用户所属的导师组（Lab_ 开头，排除 Lab_All）
$currentUserName = $env:USERNAME
$userGroups = whoami /groups
$advisorName = $null

foreach ($line in $userGroups) {
    if ($line -match 'Lab_(\S+)' -and $line -notmatch 'Lab_All') {
        $advisorName = $Matches[1]
        break
    }
}

if ($advisorName) {
    $targetDir = ""D:\GroupData\$advisorName""
    if (Test-Path $targetDir) {
        Start-Process explorer.exe -ArgumentList $targetDir
        exit 0
    }
}

# 回退：打开公共文件夹
$publicDir = ""D:\GroupData\_公共""
if (Test-Path $publicDir) {
    Start-Process explorer.exe -ArgumentList $publicDir
}
";

    /// <summary>
    /// 在公共桌面写"【必读】工作站使用须知.txt"（UTF8）。对应原 PS 第 4 段。
    /// 内容为数据存放规则，与原 PS 保持一致。
    /// </summary>
    public static void CreateDesktopReadme()
    {
        if (LabConfig.TestMode)
        {
            Console.WriteLine("[测试模式] 跳过创建桌面须知文件");
            return;
        }

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        var noticePath = Path.Combine(desktop, "【必读】工作站使用须知.txt");

        var sb = new StringBuilder();
        sb.AppendLine("═══════════════════════════════════════════════════");
        sb.AppendLine("    课题组公共工作站 · 使用须知");
        sb.AppendLine("═══════════════════════════════════════════════════");
        sb.AppendLine();
        sb.AppendLine("你正在使用一台公共工作站，请注意以下事项：");
        sb.AppendLine();
        sb.AppendLine("【数据存放规则】");
        sb.AppendLine();
        sb.AppendLine("  ■ 个人数据（草稿、私人文件）");
        sb.AppendLine("    → D:\\Users\\你的用户名\\");
        sb.AppendLine("    仅自己可见，其他人无法访问");
        sb.AppendLine();
        sb.AppendLine("  ■ 组内数据（本组的项目、报告）");
        sb.AppendLine("    → D:\\GroupData\\你的导师名\\对应类别\\");
        sb.AppendLine("    仅本组成员可见");
        sb.AppendLine();
        sb.AppendLine("  ■ 跨组数据（需要多组共享的数据）");
        sb.AppendLine("    → D:\\GroupData\\_公共\\");
        sb.AppendLine("    所有成员可见");
        sb.AppendLine();
        sb.AppendLine("  ■ 软件安装");
        sb.AppendLine("    公共软件 → 找管理员装到 Program Files");
        sb.AppendLine("    个人工具 → 装到 D:\\Users\\你的用户名\\Tools\\");
        sb.AppendLine("    禁止往 GroupData 里装任何程序");
        sb.AppendLine();
        sb.AppendLine("【注意事项】");
        sb.AppendLine();
        sb.AppendLine("  · 不要把私人数据放在 GroupData 里（所有人可见）");
        sb.AppendLine("  · 不要在 C 盘存大文件（系统盘空间有限）");
        sb.AppendLine("  · 用完远程桌面请注销（不要只断开连接）");
        sb.AppendLine("  · 跑耗时任务请限制 CPU/线程数，别占满资源");
        sb.AppendLine("  · 桌面壁纸和此须知文件不可删除（系统策略锁定）");
        sb.AppendLine();
        sb.AppendLine("【快捷操作】");
        sb.AppendLine();
        sb.AppendLine("  · 桌面右下角有悬浮导航，点击按钮可快速打开各文件夹");
        sb.AppendLine("  · 右键导航图标有更多选项");
        sb.AppendLine();
        sb.AppendLine("【需要帮助？】");
        sb.AppendLine();
        sb.AppendLine("  · 阅读桌面上的《工作站使用手册》");
        sb.AppendLine("  · 联系管理员");
        sb.AppendLine();
        sb.AppendLine("═══════════════════════════════════════════════════");

        File.WriteAllText(noticePath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }
}
