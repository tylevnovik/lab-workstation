using LabWorkstation.Common;
using LabWorkstation.Common.Audit;
using LabWorkstation.Common.Configuration;
using LabWorkstation.Common.LocalAccounts;
using LabWorkstation.Admin.Dialogs;

namespace LabWorkstation.Admin.Tabs;

/// <summary>Tab 3：分组管理。导师组列表、添加/移除成员、新建/删除导师组。</summary>
public class GroupManageTab : UserControl
{
    private readonly IAppShell _shell;
    private readonly ListView _groupListView;
    private readonly ListView _groupMemberList;
    private readonly ComboBox _grpUserCombo;

    public GroupManageTab(IAppShell shell)
    {
        _shell = shell;
        Text = "分组管理";

        var groupNote = new Label
        {
            Text = "注意：用户必须同时属于 Lab_All（全员组）和某个导师组。",
            Location = new Point(20, 12),
            AutoSize = true,
            ForeColor = Color.FromArgb(180, 0, 0),
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold)
        };
        Controls.Add(groupNote);

        // 左侧面板：导师组列表
        var leftPanel = new GroupBox
        {
            Text = "导师组列表",
            Location = new Point(15, 40),
            Size = new Size(300, 360)
        };
        Controls.Add(leftPanel);

        _groupListView = new ListView
        {
            Location = new Point(10, 22),
            Size = new Size(278, 285),
            View = View.Details,
            FullRowSelect = true,
            GridLines = true
        };
        _groupListView.Columns.Add("导师组名称", 170);
        _groupListView.Columns.Add("成员数", 70);
        _groupListView.SelectedIndexChanged += (_, _) => { RefreshGroupMemberList(); RefreshGrpUserCombo(); };
        leftPanel.Controls.Add(_groupListView);

        var grpRefreshBtn = new Button { Text = "刷新", Location = new Point(10, 315), Size = new Size(80, 30) };
        grpRefreshBtn.Click += (_, _) => { RefreshGroupListView(); RefreshGrpUserCombo(); };
        leftPanel.Controls.Add(grpRefreshBtn);

        var grpNewBtn = new Button { Text = "新建导师组", Location = new Point(100, 315), Size = new Size(90, 30) };
        grpNewBtn.Click += (_, _) => NewAdvisorGroup();
        leftPanel.Controls.Add(grpNewBtn);

        var grpDelBtn = new Button { Text = "删除导师组", Location = new Point(200, 315), Size = new Size(90, 30) };
        grpDelBtn.Click += (_, _) => DeleteAdvisorGroup();
        leftPanel.Controls.Add(grpDelBtn);

        // 右侧面板：选中组的成员
        var rightPanel = new GroupBox
        {
            Text = "选中组的成员",
            Location = new Point(330, 40),
            Size = new Size(435, 360)
        };
        Controls.Add(rightPanel);

        _groupMemberList = new ListView
        {
            Location = new Point(10, 22),
            Size = new Size(412, 245),
            View = View.Details,
            FullRowSelect = true,
            GridLines = true
        };
        _groupMemberList.Columns.Add("用户名", 130);
        _groupMemberList.Columns.Add("显示名称", 130);
        _groupMemberList.Columns.Add("账户状态", 80);
        rightPanel.Controls.Add(_groupMemberList);

        var addMemberLabel = new Label { Text = "选择用户：", Location = new Point(10, 278), AutoSize = true };
        rightPanel.Controls.Add(addMemberLabel);

        _grpUserCombo = new ComboBox
        {
            Location = new Point(85, 275),
            Size = new Size(170, 25),
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        rightPanel.Controls.Add(_grpUserCombo);

        var grpAddBtn = new Button { Text = "添加成员到此组", Location = new Point(268, 273), Size = new Size(150, 28) };
        grpAddBtn.Click += (_, _) => AddMemberToGroup();
        rightPanel.Controls.Add(grpAddBtn);

        var grpRemoveMemberBtn = new Button { Text = "从此组移除", Location = new Point(268, 310), Size = new Size(150, 28) };
        grpRemoveMemberBtn.Click += (_, _) => RemoveMemberFromGroup();
        rightPanel.Controls.Add(grpRemoveMemberBtn);
    }

    public void RefreshGroupListView()
    {
        _groupListView.Items.Clear();
        List<string> advisors;
        try { advisors = GroupManager.GetAllAdvisorGroups(); }
        catch (Exception ex) { _shell.Log($"获取导师组列表失败: {ex.Message}", "ERROR"); return; }

        foreach (var a in advisors)
        {
            var gName = LabConfig.AdvisorToGroupName(a);
            int memberCount = 0;
            try { memberCount = GroupManager.GetMembers(gName).Count; } catch { }
            var item = new ListViewItem(gName) { Tag = a };
            item.SubItems.Add(memberCount.ToString());
            _groupListView.Items.Add(item);
        }
        _shell.Log($"导师组列表已刷新，共 {advisors.Count} 个导师组");
    }

    public void RefreshGroupMemberList()
    {
        _groupMemberList.Items.Clear();
        if (_groupListView.SelectedItems.Count == 0) return;

        var gName = _groupListView.SelectedItems[0].Text;
        List<string> members;
        try { members = GroupManager.GetMembers(gName); }
        catch (Exception ex) { _shell.Log($"获取组 '{gName}' 成员失败: {ex.Message}", "ERROR"); return; }

        foreach (var name in members)
        {
            var item = new ListViewItem(name);
            string displayName = "";
            bool enabled = true;
            try
            {
                var info = AccountManager.GetUserInfo(name);
                if (info != null) { displayName = info.DisplayName; enabled = info.Enabled; }
            }
            catch { }
            item.SubItems.Add(displayName);
            item.SubItems.Add(enabled ? "已启用" : "已禁用");
            _groupMemberList.Items.Add(item);
        }
    }

    public void RefreshGrpUserCombo()
    {
        _grpUserCombo.Items.Clear();
        try
        {
            if (GroupManager.GroupExists(LabConfig.AllGroup))
            {
                foreach (var m in GroupManager.GetMembers(LabConfig.AllGroup))
                    _grpUserCombo.Items.Add(m);
            }
            // 也包含不在 Lab_All 中的本地用户
            foreach (var u in AccountManager.GetAllUsernames())
            {
                if (!_grpUserCombo.Items.Contains(u))
                    _grpUserCombo.Items.Add(u);
            }
        }
        catch (Exception ex) { _shell.Log($"刷新用户下拉列表失败: {ex.Message}", "ERROR"); }
        if (_grpUserCombo.Items.Count > 0) _grpUserCombo.SelectedIndex = 0;
    }

    private void NewAdvisorGroup()
    {
        using var dlg = new NewAdvisorGroupDialog();
        if (dlg.ShowDialog() != DialogResult.OK) return;
        try
        {
            LabAccountService.CreateAdvisorGroup(dlg.AdvisorName);
            _shell.Log($"导师组 '{LabConfig.AdvisorToGroupName(dlg.AdvisorName)}' 创建成功");
            AuditLogger.Write("CREATE_ADVISOR_GROUP", dlg.AdvisorName);
            RefreshGroupListView();
            _shell.RefreshGroups();
            MessageBox.Show(
                $"导师组 '{LabConfig.AdvisorToGroupName(dlg.AdvisorName)}' 已创建\n文件夹：{Path.Combine(LabConfig.SharePath, dlg.AdvisorName)}",
                "成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (LabOperationException ex)
        {
            _shell.Log($"创建导师组失败: {ex.Message}", "ERROR");
            AuditLogger.Write("CREATE_ADVISOR_GROUP", ex.Target, AuditLogger.Result.Failed, ex.Detail);
            MessageBox.Show($"创建导师组失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            _shell.Log($"创建导师组失败: {ex.Message}", "ERROR");
            AuditLogger.Write("CREATE_ADVISOR_GROUP", dlg.AdvisorName, AuditLogger.Result.Failed, ex.Message);
            MessageBox.Show($"创建导师组失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void DeleteAdvisorGroup()
    {
        if (_groupListView.SelectedItems.Count == 0)
        {
            MessageBox.Show("请先选择一个导师组", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning); return;
        }
        var gName = _groupListView.SelectedItems[0].Text;
        var advisorName = _groupListView.SelectedItems[0].Tag as string ?? LabConfig.GroupNameToAdvisor(gName);

        var confirm = MessageBox.Show(
            $"确定删除导师组 '{gName}' 吗？\n\n注意：\n- 仅删除安全组，不会删除文件夹 {Path.Combine(LabConfig.SharePath, advisorName)}\n- 该组的成员不会被删除，但会失去对此导师区的访问权限",
            "确认删除", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes) return;

        try
        {
            GroupManager.DeleteGroup(gName);
            _shell.Log($"已删除导师组 '{gName}'（文件夹保留）");
            AuditLogger.Write("DELETE_ADVISOR_GROUP", advisorName);
            RefreshGroupListView();
            _groupMemberList.Items.Clear();
            _shell.RefreshGroups();
            MessageBox.Show($"导师组 '{gName}' 已删除\n文件夹 {Path.Combine(LabConfig.SharePath, advisorName)} 已保留",
                "成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (LabOperationException ex)
        {
            _shell.Log($"删除导师组失败: {ex.Message}", "ERROR");
            AuditLogger.Write("DELETE_ADVISOR_GROUP", ex.Target, AuditLogger.Result.Failed, ex.Detail);
            MessageBox.Show($"删除导师组失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            _shell.Log($"删除导师组失败: {ex.Message}", "ERROR");
            MessageBox.Show($"删除导师组失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void AddMemberToGroup()
    {
        if (_groupListView.SelectedItems.Count == 0)
        {
            MessageBox.Show("请先选择一个导师组", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning); return;
        }
        var username = _grpUserCombo.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(username))
        {
            MessageBox.Show("请选择要添加的用户", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning); return;
        }
        var gName = _groupListView.SelectedItems[0].Text;

        try
        {
            if (!AccountManager.UserExists(username))
            {
                MessageBox.Show($"用户 '{username}' 不存在", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error); return;
            }
            GroupManager.AddMember(gName, username);
            _shell.Log($"已将 '{username}' 添加到 '{gName}'");
            AuditLogger.Write("ADD_TO_GROUP", username, detail: $"组: {gName}");
            RefreshGroupMemberList();
            RefreshGroupListView();
            MessageBox.Show($"已将 '{username}' 添加到 '{gName}'", "成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (LabOperationException ex)
        {
            _shell.Log($"添加成员失败: {ex.Message}", "ERROR");
            AuditLogger.Write("ADD_TO_GROUP", ex.Target, AuditLogger.Result.Failed, ex.Detail);
            MessageBox.Show($"添加成员失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            _shell.Log($"添加成员失败: {ex.Message}", "ERROR");
            MessageBox.Show($"添加成员失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RemoveMemberFromGroup()
    {
        if (_groupListView.SelectedItems.Count == 0)
        {
            MessageBox.Show("请先选择一个导师组", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning); return;
        }
        if (_groupMemberList.SelectedItems.Count == 0)
        {
            MessageBox.Show("请先选择要移除的成员", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning); return;
        }
        var gName = _groupListView.SelectedItems[0].Text;
        var username = _groupMemberList.SelectedItems[0].Text;

        var confirm = MessageBox.Show($"确定将 '{username}' 从 '{gName}' 中移除吗？\n（用户不会从 Lab_All 中移除）",
            "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes) return;

        try
        {
            GroupManager.RemoveMember(gName, username);
            _shell.Log($"已将 '{username}' 从 '{gName}' 移除");
            AuditLogger.Write("REMOVE_FROM_ADVISOR_GROUP", username, detail: $"组: {gName}");
            RefreshGroupMemberList();
            RefreshGroupListView();
        }
        catch (LabOperationException ex)
        {
            _shell.Log($"移除成员失败: {ex.Message}", "ERROR");
            AuditLogger.Write("REMOVE_FROM_ADVISOR_GROUP", ex.Target, AuditLogger.Result.Failed, ex.Detail);
            MessageBox.Show($"移除成员失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            _shell.Log($"移除成员失败: {ex.Message}", "ERROR");
            MessageBox.Show($"移除成员失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
