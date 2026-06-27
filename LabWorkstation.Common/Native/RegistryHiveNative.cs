using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace LabWorkstation.Common.Native;

/// <summary>
/// 加载/卸载 NTUSER.DAT 注册表 hive 的 P/Invoke 封装。
/// 加载 hive 需要 SE_RESTORE 与 SE_BACKUP 特权，由 <see cref="EnableRegistryPrivileges"/> 启用。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class RegistryHiveNative
{
    /// <summary>HKEY_USERS 预定义句柄。</summary>
    public static readonly IntPtr HKEY_USERS = unchecked((IntPtr)(long)0x80000003);

    private const uint SE_BACKUP_PRIVILEGE = 17;
    private const uint SE_RESTORE_PRIVILEGE = 18;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int RegLoadKey(IntPtr hKey, string lpSubKey, string lpFile);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int RegUnLoadKey(IntPtr hKey, string lpSubKey);

    [DllImport("ntdll.dll")]
    public static extern int RtlAdjustPrivilege(uint Privilege, bool Enable, bool CurrentThread, out bool Enabled);

    /// <summary>启用 SE_RESTORE 与 SE_BACKUP 特权（加载/卸载 hive 所需）。</summary>
    public static void EnableRegistryPrivileges()
    {
        RtlAdjustPrivilege(SE_RESTORE_PRIVILEGE, Enable: true, CurrentThread: false, out _);
        RtlAdjustPrivilege(SE_BACKUP_PRIVILEGE, Enable: true, CurrentThread: false, out _);
    }
}

/// <summary>
/// 已加载的注册表 hive 句柄，Dispose 时自动卸载。
/// 使用 <see cref="Load"/> 加载 hive；不再需要时 Dispose 卸载。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class RegistryHiveLoader : IDisposable
{
    private readonly string _hiveName;
    private bool _disposed;

    private RegistryHiveLoader(string hiveName)
    {
        _hiveName = hiveName;
    }

    /// <summary>
    /// 启用特权并将 hive 文件加载到 HKEY_USERS\&lt;hiveName&gt;。
    /// 返回的实例 Dispose 时卸载 hive。
    /// </summary>
    public static RegistryHiveLoader Load(string hiveFilePath, string hiveName)
    {
        // 构造（加载）时自动启用特权
        RegistryHiveNative.EnableRegistryPrivileges();

        var hr = RegistryHiveNative.RegLoadKey(RegistryHiveNative.HKEY_USERS, hiveName, hiveFilePath);
        if (hr != 0)
            throw new Win32Exception(hr, $"RegLoadKey 失败: hive='{hiveName}', file='{hiveFilePath}'");

        return new RegistryHiveLoader(hiveName);
    }

    /// <summary>卸载 hive。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        try
        {
            RegistryHiveNative.RegUnLoadKey(RegistryHiveNative.HKEY_USERS, _hiveName);
        }
        catch
        {
            // 卸载失败不应抛出，避免干扰主流程
        }
        _disposed = true;
    }

    /// <summary>
    /// 便捷方法：加载 hive 到 HKEY_USERS 下的临时子键，执行 action（参数为打开的可写 RegistryKey），最后卸载。
    /// 使用 try/finally 确保卸载。
    /// </summary>
    public static void UsingHive(string hiveFilePath, Action<RegistryKey> action)
    {
        var hiveName = "LabTempHive_" + Guid.NewGuid().ToString("N");
        var loader = Load(hiveFilePath, hiveName);
        try
        {
            using var key = Registry.Users.OpenSubKey(hiveName, writable: true)
                ?? throw new Win32Exception($"加载 hive 后无法打开子键: {hiveName}");
            action(key);
        }
        finally
        {
            loader.Dispose();
        }
    }
}
