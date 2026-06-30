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
    // 注：使用 global::System.IO 以避免被同程序集 LabWorkstation.Common.System 命名空间遮蔽。
    // 安全说明：审计/监控日志迁出公共区（旧路径 Lab_All 可改），现置于 ConfigDir\logs，
    // ACL：Administrators/SYSTEM 完全控制，Lab_All 只读（用户在自助弹窗中读取自身审计行）。
    public static string LogsDir => global::System.IO.Path.Combine(ConfigDir, "logs");
    public static string AuditLogPath => global::System.IO.Path.Combine(LogsDir, "admin_operations.log");
    public static string MonitorLogPath => global::System.IO.Path.Combine(LogsDir, "system_monitor.log");
    /// <summary>旧审计日志路径（公共区），用于一次性迁移到 LogsDir。</summary>
    public static string LegacyAuditLogPath => global::System.IO.Path.Combine(PublicPath, "_使用手册", "admin_operations.log");
    public static string LegacyMonitorLogPath => global::System.IO.Path.Combine(PublicPath, "_使用手册", "system_monitor.log");

    // ── 落盘配置（Store）──────────────────────────────────────
    /// <summary>
    /// 导师组/用户权威记录的 JSON 落盘目录。
    /// 存放在 C:\ProgramData\LabWorkstation，默认仅 Administrators/SYSTEM 可访问，
    /// 防止普通用户读取 users.json 中的存储密码。
    /// </summary>
    public static string ConfigDir => global::System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "LabWorkstation");
    public static string AdvisorStorePath => global::System.IO.Path.Combine(ConfigDir, "advisors.json");
    public static string UserStorePath => global::System.IO.Path.Combine(ConfigDir, "users.json");
    public static string SystemStatePath => global::System.IO.Path.Combine(ConfigDir, "system_state.json");

    /// <summary>
    /// Kiosk 请求队列目录（迁出公共区，置于 ConfigDir\kiosk_queue）。
    /// 安全说明：旧路径位于 D:\GroupData\_公共\_config\kiosk_queue，Lab_All 全员可读写，
    /// 存在任意账户创建与密码嗅探风险。现迁至 ConfigDir 下，按子目录细粒度授权：
    /// requests/ 仅 kiosk 可写；responses/ 仅 kiosk 可读；其他用户无任何权限。
    /// </summary>
    public static string KioskQueuePath => global::System.IO.Path.Combine(ConfigDir, "kiosk_queue");
    /// <summary>旧 Kiosk 队列路径（公共区），用于一次性迁移。</summary>
    public static string LegacyKioskQueuePath => global::System.IO.Path.Combine(PublicPath, "_config", "kiosk_queue");

    // ── 系统注册表 ───────────────────────────────────────────
    /// <summary>Windows ProfileList 注册表根路径，子键为用户 SID。</summary>
    public const string ProfileListRegPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";
    /// <summary>ProfilesDirectory 注册表值名，控制新用户 Profile 的创建位置。</summary>
    public const string ProfilesDirValueName = "ProfilesDirectory";
    /// <summary>期望的 ProfilesDirectory 值（用户配置文件存放在数据盘）。</summary>
    public const string DesiredProfilesDirectory = @"D:\Users";

    // ── 壁纸与桌面 ───────────────────────────────────────────
    public const string ScriptsDir = @"C:\Scripts";
    public static string WallpaperPath => global::System.IO.Path.Combine(ScriptsDir, "LabWallpaper.png");
    /// <summary>壁纸源文件（项目根的 wallpaper.png），由部署复制到 WallpaperPath。</summary>
    public const string WallpaperSourceName = "wallpaper.png";
    /// <summary>Default User 的 NTUSER.DAT，用于设置新用户默认壁纸。</summary>
    public static string DefaultUserHivePath => global::System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "..", "Default", "NTUSER.DAT");

    // ── SMB 共享 ─────────────────────────────────────────────
    public const string SmbShareName = "GroupData";

    // ── 通知目录 ──────────────────────────────────────────────
    public static string NotifyPendingPath => global::System.IO.Path.Combine(PublicPath, "_notifications", "pending");
    public static string NotifySentPath => global::System.IO.Path.Combine(PublicPath, "_notifications", "sent");

    // KioskRequestsPath / KioskResponsesPath 基于 KioskQueuePath（见上方 ConfigDir 区域定义）
    public static string KioskRequestsPath => global::System.IO.Path.Combine(KioskQueuePath, "requests");
    public static string KioskResponsesPath => global::System.IO.Path.Combine(KioskQueuePath, "responses");

    /// <summary>
    /// Kiosk 公告目录（ConfigDir\kiosk_announcements）。
    /// 由 Admin 写入增删改，Kiosk 只读轮询展示。
    /// 与 TrayApp 通知系统独立：不弹窗、不过期，常驻 Kiosk 界面。
    /// ACL：Administrators/SYSTEM 完全控制，kiosk 只读。
    /// </summary>
    public static string KioskAnnouncementsPath => global::System.IO.Path.Combine(ConfigDir, "kiosk_announcements");

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

    // ── Kiosk 调试模式 ────────────────────────────────────────
    /// <summary>
    /// Kiosk 调试模式开关。为 true 时：
    /// 1. KioskDeployer 将 kiosk 用户 Shell 设为 explorer.exe（而非 Kiosk.exe），
    ///    允许通过 RDP 登录看到完整桌面，便于手动运行/排查 Kiosk 应用；
    /// 2. KioskDeployer 将 kiosk 加入 Remote Desktop Users 组，允许 RDP 连接；
    /// 3. Kiosk 应用启动时写详细日志到 %LocalAppData%\LabWorkstation\kiosk_debug.log。
    /// 排查完毕后改回 false 重新部署即可恢复生产模式（自定义 Shell + 自动登录）。
    /// </summary>
    public static bool KioskDebugMode { get; set; } = false; // 黑屏已修复，切回正式模式
}
