using LabWorkstation.Common;
using LabWorkstation.Common.Audit;
using LabWorkstation.Common.Configuration;
using LabWorkstation.Common.LocalAccounts;
using LabWorkstation.Admin.Dialogs;

namespace LabWorkstation.Admin.Tabs;

/// <summary>Tab 1：成员管理。用户列表、启用/禁用、重置密码、修改分组、从组移除。</summary>
public class MembersTab : UserControl
{
    private readonly IAppShell _shell;
    private readonly ComboBox _filterCombo;
    private readonly ListView _memberList;
    private readonly Label _memberCountLabel;

    public MembersTab(IAppShell shell)
    {
        _shell = shell;
        Text = "成员管理";

        // -- 组筛选栏 --
        var filterGroup = new GroupBox
        {
            Text = "按组筛选",
            Location = new Point(15, 10),
            Size = new Size(745, 50)
        };
        Controls.Add(filterGroup);

        var filterLabel = new Label
        {
            Text = "选择组：",
            Location = new Point(15, 20),
            AutoSize = true
        };
        filterGroup.Controls.Add(filterLabel);

        _filterCombo = new ComboBox
        {
            Location = new Point(80, 17),
            Size = new Size(200, 25),
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        _filterCombo.SelectedIndexChanged += (_, _) => RefreshMemberList();
        filterGroup.Controls.Add(_filterCombo);

        var filterRefreshBtn = new Button
        {
            Text = "刷新筛选",
            Location = new Point(300, 15),
            Size = new Size(90, 28)
        };
        filterRefreshBtn.Click += (_, _) => { RefreshFilterCombo(); RefreshMemberList(); };
        filterGroup.Controls.Add(filterRefreshBtn);

        _memberCountLabel = new Label
        {
            Location = new Point(420, 20),
            AutoSize = true,
            ForeColor = Color.Gray
        };
        filterGroup.Controls.Add(_memberCountLabel);

        // -- 成员列表 --
        var listGroup = new GroupBox
        {
            Text = "成员列表",
            Location = new Point(15, 68),
            Size = new Size(745, 330)
        };
        Controls.Add(listGroup);

        _memberList = new ListView
        {
            Location = new Point(12, 22),
            Size = new Size(590, 295),
            View = View.Details,
            FullRowSelect = true,
            GridLines = true
        };
        _memberList.Columns.Add("用户名", 120);
        _memberList.Columns.Add("显示名称", 110);
        _memberList.Columns.Add("所属导师组", 110);
        _memberList.Columns.Add("账户状态", 80);
        _memberList.Columns.Add("上次登录", 140);
        listGroup.Controls.Add(_memberList);

        var refreshBtn = NewBtn("刷新列表", 22, (_, _) => { RefreshFilterCombo(); RefreshMemberList(); });
        var removeBtn = NewBtn("从组中移除", 60, (_, _) => RemoveFromGroup());
        var enableBtn = NewBtn("启用账户", 100, (_, _) => ToggleUser(enable: true));
        var disableBtn = NewBtn("禁用账户", 140, (_, _) => ToggleUser(enable: false));
        var resetPwdBtn = NewBtn("重置密码", 180, (_, _) => ResetPassword());
        var changeGroupBtn = NewBtn("修改分组", 220, (_, _) => ChangeGroup());
        listGroup.Controls.Add(refreshBtn);
        listGroup.Controls.Add(removeBtn);
        listGroup.Controls.Add(enableBtn);
        listGroup.Controls.Add(disableBtn);
        listGroup.Controls.Add(resetPwdBtn);
        listGroup.Controls.Add(changeGroupBtn);
    }

    private Button NewBtn(string text, int y, EventHandler onClick)
    {
        var b = new Button
        {
            Text = text,
            Location = new Point(618, y),
            Size = new Size(112, 30)
        };
        b.Click += onClick;
        return b;
    }

    public void RefreshFilterCombo()
    {
        _filterCombo.Items.Clear();
        _filterCombo.Items.Add("全部");
        _filterCombo.Items.Add(LabConfig.AllGroup);
        try
        {
            foreach (var a in GroupManager.GetAllAdvisorGroups())
                _filterCombo.Items.Add(LabConfig.AdvisorToGroupName(a));
        }
        catch (Exception ex) { _shell.Log($"获取导师组列表失败: {ex.Message}", "ERROR"); }
        if (_filterCombo.Items.Count > 0) _filterCombo.SelectedIndex = 0;
    }

    public void RefreshMemberList()
    {
        _memberList.Items.Clear();
        var selectedFilter = _filterCombo.SelectedItem as string ?? "全部";

        List<string> usernames = new();
        try
        {
            if (selectedFilter == "全部" || selectedFilter == LabConfig.AllGroup)
            {
                if (GroupManager.GroupExists(LabConfig.AllGroup))
                    usernames = GroupManager.GetMembers(LabConfig.AllGroup);
            }
            else
            {
                if (GroupManager.GroupExists(selectedFilter))
                    usernames = GroupManager.GetMembers(selectedFilter);
            }
        }
        catch (Exception ex) { _shell.Log($"获取成员列表失败: {ex.Message}", "ERROR"); }

        foreach (var name in usernames)
        {
            var item = new ListViewItem(name);

            string displayName = "";
            bool enabled = true;
            DateTime? lastLogon = null;
            try
            {
                var info = AccountManager.GetUserInfo(name);
                if (info != null)
                {
                    displayName = info.DisplayName;
                    enabled = info.Enabled;
                    lastLogon = info.LastLogon;
                }
            }
            catch { /* 忽略单个用户查询失败 */ }
            item.SubItems.Add(displayName);

            var advisor = "";
            try { advisor = GroupManager.GetUserAdvisorGroup(name); } catch { }
            item.SubItems.Add(string.IsNullOrEmpty(advisor) ? "(未分配)" : LabConfig.AdvisorToGroupName(advisor));

            item.SubItems.Add(enabled ? "已启用" : "已禁用");
            item.SubItems.Add(lastLogon.HasValue ? lastLogon.Value.ToString("yyyy-MM-dd HH:mm") : "未知");

            _memberList.Items.Add(item);
        }

        _memberCountLabel.Text = $"共 {usernames.Count} 个成员";
        _shell.Log($"成员列表已刷新，当前显示 {usernames.Count} 人（筛选：{selectedFilter}）");
    }

    private string? GetSelectedUser()
    {
        if (_memberList.SelectedItems.Count == 0)
        {
            MessageBox.Show("请先选择一个成员", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }
        return _memberList.SelectedItems[0].Text;
    }

    private void RemoveFromGroup()
    {
        try
        {
            var username = GetSelectedUser();
            if (username == null) return;
            var advisor = GroupManager.GetUserAdvisorGroup(username);
            var advisorGroup = string.IsNullOrEmpty(advisor) ? "" : LabConfig.AdvisorToGroupName(advisor);

            var msg = $"确定将 '{username}' 从所有组中移除吗？\n\n" +
                      $"- 将从 {LabConfig.AllGroup}（全员组）移除\n";
            if (!string.IsNullOrEmpty(advisorGroup)) msg += $"- 将从 {advisorGroup}（导师组）移除\n";
            msg += "\n移除后该用户将无法访问任何共享文件夹。";

            if (MessageBox.Show(msg, "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

            GroupManager.RemoveMember(LabConfig.AllGroup, username);
            _shell.Log($"已将 '{username}' 从 {LabConfig.AllGroup} 移除");
            if (!string.IsNullOrEmpty(advisorGroup))
            {
                GroupManager.RemoveMember(advisorGroup, username);
                _shell.Log($"已将 '{username}' 从 {advisorGroup} 移除");
            }
            AuditLogger.Write("REMOVE_FROM_GROUP", username);
            RefreshMemberList();
        }
        catch (LabOperationException ex)
        {
            _shell.Log($"移除失败: {ex.Message}", "ERROR");
            AuditLogger.Write("REMOVE_FROM_GROUP", ex.Target, AuditLogger.Result.Failed, ex.Detail);
            MessageBox.Show($"移除失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            _shell.Log($"移除失败: {ex.Message}", "ERROR");
            MessageBox.Show($"移除失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ToggleUser(bool enable)
    {
        var action = enable ? "ENABLE_USER" : "DISABLE_USER";
        try
        {
            var username = GetSelectedUser();
            if (username == null) return;

            if (!enable)
            {
                if (MessageBox.Show($"确定禁用账户 '{username}' 吗？\n禁用后该用户将无法登录，但账户数据保留。",
                        "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            }

            if (enable) AccountManager.EnableUser(username);
            else AccountManager.DisableUser(username);

            _shell.Log($"已{(enable ? "启用" : "禁用")}账户 '{username}'");
            AuditLogger.Write(action, username);
            RefreshMemberList();
        }
        catch (LabOperationException ex)
        {
            _shell.Log($"{(enable ? "启用" : "禁用")}失败: {ex.Message}", "ERROR");
            AuditLogger.Write(action, ex.Target, AuditLogger.Result.Failed, ex.Detail);
            MessageBox.Show($"{(enable ? "启用" : "禁用")}失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            _shell.Log($"{(enable ? "启用" : "禁用")}失败: {ex.Message}", "ERROR");
            MessageBox.Show($"{(enable ? "启用" : "禁用")}失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ResetPassword()
    {
        try
        {
            var username = GetSelectedUser();
            if (username == null) return;

            using var dlg = new ResetPasswordDialog(username);
            if (dlg.ShowDialog() != DialogResult.OK) return;

            AccountManager.ResetPassword(username, dlg.NewPassword);
            _shell.Log($"已重置 '{username}' 的密码");
            AuditLogger.Write("RESET_PASSWORD", username);
            MessageBox.Show("密码已重置", "成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (LabOperationException ex)
        {
            _shell.Log($"重置密码失败: {ex.Message}", "ERROR");
            AuditLogger.Write("RESET_PASSWORD", ex.Target, AuditLogger.Result.Failed, ex.Detail);
            MessageBox.Show($"重置密码失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            _shell.Log($"重置密码失败: {ex.Message}", "ERROR");
            MessageBox.Show($"重置密码失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ChangeGroup()
    {
        try
        {
            var username = GetSelectedUser();
            if (username == null) return;
            var currentAdvisor = GroupManager.GetUserAdvisorGroup(username);

            using var dlg = new ChangeGroupDialog(username, currentAdvisor);
            if (dlg.ShowDialog() != DialogResult.OK) return;

            var newAdvisor = dlg.SelectedAdvisor;
            if (newAdvisor == currentAdvisor)
            {
                MessageBox.Show("用户已在该导师组中", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var oldGroup = string.IsNullOrEmpty(currentAdvisor) ? "" : LabConfig.AdvisorToGroupName(currentAdvisor);
            var newGroup = LabConfig.AdvisorToGroupName(newAdvisor);

            if (!string.IsNullOrEmpty(oldGroup))
            {
                GroupManager.RemoveMember(oldGroup, username);
                _shell.Log($"已将 '{username}' 从 {oldGroup} 移除");
            }
            GroupManager.AddMember(newGroup, username);
            _shell.Log($"已将 '{username}' 加入 {newGroup}");
            AuditLogger.Write("CHANGE_GROUP", username, detail: $"从 {oldGroup} 转到 {newGroup}");

            var fromDisplay = string.IsNullOrEmpty(currentAdvisor) ? "(未分配)" : oldGroup;
            MessageBox.Show($"已将 '{username}' 从 {fromDisplay} 移动到 {newGroup}", "成功",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            RefreshMemberList();
        }
        catch (LabOperationException ex)
        {
            _shell.Log($"修改分组失败: {ex.Message}", "ERROR");
            AuditLogger.Write("CHANGE_GROUP", ex.Target, AuditLogger.Result.Failed, ex.Detail);
            MessageBox.Show($"修改分组失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            _shell.Log($"修改分组失败: {ex.Message}", "ERROR");
            MessageBox.Show($"修改分组失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
