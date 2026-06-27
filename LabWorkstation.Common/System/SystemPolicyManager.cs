using System.Runtime.Versioning;
using LabWorkstation.Common.Configuration;
using Microsoft.Win32;

namespace LabWorkstation.Common.System;

/// <summary>
/// 系统策略注册表设置。对应原 PS Setup-Maintenance.ps1 中的策略段：
/// 远程桌面会话超时 / Windows Update / 禁止用户安装 / 磁盘配额。
/// 用 Microsoft.Win32.Registry，CreateSubKey 确保键存在再 SetValue。
/// 测试模式（LabConfig.TestMode）下跳过。
/// </summary>
[SupportedOSPlatform("windows")]
public static class SystemPolicyManager
{
    // ── 注册表路径常量（与原 PS 一致）──────────────────────────
    private const string TerminalServicesPath = @"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services";
    private const string WindowsUpdateAuPath = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU";
    private const string InstallerPath = @"SOFTWARE\Policies\Microsoft\Windows\Installer";
    private const string DiskQuotaPath = @"SOFTWARE\Policies\Microsoft\Windows NT\DiskQuota";

    // ── RDP 授权相关 ──────────────────────────────────────────
    private const string RcmLicensingCorePath = @"SYSTEM\CurrentControlSet\Control\Terminal Server\RCM\Licensing Core";
    private const string RcmGracePeriodPath = @"SYSTEM\CurrentControlSet\Control\Terminal Server\RCM\GracePeriod";
    private const string GracePeriodBombName = "L$RTMTIMEBOMB_1320153D-8DA3-4e8e-B27B-0D888223A588";

    /// <summary>
    /// 远程桌面会话超时策略：
    /// MaxDisconnectionTime=7200000（断开 2 小时后自动注销）；
    /// MaxIdleTime=14400000（空闲 4 小时后自动断开）；
    /// fSingleSessionPerUser=1（每用户仅 1 个活跃会话）。
    /// </summary>
    public static void SetRdpSessionTimeouts()
    {
        if (LabConfig.TestMode) { Console.WriteLine("[测试模式] 跳过 RDP 会话超时策略"); return; }

        using var key = Registry.LocalMachine.CreateSubKey(TerminalServicesPath);
        key.SetValue("MaxDisconnectionTime", 7200000, RegistryValueKind.DWord);
        key.SetValue("MaxIdleTime", 14400000, RegistryValueKind.DWord);
        key.SetValue("fSingleSessionPerUser", 1, RegistryValueKind.DWord);
    }

    /// <summary>
    /// 修复 RDP 授权问题（Windows Server）：
    /// 1. 将授权模式改为每用户（LicensingMode=4），避免每设备模式需要许可证；
    /// 2. 清除 GracePeriod 时间炸弹（宽限期过期会阻止 RDP 连接），需要取得注册表键所有权。
    /// 解决"没有远程桌面授权服务器提供许可证"错误。
    /// </summary>
    public static void FixRdpLicensing()
    {
        if (LabConfig.TestMode) { Console.WriteLine("[测试模式] 跳过 RDP 授权修复"); return; }

        // 1. 设置授权模式为每用户（4）
        using (var licKey = Registry.LocalMachine.CreateSubKey(RcmLicensingCorePath))
        {
            var current = (int?)licKey.GetValue("LicensingMode");
            if (current == 4)
            {
                Console.WriteLine("[SystemPolicyManager] RDP 授权模式已为每用户(4)，跳过");
            }
            else
            {
                licKey.SetValue("LicensingMode", 4, RegistryValueKind.DWord);
                Console.WriteLine("[SystemPolicyManager] RDP 授权模式已改为每用户(4)");
            }
        }

        // 2. 清除 GracePeriod 时间炸弹（需要取得所有权）
        try
        {
            ClearGracePeriodBomb();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SystemPolicyManager] 清除 GracePeriod 时间炸弹失败（可能已清除）: {ex.Message}");
        }
    }

    /// <summary>取得 GracePeriod 键所有权并删除时间炸弹值。</summary>
    private static void ClearGracePeriodBomb()
    {
        // 授予 Administrators 完全控制权限
        using var key = Registry.LocalMachine.OpenSubKey(RcmGracePeriodPath,
            RegistryKeyPermissionCheck.ReadWriteSubTree,
            global::System.Security.AccessControl.RegistryRights.ChangePermissions);
        if (key == null)
        {
            Console.WriteLine("[SystemPolicyManager] GracePeriod 键不存在，跳过");
            return;
        }

        var acl = key.GetAccessControl();
        var rule = new global::System.Security.AccessControl.RegistryAccessRule(
            "Administrators",
            global::System.Security.AccessControl.RegistryRights.FullControl,
            global::System.Security.AccessControl.InheritanceFlags.ContainerInherit,
            global::System.Security.AccessControl.PropagationFlags.None,
            global::System.Security.AccessControl.AccessControlType.Allow);
        acl.SetAccessRule(rule);
        key.SetAccessControl(acl);
        key.Close();

        // 删除时间炸弹值
        using var key2 = Registry.LocalMachine.OpenSubKey(RcmGracePeriodPath, writable: true);
        if (key2 == null) return;
        var bombValue = key2.GetValue(GracePeriodBombName);
        if (bombValue != null)
        {
            key2.DeleteValue(GracePeriodBombName, throwOnMissingValue: false);
            Console.WriteLine("[SystemPolicyManager] GracePeriod 时间炸弹已清除");
        }
        else
        {
            Console.WriteLine("[SystemPolicyManager] GracePeriod 无时间炸弹，跳过");
        }
    }

    /// <summary>
    /// Windows Update 策略：
    /// AUOptions=3（自动下载，通知安装，不自动重启）；
    /// NoAutoRebootWithLoggedOnUsers=1（有用户登录时不自动重启）。
    /// </summary>
    public static void SetWindowsUpdatePolicy()
    {
        if (LabConfig.TestMode) { Console.WriteLine("[测试模式] 跳过 Windows Update 策略"); return; }

        using var key = Registry.LocalMachine.CreateSubKey(WindowsUpdateAuPath);
        key.SetValue("AUOptions", 3, RegistryValueKind.DWord);
        key.SetValue("NoAutoRebootWithLoggedOnUsers", 1, RegistryValueKind.DWord);
    }

    /// <summary>
    /// 禁止标准用户安装需要管理员权限的程序：DisableUserInstalls=1。
    /// </summary>
    public static void DisableUserInstalls()
    {
        if (LabConfig.TestMode) { Console.WriteLine("[测试模式] 跳过禁用用户安装策略"); return; }

        using var key = Registry.LocalMachine.CreateSubKey(InstallerPath);
        key.SetValue("DisableUserInstalls", 1, RegistryValueKind.DWord);
    }

    /// <summary>
    /// 磁盘配额策略：Enable=1（启用记录模式）、Enforce=0（不强制限制，仅追踪用量）。
    /// </summary>
    public static void SetDiskQuotaLogging()
    {
        if (LabConfig.TestMode) { Console.WriteLine("[测试模式] 跳过磁盘配额策略"); return; }

        using var key = Registry.LocalMachine.CreateSubKey(DiskQuotaPath);
        key.SetValue("Enable", 1, RegistryValueKind.DWord);
        key.SetValue("Enforce", 0, RegistryValueKind.DWord);
    }

    /// <summary>
    /// 禁止普通用户通过 Ctrl+Alt+Del 修改密码。
    /// 通过注册表禁用"更改密码"按钮，用户只能通过 TrayApp 修改密码（同步更新存储）。
    /// </summary>
    public static void DisableUserPasswordChange()
    {
        if (LabConfig.TestMode) { Console.WriteLine("[测试模式] 跳过禁止用户改密码"); return; }

        // 禁用 Ctrl+Alt+Del 中的"更改密码"按钮
        using var key = Registry.LocalMachine.CreateSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
        key.SetValue("DisableChangePassword", 1, RegistryValueKind.DWord);

        Console.WriteLine("[SystemPolicyManager] 已禁止用户通过系统方式修改密码");
    }

    /// <summary>
    /// 将系统 ProfilesDirectory 设为 D:\Users，使后续新建用户的 Profile 存放在数据盘。
    /// 必须在创建任何用户 Profile 之前执行（建议在系统初始化第一步）。
    /// 已存在的 C:\Users 下的 Profile 不受影响（仅对新 Profile 生效）。
    /// </summary>
    public static void ConfigureProfilesDirectory()
    {
        if (LabConfig.TestMode) { Console.WriteLine("[测试模式] 跳过配置 ProfilesDirectory"); return; }

        using var key = Registry.LocalMachine.CreateSubKey(LabConfig.ProfileListRegPath);
        var current = key.GetValue(LabConfig.ProfilesDirValueName) as string;

        if (string.Equals(current, LabConfig.DesiredProfilesDirectory,
            StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"[SystemPolicyManager] ProfilesDirectory 已为 {LabConfig.DesiredProfilesDirectory}，跳过");
            return;
        }

        key.SetValue(LabConfig.ProfilesDirValueName, LabConfig.DesiredProfilesDirectory,
            RegistryValueKind.ExpandString);
        Console.WriteLine($"[SystemPolicyManager] ProfilesDirectory 已设为 {LabConfig.DesiredProfilesDirectory}");
    }

    /// <summary>依次应用上述策略及 Profile 目录配置。</summary>
    public static void ApplyAll()
    {
        if (LabConfig.TestMode)
        {
            Console.WriteLine("[测试模式] 跳过应用全部系统策略");
            return;
        }
        ConfigureProfilesDirectory();
        FixRdpLicensing();
        SetRdpSessionTimeouts();
        SetWindowsUpdatePolicy();
        DisableUserInstalls();
        SetDiskQuotaLogging();
        DisableUserPasswordChange();
    }
}
