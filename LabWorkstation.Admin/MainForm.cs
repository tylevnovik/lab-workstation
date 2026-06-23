using LabWorkstation.Common.Audit;
using LabWorkstation.Common.Configuration;
using LabWorkstation.Common.LocalAccounts;
using LabWorkstation.Admin.Tabs;

namespace LabWorkstation.Admin;

/// <summary>
/// 跨 Tab 的 UI 交互接口。由 MainForm 实现，各 Tab 通过它写日志、刷新其它 Tab、切换页。
/// </summary>
public interface IAppShell
{
    void Log(string message, string level = "INFO");
    void RefreshMembers();
    void RefreshGroups();
    void RefreshDepartUsers();
    void SelectTab(int index);
}

public partial class MainForm : Form, IAppShell
{
    private readonly RichTextBox _logBox;
    private readonly TabControl _tabs;

    private readonly MembersTab _membersTab;
    private readonly CreateAccountTab _createAccountTab;
    private readonly GroupManageTab _groupManageTab;
    private readonly PerformanceTab _performanceTab;
    private readonly BatchTab _batchTab;
    private readonly DepartureTab _departureTab;
    private readonly BroadcastTab _broadcastTab;

    public MainForm()
    {
        Text = LabConfig.TestMode
            ? "【测试模式】课题组工作站 - 账户管理工具"
            : "课题组工作站 - 账户管理工具";
        Size = new Size(820, 700);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        Font = new Font("Microsoft YaHei UI", 9F);

        // 顶部标题栏
        var headerPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 44,
            BackColor = Color.FromArgb(0, 102, 179)
        };
        var headerLabel = new Label
        {
            Text = "  课题组工作站 · 账户管理工具",
            ForeColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
        headerPanel.Controls.Add(headerLabel);
        Controls.Add(headerPanel);

        // 底部日志区
        var logGroup = new GroupBox
        {
            Text = "操作日志",
            Dock = DockStyle.Bottom,
            Height = 154,
            Font = new Font("Microsoft YaHei UI", 8.5F)
        };
        _logBox = new RichTextBox
        {
            Location = new Point(8, 18),
            Size = new Size(785 - 16, 154 - 26),
            ReadOnly = true,
            BackColor = Color.FromArgb(250, 250, 250),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 8.5F)
        };
        logGroup.Resize += (s, e) =>
        {
            _logBox.Width = logGroup.Width - 16;
            _logBox.Height = logGroup.Height - 26;
        };
        logGroup.Controls.Add(_logBox);
        Controls.Add(logGroup);

        // 标签页容器
        _tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Font = new Font("Microsoft YaHei UI", 9.5F)
        };
        Controls.Add(_tabs);
        // 确保 TabControl 在中间层（header 顶部、log 底部之间）
        _tabs.BringToFront();
        logGroup.SendToBack();

        // 实例化各 Tab
        _membersTab = new MembersTab(this);
        _createAccountTab = new CreateAccountTab(this);
        _groupManageTab = new GroupManageTab(this);
        _performanceTab = new PerformanceTab(this);
        _batchTab = new BatchTab(this);
        _departureTab = new DepartureTab(this);
        _broadcastTab = new BroadcastTab(this);

        AddTab("成员管理", _membersTab);
        AddTab("创建账户", _createAccountTab);
        AddTab("分组管理", _groupManageTab);
        AddTab("性能优化", _performanceTab);
        AddTab("批量操作", _batchTab);
        AddTab("离校管理", _departureTab);
        AddTab("公告推送", _broadcastTab);

        Shown += OnShown;
    }

    private void AddTab(string title, UserControl control)
    {
        var page = new TabPage(title);
        control.Dock = DockStyle.Fill;
        page.Controls.Add(control);
        _tabs.TabPages.Add(page);
    }

    private void OnShown(object? sender, EventArgs e)
    {
        Log("工具已启动，正在检查环境...");

        // 检查 Lab_All
        if (GroupManager.GroupExists(LabConfig.AllGroup))
        {
            Log($"全员组 '{LabConfig.AllGroup}' 已存在");
        }
        else
        {
            Log($"全员组 '{LabConfig.AllGroup}' 不存在，正在自动创建...", "WARN");
            try
            {
                GroupManager.CreateGroup(LabConfig.AllGroup);
                Log($"全员组 '{LabConfig.AllGroup}' 已创建");
            }
            catch (Exception ex) { Log($"创建全员组失败: {ex.Message}", "ERROR"); }
        }

        // 检查数据区
        CheckPath(LabConfig.SharePath, "数据区");
        CheckPath(LabConfig.PublicPath, "公共区");
        CheckPath(LabConfig.UsersRootPath, "个人目录区");

        // 列出导师组
        try
        {
            var advisors = GroupManager.GetAllAdvisorGroups();
            if (advisors.Count > 0)
                Log($"发现 {advisors.Count} 个导师组：{string.Join(", ", advisors)}");
            else
                Log("未发现任何导师组（可在「分组管理」或「创建账户」页新建）", "WARN");
        }
        catch (Exception ex) { Log($"获取导师组列表失败: {ex.Message}", "ERROR"); }

        // 初始化下拉框和列表
        _membersTab.RefreshFilterCombo();
        _membersTab.RefreshMemberList();
        _createAccountTab.RefreshAdvisorCombo();
        _groupManageTab.RefreshGroupListView();
        _groupManageTab.RefreshGrpUserCombo();
        _departureTab.RefreshDepartUserCombo();
        _broadcastTab.RefreshHistory();
    }

    private void CheckPath(string path, string label)
    {
        if (Directory.Exists(path))
            Log($"{label} {path} 已就绪");
        else
            Log($"{label} {path} 不存在（请先运行初始化脚本或手动创建）", "WARN");
    }

    // ── IAppShell ──────────────────────────────────────────────
    public void Log(string message, string level = "INFO")
    {
        var ts = DateTime.Now.ToString("HH:mm:ss");
        var color = level switch
        {
            "ERROR" => Color.Red,
            "WARN" => Color.Orange,
            _ => Color.Black
        };
        var line = $"[{ts}] {message}\r\n";
        if (_logBox.InvokeRequired)
        {
            _logBox.BeginInvoke(() => AppendLog(line, color));
        }
        else
        {
            AppendLog(line, color);
        }
    }

    private void AppendLog(string line, Color color)
    {
        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.SelectionLength = 0;
        _logBox.SelectionColor = color;
        _logBox.AppendText(line);
        _logBox.ScrollToCaret();
    }

    public void RefreshMembers()
    {
        _membersTab.RefreshFilterCombo();
        _membersTab.RefreshMemberList();
    }

    public void RefreshGroups()
    {
        _groupManageTab.RefreshGroupListView();
        _groupManageTab.RefreshGrpUserCombo();
        _createAccountTab.RefreshAdvisorCombo();
    }

    public void RefreshDepartUsers()
    {
        _departureTab.RefreshDepartUserCombo();
    }

    public void SelectTab(int index)
    {
        if (index >= 0 && index < _tabs.TabPages.Count)
            _tabs.SelectedIndex = index;
    }
}
