using System.Diagnostics;
using System.IO;
using LabWorkstation.Common.Configuration;
using LabWorkstation.Common.LocalAccounts;
using LabWorkstation.Common.Native;
using Microsoft.Win32;

namespace LabWorkstation.Common.System;

/// <summary>
/// Kiosk 自助开户系统部署器。
/// 1. 将 Kiosk 应用复制到 C:\Scripts\LabWorkstation.Kiosk
/// 2. 创建 kiosk 账户（随机强密码，无桌面访问权限，仅运行 Kiosk 应用）
/// 3. 设置自定义 Shell：kiosk 用户登录后只启动 Kiosk 应用
/// 4. 配置自动登录：开机后自动以 kiosk 身份登录
/// 5. 收紧 kiosk_queue/ 目录 ACL：按子目录细粒度授权
/// 安全说明：原版本硬编码密码 "Kiosk@2026!"，本版本改为随机强密码。
/// DefaultPassword 注册表值仍为明文（Windows 自动登录机制要求），但 kiosk
/// 账户受限 Shell 提供补偿控制：即使密码泄露，攻击者只能进入 Kiosk 应用环境。
/// </summary>
public static class KioskDeployer
{
    private const string KioskUsername = "kiosk";

    /// <summary>部署 Kiosk 系统：复制文件 + 创建账户 + 配置自定义 Shell + 自动登录 + 收紧 ACL。</summary>
    public static void Deploy()
    {
        if (LabConfig.TestMode)
        {
            Console.WriteLine("[测试模式] 跳过 Kiosk 部署");
            return;
        }

        // 1. 复制 Kiosk 应用
        DeployKioskApp();

        // 2. 创建 kiosk 账户（返回本次使用的密码，已存在账户返回 null 表示不修改密码）
        var kioskPassword = CreateKioskAccount();

        // 2.5 调试模式：将 kiosk 加入 Remote Desktop Users 组，允许 RDP 连接排查
        //     正式模式：主动从 Remote Desktop Users 组移除 kiosk，避免生产环境下可被 RDP（安全收紧）
        if (LabConfig.KioskDebugMode)
        {
            try
            {
                GroupManager.AddMember("Remote Desktop Users", KioskUsername);
                Console.WriteLine("[KioskDeployer] 调试模式：kiosk 已加入 Remote Desktop Users 组（可 RDP）");
            }
            catch (Exception ex) { Console.WriteLine($"[KioskDeployer] 加入 RDP 组失败（可忽略）: {ex.Message}"); }
        }
        else
        {
            try
            {
                GroupManager.RemoveMember("Remote Desktop Users", KioskUsername);
                Console.WriteLine("[KioskDeployer] 正式模式：kiosk 已从 Remote Desktop Users 组移除（禁止 RDP）");
            }
            catch (Exception ex) { Console.WriteLine($"[KioskDeployer] 移除 RDP 组失败（可忽略，可能本就不在组内）: {ex.Message}"); }
        }

        // 3. 配置 Shell：调试模式用 explorer.exe（RDP 可见桌面），生产模式用 Kiosk.exe
        var shellPath = LabConfig.KioskDebugMode
            ? @"explorer.exe"
            : Path.Combine(LabConfig.ScriptsDir, "LabWorkstation.Kiosk", "LabWorkstation.Kiosk.exe");
        ConfigureCustomShell(shellPath);
        if (LabConfig.KioskDebugMode)
            Console.WriteLine("[KioskDeployer] 调试模式：Shell 已设为 explorer.exe（RDP 可见桌面）");

        // 4. 配置自动登录（使用步骤 2 返回的密码；若账户已存在则用 null 跳过密码写入）
        ConfigureAutoLogon(kioskPassword);

        // 5. 收紧 kiosk_queue 目录 ACL（账户已创建，可授权）
        try
        {
            NtfsAclHelper.SecureKioskQueueDir(KioskUsername);
            Console.WriteLine("[KioskDeployer] kiosk_queue 目录 ACL 已收紧");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[KioskDeployer] 收紧 kiosk_queue ACL 失败: {ex.Message}");
        }

        // 6. 收紧 kiosk_announcements 目录 ACL（kiosk 只读，Admin 写入）
        try
        {
            NtfsAclHelper.SecureKioskAnnouncementsDir(KioskUsername);
            Console.WriteLine("[KioskDeployer] kiosk_announcements 目录 ACL 已收紧");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[KioskDeployer] 收紧 kiosk_announcements ACL 失败: {ex.Message}");
        }

        Console.WriteLine("[KioskDeployer] Kiosk 自助开户系统部署完成");
    }

    /// <summary>将 Kiosk 应用从构建产物目录复制到 C:\Scripts\LabWorkstation.Kiosk。</summary>
    private static void DeployKioskApp()
    {
        var targetDir = Path.Combine(LabConfig.ScriptsDir, "LabWorkstation.Kiosk");
        var sourceDir = BuildArtifactLocator.ResolveProjectBinDir("LabWorkstation.Kiosk");

        // 强制关闭正在运行的 Kiosk 进程，避免文件被占用导致复制失败
        KillRunningProcess("LabWorkstation.Kiosk");

        // 清理旧部署目录（进程已关闭，可安全删除）
        if (Directory.Exists(targetDir))
        {
            try { Directory.Delete(targetDir, recursive: true); }
            catch { /* 删除失败不阻断，后续 File.Copy 会覆盖 */ }
        }
        Directory.CreateDirectory(targetDir);

        // 复制所有文件
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relPath = Path.GetRelativePath(sourceDir, file);
            // 跳过 obj 目录
            if (relPath.StartsWith("obj", StringComparison.OrdinalIgnoreCase)) continue;
            var destPath = Path.Combine(targetDir, relPath);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.Copy(file, destPath, overwrite: true);
        }

        // 部署后验证：确认 Kiosk.exe 到位（Kiosk 不主动启动，下次登录 kiosk 账户时自启）
        var kioskExe = Path.Combine(targetDir, "LabWorkstation.Kiosk.exe");
        if (!File.Exists(kioskExe))
        {
            throw new LabOperationException("DEPLOY_KIOSK_VERIFY", kioskExe,
                detail: $"部署后未找到 {kioskExe}",
                message: "Kiosk 部署后验证失败：LabWorkstation.Kiosk.exe 未到位，请检查构建产物");
        }

        Console.WriteLine($"[KioskDeployer] Kiosk 应用已部署到 {targetDir}（exe 验证通过）");
    }

    /// <summary>
    /// 创建 kiosk 账户（禁用桌面，仅用于运行 Kiosk 应用）。
    /// 账户已存在则不修改密码（返回 null），首次创建则生成随机强密码（返回明文密码）。
    /// </summary>
    /// <returns>首次创建账户时返回生成的明文密码；账户已存在时返回 null。</returns>
    private static string? CreateKioskAccount()
    {
        try
        {
            if (AccountManager.UserExists(KioskUsername))
            {
                Console.WriteLine("[KioskDeployer] kiosk 账户已存在，跳过创建（保留原密码）");
                return null;
            }

            // 生成随机强密码（不硬编码常量）
            var password = LabAccountService.GenerateRandomPassword(length: 16);
            AccountManager.CreateUser(KioskUsername, password, "Kiosk 自助开户", "Kiosk 自助开户终端");
            Console.WriteLine("[KioskDeployer] kiosk 账户已创建（随机密码已设置）");
            return password;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[KioskDeployer] 创建 kiosk 账户失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 配置 kiosk 用户的 Shell：在 kiosk 用户的 NTUSER.DAT 中将 Shell 替换为指定路径。
    /// 生产模式传入 Kiosk.exe 路径（登录后只运行 Kiosk 应用）；
    /// 调试模式传入 explorer.exe（RDP 可见桌面，便于手动运行/排查 Kiosk 应用）。
    /// </summary>
    /// <param name="shellPath">Shell 可执行文件路径。</param>
    private static void ConfigureCustomShell(string shellPath)
    {
        try
        {
            // 确保 Profile 已生成
            ProfileManager.EnsureProfile(KioskUsername);

            // 从注册表读取实际 Profile 路径
            var sid = AccountManager.GetUserSid(KioskUsername);
            if (string.IsNullOrEmpty(sid))
            {
                Console.WriteLine("[KioskDeployer] 无法获取 kiosk SID，跳过 Shell 配置");
                return;
            }

            string? profilePath = null;
            using (var profileList = Registry.LocalMachine.OpenSubKey(LabConfig.ProfileListRegPath))
            {
                using var subKey = profileList?.OpenSubKey(sid);
                profilePath = subKey?.GetValue("ProfileImagePath") as string;
            }

            if (string.IsNullOrEmpty(profilePath))
            {
                Console.WriteLine("[KioskDeployer] 无法获取 kiosk Profile 路径");
                return;
            }

            var hivePath = Path.Combine(profilePath, "NTUSER.DAT");
            if (!File.Exists(hivePath))
            {
                Console.WriteLine($"[KioskDeployer] kiosk NTUSER.DAT 不存在: {hivePath}");
                return;
            }

            // 加载 hive 并设置 Shell
            Native.RegistryHiveLoader.UsingHive(hivePath, hiveKey =>
            {
                using var winlogon = hiveKey.CreateSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon");
                winlogon.SetValue("Shell", shellPath, RegistryValueKind.String);
                Console.WriteLine($"[KioskDeployer] kiosk Shell 已设置为: {shellPath}");
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[KioskDeployer] 配置自定义 Shell 失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 配置开机自动登录到 kiosk 账户。
    /// 仅在传入密码非空（首次创建账户）时写入 DefaultPassword 值。
    /// 注意：DefaultPassword 必须为明文（Windows 自动登录机制要求），且 HKLM\...\Winlogon
    /// 键的默认 ACL 允许所有本地用户读取。收紧该键 ACL 会破坏 Windows 登录流程
    /// （winlogon.exe、LogonUI、userinit 等系统进程需要读取 Shell/Userinit 等值）。
    /// 安全补偿：kiosk 账户使用随机强密码 + 受限 Shell（仅运行 Kiosk 应用），
    /// 即便密码泄露，攻击者登录 kiosk 也只能进入 Kiosk 应用环境，无法访问系统其他部分。
    /// 更彻底的方案需要使用 LSA 私有数据（LsaStorePrivateData）存储 DefaultPassword 秘密，
    /// 但实现复杂度较高，当前版本暂不采用。
    /// </summary>
    /// <param name="password">kiosk 账户密码（首次创建账户时传入；null 表示不修改密码值）。</param>
    private static void ConfigureAutoLogon(string? password)
    {
        const string winlogonPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(winlogonPath, writable: true);

            key!.SetValue("AutoAdminLogon", "1", RegistryValueKind.String);
            key.SetValue("DefaultUserName", KioskUsername, RegistryValueKind.String);
            if (!string.IsNullOrEmpty(password))
            {
                key.SetValue("DefaultPassword", password, RegistryValueKind.String);
            }
            key.SetValue("DefaultDomainName", ".", RegistryValueKind.String);

            Console.WriteLine("[KioskDeployer] 开机自动登录已配置（用户: kiosk）");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[KioskDeployer] 配置自动登录失败: {ex.Message}");
        }
    }

    /// <summary>取消自动登录（管理员恢复正常登录）。</summary>
    public static void DisableAutoLogon()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon",
                writable: true);

            key!.SetValue("AutoAdminLogon", "0", RegistryValueKind.String);
            key.DeleteValue("DefaultUserName", false);
            key.DeleteValue("DefaultPassword", false);

            Console.WriteLine("[KioskDeployer] 自动登录已取消");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[KioskDeployer] 取消自动登录失败: {ex.Message}");
        }
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
            Console.WriteLine($"[KioskDeployer] 已尝试关闭运行中的 {processName}.exe");
        }
        catch
        {
            // 进程不存在或关闭失败，忽略
        }
    }
}
