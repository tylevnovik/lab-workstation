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
    private bool _usersPanelVisible;

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

        // 启动后台心跳检查（首次立即检查，之后每 10 秒一次）
        CheckHeartbeat();
        _heartbeatTimer = new System.Windows.Forms.Timer { Interval = HeartbeatCheckIntervalSeconds * 1000 };
        _heartbeatTimer.Tick += (_, _) => CheckHeartbeat();
        _heartbeatTimer.Start();

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
        LoadAnnouncements();
        _announcementRefreshTimer = new System.Windows.Forms.Timer
        {
            Interval = AnnouncementRefreshIntervalSeconds * 1000
        };
        _announcementRefreshTimer.Tick += (_, _) => LoadAnnouncements();
        _announcementRefreshTimer.Start();

        // ── 表单卡片 ──
        var cardWidth = 480;
        // 左侧固定边距，右侧空间留给公告面板
        var cardX = 60;
        var cardY = 170;

        var card = new Panel
        {
            Location = new Point(cardX, cardY),
            Size = new Size(cardWidth, 360),
            BackColor = CardBg
        };
        Controls.Add(card);

        // 用户名
        var userLabel = new Label
        {
            Text = "登录用户名",
            ForeColor = MutedText,
            Font = new Font("Microsoft YaHei UI", 10F),
            Location = new Point(30, 30),
            AutoSize = true
        };
        card.Controls.Add(userLabel);

        _usernameBox = new TextBox
        {
            Location = new Point(30, 55),
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
            Location = new Point(30, 110),
            AutoSize = true
        };
        card.Controls.Add(displayLabel);

        _displayNameBox = new TextBox
        {
            Location = new Point(30, 135),
            Size = new Size(cardWidth - 60, 35),
            Font = new Font("Microsoft YaHei UI", 13F),
            BackColor = BgColor,
            ForeColor = TextColor,
            BorderStyle = BorderStyle.FixedSingle
        };
        card.Controls.Add(_displayNameBox);

        // 导师组
        var advisorLabel = new Label
        {
            Text = "选择你的导师组",
            ForeColor = MutedText,
            Font = new Font("Microsoft YaHei UI", 10F),
            Location = new Point(30, 190),
            AutoSize = true
        };
        card.Controls.Add(advisorLabel);

        _advisorCombo = new ComboBox
        {
            Location = new Point(30, 215),
            Size = new Size(cardWidth - 60, 35),
            Font = new Font("Microsoft YaHei UI", 12F),
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = BgColor,
            ForeColor = TextColor
        };
        LoadAdvisors();
        card.Controls.Add(_advisorCombo);

        // 提交按钮
        _submitBtn = new Button
        {
            Text = "创建账户",
            Location = new Point(30, 280),
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

        // 状态标签
        _statusLabel = new Label
        {
            Text = "",
            ForeColor = MutedText,
            Font = new Font("Microsoft YaHei UI", 11F),
            TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(0, cardY + 380),
            Size = new Size(screen.Width, 30)
        };
        Controls.Add(_statusLabel);

        // ── 结果面板（初始隐藏）──
        _resultPanel = new Panel
        {
            Location = new Point(cardX, cardY),
            Size = new Size(cardWidth, 360),
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

        // ── 已有用户面板（初始隐藏）──
        var usersPanelY = cardY + 380 + 40;
        var usersPanelWidth = cardWidth;
        _usersPanel = new Panel
        {
            Location = new Point(cardX, usersPanelY),
            Size = new Size(usersPanelWidth, 200),
            BackColor = CardBg,
            Visible = false
        };
        Controls.Add(_usersPanel);

        var usersTitle = new Label
        {
            Text = "已有工作站账户",
            ForeColor = MutedText,
            Font = new Font("Microsoft YaHei UI", 10F),
            Location = new Point(15, 10),
            AutoSize = true
        };
        _usersPanel.Controls.Add(usersTitle);

        _usersList = new ListBox
        {
            Location = new Point(15, 35),
            Size = new Size(usersPanelWidth - 30, 150),
            Font = new Font("Microsoft YaHei UI", 11F),
            BackColor = BgColor,
            ForeColor = TextColor,
            BorderStyle = BorderStyle.None,
            ScrollAlwaysVisible = true
        };
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

        // 右侧按钮组
        var rightBtns = new[] { ("登录已有账户", (EventHandler)((_, _) => SwitchUser())),
                                 ("重启", (EventHandler)((_, _) => Restart())),
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
            var advisors = GroupManager.GetAllAdvisorGroups();
            _advisorCombo.Items.Clear();
            foreach (var name in advisors)
                _advisorCombo.Items.Add(name);
            if (_advisorCombo.Items.Count > 0)
            {
                _advisorCombo.SelectedIndex = 0;
                _submitBtn.Enabled = true;
            }
            else
            {
                _advisorCombo.Items.Add("（尚无导师组，请联系管理员）");
                _advisorCombo.SelectedIndex = 0;
                _advisorCombo.Enabled = false;
                _submitBtn.Enabled = false;
            }
        }
        catch
        {
            _advisorCombo.Items.Clear();
            _advisorCombo.Items.Add("（读取导师组失败，请联系管理员）");
            _advisorCombo.SelectedIndex = 0;
            _advisorCombo.Enabled = false;
            _submitBtn.Enabled = false;
        }
    }

    /// <summary>提交开户请求到队列。</summary>
    private void SubmitRequest()
    {
        // 前置检查：Monitor 必须在线，否则请求无人处理
        if (!_monitorOnline)
        {
            ShowStatus("监控服务离线，无法创建账户，请联系管理员", DangerColor);
            return;
        }

        var username = _usernameBox.Text.Trim();
        var displayName = _displayNameBox.Text.Trim();
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
        // 导师组为空或占位符时拒绝提交
        if (string.IsNullOrEmpty(advisorName) || advisorName.StartsWith("（"))
        {
            ShowStatus("请先选择有效的导师组", DangerColor);
            return;
        }

        // 创建请求
        var request = new KioskRequest
        {
            RequestId = Guid.NewGuid().ToString("N")[..12],
            Type = "CreateAccount",
            Username = username,
            DisplayName = displayName,
            AdvisorName = advisorName,
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
            ShowStatus("创建超时：监控服务可能未运行，请联系管理员检查后重试", DangerColor);
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
        _statusLabel.Text = "";

        if (resp.Success)
        {
            _resultLabel.Text = $"✓ 账户创建成功！\n\n" +
                $"初始密码：{resp.Password}\n\n" +
                $"请记下你的密码。\n" +
                $"点击下方「登录已有账户」即可使用此账户登录。\n" +
                $"登录后请尽快修改密码。";
            _resultLabel.ForeColor = SuccessColor;
        }
        else
        {
            _resultLabel.Text = $"✗ 创建失败\n\n{resp.Message}";
            _resultLabel.ForeColor = DangerColor;
        }

        _resultPanel.Visible = true;
    }

    /// <summary>重置表单回到初始状态。</summary>
    private void ResetForm()
    {
        _resultPanel.Visible = false;
        _usernameBox.Text = "";
        _displayNameBox.Text = "";
        _submitBtn.Enabled = true;
        _statusLabel.Text = "";
        LoadAdvisors();
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
    /// 加载已有用户列表到 ListBox。
    /// 通过 GroupManager.GetMembers(Lab_All) 枚举实验室用户（普通用户可读取组成员），
    /// 逐个查询显示名与导师组信息。
    /// </summary>
    private void LoadExistingUsers()
    {
        _usersList.Items.Clear();
        _usersList.Items.Add("正在加载...");

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
            }
        }
        catch (Exception ex)
        {
            _usersList.Items.Clear();
            _usersList.Items.Add($"加载失败: {ex.Message}");
        }
    }

    // ── 系统操作 ──────────────────────────────────────────

    /// <summary>
    /// 启动远程桌面连接到 127.0.0.1，让已有账户通过 RDP 登录。
    /// kiosk 账户使用自定义 Shell 自动登录，无法直接注销切换用户
    /// （注销后会自动重新登录 kiosk）。通过 RDP 让其他用户登录，
    /// kiosk 控制台会话保持运行，RDP 窗口关闭后自动回到开户界面。
    /// </summary>
    private void SwitchUser()
    {
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
            _monitorStatusLabel.Text = "● 监控服务在线";
            _monitorStatusLabel.ForeColor = SuccessColor;
        }
        else
        {
            _monitorStatusLabel.Text = "● 监控服务离线，请联系管理员";
            _monitorStatusLabel.ForeColor = DangerColor;
        }

        // 提交按钮在 Monitor 离线时禁用（已在处理中的请求不受影响）
        if (!_monitorOnline && _pendingRequestId == null)
        {
            _submitBtn.Enabled = false;
        }
        else if (_monitorOnline && _pendingRequestId == null)
        {
            // 恢复时需重新校验导师组是否可选
            var advisor = _advisorCombo.SelectedItem?.ToString() ?? "";
            _submitBtn.Enabled = !string.IsNullOrEmpty(advisor) && !advisor.StartsWith("（");
        }
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
