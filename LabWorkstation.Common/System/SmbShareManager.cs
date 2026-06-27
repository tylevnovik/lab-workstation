using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LabWorkstation.Common.Configuration;

namespace LabWorkstation.Common.System;

/// <summary>
/// SMB 共享管理。对应原 PS 通过 net share / Get-SmbShare 创建共享的逻辑。
/// 采用 P/Invoke 调用 Netapi32.dll（NetShareAdd/NetShareDel/NetShareGetInfo）。
/// 
/// 权限策略：共享级使用 SHARE_INFO_2（无安全描述符），默认 Everyone 读；
/// 真正的隔离由 NTFS ACL（NtfsAclHelper）控制，与原 PS 设计一致。
/// 测试模式（LabConfig.TestMode）下跳过。
/// </summary>
[SupportedOSPlatform("windows")]
public static class SmbShareManager
{
    /// <summary>磁盘共享类型。</summary>
    private const uint STYPE_DISKTREE = 0;

    /// <summary>NetShareAdd 成功。</summary>
    private const uint NERR_Success = 0;

    /// <summary>共享已存在（NERR_DuplicateShare = 2118）。</summary>
    private const uint NERR_DuplicateShare = 2118;

    /// <summary>共享不存在（NERR_NetNameNotFound = 2310）。</summary>
    private const uint NERR_NetNameNotFound = 2310;

    /// <summary>SHARE_INFO_2 结构（无安全描述符，权限交由 NTFS 控制）。</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHARE_INFO_2
    {
        public string shi2_netname;
        public uint shi2_type;
        public string shi2_remark;
        public uint shi2_permissions;
        public uint shi2_max_uses;       // -1 表示不限
        public uint shi2_current_uses;
        public string shi2_path;
        public string shi2_passwd;
    }

    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint NetShareAdd(string? servername, uint level, ref SHARE_INFO_2 buf, out uint parm_err);

    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint NetShareDel(string? servername, string netname, uint reserved);

    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint NetShareGetInfo(string? servername, string netname, uint level, out IntPtr bufptr);

    [DllImport("Netapi32.dll")]
    private static extern uint NetApiBufferFree(IntPtr Buffer);

    /// <summary>
    /// 确保 <see cref="LabConfig.SmbShareName"/> 共享存在并指向 <see cref="LabConfig.SharePath"/>。
    /// 先 NetShareGetInfo 查询；不存在则先 NetShareDel（清理旧的，忽略错误）再 NetShareAdd。
    /// </summary>
    public static void EnsureShare()
    {
        if (LabConfig.TestMode)
        {
            Console.WriteLine($"[测试模式] 跳过创建 SMB 共享: {LabConfig.SmbShareName} -> {LabConfig.SharePath}");
            return;
        }

        // 先查询：已存在则直接返回（幂等）
        var queryResult = NetShareGetInfo(null, LabConfig.SmbShareName, 2, out var queryBuf);
        if (queryResult == NERR_Success)
        {
            NetApiBufferFree(queryBuf);
            return;
        }

        // 不存在：清理可能残留的旧共享（忽略失败）
        NetShareDel(null, LabConfig.SmbShareName, 0);

        var info = new SHARE_INFO_2
        {
            shi2_netname = LabConfig.SmbShareName,
            shi2_type = STYPE_DISKTREE,
            shi2_remark = "课题组工作站数据共享",
            shi2_permissions = 0,        // 共享级权限交由 NTFS 控制
            shi2_max_uses = uint.MaxValue, // 不限并发
            shi2_current_uses = 0,
            shi2_path = LabConfig.SharePath,
            shi2_passwd = null!
        };

        var result = NetShareAdd(null, 2, ref info, out var parmErr);
        if (result != NERR_Success && result != NERR_DuplicateShare)
        {
            var msg = result == NERR_DuplicateShare
                ? "共享已存在"
                : new Win32Exception((int)result).Message;
            throw new LabOperationException("SMB_SHARE", LabConfig.SmbShareName,
                detail: $"path={LabConfig.SharePath}, parm_err={parmErr}, code={result}",
                message: $"NetShareAdd 失败: {msg}");
        }
    }
}
