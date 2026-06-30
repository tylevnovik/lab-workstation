using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using LabWorkstation.Common.Configuration;
using LabWorkstation.Common.Kiosk;
using LabWorkstation.Common.LocalAccounts;

namespace LabWorkstation.Kiosk;

/// <summary>
/// Kiosk 自助开户主界面。全屏无边框，禁止访问其他任何东西。
/// 用户输入用户名、显示名、选择导师组，提交后写入请求文件，
/// 轮询等待 Monitor 处理完成，显示结果（含初始密码）。
/// 底部提供查看已有用户、登录已有账户（RDP）、关机/重启等功能。
/// </summary>
public sealed class KioskForm : Form
{
    private static readonly Color BgColor = Color.FromArgb(24, 25, 38);
    private static readonly Color CardBg = Color.FromArgb(36, 38, 54);
    private static readonly Color Accent = Color.FromArgb(137, 180, 250);
    private static readonly Color TextColor = Color.FromArgb(205, 214, 244);
    private static readonly Color MutedText = Color.FromArgb(150, 160, 180);
    private static readonly Color DangerColor = Color.FromArgb(243, 139, 168);
    private static readonly Color SuccessColor = Color.FromArgb(166, 227, 161);

    private readonly TextBox _usernameBox;
    private readonly TextBox _displayNameBox;
    private readonly TextBox _passwordBox;
    private readonly TextBox _passwordConfirmBox;
    private readonly ComboBox _advisorCombo;
    private readonly Label _statusLabel;
    private readonly Button _submitBtn;
    private readonly Panel _resultPanel;
    private readonly Label _resultLabel;
    private System.Windows.Forms.Timer? _pollTimer;
    private System.Windows.Forms.Timer? _heartbeatTimer;
    private string? _pendingRequestId;
    private int _pollCount;
    private bool _monitorOnline;

    // 已有用户面板
    private readonly Panel _usersPanel;
    private readonly ListBox _usersList;
    private readonly Button _toggleUsersBtn;
    private readonly Button _loginSelectedBtn;
    private bool _usersPanelVisible;
    /// <summary>用户列表项对应的用户名（与 _usersList 显示项一一对应）。</summary>
    private List<string> _usernames = new();

    // Monitor 在线状态指示器
    private readonly Label _monitorStatusLabel;

    // 公告面板
    private readonly Panel _announcementsPanel;
    private readonly Label _pinnedTitleLabel;
    private readonly Label _pinnedContentLabel;
    private readonly Panel _pinnedBox;
    private readonly ListBox _announcementsList;
    private System.Windows.Forms.Timer? _announcementRefreshTimer;

    /// <summary>轮询超时秒数，超过后提示用户可能服务未运行。</summary>
    private const int PollTimeoutSeconds = 90;

    /// <summary>心跳检查间隔（秒）。Monitor 每 5 秒轮询并写心跳，这里 10 秒检查一次足够。</summary>
    private const int HeartbeatCheckIntervalSeconds = 10;

    /// <summary>心跳判定阈值：心跳时间戳距今超过该秒数视为离线。</summary>
    private const int HeartbeatTimeoutSeconds = 30;

    /// <summary>Monitor 写入 responses/ 的心跳文件名（与 Monitor 端常量一致）。</summary>
    private const string HeartbeatFileName = "monitor_heartbeat.json";

    /// <summary>公告刷新间隔（秒）。Admin 修改公告后 Kiosk 最多 30 秒内同步。</summary>
    private const int AnnouncementRefreshIntervalSeconds = 30;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true
    };

    public KioskForm()
    {
        Program.WriteDebugLog("[FORM] KioskForm 构造开始");
        Text = "工作站自助开户";
        FormBorderStyle = FormBorderStyle.None;
        WindowState = FormWindowState.Maximized;
        StartPosition = FormStartPosition.Manual;
        BackColor = BgColor;
        Font = new Font("Microsoft YaHei UI", 11F);
        KeyPreview = true;

        // 禁用 Alt+Tab、Win 键等（Kiosk 模式）
        // 通过 TopMost 和全屏覆盖来限制访问

        var screen = Screen.PrimaryScreen!.Bounds;
        Bounds = screen;

        // ── 标题 ──
        var titleLabel = new Label
        {
            Text = "课题组工作站自助开户",
            Font = new Font("Microsoft YaHei UI", 24F, FontStyle.Bold),
            ForeColor = TextColor,
            TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(0, 40),
            Size = new Size(screen.Width, 50)
        };
        Controls.Add(titleLabel);

        var subtitleLabel = new Label
        {
            Text = "请填写以下信息创建你的工作站账户",
            Font = new Font("Microsoft YaHei UI", 12F),
            ForeColor = MutedText,
            TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(0, 100),
            Size = new Size(screen.Width, 30)
        };
        Controls.Add(subtitleLabel);

        // ── Monitor 在线状态指示器 ──
        _monitorStatusLabel = new Label
        {
            Text = "● 检查服务状态中...",
            Font = new Font("Microsoft YaHei UI", 10F),
            ForeColor = MutedText,
            TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(0, 132),
            Size = new Size(screen.Width, 24)
        };
        Controls.Add(_monitorStatusLabel);

        // 心跳检查与定时器在构造函数末尾启动（避免 _advisorCombo/_submitBtn 未就绪时触发 UI 更新）

        // ── 公告面板（右侧）──
        var annPanelWidth = 420;
        var annPanelX = screen.Width - annPanelWidth - 40;
        var annPanelY = 170;
        var annPanelHeight = screen.Height - annPanelY - 80; // 底部留出底部按钮栏空间

        _announcementsPanel = new Panel
        {
            Location = new Point(annPanelX, annPanelY),
            Size = new Size(annPanelWidth, annPanelHeight),
            BackColor = CardBg
        };
        Controls.Add(_announcementsPanel);

        var annTitleLabel = new Label
        {
            Text = "📢 公告",
            Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold),
            ForeColor = TextColor,
            Location = new Point(20, 15),
            AutoSize = true
        };
        _announcementsPanel.Controls.Add(annTitleLabel);

        // 置顶公告框（单独显示，默认展开第一条置顶）
        _pinnedBox = new Panel
        {
            Location = new Point(20, 50),
            Size = new Size(annPanelWidth - 40, 140),
            BackColor = Color.FromArgb(60, 50, 30), // 暖色背景突出置顶
            BorderStyle = BorderStyle.FixedSingle
        };
        _announcementsPanel.Controls.Add(_pinnedBox);

        _pinnedTitleLabel = new Label
        {
            Text = "",
            Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold),
            ForeColor = Color.FromArgb(255, 220, 130),
            Location = new Point(10, 8),
            Size = new Size(_pinnedBox.Width - 20, 25),
            AutoEllipsis = true
        };
        _pinnedBox.Controls.Add(_pinnedTitleLabel);

        _pinnedContentLabel = new Label
        {
            Text = "暂无置顶公告",
            Font = new Font("Microsoft YaHei UI", 10F),
            ForeColor = MutedText,
            Location = new Point(10, 38),
            Size = new Size(_pinnedBox.Width - 20, _pinnedBox.Height - 48),
            AutoSize = false
        };
        _pinnedBox.Controls.Add(_pinnedContentLabel);

        // 普通公告列表
        var listTitleLabel = new Label
        {
            Text = "全部公告",
            Font = new Font("Microsoft YaHei UI", 10F),
            ForeColor = MutedText,
            Location = new Point(20, 200),
            AutoSize = true
        };
        _announcementsPanel.Controls.Add(listTitleLabel);

        _announcementsList = new ListBox
        {
            Location = new Point(20, 225),
            Size = new Size(annPanelWidth - 40, annPanelHeight - 245),
            Font = new Font("Microsoft YaHei UI", 10F),
            BackColor = BgColor,
            ForeColor = TextColor,
            BorderStyle = BorderStyle.None,
            ScrollAlwaysVisible = true,
            DisplayMember = "Title" // 显示 Title 字段
        };
        _announcementsList.SelectedIndexChanged += (_, _) => OnAnnouncementSelected();
        _announcementsPanel.Controls.Add(_announcementsList);

        // 首次加载公告 + 启动定时刷新
        Program.WriteDebugLog("[FORM] 加载公告");
        LoadAnnouncements();
        _announcementRefreshTimer = new System.Windows.Forms.Timer
        {
            Interval = AnnouncementRefreshIntervalSeconds * 1000
        };
        _announcementRefreshTimer.Tick += (_, _) => LoadAnnouncements();
        _announcementRefreshTimer.Start();

        // ── 表单卡片（在左侧可用区域居中，右侧留给公告面板）──
        var cardWidth = 480;
        var annPanelWidthForLayout = 420;
        var annPanelRightMargin = 40;
        var leftAreaWidth = screen.Width - annPanelWidthForLayout - annPanelRightMargin;
        var cardX = Math.Max(20, (leftAreaWidth - cardWidth) / 2);
        var cardY = 170;
        var cardHeight = 480; // 加密码+确认密码两行后增高

        var card = new Panel
        {
            Location = new Point(cardX, cardY),
            Size = new Size(cardWidth, cardHeight),
            BackColor = CardBg
        };
        Controls.Add(card);

        // 用户名
        var userLabel = new Label
        {
            Text = "登录用户名",
            ForeColor = MutedText,
            Font = new Font("Microsoft YaHei UI", 10F),
            Location = new Point(30, 20),
            AutoSize = true
        };
        card.Controls.Add(userLabel);

        _usernameBox = new TextBox
        {
            Location = new Point(30, 45),
            Size = new Size(cardWidth - 60, 35),
            Font = new Font("Microsoft YaHei UI", 13F),
            BackColor = BgColor,
            ForeColor = TextColor,
            BorderStyle = BorderStyle.FixedSingle
        };
        card.Controls.Add(_usernameBox);

        // 显示名
        var displayLabel = new Label
        {
            Text = "你的姓名",
            ForeColor = MutedText,
            Font = new Font("Microsoft YaHei UI", 10F),
            Location = new Point(30, 95),
            AutoSize = true
        };
        card.Controls.Add(displayLabel);

        _displayNameBox = new TextBox
        {
            Location = new Point(30, 120),
            Size = new Size(cardWidth - 60, 35),
            Font = new Font("Microsoft YaHei UI", 13F),
            BackColor = BgColor,
            ForeColor = TextColor,
            BorderStyle = BorderStyle.FixedSingle
        };
        card.Controls.Add(_displayNameBox);

        // 密码
        var pwdLabel = new Label
        {
            Text = "设置密码（需含大小写字母、数字、符号，至少 8 位）",
            ForeColor = MutedText,
            Font = new Font("Microsoft YaHei UI", 9F),
            Location = new Point(30, 170),
            AutoSize = true
        };
        card.Controls.Add(pwdLabel);

        _passwordBox = new TextBox
        {
            Location = new Point(30, 192),
            Size = new Size(cardWidth - 60, 35),
            Font = new Font("Microsoft YaHei UI", 13F),
            BackColor = BgColor,
            ForeColor = TextColor,
            BorderStyle = BorderStyle.FixedSingle,
            UseSystemPasswordChar = true
        };
        card.Controls.Add(_passwordBox);

        // 确认密码
        var pwdConfirmLabel = new Label
        {
            Text = "确认密码",
            ForeColor = MutedText,
            Font = new Font("Microsoft YaHei UI", 10F),
            Location = new Point(30, 240),
            AutoSize = true
        };
        card.Controls.Add(pwdConfirmLabel);

        _passwordConfirmBox = new TextBox
        {
            Location = new Point(30, 265),
            Size = new Size(cardWidth - 60, 35),
            Font = new Font("Microsoft YaHei UI", 13F),
            BackColor = BgColor,
            ForeColor = TextColor,
            BorderStyle = BorderStyle.FixedSingle,
            UseSystemPasswordChar = true
        };
        card.Controls.Add(_passwordConfirmBox);

        // 导师组
        var advisorLabel = new Label
        {
            Text = "选择你的导师组",
            ForeColor = MutedText,
            Font = new Font("Microsoft YaHei UI", 10F),
            Location = new Point(30, 315),
            AutoSize = true
        };
        card.Controls.Add(advisorLabel);

        _advisorCombo = new ComboBox
        {
            Location = new Point(30, 340),
            Size = new Size(cardWidth - 60, 35),
            Font = new Font("Microsoft YaHei UI", 12F),
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = BgColor,
            ForeColor = TextColor
        };
        _advisorCombo.SelectedIndexChanged += (_, _) => UpdateSubmitButton();
        card.Controls.Add(_advisorCombo);

        // 提交按钮（必须在 LoadAdvisors 之前创建：LoadAdvisors 会设置 _submitBtn.Enabled）
        _submitBtn = new Button
        {
            Text = "创建账户",
            Location = new Point(30, 400),
            Size = new Size(cardWidth - 60, 50),
            BackColor = Accent,
            ForeColor = BgColor,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        _submitBtn.FlatAppearance.BorderSize = 0;
        _submitBtn.Click += (_, _) => SubmitRequest();
        card.Controls.Add(_submitBtn);

        // 加载导师组（此时 _advisorCombo 与 _submitBtn 均已就绪）
        Program.WriteDebugLog("[FORM] 加载导师组");
        LoadAdvisors();

        // 状态标签
        _statusLabel = new Label
        {
            Text = "",
            ForeColor = MutedText,
            Font = new Font("Microsoft YaHei UI", 11F),
            TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(0, cardY + cardHeight + 20),
            Size = new Size(screen.Width, 30)
        };
        Controls.Add(_statusLabel);

        // ── 结果面板（初始隐藏）──
        _resultPanel = new Panel
        {
            Location = new Point(cardX, cardY),
            Size = new Size(cardWidth, cardHeight),
            BackColor = CardBg,
            Visible = false
        };
        Controls.Add(_resultPanel);

        _resultLabel = new Label
        {
            Text = "",
            ForeColor = TextColor,
            Font = new Font("Microsoft YaHei UI", 13F),
            Location = new Point(30, 30),
            Size = new Size(cardWidth - 60, 250),
            AutoSize = false
        };
        _resultPanel.Controls.Add(_resultLabel);

        var closeBtn = new Button
        {
            Text = "完成",
            Location = new Point(30, 290),
            Size = new Size(cardWidth - 60, 50),
            BackColor = Accent,
            ForeColor = BgColor,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        closeBtn.FlatAppearance.BorderSize = 0;
        closeBtn.Click += (_, _) => ResetForm();
        _resultPanel.Controls.Add(closeBtn);

        // ── 已有用户面板（初始隐藏，含登录按钮）──
        var usersPanelY = cardY + cardHeight + 60;
        var usersPanelWidth = cardWidth;
        _usersPanel = new Panel
        {
            Location = new Point(cardX, usersPanelY),
            Size = new Size(usersPanelWidth, 240),
            BackColor = CardBg,
            Visible = false
        };
        Controls.Add(_usersPanel);

        var usersTitle = new Label
        {
            Text = "已有工作站账户（选中后点击下方登录）",
            ForeColor = MutedText,
            Font = new Font("Microsoft YaHei UI", 10F),
            Location = new Point(15, 10),
            AutoSize = true
        };
        _usersPanel.Controls.Add(usersTitle);

        // 登录选中账户按钮（先创建，后续 _usersList 事件 lambda 中引用才安全）
        _loginSelectedBtn = new Button
        {
            Text = "登录选中账户",
            Location = new Point(15, 190),
            Size = new Size(usersPanelWidth - 30, 40),
            BackColor = Accent,
            ForeColor = BgColor,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold),
            Cursor = Cursors.Hand,
            Enabled = false
        };
        _loginSelectedBtn.FlatAppearance.BorderSize = 0;
        _loginSelectedBtn.Click += (_, _) => LoginSelectedUser();
        _usersPanel.Controls.Add(_loginSelectedBtn);

        _usersList = new ListBox
        {
            Location = new Point(15, 35),
            Size = new Size(usersPanelWidth - 30, 145),
            Font = new Font("Microsoft YaHei UI", 11F),
            BackColor = BgColor,
            ForeColor = TextColor,
            BorderStyle = BorderStyle.None,
            ScrollAlwaysVisible = true
        };
        _usersList.SelectedIndexChanged += (_, _) => _loginSelectedBtn.Enabled = _usersList.SelectedIndex >= 0
            && _usersList.SelectedIndex < _usernames.Count;
        _usersList.DoubleClick += (_, _) => LoginSelectedUser();
        _usersPanel.Controls.Add(_usersList);

        // ── 底部系统按钮栏 ──
        var bottomBarHeight = 56;
        var bottomBar = new Panel
        {
            Location = new Point(0, screen.Height - bottomBarHeight),
            Size = new Size(screen.Width, bottomBarHeight),
            BackColor = CardBg
        };
        Controls.Add(bottomBar);

        // 查看已有用户（左侧）
        _toggleUsersBtn = new Button
        {
            Text = "查看已有用户",
            Location = new Point(30, 10),
            Size = new Size(150, 36),
            FlatStyle = FlatStyle.Flat,
            BackColor = BgColor,
            ForeColor = TextColor,
            Font = new Font("Microsoft YaHei UI", 10F),
            Cursor = Cursors.Hand,
            TextAlign = ContentAlignment.MiddleCenter
        };
        _toggleUsersBtn.FlatAppearance.BorderSize = 0;
        _toggleUsersBtn.Click += (_, _) => ToggleUsersPanel();
        bottomBar.Controls.Add(_toggleUsersBtn);

        // 右侧按钮组：重启 | 关机
        // 注：Kiosk 配置了开机自动登录，注销后会立即重新登录 kiosk，故不提供注销按钮。
        // 切换账户请通过"查看已有用户"→选中账户→"登录选中账户"（RDP）。
        var rightBtns = new[] { ("重启", (EventHandler)((_, _) => Restart())),
                                 ("关机", (EventHandler)((_, _) => Shutdown())) };
        var rightX = screen.Width - 30;
        var btnWidth = 130;
        var btnGap = 10;
        for (var i = rightBtns.Length - 1; i >= 0; i--)
        {
            var (label, handler) = rightBtns[i];
            rightX -= btnWidth;
            var btn = new Button
            {
                Text = label,
                Location = new Point(rightX, 10),
                Size = new Size(btnWidth, 36),
                FlatStyle = FlatStyle.Flat,
                BackColor = BgColor,
                ForeColor = label == "关机" ? DangerColor : TextColor,
                Font = new Font("Microsoft YaHei UI", 10F),
                Cursor = Cursors.Hand,
                TextAlign = ContentAlignment.MiddleCenter
            };
            btn.FlatAppearance.BorderSize = 0;
            btn.Click += handler;
            bottomBar.Controls.Add(btn);
            rightX -= btnGap;
        }

        // 拦截系统按键
        KeyDown += (_, e) =>
        {
            if (e.Alt && e.KeyCode != Keys.F4) e.Handled = true;
        };

        // 启动后台心跳检查（首次立即检查，之后每 10 秒一次）
        // 放在构造函数末尾：此时 _advisorCombo/_submitBtn 等所有控件均已创建，
        // CheckHeartbeat → UpdateMonitorStatusUi 访问这些控件才安全。
        Program.WriteDebugLog("[FORM] 启动心跳检查");
        CheckHeartbeat();
        _heartbeatTimer = new System.Windows.Forms.Timer { Interval = HeartbeatCheckIntervalSeconds * 1000 };
        _heartbeatTimer.Tick += (_, _) => CheckHeartbeat();
        _heartbeatTimer.Start();

        Program.WriteDebugLog("[FORM] KioskForm 构造完成，UI 已就绪");
    }

    /// <summary>
    /// 加载导师列表到下拉框。
    /// 使用 GroupManager 枚举系统 Lab_ 组（普通用户可枚举），
    /// 而非 AdvisorStore（其文件位于 ConfigDir，普通用户无读取权限）。
    /// </summary>
    private void LoadAdvisors()
    {
        try
        {
            // 过滤空字符串与空白项，避免出现可选的"空"导师组
            var advisors = GroupManager.GetAllAdvisorGroups()
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .OrderBy(n => n)
                .ToList();
            _advisorCombo.Items.Clear();
            foreach (var name in advisors)
                _advisorCombo.Items.Add(name);
            if (_advisorCombo.Items.Count > 0)
            {
                _advisorCombo.SelectedIndex = 0;
                _advisorCombo.Enabled = true;
            }
            else
            {
                _advisorCombo.Items.Add("（尚无导师组，请联系管理员）");
                _advisorCombo.SelectedIndex = 0;
                _advisorCombo.Enabled = false;
            }
        }
        catch
        {
            _advisorCombo.Items.Clear();
            _advisorCombo.Items.Add("（读取导师组失败，请联系管理员）");
            _advisorCombo.SelectedIndex = 0;
            _advisorCombo.Enabled = false;
        }
        // 提交按钮的最终启用状态由 UpdateSubmitButton 统一判定
        UpdateSubmitButton();
    }

    /// <summary>
    /// 根据当前表单状态（Monitor 在线 + 导师组有效 + 未在处理中）统一判定提交按钮启用状态。
    /// 在 LoadAdvisors、SelectedIndexChanged、CheckHeartbeat 后调用。
    /// </summary>
    private void UpdateSubmitButton()
    {
        if (_pendingRequestId != null)
        {
            _submitBtn.Enabled = false;
            return;
        }
        if (!_monitorOnline)
        {
            _submitBtn.Enabled = false;
            return;
        }
        var advisor = _advisorCombo.SelectedItem?.ToString() ?? "";
        _submitBtn.Enabled = !string.IsNullOrEmpty(advisor) && !advisor.StartsWith("（");
    }

    /// <summary>
    /// 校验密码复杂度：必须同时包含大写字母、小写字母、数字、符号四类。
    /// 满足 Windows 默认密码策略（要求至少 3 类，这里 4 类全包更稳妥）。
    /// 返回 null 表示通过，否则返回错误描述。
    /// </summary>
    private static string? ValidatePasswordComplexity(string password)
    {
        bool hasUpper = false, hasLower = false, hasDigit = false, hasSymbol = false;
        foreach (var c in password)
        {
            if (char.IsUpper(c)) hasUpper = true;
            else if (char.IsLower(c)) hasLower = true;
            else if (char.IsDigit(c)) hasDigit = true;
            else if (!char.IsWhiteSpace(c)) hasSymbol = true;
        }
        if (!hasUpper) return "密码必须包含大写字母";
        if (!hasLower) return "密码必须包含小写字母";
        if (!hasDigit) return "密码必须包含数字";
        if (!hasSymbol) return "密码必须包含符号（如 !@#$%）";
        return null;
    }

    /// <summary>提交开户请求到队列。</summary>
    private void SubmitRequest()
    {
        // 前置检查：Monitor 必须在线，否则请求无人处理
        if (!_monitorOnline)
        {
            ShowStatus("自助开户服务离线，无法创建账户，请联系管理员", DangerColor);
            return;
        }

        var username = _usernameBox.Text.Trim();
        var displayName = _displayNameBox.Text.Trim();
        var password = _passwordBox.Text;
        var passwordConfirm = _passwordConfirmBox.Text;
        var advisorName = _advisorCombo.SelectedItem?.ToString() ?? "";

        // 验证
        if (string.IsNullOrEmpty(username))
        {
            ShowStatus("请输入登录用户名", DangerColor);
            return;
        }
        if (string.IsNullOrEmpty(displayName))
        {
            ShowStatus("请输入你的姓名", DangerColor);
            return;
        }
        // 用户名只能英文字母+数字
        if (!username.All(c => char.IsLetterOrDigit(c)))
        {
            ShowStatus("用户名只能包含英文字母和数字", DangerColor);
            return;
        }
        // 密码长度校验
        if (password.Length < 8)
        {
            ShowStatus("密码至少需要 8 位", DangerColor);
            return;
        }
        // 密码复杂度校验：必须同时包含大写、小写、数字、符号
        var pwdError = ValidatePasswordComplexity(password);
        if (pwdError != null)
        {
            ShowStatus(pwdError, DangerColor);
            return;
        }
        // 两次密码必须一致
        if (password != passwordConfirm)
        {
            ShowStatus("两次输入的密码不一致", DangerColor);
            return;
        }
        // 导师组为空或占位符时拒绝提交
        if (string.IsNullOrEmpty(advisorName) || advisorName.StartsWith("（"))
        {
            ShowStatus("请先选择有效的导师组", DangerColor);
            return;
        }

        // 创建请求（密码由用户自设，明文经队列文件传递给 Monitor）
        var request = new KioskRequest
        {
            RequestId = Guid.NewGuid().ToString("N")[..12],
            Type = "CreateAccount",
            Username = username,
            DisplayName = displayName,
            AdvisorName = advisorName,
            Password = password,
            Timestamp = DateTime.Now
        };

        try
        {
            Directory.CreateDirectory(LabConfig.KioskRequestsPath);
            Directory.CreateDirectory(LabConfig.KioskResponsesPath);

            var reqPath = Path.Combine(LabConfig.KioskRequestsPath, $"req_{request.RequestId}.json");
            var json = JsonSerializer.Serialize(request, JsonOptions);
            File.WriteAllText(reqPath, json, Encoding.UTF8);

            _pendingRequestId = request.RequestId;
            _pollCount = 0;
            ShowStatus("正在创建账户，请稍候...", MutedText);
            _submitBtn.Enabled = false;

            // 启动轮询等待响应（每 2 秒检查一次）
            _pollTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            _pollTimer.Tick += (_, _) => CheckResponse();
            _pollTimer.Start();
        }
        catch (Exception ex)
        {
            ShowStatus($"提交失败: {ex.Message}", DangerColor);
        }
    }

    /// <summary>轮询检查响应文件，含超时检测。</summary>
    private void CheckResponse()
    {
        if (_pendingRequestId == null) return;

        _pollCount++;

        // 超时检查：超过 PollTimeoutSeconds 秒未收到响应
        var elapsedSeconds = _pollCount * 2;
        if (elapsedSeconds >= PollTimeoutSeconds)
        {
            _pollTimer?.Stop();
            _pollTimer?.Dispose();
            _pollTimer = null;
            _pendingRequestId = null;
            _submitBtn.Enabled = true;
            ShowStatus("创建超时：自助开户服务可能未运行，请联系管理员检查后重试", DangerColor);
            return;
        }

        var respPath = Path.Combine(LabConfig.KioskResponsesPath, $"resp_{_pendingRequestId}.json");
        if (!File.Exists(respPath)) return;

        _pollTimer?.Stop();
        _pollTimer?.Dispose();
        _pollTimer = null;

        try
        {
            var json = File.ReadAllText(respPath, Encoding.UTF8);
            var resp = JsonSerializer.Deserialize<KioskResponse>(json, JsonOptions);
            if (resp != null)
            {
                ShowResult(resp);
            }
        }
        catch (Exception ex)
        {
            ShowStatus($"读取结果失败: {ex.Message}", DangerColor);
        }

        // 清理响应文件
        try { File.Delete(respPath); } catch { }
        _pendingRequestId = null;
    }

    /// <summary>显示创建结果。</summary>
    private void ShowResult(KioskResponse resp)
    {
        if (resp.Success)
        {
            _resultLabel.Text = $"✓ 账户创建成功！\n\n" +
                $"用户名：{_usernameBox.Text.Trim()}\n" +
                $"密码：你刚才设置的密码\n\n" +
                $"点击下方「完成」后可在用户列表中选中并登录。\n" +
                $"请牢记你设置的密码。";
            _resultLabel.ForeColor = SuccessColor;
            _statusLabel.Text = "✓ 账户创建成功！请查看上方信息";
            _statusLabel.ForeColor = SuccessColor;

            // 创建成功后自动刷新用户列表（如果面板可见）
            if (_usersPanelVisible)
            {
                LoadExistingUsers();
            }
        }
        else
        {
            _resultLabel.Text = $"✗ 创建失败\n\n{resp.Message}";
            _resultLabel.ForeColor = DangerColor;
            _statusLabel.Text = "✗ 创建失败，请查看上方信息";
            _statusLabel.ForeColor = DangerColor;
        }

        // BringToFront 确保结果面板在 card 之上，避免被遮挡
        _resultPanel.Visible = true;
        _resultPanel.BringToFront();
    }

    /// <summary>重置表单回到初始状态。</summary>
    private void ResetForm()
    {
        _resultPanel.Visible = false;
        _usernameBox.Text = "";
        _displayNameBox.Text = "";
        _passwordBox.Text = "";
        _passwordConfirmBox.Text = "";
        _statusLabel.Text = "";
        LoadAdvisors(); // LoadAdvisors 末尾会调用 UpdateSubmitButton 统一判定按钮状态

        // 重置后自动刷新用户列表（如果面板可见），确保显示最新账户
        if (_usersPanelVisible)
        {
            LoadExistingUsers();
        }
    }

    // ── 已有用户面板 ──────────────────────────────────────

    /// <summary>切换已有用户面板的显示状态。</summary>
    private void ToggleUsersPanel()
    {
        _usersPanelVisible = !_usersPanelVisible;
        _usersPanel.Visible = _usersPanelVisible;
        if (_usersPanelVisible)
        {
            _toggleUsersBtn.Text = "隐藏用户列表";
            LoadExistingUsers();
        }
        else
        {
            _toggleUsersBtn.Text = "查看已有用户";
        }
    }

    /// <summary>
    /// 加载已有用户列表到 ListBox，同步填充 _usernames 列表。
    /// 通过 GroupManager.GetMembers(Lab_All) 枚举实验室用户（普通用户可读取组成员），
    /// 逐个查询显示名与导师组信息。_usernames 与 _usersList 显示项一一对应，
    /// 选中后可通过索引取出真实用户名供 RDP 登录使用。
    /// </summary>
    private void LoadExistingUsers()
    {
        _usersList.Items.Clear();
        _usernames = new List<string>();
        _usersList.Items.Add("正在加载...");
        _loginSelectedBtn.Enabled = false;

        try
        {
            var members = GroupManager.GetMembers(LabConfig.AllGroup);
            _usersList.Items.Clear();

            if (members.Count == 0)
            {
                _usersList.Items.Add("（暂无工作站账户）");
                return;
            }

            foreach (var username in members)
            {
                var info = AccountManager.GetUserInfo(username);
                var displayName = info?.DisplayName ?? "";
                var advisor = GroupManager.GetUserAdvisorGroup(username);
                var display = string.IsNullOrEmpty(displayName)
                    ? (string.IsNullOrEmpty(advisor) ? username : $"{username}  （{advisor}）")
                    : (string.IsNullOrEmpty(advisor)
                        ? $"{username}  （{displayName}）"
                        : $"{username}  （{displayName}，{advisor}）");
                _usersList.Items.Add(display);
                _usernames.Add(username); // 与显示项一一对应
            }
        }
        catch (Exception ex)
        {
            _usersList.Items.Clear();
            _usernames = new List<string>();
            _usersList.Items.Add($"加载失败: {ex.Message}");
        }
    }

    // ── 系统操作 ──────────────────────────────────────────

    /// <summary>
    /// 登录用户列表中选中的账户：启动 mstsc 连接到 127.0.0.1，
    /// 并提示用户在 RDP 窗口中输入该账户的密码。
    /// kiosk 控制台会话保持运行，RDP 窗口关闭后自动回到开户界面。
    /// </summary>
    private void LoginSelectedUser()
    {
        var idx = _usersList.SelectedIndex;
        if (idx < 0 || idx >= _usernames.Count)
        {
            ShowStatus("请先在列表中选择一个账户", DangerColor);
            return;
        }
        var username = _usernames[idx];
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "mstsc.exe",
                Arguments = "/v:127.0.0.1",
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Normal
            };
            Process.Start(psi);
            ShowStatus($"已启动远程桌面，请在 RDP 窗口中以 [{username}] 身份登录", SuccessColor);
        }
        catch (Exception ex)
        {
            ShowStatus($"启动远程桌面失败: {ex.Message}", DangerColor);
        }
    }

    /// <summary>重启计算机。</summary>
    private void Restart()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "shutdown.exe",
                Arguments = "/r /t 0",
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch (Exception ex)
        {
            ShowStatus($"重启失败: {ex.Message}", DangerColor);
        }
    }

    /// <summary>关闭计算机。</summary>
    private void Shutdown()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "shutdown.exe",
                Arguments = "/s /t 0",
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch (Exception ex)
        {
            ShowStatus($"关机失败: {ex.Message}", DangerColor);
        }
    }

    // ── 工具方法 ──────────────────────────────────────────

    private void ShowStatus(string text, Color color)
    {
        _statusLabel.Text = text;
        _statusLabel.ForeColor = color;
    }

    // ── Monitor 心跳检查 ─────────────────────────────────

    /// <summary>
    /// 检查 Monitor 心跳文件，更新在线状态与 UI 指示器。
    /// 心跳文件由 Monitor 每 5 秒写入一次（responses/monitor_heartbeat.json），
    /// 若时间戳距今超过 <see cref="HeartbeatTimeoutSeconds"/> 秒视为离线。
    /// </summary>
    private void CheckHeartbeat()
    {
        var online = IsMonitorOnline();
        if (online == _monitorOnline) return;

        _monitorOnline = online;
        UpdateMonitorStatusUi();
    }

    /// <summary>读取心跳文件并判断 Monitor 是否在线。</summary>
    private static bool IsMonitorOnline()
    {
        try
        {
            var path = Path.Combine(LabConfig.KioskResponsesPath, HeartbeatFileName);
            if (!File.Exists(path)) return false;

            var json = File.ReadAllText(path, Encoding.UTF8);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("Timestamp", out var tsEl)) return false;

            // 兼容 ISO 8601 字符串与 DateTime 解析
            if (tsEl.ValueKind != JsonValueKind.String) return false;
            if (!DateTime.TryParse(tsEl.GetString(), out var ts)) return false;

            return (DateTime.Now - ts).TotalSeconds <= HeartbeatTimeoutSeconds;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>根据 _monitorOnline 更新指示器文字与颜色，并同步提交按钮可用状态。</summary>
    private void UpdateMonitorStatusUi()
    {
        if (_monitorOnline)
        {
            _monitorStatusLabel.Text = "● 自助开户服务在线";
            _monitorStatusLabel.ForeColor = SuccessColor;
        }
        else
        {
            _monitorStatusLabel.Text = "● 自助开户服务离线，请联系管理员";
            _monitorStatusLabel.ForeColor = DangerColor;
        }
        // 提交按钮启用状态统一交给 UpdateSubmitButton 判定
        UpdateSubmitButton();
    }

    // ── 公告加载与展示 ───────────────────────────────────

    /// <summary>
    /// 加载公告列表并更新 UI。
    /// 置顶公告显示在顶部独立框（默认展开第一条），所有公告（含置顶）在列表中展示。
    /// 加载失败时保留原显示，不阻断界面。
    /// </summary>
    private void LoadAnnouncements()
    {
        List<Announcement> announcements;
        try
        {
            announcements = AnnouncementStore.LoadAll();
        }
        catch
        {
            // 读取失败（权限/IO 错误）保留原显示
            return;
        }

        // 更新置顶框：取第一条置顶公告
        var pinned = announcements.FirstOrDefault(a => a.IsPinned);
        if (pinned != null)
        {
            _pinnedTitleLabel.Text = $"📌 {pinned.Title}";
            _pinnedContentLabel.Text = pinned.Content;
            _pinnedContentLabel.ForeColor = TextColor;
        }
        else
        {
            _pinnedTitleLabel.Text = "";
            _pinnedContentLabel.Text = "暂无置顶公告";
            _pinnedContentLabel.ForeColor = MutedText;
        }

        // 更新列表：所有公告（置顶的标注 📌 前缀）
        // 记录当前选中 Id 以便刷新后保持选中
        string? selectedId = null;
        if (_announcementsList.SelectedItem is Announcement sel)
            selectedId = sel.Id;

        _announcementsList.Items.Clear();
        if (announcements.Count == 0)
        {
            _announcementsList.Items.Add("暂无公告");
            return;
        }

        foreach (var a in announcements)
        {
            var prefix = a.IsPinned ? "📌 " : "";
            var display = $"{prefix}{a.Title}";
            _announcementsList.Items.Add(a);

            // 恢复选中
            if (selectedId != null && a.Id == selectedId)
                _announcementsList.SelectedIndex = _announcementsList.Items.Count - 1;
        }
    }

    /// <summary>列表选中项变化时，弹出公告详情对话框。</summary>
    private void OnAnnouncementSelected()
    {
        if (_announcementsList.SelectedItem is not Announcement a) return;

        // 立即取消选中，避免下次刷新触发重复弹窗
        _announcementsList.SelectedIndex = -1;

        var pinTag = a.IsPinned ? "【置顶】" : "";
        var time = a.UpdatedAt.ToString("yyyy-MM-dd HH:mm");
        var message = $"{pinTag}{a.Title}\n\n" +
            $"{a.Content}\n\n" +
            $"────────────\n" +
            $"发布人：{a.CreatedBy}\n" +
            $"更新时间：{time}";

        // 用自定义对话框显示（Kiosk 模式下 MessageBox 可能被遮挡，用置顶 Form）
        var dlg = new Form
        {
            Text = "公告详情",
            StartPosition = FormStartPosition.CenterScreen,
            Size = new Size(480, 380),
            FormBorderStyle = FormBorderStyle.FixedSingle,
            MaximizeBox = false,
            MinimizeBox = false,
            TopMost = true,
            BackColor = CardBg,
            Font = new Font("Microsoft YaHei UI", 10F)
        };

        var content = new Label
        {
            Text = message,
            ForeColor = TextColor,
            Location = new Point(20, 20),
            Size = new Size(440, 280),
            AutoSize = false
        };
        dlg.Controls.Add(content);

        var okBtn = new Button
        {
            Text = "关闭",
            Location = new Point(360, 310),
            Size = new Size(100, 36),
            BackColor = Accent,
            ForeColor = BgColor,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        okBtn.FlatAppearance.BorderSize = 0;
        okBtn.Click += (_, _) => dlg.Close();
        dlg.Controls.Add(okBtn);

        dlg.ShowDialog(this);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Kiosk 模式：阻止所有关闭操作
        if (e.CloseReason == CloseReason.UserClosing ||
            e.CloseReason == CloseReason.TaskManagerClosing)
        {
            e.Cancel = true;
        }
        base.OnFormClosing(e);
    }
}
