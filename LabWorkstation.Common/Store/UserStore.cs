using System.Linq;
using LabWorkstation.Common.Configuration;
using LabWorkstation.Common.Security;
using LabWorkstation.Common.Store.Models;

namespace LabWorkstation.Common.Store;

/// <summary>
/// 用户权威清单的 JSON 落盘 Store。记录每个账户的导师归属与状态。
/// 数据文件：users.json。
/// </summary>
public class UserStore : LabStore
{
    /// <summary>users.json 的根结构。</summary>
    private class UserStoreData
    {
        public int Version { get; set; }
        public DateTime UpdatedAt { get; set; }
        public List<UserRecord> Users { get; set; } = new();
    }

    /// <summary>
    /// 加载全部用户记录。
    /// 文件不存在或测试模式返回空列表。
    /// </summary>
    public static List<UserRecord> LoadAll()
    {
        var data = Load<UserStoreData>(LabConfig.UserStorePath);
        return data?.Users ?? new List<UserRecord>();
    }

    /// <summary>
    /// 添加一条用户记录（按 <see cref="UserRecord.Username"/> 去重：移除同用户名旧记录后追加新记录）。
    /// StoredPassword 会在写入前用 DPAPI 加密（若尚未加密）。测试模式跳过。
    /// </summary>
    public static void Add(UserRecord record)
    {
        if (LabConfig.TestMode) return;
        var data = LoadOrCreate();
        data.Users.RemoveAll(u => u.Username == record.Username);
        // 写入前确保密码已加密（兼容调用方传入明文的情况）
        data.Users.Add(WithEncryptedPassword(record));
        data.UpdatedAt = DateTime.Now;
        Save(LabConfig.UserStorePath, data);
    }

    /// <summary>
    /// 更新指定用户的账户状态。用户不存在则忽略。测试模式跳过。
    /// </summary>
    public static void UpdateStatus(string username, UserStatus status)
    {
        if (LabConfig.TestMode) return;
        var data = LoadOrCreate();
        var idx = data.Users.FindIndex(u => u.Username == username);
        if (idx < 0) return;

        data.Users[idx] = data.Users[idx] with { Status = status };
        data.UpdatedAt = DateTime.Now;
        Save(LabConfig.UserStorePath, data);
    }

    /// <summary>
    /// 标记用户离校：设 Status=Departed、DepartedAt=now、ArchiveDir。
    /// 用户不存在则忽略。测试模式跳过。
    /// </summary>
    public static void MarkDeparted(string username, string archiveDir)
    {
        if (LabConfig.TestMode) return;
        var data = LoadOrCreate();
        var idx = data.Users.FindIndex(u => u.Username == username);
        if (idx < 0) return;

        data.Users[idx] = data.Users[idx] with
        {
            Status = UserStatus.Departed,
            DepartedAt = DateTime.Now,
            ArchiveDir = archiveDir
        };
        data.UpdatedAt = DateTime.Now;
        Save(LabConfig.UserStorePath, data);
    }

    /// <summary>判断指定用户名的记录是否存在。</summary>
    public static bool Exists(string username) =>
        LoadAll().Any(u => u.Username == username);

    /// <summary>查找指定用户名的记录，不存在返回 null。</summary>
    public static UserRecord? Find(string username) =>
        LoadAll().FirstOrDefault(u => u.Username == username);

    /// <summary>
    /// 更新指定用户的存储密码（用户通过托盘修改密码后调用）。
    /// 密码会用 DPAPI 加密后再落盘。用户不存在则忽略。测试模式跳过。
    /// </summary>
    public static void UpdatePassword(string username, string newPassword)
    {
        if (LabConfig.TestMode) return;
        var data = LoadOrCreate();
        var idx = data.Users.FindIndex(u => u.Username == username);
        if (idx < 0) return;

        data.Users[idx] = data.Users[idx] with { StoredPassword = PasswordProtector.Protect(newPassword) };
        data.UpdatedAt = DateTime.Now;
        Save(LabConfig.UserStorePath, data);
    }

    /// <summary>
    /// 获取指定用户的存储密码明文（用于管理员切换用户时自动登录）。
    /// 自动兼容旧版明文记录：解密失败时回退为按明文返回。
    /// 用户不存在或无存储密码返回 null。
    /// </summary>
    public static string? GetStoredPassword(string username)
    {
        var stored = Find(username)?.StoredPassword;
        return PasswordProtector.UnprotectOrFallback(stored);
    }

    /// <summary>
    /// 彻底移除指定用户名的记录（用于账户删除后清理 Store）。
    /// 用户不存在则忽略。测试模式跳过。
    /// </summary>
    public static void Remove(string username)
    {
        if (LabConfig.TestMode) return;
        var data = LoadOrCreate();
        if (data.Users.RemoveAll(u => u.Username == username) > 0)
        {
            data.UpdatedAt = DateTime.Now;
            Save(LabConfig.UserStorePath, data);
        }
    }

    /// <summary>加载已有数据；文件不存在或测试模式返回新的空结构。</summary>
    private static UserStoreData LoadOrCreate()
    {
        var data = Load<UserStoreData>(LabConfig.UserStorePath);
        return data ?? new UserStoreData { Version = 1 };
    }

    /// <summary>
    /// 返回一个新的 UserRecord，其 StoredPassword 已用 DPAPI 加密。
    /// 若传入记录的密码已经是密文（解密成功），原样返回避免双重加密；
    /// 若为明文，则加密后返回；若为 null，原样返回。
    /// </summary>
    private static UserRecord WithEncryptedPassword(UserRecord record)
    {
        var stored = record.StoredPassword;
        if (string.IsNullOrEmpty(stored)) return record;

        // 先尝试解密：成功说明已是密文，无需重复加密
        if (PasswordProtector.Unprotect(stored) != null) return record;

        // 解密失败 → 视为明文，加密后返回新记录
        return record with { StoredPassword = PasswordProtector.Protect(stored) };
    }
}
