using System.Collections.Generic;
using System.Linq;
using LabWorkstation.Common.Configuration;
using LabWorkstation.Common.LocalAccounts;
using LabWorkstation.Common.Notifications;
using LabWorkstation.Common.Storage;

namespace LabWorkstation.Common.Mock;

/// <summary>
/// 测试模式内存状态。当 <see cref="LabConfig.TestMode"/> 为 true 时，
/// 所有账户/组/通知操作都作用于此内存状态，绝不触碰真实系统。
/// 提供合理的种子数据，使 UI 可被完整点遍。
/// </summary>
public static class MockState
{
    public sealed record MockUser(
        string Username, string DisplayName, bool Enabled,
        DateTime? LastLogon, string Password, string AdvisorGroup);

    private static readonly Dictionary<string, MockUser> Users = new();
    private static readonly Dictionary<string, HashSet<string>> Groups = new();
    private static readonly List<Notification> Pending = new();
    private static readonly List<Notification> Sent = new();
    private static readonly List<string> AuditLines = new();
    private static readonly Dictionary<string, long> FolderSizes = new();

    static MockState()
    {
        Seed();
    }

    private static void Seed()
    {
        // ── 用户 ──────────────────────────────────────────
        var now = DateTime.Now;
        Register(new MockUser("张三", "张三", true, now.AddHours(-2), "Test@1234", "张老师"));
        Register(new MockUser("李四", "李四", true, now.AddHours(-5), "Test@1234", "张老师"));
        Register(new MockUser("王五", "王五", true, now.AddMinutes(-30), "Test@1234", "李老师"));
        Register(new MockUser("赵六", "赵六", false, now.AddDays(-3), "Test@1234", "李老师"));
        Register(new MockUser("钱七", "钱七", true, now.AddHours(-1), "Test@1234", ""));

        // ── 组 ────────────────────────────────────────────
        Groups[LabConfig.AllGroup] = new HashSet<string> { "张三", "李四", "王五", "赵六", "钱七" };
        Groups[LabConfig.AdvisorToGroupName("张老师")] = new HashSet<string> { "张三", "李四" };
        Groups[LabConfig.AdvisorToGroupName("李老师")] = new HashSet<string> { "王五", "赵六" };

        // ── 通知 ──────────────────────────────────────────
        Pending.Add(new Notification
        {
            Id = "mock001",
            Title = "欢迎使用测试模式",
            Message = "当前为测试模式，所有操作仅作用于内存，不会修改真实系统。可放心点遍所有功能。",
            ImportanceLevel = Importance.Normal,
            Timestamp = now,
            Sender = "系统"
        });
        Pending.Add(new Notification
        {
            Id = "mock002",
            Title = "紧急：服务器维护通知（示例）",
            Message = "本周六 22:00-次日 02:00 进行服务器维护，届时无法访问共享数据。",
            ImportanceLevel = Importance.Urgent,
            Timestamp = now.AddMinutes(-10),
            Sender = "管理员"
        });

        Sent.Add(new Notification
        {
            Id = "sent001",
            Title = "存储清理提醒（历史示例）",
            Message = "请各位及时清理个人临时文件。",
            ImportanceLevel = Importance.Important,
            Timestamp = now.AddDays(-7),
            Sender = "管理员"
        });

        // ── 审计日志 ──────────────────────────────────────
        AuditLines.Add($"[{now:yyyy-MM-dd HH:mm:ss}] 操作人: TEST\\管理员 | 操作: CREATE_USER | 对象: 张三 | 结果: Success");
        AuditLines.Add($"[{now:yyyy-MM-dd HH:mm:ss}] 操作人: TEST\\管理员 | 操作: CHANGE_GROUP | 对象: 李四 | 结果: Success | 详情: 移至 Lab_张老师");
        AuditLines.Add($"[{now.AddDays(-1):yyyy-MM-dd HH:mm:ss}] 操作人: TEST\\管理员 | 操作: DISABLE_USER | 对象: 赵六 | 结果: Success");

        // ── 文件夹大小（假数据）──────────────────────────
        FolderSizes[@"D:\Users\张三"] = 2_300_000_000L;   // 2.3 GB
        FolderSizes[@"D:\Users\李四"] = 850_000_000L;      // 850 MB
        FolderSizes[@"D:\Users\王五"] = 5_100_000_000L;    // 5.1 GB
        FolderSizes[@"D:\GroupData\张老师"] = 18_700_000_000L; // 18.7 GB
        FolderSizes[@"D:\GroupData\李老师"] = 9_200_000_000L;  // 9.2 GB
    }

    private static void Register(MockUser u) => Users[u.Username] = u;

    // ── 账户查询 ──────────────────────────────────────────
    public static bool UserExists(string username) => Users.ContainsKey(username);

    public static AccountManager.LocalUserInfo? GetUserInfo(string username) =>
        Users.TryGetValue(username, out var u)
            ? new AccountManager.LocalUserInfo(u.Username, u.DisplayName, u.Enabled, u.LastLogon)
            : null;

    public static List<string> GetAllUsernames() => Users.Keys.ToList();

    public static string GetCurrentUserName() => "张三";

    // ── 账户写操作 ────────────────────────────────────────
    public static void CreateUser(string username, string password, string displayName, string description)
    {
        Users[username] = new MockUser(username, displayName, true, DateTime.Now, password, "");
    }

    public static void SetEnabled(string username, bool enabled)
    {
        if (Users.TryGetValue(username, out var u))
            Users[username] = u with { Enabled = enabled };
    }

    public static void SetPassword(string username, string newPassword)
    {
        if (Users.TryGetValue(username, out var u))
            Users[username] = u with { Password = newPassword };
    }

    /// <summary>获取测试模式下用户的密码（仅供 VerifyPassword 等测试场景使用）。</summary>
    public static string? GetPassword(string username) =>
        Users.TryGetValue(username, out var u) ? u.Password : null;

    public static string? GetUserSid(string username) =>
        Users.TryGetValue(username, out _) ? $"S-1-5-21-MOCK-{username.GetHashCode()}" : null;

    public static void DeleteUser(string username)
    {
        Users.Remove(username);
        foreach (var g in Groups.Values) g.Remove(username);
    }

    // ── 组查询 ────────────────────────────────────────────
    public static bool GroupExists(string groupName) => Groups.ContainsKey(groupName);

    public static List<string> GetMembers(string groupName) =>
        Groups.TryGetValue(groupName, out var members) ? members.ToList() : new List<string>();

    public static bool IsMember(string groupName, string username) =>
        Groups.TryGetValue(groupName, out var members) && members.Contains(username);

    public static List<string> GetAllAdvisorGroups() =>
        Groups.Keys
            .Where(LabConfig.IsAdvisorGroup)
            .Select(LabConfig.GroupNameToAdvisor)
            .OrderBy(x => x)
            .ToList();

    public static string GetUserAdvisorGroup(string username) =>
        Users.TryGetValue(username, out var u) ? u.AdvisorGroup : "";

    // ── 组写操作 ──────────────────────────────────────────
    public static void CreateGroup(string groupName) => Groups.TryAdd(groupName, new HashSet<string>());

    public static void DeleteGroup(string groupName) => Groups.Remove(groupName);

    public static void AddMember(string groupName, string username)
    {
        if (!Groups.ContainsKey(groupName)) Groups[groupName] = new HashSet<string>();
        Groups[groupName].Add(username);
        if (Users.TryGetValue(username, out var u) && LabConfig.IsAdvisorGroup(groupName))
            Users[username] = u with { AdvisorGroup = LabConfig.GroupNameToAdvisor(groupName) };
    }

    public static void RemoveMember(string groupName, string username)
    {
        if (Groups.TryGetValue(groupName, out var members)) members.Remove(username);
        if (Users.TryGetValue(username, out var u) && LabConfig.IsAdvisorGroup(groupName)
            && u.AdvisorGroup == LabConfig.GroupNameToAdvisor(groupName))
            Users[username] = u with { AdvisorGroup = "" };
    }

    // ── 通知 ──────────────────────────────────────────────
    public static List<Notification> GetPending() => Pending.ToList();

    public static List<Notification> GetSent() => Sent.OrderByDescending(n => n.Timestamp).ToList();

    public static string SendNotification(string title, string message, Importance importance, string sender)
    {
        var n = new Notification
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Title = title,
            Message = message,
            ImportanceLevel = importance,
            Timestamp = DateTime.Now,
            Sender = sender
        };
        Pending.Add(n);
        return n.Id;
    }

    public static int ArchivePending()
    {
        var count = Pending.Count;
        Sent.AddRange(Pending);
        Pending.Clear();
        return count;
    }

    // ── 审计日志 ──────────────────────────────────────────
    public static void WriteAudit(string action, string target, string result, string detail)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 操作人: TEST\\管理员 | 操作: {action} | 对象: {target} | 结果: {result}"
                   + (string.IsNullOrEmpty(detail) ? "" : $" | 详情: {detail}");
        AuditLines.Add(line);
        global::System.Console.WriteLine("[测试模式审计] " + line);
    }

    public static List<string> ReadUserLines(string username, int count) =>
        AuditLines.Where(l => l.Contains(username, StringComparison.Ordinal)).TakeLast(count).ToList();

    // ── 存储 ──────────────────────────────────────────────
    public static long GetSizeBytes(string path) =>
        FolderSizes.TryGetValue(path, out var size) ? size : 0;

    public static string GetSizeDisplay(string path)
    {
        if (FolderSizes.TryGetValue(path, out var size)) return FolderSizer.FormatSize(size);
        return "(测试模式：路径未模拟)";
    }
}
