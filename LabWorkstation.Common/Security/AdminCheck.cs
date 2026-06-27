using System.Runtime.Versioning;
using System.Security.Principal;

namespace LabWorkstation.Common.Security;

/// <summary>
/// 当前用户管理员权限检测。用于限制 Admin 面板和 TrayApp 退出功能仅管理员可用。
/// </summary>
[SupportedOSPlatform("windows")]
public static class AdminCheck
{
    /// <summary>
    /// 检测当前进程的用户是否属于 Administrators 组。
    /// 使用 WindowsIdentity + WindowsPrincipal，无需 P/Invoke。
    /// </summary>
    public static bool IsCurrentUserAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
