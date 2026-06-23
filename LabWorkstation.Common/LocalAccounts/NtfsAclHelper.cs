using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using LabWorkstation.Common.Configuration;

namespace LabWorkstation.Common.LocalAccounts;

/// <summary>
/// NTFS 权限设置与目录初始化。对应原 PS 的 New-AdvisorGroup / New-UserPersonalDir
/// 中的 ACL 部分（断开继承、仅本组/本人与管理员可访问）。
/// </summary>
[SupportedOSPlatform("windows")]
public static class NtfsAclHelper
{
    /// <summary>
    /// 创建导师文件夹及分类子目录，并设置 NTFS 权限：
    /// 断开继承；Administrators/SYSTEM 完全控制；导师组 Modify。
    /// 递归应用到所有子文件夹。
    /// </summary>
    public static void CreateAdvisorFolder(string advisorName)
    {
        if (LabConfig.TestMode) { Console.WriteLine($"[测试模式] 跳过创建导师文件夹: {advisorName}"); return; }
        var advisorPath = Path.Combine(LabConfig.SharePath, advisorName);
        Directory.CreateDirectory(advisorPath);

        foreach (var cat in LabConfig.GroupCategories)
            Directory.CreateDirectory(Path.Combine(advisorPath, cat));

        var groupName = LabConfig.AdvisorToGroupName(advisorName);
        var acl = BuildIsolatedAcl(new[]
        {
            ("Administrators", FileSystemRights.FullControl),
            (@"NT AUTHORITY\SYSTEM", FileSystemRights.FullControl),
            (groupName, FileSystemRights.Modify)
        });

        ApplyAclRecursive(advisorPath, acl);
    }

    /// <summary>
    /// 创建用户个人目录并隔离：断开继承；Administrators/SYSTEM 完全控制；
    /// 本人 FullControl。对应原 PS 的 New-UserPersonalDir。
    /// </summary>
    public static string CreateUserPersonalDir(string username)
    {
        var userDir = Path.Combine(LabConfig.UsersRootPath, username);
        if (LabConfig.TestMode) { Console.WriteLine($"[测试模式] 跳过创建个人目录: {userDir}"); return userDir; }
        Directory.CreateDirectory(LabConfig.UsersRootPath);
        if (Directory.Exists(userDir)) return userDir;

        Directory.CreateDirectory(userDir);
        var acl = BuildIsolatedAcl(new[]
        {
            ("Administrators", FileSystemRights.FullControl),
            (@"NT AUTHORITY\SYSTEM", FileSystemRights.FullControl),
            (username, FileSystemRights.FullControl)
        });
        ApplyAclRecursive(userDir, acl);
        return userDir;
    }

    /// <summary>构造断开继承的 ACL，仅包含指定规则。</summary>
    private static DirectorySecurity BuildIsolatedAcl(
        IEnumerable<(string Identity, FileSystemRights Rights)> rules)
    {
        var acl = new DirectorySecurity();
        // 断开继承，不保留拷贝
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        foreach (var (identity, rights) in rules)
        {
            var account = new NTAccount(identity);
            var rule = new FileSystemAccessRule(
                account, rights,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow);
            acl.AddAccessRule(rule);
        }
        return acl;
    }

    private static void ApplyAclRecursive(string path, DirectorySecurity acl)
    {
        new DirectoryInfo(path).SetAccessControl(acl);
        foreach (var dir in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
            new DirectoryInfo(dir).SetAccessControl(acl);
    }
}
