using System.Collections.Generic;
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
    /// 若目录已存在（Profile 已由 EnsureProfile 创建），仅设置 ACL 不重建。
    /// </summary>
    public static string CreateUserPersonalDir(string username)
    {
        var userDir = Path.Combine(LabConfig.UsersRootPath, username);
        if (LabConfig.TestMode) { Console.WriteLine($"[测试模式] 跳过创建个人目录: {userDir}"); return userDir; }
        Directory.CreateDirectory(LabConfig.UsersRootPath);

        // 目录可能已由 ProfileManager.EnsureProfile 创建（这是正确的行为，
        // Profile 目录和个人数据目录应合二为一），此时仅设置 ACL
        if (!Directory.Exists(userDir))
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

    /// <summary>
    /// 递归应用 ACL，跳过符号链接和 junction（如 Profile 中的 Application Data），
    /// 避免无限递归。仅处理真实目录。
    /// </summary>
    private static void ApplyAclRecursive(string path, DirectorySecurity acl)
    {
        new DirectoryInfo(path).SetAccessControl(acl);

        // 手动递归，跳过 ReparsePoint（符号链接/junction）
        var stack = new Stack<string>();
        stack.Push(path);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            string[] subDirs;
            try
            {
                subDirs = Directory.GetDirectories(current, "*", SearchOption.TopDirectoryOnly);
            }
            catch { continue; }

            foreach (var dir in subDirs)
            {
                // 跳过符号链接和 junction（ReparsePoint），防止无限递归
                var attrs = File.GetAttributes(dir);
                if ((attrs & FileAttributes.ReparsePoint) != 0)
                    continue;

                new DirectoryInfo(dir).SetAccessControl(acl);
                stack.Push(dir);
            }
        }
    }

    /// <summary>初始化数据区根目录 ACL：Administrators/SYSTEM 完全控制，Lab_All 仅可浏览（Traverse+ReadAttributes）。</summary>
    public static void InitShareRootAcl()
    {
        if (LabConfig.TestMode) { Console.WriteLine("[测试模式] 跳过初始化根目录 ACL"); return; }
        Directory.CreateDirectory(LabConfig.SharePath);
        var acl = BuildIsolatedAcl(new[]
        {
            ("Administrators", FileSystemRights.FullControl),
            (@"NT AUTHORITY\SYSTEM", FileSystemRights.FullControl),
            (LabConfig.AllGroup, FileSystemRights.Traverse | FileSystemRights.ReadAttributes)
        });
        new DirectoryInfo(LabConfig.SharePath).SetAccessControl(acl);
    }

    /// <summary>初始化公共区 ACL（递归）：Administrators/SYSTEM 完全控制，Lab_All 可修改。</summary>
    public static void InitPublicAreaAcl()
    {
        if (LabConfig.TestMode) { Console.WriteLine("[测试模式] 跳过初始化公共区 ACL"); return; }
        Directory.CreateDirectory(LabConfig.PublicPath);
        var acl = BuildIsolatedAcl(new[]
        {
            ("Administrators", FileSystemRights.FullControl),
            (@"NT AUTHORITY\SYSTEM", FileSystemRights.FullControl),
            (LabConfig.AllGroup, FileSystemRights.Modify)
        });
        ApplyAclRecursive(LabConfig.PublicPath, acl);

        // 公共区 ACL 应用后，立即收紧 _notifications 子目录：
        // 通知文件由 Admin 写入/删除，普通用户只读，防止篡改或误删
        SecureNotificationsDir();
    }

    /// <summary>
    /// 收紧 _notifications 目录 ACL：断开继承；Administrators/SYSTEM 完全控制；Lab_All 只读。
    /// 通知文件由 Admin 写入/删除（Send/Archive/Delete），普通用户通过 TrayApp 只读轮询。
    /// 防止普通用户编辑或删除通知文件（可能导致其他人看不到通知或弹窗异常）。
    /// </summary>
    public static void SecureNotificationsDir()
    {
        if (LabConfig.TestMode) { Console.WriteLine("[测试模式] 跳过收紧 _notifications 权限"); return; }

        var pendingDir = LabConfig.NotifyPendingPath;
        var sentDir = LabConfig.NotifySentPath;
        Directory.CreateDirectory(pendingDir);
        Directory.CreateDirectory(sentDir);

        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddIsolatedRule(acl, "Administrators", FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);
        AddIsolatedRule(acl, @"NT AUTHORITY\SYSTEM", FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);
        AddIsolatedRule(acl, LabConfig.AllGroup,
            FileSystemRights.Read | FileSystemRights.ReadAttributes | FileSystemRights.ReadExtendedAttributes | FileSystemRights.Traverse,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);

        new DirectoryInfo(pendingDir).SetAccessControl(acl);
        new DirectoryInfo(sentDir).SetAccessControl(acl);
        Console.WriteLine($"[NtfsAclHelper] _notifications 权限已收紧（Administrators/SYSTEM 完全 + {LabConfig.AllGroup} 只读）");
    }

    /// <summary>初始化个人目录根 ACL：Administrators/SYSTEM 完全控制，Lab_All 仅可浏览。</summary>
    public static void InitUsersRootAcl()
    {
        if (LabConfig.TestMode) { Console.WriteLine("[测试模式] 跳过初始化个人目录根 ACL"); return; }
        Directory.CreateDirectory(LabConfig.UsersRootPath);
        var acl = BuildIsolatedAcl(new[]
        {
            ("Administrators", FileSystemRights.FullControl),
            (@"NT AUTHORITY\SYSTEM", FileSystemRights.FullControl),
            (LabConfig.AllGroup, FileSystemRights.Traverse | FileSystemRights.ReadAttributes)
        });
        new DirectoryInfo(LabConfig.UsersRootPath).SetAccessControl(acl);
    }

    /// <summary>创建数据区完整目录骨架（根/公共/通知/归档/Users）。若根目录已有内容则退避跳过。</summary>
    public static void CreateDirectorySkeleton()
    {
        if (LabConfig.TestMode) { Console.WriteLine("[测试模式] 跳过创建目录骨架"); return; }

        // 确保 ConfigDir（C:\ProgramData\LabWorkstation）存在且权限收紧
        SecureConfigDir();

        // 收紧 logs/ 目录权限（Administrators/SYSTEM 完全控制 + Lab_All 只读）
        SecureLogsDir();

        // 迁移旧 ConfigDir 数据（D:\GroupData\_公共\_config → C:\ProgramData\LabWorkstation）
        MigrateOldConfigDir();

        // 迁移旧日志文件（D:\GroupData\_公共\_使用手册\*.log → ConfigDir\logs\）
        MigrateLegacyLogs();

        // 退避检查：根目录已有内容则不动
        if (Directory.Exists(LabConfig.SharePath) && Directory.EnumerateFileSystemEntries(LabConfig.SharePath).Any())
        {
            Console.WriteLine($"[退避] {LabConfig.SharePath} 已有内容，跳过目录骨架创建");
            return;
        }

        Directory.CreateDirectory(LabConfig.SharePath);
        Directory.CreateDirectory(LabConfig.PublicPath);
        Directory.CreateDirectory(Path.Combine(LabConfig.PublicPath, "_使用手册"));
        Directory.CreateDirectory(Path.Combine(LabConfig.PublicPath, "跨组共享数据"));
        var toolsDir = Path.Combine(LabConfig.PublicPath, "工具与模板");
        Directory.CreateDirectory(toolsDir);
        foreach (var sub in new[] { "报告模板", "数据清洗脚本", "可视化工具" })
            Directory.CreateDirectory(Path.Combine(toolsDir, sub));
        Directory.CreateDirectory(Path.Combine(LabConfig.PublicPath, "_notifications", "pending"));
        Directory.CreateDirectory(Path.Combine(LabConfig.PublicPath, "_notifications", "sent"));
        Directory.CreateDirectory(Path.Combine(LabConfig.PublicPath, "99_归档"));
        Directory.CreateDirectory(LabConfig.UsersRootPath);
    }

    /// <summary>
    /// 收紧硬盘根目录权限：
    /// - D:\ 根目录：将 Authenticated Users 的 Modify 降为 ReadAndExecute，
    ///   防止普通用户在 D:\ 根目录创建/修改文件（只允许浏览）。
    ///   子目录（GroupData/Users）有各自的隔离 ACL，不受影响。
    /// - C:\ 根目录：仅移除非内置账户的直接 ACE 残留（如历史遗留的 FullControl），
    ///   不动 Windows 默认继承权限（保护 Program Files 等目录的继承链）。
    /// </summary>
    public static void HardenDriveRoots()
    {
        if (LabConfig.TestMode) { Console.WriteLine("[测试模式] 跳过硬盘根目录权限收紧"); return; }

        // Windows 内置安全主体
        var builtinIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"NT AUTHORITY\SYSTEM",
            @"NT AUTHORITY\Authenticated Users",
            @"BUILTIN\Administrators",
            @"BUILTIN\Users",
            @"BUILTIN\Backup Operators",
            @"NT SERVICE\TrustedInstaller",
            @"CREATOR OWNER",
            @"Everyone"
        };

        // ── C:\ 仅清理非内置账户残留 ──
        HardenDriveRoot(@"C:\", builtinIdentities, replaceAuthenticatedUsers: false);

        // ── D:\ 清理残留 + 将 Authenticated Users 降为 RX ──
        HardenDriveRoot(@"D:\", builtinIdentities, replaceAuthenticatedUsers: true);
    }

    private static void HardenDriveRoot(string drive, HashSet<string> builtinIdentities, bool replaceAuthenticatedUsers)
    {
        if (!Directory.Exists(drive)) return;
        try
        {
            var di = new DirectoryInfo(drive);
            var acl = di.GetAccessControl();
            var rules = acl.GetAccessRules(true, false, typeof(NTAccount));
            var toRemove = new List<FileSystemAccessRule>();

            foreach (FileSystemAccessRule rule in rules)
            {
                var identity = rule.IdentityReference.Value;

                // 移除非内置账户的直接权限（如 rqy、renqiaoyang 等残留）
                if (!builtinIdentities.Contains(identity))
                {
                    toRemove.Add(rule);
                    Console.WriteLine($"[NtfsAclHelper] {drive} 移除非内置账户 {identity} 的 {rule.FileSystemRights}");
                    continue;
                }

                // D:\ 上 Authenticated Users 的写权限需要降级
                if (replaceAuthenticatedUsers &&
                    identity.EndsWith("Authenticated Users", StringComparison.OrdinalIgnoreCase) &&
                    rule.AccessControlType == AccessControlType.Allow &&
                    (rule.FileSystemRights & (FileSystemRights.Modify | FileSystemRights.Write | FileSystemRights.FullControl)) != 0)
                {
                    toRemove.Add(rule);
                    Console.WriteLine($"[NtfsAclHelper] {drive} 移除 Authenticated Users 的 {rule.FileSystemRights}（将替换为 RX）");
                }
            }

            foreach (var rule in toRemove)
                acl.RemoveAccessRule(rule);

            // D:\ 重新添加 Authenticated Users 为 RX
            if (replaceAuthenticatedUsers)
            {
                var rxeRule = new FileSystemAccessRule(
                    new NTAccount(@"NT AUTHORITY\Authenticated Users"),
                    FileSystemRights.ReadAndExecute,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow);
                acl.SetAccessRule(rxeRule);
                Console.WriteLine($"[NtfsAclHelper] {drive} 已添加 Authenticated Users:(RX)");
            }

            if (toRemove.Count > 0 || replaceAuthenticatedUsers)
            {
                di.SetAccessControl(acl);
                Console.WriteLine($"[NtfsAclHelper] {drive} 权限已收紧");
            }
            else
            {
                Console.WriteLine($"[NtfsAclHelper] {drive} 无需修改");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NtfsAclHelper] 收紧 {drive} 权限失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 确保 ConfigDir（C:\ProgramData\LabWorkstation）权限收紧：
    /// 断开继承；Administrators/SYSTEM 完全控制；Authenticated Users 仅 Traverse（仅容器继承）。
    /// Traverse 允许普通用户穿越 ConfigDir 进入子目录（如 logs/、kiosk_queue/），
    /// 但不授予 ListDirectory/ReadData，无法列出或读取 ConfigDir 根下的文件（users.json 等）。
    /// 子目录各自有独立 ACL 控制实际访问权限。
    /// </summary>
    public static void SecureConfigDir()
    {
        if (LabConfig.TestMode) { Console.WriteLine("[测试模式] 跳过收紧 ConfigDir 权限"); return; }

        var dir = LabConfig.ConfigDir;
        Directory.CreateDirectory(dir);

        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        // Administrators / SYSTEM：完全控制，容器+对象继承
        AddIsolatedRule(acl, "Administrators", FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);
        AddIsolatedRule(acl, @"NT AUTHORITY\SYSTEM", FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);
        // Authenticated Users：仅 Traverse，仅容器继承（不继承到文件）
        // 这样普通用户能进入子目录但不能读取 ConfigDir 根的文件
        AddIsolatedRule(acl, @"NT AUTHORITY\Authenticated Users", FileSystemRights.Traverse,
            InheritanceFlags.ContainerInherit);

        new DirectoryInfo(dir).SetAccessControl(acl);
        Console.WriteLine($"[NtfsAclHelper] ConfigDir 权限已收紧: {dir}（Administrators/SYSTEM 完全 + Authenticated Users Traverse）");
    }

    /// <summary>
    /// 收紧 logs/ 目录权限：Administrators/SYSTEM 完全控制；Lab_All 只读。
    /// 供审计日志与监控日志使用——用户在 TrayApp 自助弹窗中读取自身审计行需要读权限，
    /// 但不能修改/删除（防篡改）。
    /// </summary>
    public static void SecureLogsDir()
    {
        if (LabConfig.TestMode) { Console.WriteLine("[测试模式] 跳过收紧 logs 目录权限"); return; }

        var dir = LabConfig.LogsDir;
        Directory.CreateDirectory(dir);

        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddIsolatedRule(acl, "Administrators", FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);
        AddIsolatedRule(acl, @"NT AUTHORITY\SYSTEM", FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);
        AddIsolatedRule(acl, LabConfig.AllGroup,
            FileSystemRights.Read | FileSystemRights.ReadAttributes | FileSystemRights.ReadExtendedAttributes,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);

        new DirectoryInfo(dir).SetAccessControl(acl);
        Console.WriteLine($"[NtfsAclHelper] logs 目录权限已设置: {dir}（Administrators/SYSTEM 完全 + {LabConfig.AllGroup} 只读）");
    }

    /// <summary>
    /// 收紧 kiosk_queue/ 及其 requests/、responses/ 子目录权限。
    /// 队列迁出公共区后，仅 kiosk 账户与 Administrators/SYSTEM 可访问：
    /// - kiosk_queue/：kiosk Traverse（仅容器继承），允许穿越进入子目录
    /// - requests/：kiosk Write（可创建/写入请求文件，不可读取他人请求），Admins/SYSTEM 完全
    /// - responses/：kiosk Read（可读取响应文件，不可创建/删除），Admins/SYSTEM 完全
    /// 必须在 kiosk 账户已创建后调用。
    /// </summary>
    /// <param name="kioskUsername">kiosk 账户名（用于授权）。</param>
    public static void SecureKioskQueueDir(string kioskUsername)
    {
        if (LabConfig.TestMode) { Console.WriteLine($"[测试模式] 跳过收紧 kiosk_queue 权限"); return; }

        var queueDir = LabConfig.KioskQueuePath;
        var reqDir = LabConfig.KioskRequestsPath;
        var respDir = LabConfig.KioskResponsesPath;
        Directory.CreateDirectory(queueDir);
        Directory.CreateDirectory(reqDir);
        Directory.CreateDirectory(respDir);

        // kiosk_queue/：Admins/SYSTEM 完全 + kiosk Traverse（容器继承）
        var queueAcl = new DirectorySecurity();
        queueAcl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddIsolatedRule(queueAcl, "Administrators", FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);
        AddIsolatedRule(queueAcl, @"NT AUTHORITY\SYSTEM", FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);
        AddIsolatedRule(queueAcl, kioskUsername, FileSystemRights.Traverse,
            InheritanceFlags.ContainerInherit);
        new DirectoryInfo(queueDir).SetAccessControl(queueAcl);

        // requests/：Admins/SYSTEM 完全 + kiosk Write（CreateFiles+WriteData，不含 ReadData/ListDirectory）
        var reqAcl = new DirectorySecurity();
        reqAcl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddIsolatedRule(reqAcl, "Administrators", FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);
        AddIsolatedRule(reqAcl, @"NT AUTHORITY\SYSTEM", FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);
        AddIsolatedRule(reqAcl, kioskUsername,
            FileSystemRights.Write | FileSystemRights.ReadAttributes,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);
        new DirectoryInfo(reqDir).SetAccessControl(reqAcl);

        // responses/：Admins/SYSTEM 完全 + kiosk Read
        var respAcl = new DirectorySecurity();
        respAcl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddIsolatedRule(respAcl, "Administrators", FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);
        AddIsolatedRule(respAcl, @"NT AUTHORITY\SYSTEM", FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);
        AddIsolatedRule(respAcl, kioskUsername,
            FileSystemRights.Read | FileSystemRights.ReadAttributes | FileSystemRights.ReadExtendedAttributes,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);
        new DirectoryInfo(respDir).SetAccessControl(respAcl);

        Console.WriteLine($"[NtfsAclHelper] kiosk_queue 权限已设置: {queueDir}（kiosk={kioskUsername}）");
    }

    /// <summary>
    /// 收紧 kiosk_announcements/ 目录权限：Administrators/SYSTEM 完全控制，kiosk 只读。
    /// Admin 通过 AnnouncementStore 写入公告，Kiosk 只读轮询展示。
    /// 必须在 kiosk 账户已创建后调用。
    /// </summary>
    /// <param name="kioskUsername">kiosk 账户名。</param>
    public static void SecureKioskAnnouncementsDir(string kioskUsername)
    {
        if (LabConfig.TestMode) { Console.WriteLine($"[测试模式] 跳过收紧 kiosk_announcements 权限"); return; }

        var dir = LabConfig.KioskAnnouncementsPath;
        Directory.CreateDirectory(dir);

        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddIsolatedRule(acl, "Administrators", FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);
        AddIsolatedRule(acl, @"NT AUTHORITY\SYSTEM", FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);
        AddIsolatedRule(acl, kioskUsername,
            FileSystemRights.Read | FileSystemRights.ReadAttributes | FileSystemRights.ReadExtendedAttributes | FileSystemRights.Traverse,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);

        new DirectoryInfo(dir).SetAccessControl(acl);
        Console.WriteLine($"[NtfsAclHelper] kiosk_announcements 权限已设置: {dir}（kiosk={kioskUsername} 只读）");
    }

    /// <summary>添加一条 Allow 规则，自定义继承标志。</summary>
    private static void AddIsolatedRule(DirectorySecurity acl, string identity,
        FileSystemRights rights, InheritanceFlags inheritance)
    {
        var account = new NTAccount(identity);
        var rule = new FileSystemAccessRule(
            account, rights, inheritance,
            PropagationFlags.None, AccessControlType.Allow);
        acl.AddAccessRule(rule);
    }

    /// <summary>
    /// 迁移旧 ConfigDir（D:\GroupData\_公共\_config）中的 JSON 文件到新位置
    /// （C:\ProgramData\LabWorkstation）。仅迁移 .json 文件，不迁移 kiosk_queue。
    /// </summary>
    private static void MigrateOldConfigDir()
    {
        var oldDir = global::System.IO.Path.Combine(LabConfig.PublicPath, "_config");
        if (!Directory.Exists(oldDir)) return;

        var newDir = LabConfig.ConfigDir;
        var moved = 0;

        foreach (var file in Directory.EnumerateFiles(oldDir, "*.json"))
        {
            var fileName = global::System.IO.Path.GetFileName(file);
            var destPath = global::System.IO.Path.Combine(newDir, fileName);
            if (!File.Exists(destPath))
            {
                try
                {
                    File.Copy(file, destPath, overwrite: false);
                    moved++;
                }
                catch { /* 跳过复制失败 */ }
            }
        }

        if (moved > 0)
            Console.WriteLine($"[NtfsAclHelper] 已迁移 {moved} 个配置文件从 {oldDir} 到 {newDir}");
    }

    /// <summary>
    /// 迁移旧日志文件到 ConfigDir\logs\：
    /// - D:\GroupData\_公共\_使用手册\admin_operations.log → ConfigDir\logs\admin_operations.log
    /// - D:\GroupData\_公共\_使用手册\system_monitor.log → ConfigDir\logs\system_monitor.log
    /// 仅在目标文件不存在时迁移；迁移成功后删除源文件。
    /// </summary>
    private static void MigrateLegacyLogs()
    {
        var pairs = new[]
        {
            (LabConfig.LegacyAuditLogPath, LabConfig.AuditLogPath),
            (LabConfig.LegacyMonitorLogPath, LabConfig.MonitorLogPath)
        };

        foreach (var (legacy, current) in pairs)
        {
            if (string.IsNullOrEmpty(legacy) || !File.Exists(legacy)) continue;

            Directory.CreateDirectory(LabConfig.LogsDir);
            if (!File.Exists(current))
            {
                try
                {
                    File.Copy(legacy, current, overwrite: false);
                    Console.WriteLine($"[NtfsAclHelper] 已迁移日志文件 {legacy} → {current}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[NtfsAclHelper] 迁移日志 {legacy} 失败: {ex.Message}");
                    continue;
                }
            }

            // 迁移成功后删除源文件（避免旧日志残留可被普通用户篡改）
            try { File.Delete(legacy); }
            catch { /* 删除失败不影响主流程 */ }
        }
    }
}
