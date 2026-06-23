using System.DirectoryServices.AccountManagement;
using System.Runtime.Versioning;
using LabWorkstation.Common.Configuration;
using LabWorkstation.Common.Mock;

namespace LabWorkstation.Common.LocalAccounts;

/// <summary>
/// 本地用户账户操作。统一使用 System.DirectoryServices.AccountManagement，
/// 替代原 PS 中混用的 net user / Get-LocalUser / Set-LocalUser。
/// 需管理员权限执行写操作。
/// 测试模式（LabConfig.TestMode）下所有操作仅作用于内存模拟状态。
/// </summary>
[SupportedOSPlatform("windows")]
public static class AccountManager
{
    private static PrincipalContext CreateContext() =>
        new(ContextType.Machine, Environment.MachineName);

    /// <summary>用户是否存在。</summary>
    public static bool UserExists(string username) =>
        LabConfig.TestMode ? MockState.UserExists(username) : UserExistsReal(username);

    private static bool UserExistsReal(string username)
    {
        using var ctx = CreateContext();
        using var up = UserPrincipal.FindByIdentity(ctx, IdentityType.SamAccountName, username);
        return up != null;
    }

    /// <summary>获取用户主体（调用方负责 Dispose）。测试模式下返回 null（仅供内部使用）。</summary>
    public static UserPrincipal? FindUser(string username)
    {
        var ctx = CreateContext();
        var up = UserPrincipal.FindByIdentity(ctx, IdentityType.SamAccountName, username);
        if (up == null) ctx.Dispose();
        return up;
    }

    public sealed record LocalUserInfo(
        string Username,
        string DisplayName,
        bool Enabled,
        DateTime? LastLogon);

    /// <summary>获取用户摘要信息。</summary>
    public static LocalUserInfo? GetUserInfo(string username) =>
        LabConfig.TestMode ? MockState.GetUserInfo(username) : GetUserInfoReal(username);

    private static LocalUserInfo? GetUserInfoReal(string username)
    {
        using var up = FindUser(username);
        if (up == null) return null;
        return new LocalUserInfo(
            up.SamAccountName ?? username,
            up.Description ?? up.DisplayName ?? "",
            up.Enabled ?? true,
            up.LastLogon);
    }

    /// <summary>创建本地用户。密码永不过期。</summary>
    public static void CreateUser(string username, string password, string displayName, string description)
    {
        if (LabConfig.TestMode) { MockState.CreateUser(username, password, displayName, description); return; }
        using var ctx = CreateContext();
        using var up = new UserPrincipal(ctx, username, password, enabled: true)
        {
            DisplayName = displayName,
            Description = description
        };
        up.PasswordNeverExpires = true;
        up.UserCannotChangePassword = false;
        up.Save();
    }

    /// <summary>禁用账户。</summary>
    public static void DisableUser(string username)
    {
        if (LabConfig.TestMode) { MockState.SetEnabled(username, false); return; }
        using var up = FindUser(username) ?? throw new LabOperationException("DISABLE_USER", username, $"用户 '{username}' 不存在");
        up.Enabled = false;
        up.Save();
    }

    /// <summary>启用账户。</summary>
    public static void EnableUser(string username)
    {
        if (LabConfig.TestMode) { MockState.SetEnabled(username, true); return; }
        using var up = FindUser(username) ?? throw new LabOperationException("ENABLE_USER", username, $"用户 '{username}' 不存在");
        up.Enabled = true;
        up.Save();
    }

    /// <summary>重置密码（管理员）。</summary>
    public static void ResetPassword(string username, string newPassword)
    {
        if (LabConfig.TestMode) { MockState.SetPassword(username, newPassword); return; }
        using var up = FindUser(username) ?? throw new LabOperationException("RESET_PASSWORD", username, $"用户 '{username}' 不存在");
        up.SetPassword(newPassword);
        up.Save();
    }

    /// <summary>修改自己的密码（需提供旧密码）。</summary>
    public static void ChangePassword(string username, string oldPassword, string newPassword)
    {
        if (LabConfig.TestMode) { MockState.SetPassword(username, newPassword); return; }
        using var up = FindUser(username) ?? throw new LabOperationException("CHANGE_PASSWORD", username, $"用户 '{username}' 不存在");
        up.ChangePassword(oldPassword, newPassword);
        up.Save();
    }

    /// <summary>枚举所有本地用户名。</summary>
    public static List<string> GetAllUsernames() =>
        LabConfig.TestMode ? MockState.GetAllUsernames() : GetAllUsernamesReal();

    private static List<string> GetAllUsernamesReal()
    {
        var result = new List<string>();
        using var ctx = CreateContext();
        using var searcher = new PrincipalSearcher(new UserPrincipal(ctx) { });
        foreach (var found in searcher.FindAll())
        {
            if (found is UserPrincipal up && up.SamAccountName != null)
                result.Add(up.SamAccountName);
            found.Dispose();
        }
        return result;
    }

    /// <summary>获取当前登录用户的短名（DOMAIN\user → user）。测试模式返回模拟用户。</summary>
    public static string GetCurrentShortUserName() =>
        LabConfig.TestMode ? MockState.GetCurrentUserName() : GetCurrentShortUserNameReal();

    private static string GetCurrentShortUserNameReal()
    {
        var full = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        var idx = full.IndexOf('\\');
        return idx >= 0 ? full[(idx + 1)..] : full;
    }
}
