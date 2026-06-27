using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LabWorkstation.Common.Audit;
using LabWorkstation.Common.Configuration;
using LabWorkstation.Common.Native;
using Microsoft.Win32;

namespace LabWorkstation.Common.LocalAccounts;

/// <summary>
/// 账户 Profile 预生成与清理编排。在用户首次登录前调用 userenv.dll 的 CreateProfile
/// 预先创建用户配置文件目录，避免首次登录时的延迟。
/// 删除用户时调用 DeleteProfile 清理 Profile 目录与注册表残留。
/// 测试模式（LabConfig.TestMode）下仅记录日志并返回模拟路径，不修改真实系统。
/// </summary>
[SupportedOSPlatform("windows")]
public static class ProfileManager
{
    /// <summary>
    /// 确保指定用户的 Profile 已生成。幂等：已存在则直接返回路径。
    /// </summary>
    /// <param name="username">用户名。</param>
    /// <returns>用户 Profile 目录路径。</returns>
    public static string EnsureProfile(string username)
    {
        if (LabConfig.TestMode)
        {
            var mockPath = GetExpectedProfilePath(username);
            Console.WriteLine($"[测试模式] 跳过 Profile 预生成: {username}（模拟路径: {mockPath}）");
            return mockPath;
        }

        // 获取用户 SID
        using var up = AccountManager.FindUser(username)
            ?? throw new LabOperationException("ENSURE_PROFILE", username, $"用户 '{username}' 不存在，无法预生成 Profile");

        var sid = up.Sid?.Value
            ?? throw new LabOperationException("ENSURE_PROFILE", username, "无法获取用户 SID");

        if (UserenvNative.TryCreateProfile(sid, username, out var profilePath))
        {
            // CreateProfile 返回 E_ALREADY_EXISTS 时路径缓冲区可能为空，回退到标准路径
            if (string.IsNullOrEmpty(profilePath))
                profilePath = GetExpectedProfilePath(username);

            AuditLogger.Write("ENSURE_PROFILE", username, AuditLogger.Result.Success, $"Profile 路径: {profilePath}");
            return profilePath;
        }

        var err = Marshal.GetLastWin32Error();
        throw new LabOperationException("ENSURE_PROFILE", username,
            detail: $"预生成 Profile 失败（SID: {sid}）",
            message: $"CreateProfile 失败，Win32 错误码: {err}");
    }

    /// <summary>
    /// 删除指定用户的 Profile：先调用 userenv.dll DeleteProfile API 清理目录与注册表，
    /// 若 API 失败则手动清理注册表项和目录作为 fallback。
    /// 幂等：用户无 Profile 或已清理时静默成功。
    /// </summary>
    /// <param name="username">用户名。</param>
    /// <returns>实际删除的 Profile 目录路径（未找到则 null）。</returns>
    public static string? DeleteProfile(string username)
    {
        if (LabConfig.TestMode)
        {
            Console.WriteLine($"[测试模式] 跳过删除 Profile: {username}");
            return null;
        }

        // 获取用户 SID（账户可能已被删除，此时无法获取 SID）
        var sid = AccountManager.GetUserSid(username);
        if (string.IsNullOrEmpty(sid))
        {
            // 账户已不存在，尝试从注册表残留中查找匹配的 SID
            sid = FindSidByUsername(username);
            if (string.IsNullOrEmpty(sid))
            {
                Console.WriteLine($"[ProfileManager] 用户 '{username}' 无 SID，跳过 Profile 清理");
                return null;
            }
        }

        // 获取 Profile 路径（从注册表读取，用于 fallback 和返回值）
        var profilePath = GetProfilePathFromRegistry(sid) ?? GetExpectedProfilePath(username);

        // 方案 1：调用 DeleteProfile API（推荐，自动清理目录 + 注册表）
        if (UserenvNative.TryDeleteProfile(sid, profilePath))
        {
            AuditLogger.Write("DELETE_PROFILE", username, AuditLogger.Result.Success, $"SID: {sid}, 路径: {profilePath}");
            Console.WriteLine($"[ProfileManager] DeleteProfile API 成功: {username} ({profilePath})");
            return profilePath;
        }

        var err = Marshal.GetLastWin32Error();
        Console.WriteLine($"[ProfileManager] DeleteProfile API 失败 (错误码 {err})，尝试手动清理...");

        // 方案 2：手动清理（API 失败时的 fallback）
        return ManualDeleteProfile(username, sid, profilePath);
    }

    /// <summary>手动删除 Profile：删除注册表 SID 子键 + 删除 Profile 目录。</summary>
    private static string? ManualDeleteProfile(string username, string sid, string profilePath)
    {
        var cleaned = false;

        // 删除注册表项
        try
        {
            using var profileList = Registry.LocalMachine.OpenSubKey(LabConfig.ProfileListRegPath, writable: true);
            if (profileList?.OpenSubKey(sid) != null)
            {
                profileList.DeleteSubKeyTree(sid, throwOnMissingSubKey: false);
                Console.WriteLine($"[ProfileManager] 已删除注册表 ProfileList\\{sid}");
                cleaned = true;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ProfileManager] 删除注册表项失败: {ex.Message}");
        }

        // 删除 Profile 目录
        try
        {
            if (Directory.Exists(profilePath))
            {
                Directory.Delete(profilePath, recursive: true);
                Console.WriteLine($"[ProfileManager] 已删除 Profile 目录: {profilePath}");
                cleaned = true;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ProfileManager] 删除 Profile 目录失败: {ex.Message}");
        }

        if (cleaned)
        {
            AuditLogger.Write("DELETE_PROFILE", username, AuditLogger.Result.Success,
                $"手动清理: SID={sid}, 路径={profilePath}");
            return profilePath;
        }

        AuditLogger.Write("DELETE_PROFILE", username, AuditLogger.Result.Failed,
            $"手动清理未找到残留: SID={sid}");
        return null;
    }

    /// <summary>从 ProfileList 注册表读取指定 SID 的 ProfileImagePath。</summary>
    private static string? GetProfilePathFromRegistry(string sid)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                Path.Combine(LabConfig.ProfileListRegPath, sid));
            return key?.GetValue("ProfileImagePath") as string;
        }
        catch { return null; }
    }

    /// <summary>遍历 ProfileList 注册表，查找 ProfileImagePath 以 \username 结尾的 SID。</summary>
    private static string? FindSidByUsername(string username)
    {
        try
        {
            using var profileList = Registry.LocalMachine.OpenSubKey(LabConfig.ProfileListRegPath);
            if (profileList == null) return null;

            foreach (var subSid in profileList.GetSubKeyNames())
            {
                using var subKey = profileList.OpenSubKey(subSid);
                var path = subKey?.GetValue("ProfileImagePath") as string;
                if (path != null && path.EndsWith("\\" + username, StringComparison.OrdinalIgnoreCase))
                    return subSid;
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 构造标准 Profile 目录路径。优先使用系统 ProfilesDirectory 注册表值，
    /// 回退到 LabConfig.DesiredProfilesDirectory（D:\Users）。
    /// </summary>
    private static string GetExpectedProfilePath(string username)
    {
        // 读取系统 ProfilesDirectory 注册表值
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(LabConfig.ProfileListRegPath);
            var dir = key?.GetValue(LabConfig.ProfilesDirValueName) as string;
            if (!string.IsNullOrEmpty(dir))
                return Path.Combine(dir, username);
        }
        catch { }

        // 回退到 LabConfig 配置（D:\Users）
        return Path.Combine(LabConfig.DesiredProfilesDirectory, username);
    }
}
