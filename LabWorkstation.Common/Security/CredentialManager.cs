using System.Runtime.InteropServices;

namespace LabWorkstation.Common.Security;

/// <summary>
/// Windows 凭据管理器封装（advapi32.dll CredWrite / CredDelete / CredFree）。
/// 替代原 cmdkey.exe 命令行方式，避免密码出现在进程命令行参数中（可被任务管理器/WMI 窥视）。
/// 凭据存储在当前用户的凭据库中（CRED_PERSIST_LOCAL_MACHINE），仅当前用户身份可读取。
/// </summary>
public static class CredentialManager
{
    private const uint CRED_TYPE_GENERIC = 1;
    private const uint CRED_TYPE_DOMAIN_PASSWORD = 2;
    private const uint CRED_PERSIST_LOCAL_MACHINE = 2;
    private const uint CRED_PERSIST_ENTERPRISE = 3;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public global::System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredWrite(ref CREDENTIAL cred, uint flags);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    /// <summary>
    /// 写入一条通用凭据（Generic Credential）到当前用户凭据库。
    /// 密码以 UTF-16 LE 字节形式存入 CredentialBlob，不经过命令行。
    /// </summary>
    /// <param name="target">凭据目标名（如 "127.0.0.1"），删除时按此名称匹配。</param>
    /// <param name="userName">用户名。</param>
    /// <param name="password">密码明文。</param>
    /// <returns>成功返回 true，失败返回 false（可用 Marshal.GetLastWin32Error 查原因）。</returns>
    public static bool WriteCredential(string target, string userName, string password)
    {
        // 将密码编为 UTF-16 LE 字节（与 Windows 凭据库约定一致）
        var blobBytes = global::System.Text.Encoding.Unicode.GetBytes(password);
        var blobPtr = Marshal.AllocHGlobal(blobBytes.Length);
        try
        {
            Marshal.Copy(blobBytes, 0, blobPtr, blobBytes.Length);

            var cred = new CREDENTIAL
            {
                Flags = 0,
                Type = CRED_TYPE_GENERIC,
                TargetName = target,
                Comment = null,
                LastWritten = default,
                CredentialBlobSize = (uint)blobBytes.Length,
                CredentialBlob = blobPtr,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                AttributeCount = 0,
                Attributes = IntPtr.Zero,
                TargetAlias = null,
                UserName = userName
            };

            return CredWrite(ref cred, 0);
        }
        finally
        {
            // 立即清零并释放非托管内存，减少密码在堆中驻留时间
            for (var i = 0; i < blobBytes.Length; i++) blobBytes[i] = 0;
            Marshal.FreeHGlobal(blobPtr);
        }
    }

    /// <summary>
    /// 删除指定目标名的通用凭据。不存在时返回 false（不抛异常）。
    /// </summary>
    public static bool DeleteCredential(string target)
    {
        return CredDelete(target, CRED_TYPE_GENERIC, 0);
    }

    /// <summary>
    /// 删除指定目标名的域密码凭据（CRED_TYPE_DOMAIN_PASSWORD）。
    /// mstsc 连接 RDP 时会自动保存此类凭据，target 格式为 TERMSRV/server。
    /// 不存在时返回 false（不抛异常）。
    /// </summary>
    public static bool DeleteDomainCredential(string target)
    {
        return CredDelete(target, CRED_TYPE_DOMAIN_PASSWORD, 0);
    }

    /// <summary>
    /// 清除 RDP 连接相关的所有凭据（generic + domain_password）。
    /// 同时删除 "127.0.0.1" 和 "TERMSRV/127.0.0.1" 两个 target，
    /// 确保上次切换用户时残留的凭据不会影响下次切换。
    /// </summary>
    /// <param name="server">RDP 服务器名（如 "127.0.0.1"）。</param>
    public static void ClearRdpCredentials(string server)
    {
        var targets = new[] { server, $"TERMSRV/{server}" };
        foreach (var target in targets)
        {
            try { CredDelete(target, CRED_TYPE_GENERIC, 0); } catch { }
            try { CredDelete(target, CRED_TYPE_DOMAIN_PASSWORD, 0); } catch { }
        }
    }
}
