using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using LabWorkstation.Common.Configuration;

namespace LabWorkstation.Common.Desktop;

/// <summary>
/// 保护公共桌面只读。对应原 PS 第 5 段 Set-Acl 逻辑。
/// 断开继承；Administrators/SYSTEM 完全控制；Users 仅读取与执行，
/// 使标准用户无法删除/修改桌面项目（快捷方式、须知文件等）。
/// 参考 <see cref="LocalAccounts.NtfsAclHelper.BuildIsolatedAcl"/> 的风格，但此处保留 Users 的读取执行。
/// 测试模式（LabConfig.TestMode）下跳过。
/// </summary>
[SupportedOSPlatform("windows")]
public static class DesktopProtector
{
    /// <summary>
    /// 将公共桌面目录设为只读：断开继承，Administrators/SYSTEM 完全控制，Users 读取与执行。
    /// </summary>
    public static void ProtectCommonDesktop()
    {
        if (LabConfig.TestMode)
        {
            Console.WriteLine("[测试模式] 跳过保护公共桌面（只读 ACL）");
            return;
        }

        var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        var acl = new DirectorySecurity();

        // 断开继承，不保留拷贝（与原 PS SetAccessRuleProtection($true, $false) 一致）
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        AddAllow(acl, "Administrators", FileSystemRights.FullControl);
        AddAllow(acl, @"NT AUTHORITY\SYSTEM", FileSystemRights.FullControl);
        AddAllow(acl, "Users", FileSystemRights.ReadAndExecute);

        new DirectoryInfo(desktopPath).SetAccessControl(acl);
    }

    /// <summary>添加一条 Allow 规则（容器与对象继承，无传播）。</summary>
    private static void AddAllow(DirectorySecurity acl, string identity, FileSystemRights rights)
    {
        var account = new NTAccount(identity);
        var rule = new FileSystemAccessRule(
            account, rights,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow);
        acl.AddAccessRule(rule);
    }
}
