using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LabWorkstation.Common.Audit;
using LabWorkstation.Common.Configuration;
using LabWorkstation.Common.LocalAccounts;
using LabWorkstation.Common.Native;
using Microsoft.Win32;

namespace LabWorkstation.Common.Desktop;

/// <summary>
/// 三层壁纸设置：当前会话壁纸 / 用户 Profile 默认壁纸 / Default User 默认壁纸，
/// 并通过组策略注册表锁定壁纸不允许更改。
/// 对应原 PS 的 Set-LabWallpaper 三层逻辑。
/// 测试模式（LabConfig.TestMode）下所有方法仅记录日志，不修改真实系统。
/// </summary>
[SupportedOSPlatform("windows")]
public static class WallpaperManager
{
    private const uint SPI_SETDESKWALLPAPER = 0x0014;
    private const uint SPIF_UPDATEINIFILE = 0x01;
    private const uint SPIF_SENDWININICHANGE = 0x02;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SystemParametersInfo(uint uiAction, uint uiParam, string pvParam, uint fWinIni);

    /// <summary>
    /// 设置当前会话壁纸（立即生效）。
    /// </summary>
    /// <param name="wallpaperPath">壁纸文件全路径。</param>
    public static void SetCurrentWallpaper(string wallpaperPath)
    {
        if (LabConfig.TestMode)
        {
            Console.WriteLine($"[测试模式] 跳过设置当前会话壁纸: {wallpaperPath}");
            return;
        }

        var ok = SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, wallpaperPath,
            SPIF_UPDATEINIFILE | SPIF_SENDWININICHANGE);
        if (ok == 0)
        {
            var err = Marshal.GetLastWin32Error();
            throw new LabOperationException("SET_WALLPAPER", "CurrentSession",
                detail: wallpaperPath,
                message: $"SystemParametersInfo 失败，Win32 错误码: {err}");
        }
        AuditLogger.Write("SET_WALLPAPER", "CurrentSession", AuditLogger.Result.Success, wallpaperPath);
    }

    /// <summary>
    /// 设置指定用户 Profile 的默认壁纸（写入其 NTUSER.DAT 的 Control Panel\Desktop）。
    /// 从注册表 ProfileList 读取实际的 ProfileImagePath，确保写入正确位置。
    /// </summary>
    /// <param name="username">用户名。</param>
    public static void SetProfileWallpaper(string username)
    {
        if (LabConfig.TestMode)
        {
            Console.WriteLine($"[测试模式] 跳过设置用户 Profile 壁纸: {username}");
            return;
        }

        // 从注册表读取实际的 Profile 路径（而非重新调用 EnsureProfile，避免路径不一致）
        var profilePath = GetProfilePathFromRegistry(username);
        if (string.IsNullOrEmpty(profilePath))
        {
            // 回退：Profile 可能尚未创建，尝试创建
            profilePath = ProfileManager.EnsureProfile(username);
        }

        var hivePath = Path.Combine(profilePath, "NTUSER.DAT");
        if (!File.Exists(hivePath))
        {
            AuditLogger.Write("SET_PROFILE_WALLPAPER", username, AuditLogger.Result.Failed,
                $"NTUSER.DAT 不存在: {hivePath}");
            return;
        }

        WriteWallpaperToHive(hivePath);
        AuditLogger.Write("SET_PROFILE_WALLPAPER", username, AuditLogger.Result.Success, $"hive: {hivePath}");
    }

    /// <summary>从 ProfileList 注册表读取指定用户的 ProfileImagePath。</summary>
    private static string? GetProfilePathFromRegistry(string username)
    {
        try
        {
            using var profileList = Registry.LocalMachine.OpenSubKey(LabConfig.ProfileListRegPath);
            if (profileList == null) return null;

            // 通过 AccountManager 获取 SID
            var sid = AccountManager.GetUserSid(username);
            if (!string.IsNullOrEmpty(sid))
            {
                using var subKey = profileList.OpenSubKey(sid);
                var path = subKey?.GetValue("ProfileImagePath") as string;
                if (!string.IsNullOrEmpty(path)) return path;
            }

            // SID 获取失败时遍历查找
            foreach (var subSid in profileList.GetSubKeyNames())
            {
                using var subKey = profileList.OpenSubKey(subSid);
                var path = subKey?.GetValue("ProfileImagePath") as string;
                if (path != null && path.EndsWith("\\" + username, StringComparison.OrdinalIgnoreCase))
                    return path;
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 设置 Default User 的默认壁纸（写入 C:\Users\Default\NTUSER.DAT）。
    /// </summary>
    public static void SetDefaultUserWallpaper()
    {
        if (LabConfig.TestMode)
        {
            Console.WriteLine("[测试模式] 跳过设置 Default User 壁纸");
            return;
        }

        WriteWallpaperToHive(LabConfig.DefaultUserHivePath);
        AuditLogger.Write("SET_DEFAULT_WALLPAPER", "DefaultUser", AuditLogger.Result.Success, LabConfig.DefaultUserHivePath);
    }

    /// <summary>
    /// 写 HKLM 组策略注册表锁定壁纸（禁止更改壁纸/锁屏）。
    /// </summary>
    public static void LockWallpaperPolicy()
    {
        if (LabConfig.TestMode)
        {
            Console.WriteLine("[测试模式] 跳过锁定壁纸组策略");
            return;
        }

        using (var activeDesktop = Registry.LocalMachine.CreateSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\ActiveDesktop"))
        {
            activeDesktop.SetValue("DesktopWallpaper", LabConfig.WallpaperPath);
            activeDesktop.SetValue("NoChangingWallpaper", 1, RegistryValueKind.DWord);
        }

        using (var system = Registry.LocalMachine.CreateSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System"))
        {
            system.SetValue("WallpaperStyle", "10");
        }

        using (var personalization = Registry.LocalMachine.CreateSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Personalization"))
        {
            personalization.SetValue("NoChangingLockScreen", 1, RegistryValueKind.DWord);
        }

        AuditLogger.Write("LOCK_WALLPAPER_POLICY", "HKLM", AuditLogger.Result.Success, LabConfig.WallpaperPath);
    }

    /// <summary>
    /// 将项目根的壁纸源文件（wallpaper.png）复制到部署路径（C:\Scripts\LabWallpaper.png）。
    /// 依次在 AppContext.BaseDirectory 与当前工作目录查找源文件。
    /// </summary>
    public static void DeployWallpaperFile()
    {
        if (LabConfig.TestMode)
        {
            Console.WriteLine($"[测试模式] 跳过部署壁纸文件到 {LabConfig.WallpaperPath}");
            return;
        }

        // 候选源路径：AppContext.BaseDirectory → 向上回溯到解决方案根 → 当前目录 → 硬编码
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, LabConfig.WallpaperSourceName),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", LabConfig.WallpaperSourceName)),
            Path.Combine(Directory.GetCurrentDirectory(), LabConfig.WallpaperSourceName),
            @"d:\lab-workstation\wallpaper.png"
        };

        var sourcePath = candidates.FirstOrDefault(File.Exists);
        if (sourcePath == null)
            throw new LabOperationException("DEPLOY_WALLPAPER", LabConfig.WallpaperPath,
                detail: $"源文件: {LabConfig.WallpaperSourceName}, 尝试过: {string.Join(" | ", candidates)}",
                message: "找不到壁纸源文件");

        Directory.CreateDirectory(LabConfig.ScriptsDir);
        File.Copy(sourcePath, LabConfig.WallpaperPath, overwrite: true);
        AuditLogger.Write("DEPLOY_WALLPAPER", LabConfig.WallpaperPath, AuditLogger.Result.Success, $"from: {sourcePath}");
    }

    /// <summary>
    /// 加载 hive 并写入壁纸注册表项（Wallpaper / WallpaperStyle / TileWallpaper）。
    /// </summary>
    private static void WriteWallpaperToHive(string hiveFilePath)
    {
        RegistryHiveLoader.UsingHive(hiveFilePath, key =>
        {
            using var desktop = key.CreateSubKey(@"Control Panel\Desktop");
            desktop.SetValue("Wallpaper", LabConfig.WallpaperPath);
            desktop.SetValue("WallpaperStyle", "10");
            desktop.SetValue("TileWallpaper", "0");
        });
    }
}
