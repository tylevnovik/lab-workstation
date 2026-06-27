using System.IO;
using LabWorkstation.Common.Configuration;

namespace LabWorkstation.Common.Store.Models;

/// <summary>用户账户状态。</summary>
public enum UserStatus
{
    /// <summary>正常使用。</summary>
    Active,

    /// <summary>已禁用（账户存在但不可登录）。</summary>
    Disabled,

    /// <summary>已离校并归档。</summary>
    Departed
}

/// <summary>
/// 用户的权威落盘记录。对应一个本地账户及其个人目录。
/// 由 <see cref="LabWorkstation.Common.Store.UserStore"/> 持久化到 users.json。
/// </summary>
public sealed record UserRecord
{
    /// <summary>登录用户名。</summary>
    public string Username { get; init; } = string.Empty;

    /// <summary>显示名（全名）。</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>所属导师名（导师组名去掉 Lab_ 前缀）。</summary>
    public string AdvisorName { get; init; } = string.Empty;

    /// <summary>记录创建时间。</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>创建人（管理员账号）。</summary>
    public string CreatedBy { get; init; } = string.Empty;

    /// <summary>账户状态。</summary>
    public UserStatus Status { get; init; } = UserStatus.Active;

    /// <summary>个人数据目录，如 D:\Users\用户名。</summary>
    public string PersonalDir { get; init; } = string.Empty;

    /// <summary>Windows 用户配置文件路径，如 C:\Users\用户名（新建账户时捕获，可能为空）。</summary>
    public string? ProfilePath { get; init; }

    /// <summary>
    /// 系统存储的账户密码（DPAPI 加密后的 Base64 字符串，仅用于管理员切换用户时自动登录）。
    /// 加密作用域为 LocalMachine，仅本机 Administrators/SYSTEM 可解密；文件被复制到他机后无法解密。
    /// 用户通过托盘修改密码或管理员创建/重置密码时会同步更新此字段。
    /// 旧版数据可能为明文，<see cref="LabWorkstation.Common.Security.PasswordProtector.UnprotectOrFallback"/>
    /// 会自动兼容并在下次写入时转为密文。
    /// </summary>
    public string? StoredPassword { get; init; }

    /// <summary>离校时间（仅 Status=Departed 时有值）。</summary>
    public DateTime? DepartedAt { get; init; }

    /// <summary>离校归档目录（仅 Status=Departed 时有值）。</summary>
    public string? ArchiveDir { get; init; }

    /// <summary>
    /// 工厂方法：由用户名、显示名、导师名、创建人构造记录，
    /// 自动填充 PersonalDir（UsersRootPath\用户名）并置 Status=Active。
    /// </summary>
    public static UserRecord Create(
        string username,
        string displayName,
        string advisorName,
        string createdBy,
        string? profilePath,
        string? storedPassword = null) => new()
    {
        Username = username,
        DisplayName = displayName,
        AdvisorName = advisorName,
        CreatedAt = DateTime.Now,
        CreatedBy = createdBy,
        Status = UserStatus.Active,
        PersonalDir = Path.Combine(LabConfig.UsersRootPath, username),
        ProfilePath = profilePath,
        StoredPassword = storedPassword
    };
}
