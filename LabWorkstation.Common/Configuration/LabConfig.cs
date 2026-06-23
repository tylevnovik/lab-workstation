namespace LabWorkstation.Common.Configuration;

/// <summary>
/// 课题组工作站的路径与常量配置。集中管理，避免散落各处。
/// 保持与原 PowerShell 脚本中的约定一致。
/// </summary>
public static class LabConfig
{
    // ── 数据路径 ──────────────────────────────────────────────
    public const string SharePath = @"D:\GroupData";
    public const string PublicPath = @"D:\GroupData\_公共";
    public const string UsersRootPath = @"D:\Users";

    // ── 安全组 ────────────────────────────────────────────────
    public const string AllGroup = "Lab_All";
    private const string AdvisorGroupPrefix = "Lab_";

    /// <summary>导师组名前缀（Lab_）。导师名 = 组名去掉前缀。</summary>
    public static string AdvisorGroupPrefixName => AdvisorGroupPrefix;

    /// <summary>由导师名构造导师组名（张老师 → Lab_张老师）。</summary>
    public static string AdvisorToGroupName(string advisorName) => AdvisorGroupPrefix + advisorName;

    /// <summary>由导师组名还原导师名（Lab_张老师 → 张老师）。</summary>
    public static string GroupNameToAdvisor(string groupName) =>
        groupName.StartsWith(AdvisorGroupPrefix, StringComparison.Ordinal)
            ? groupName[AdvisorGroupPrefix.Length..]
            : groupName;

    /// <summary>判断某本地组名是否为导师组（Lab_ 开头且非 Lab_All）。</summary>
    public static bool IsAdvisorGroup(string groupName) =>
        groupName.StartsWith(AdvisorGroupPrefix, StringComparison.Ordinal) && groupName != AllGroup;

    // ── 导师区分类子目录 ────────────────────────────────────
    public static readonly string[] GroupCategories =
    {
        "01_人才类数据",
        "02_温故知新数据",
        "03_科技报告",
        "04_资政报告",
        "05_项目资料",
        "99_归档"
    };

    // ── 日志路径 ──────────────────────────────────────────────
    public static string AuditLogPath => System.IO.Path.Combine(PublicPath, "_使用手册", "admin_operations.log");
    public static string MonitorLogPath => System.IO.Path.Combine(PublicPath, "_使用手册", "system_monitor.log");

    // ── 通知目录 ──────────────────────────────────────────────
    public static string NotifyPendingPath => System.IO.Path.Combine(PublicPath, "_notifications", "pending");
    public static string NotifySentPath => System.IO.Path.Combine(PublicPath, "_notifications", "sent");

    // ── 审计日志轮转 ──────────────────────────────────────────
    public const long AuditLogMaxSizeBytes = 10L * 1024 * 1024; // 10 MB
    public const int AuditLogMaxArchives = 12;

    // ── 监控日志轮转 ──────────────────────────────────────────
    public const long MonitorLogMaxSizeBytes = 5L * 1024 * 1024; // 5 MB

    // ── 测试模式 ──────────────────────────────────────────────
    /// <summary>
    /// 测试模式开关。为 true 时所有账户/组/通知/ACL 操作仅作用于内存模拟状态，
    /// 绝不修改真实系统。由各应用入口（--test 参数）设置。
    /// </summary>
    public static bool TestMode { get; set; }
}
