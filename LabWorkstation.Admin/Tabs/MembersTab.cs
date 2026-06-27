using System.Diagnostics;
using LabWorkstation.Common;
using LabWorkstation.Common.Audit;
using LabWorkstation.Common.Configuration;
using LabWorkstation.Common.LocalAccounts;
using LabWorkstation.Common.Security;
using LabWorkstation.Common.Store;
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
            Size = new Size(745, 340)
        };
        Controls.Add(listGroup);

        _memberList = new ListView
        {
            Location = new Point(12, 22),
            Size = new Size(590, 300),
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
        var removeBtn = NewBtn("从组中移除", 56, (_, _) => RemoveFromGroup());
        var enableBtn = NewBtn("启用账户", 90, (_, _) => ToggleUser(enable: true));
        var disableBtn = NewBtn("禁用账户", 124, (_, _) => ToggleUser(enable: false));
        var resetPwdBtn = NewBtn("重置密码", 158, (_, _) => ResetPassword());
        var changeGroupBtn = NewBtn("修改分组", 192, (_, _) => ChangeGroup());
        listGroup.Controls.Add(refreshBtn);
        listGroup.Controls.Add(removeBtn);
        listGroup.Controls.Add(enableBtn);
        listGroup.Controls.Add(disableBtn);
        listGroup.Controls.Add(resetPwdBtn);
        listGroup.Controls.Add(changeGroupBtn);

        // 删除账户按钮（红色背景，自定义样式，不使用 NewBtn）
        var deleteAccountBtn = new Button
        {
            Text = "删除账户",
            Location = new Point(618, 226),
            Size = new Size(112, 30),
            BackColor = Color.FromArgb(180, 30, 30),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        deleteAccountBtn.FlatAppearance.BorderSize = 0;
        deleteAccountBtn.Click += (_, _) => DeleteAccount();
        listGroup.Controls.Add(deleteAccountBtn);

        // 切换用户按钮（蓝色背景，自定义样式，不使用 NewBtn）
        var switchUserBtn = new Button
        {
            Text = "切换用户",
            Location = new Point(618, 264),
            Size = new Size(112, 30),
            BackColor = Color.FromArgb(0, 122, 204),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        switchUserBtn.FlatAppearance.BorderSize = 0;
        switchUserBtn.Click += async (_, _) => await SwitchUserAsync();
        listGroup.Controls.Add(switchUserBtn);
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
            // 同步更新密码存储，确保切换用户时使用新密码（与 TrayApp 自助改密码保持一致）
            UserStore.UpdatePassword(username, dlg.NewPassword);
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

    /// <summary>
    /// 删除账户：彻底删除账户、Profile 和个人数据目录，不可撤销。
    /// 删除后可用同名重新创建账户。
    /// </summary>
    private void DeleteAccount()
    {
        try
        {
            var username = GetSelectedUser();
            if (username == null) return;

            var msg = $"确定要删除账户 '{username}' 吗？\n\n" +
                      "此操作将永久删除账户、Profile和个人数据目录，不可撤销。\n" +
                      "删除后可用同名重新创建。";
            if (MessageBox.Show(msg, "危险操作确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning)
                != DialogResult.Yes) return;

            _shell.Log($"开始删除账户 '{username}'...");
            LabAccountService.DeleteLabUser(username, m => _shell.Log(m));

            _shell.Log($"账户 '{username}' 已删除");
            AuditLogger.Write("DELETE_USER", username);

            _shell.RefreshMembers();
            _shell.RefreshDepartUsers();
            MessageBox.Show($"账户 '{username}' 已删除", "完成",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (LabOperationException ex)
        {
            _shell.Log($"删除账户失败: {ex.Message}", "ERROR");
            AuditLogger.Write("DELETE_USER", ex.Target, AuditLogger.Result.Failed, ex.Detail);
            MessageBox.Show($"删除账户失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            _shell.Log($"删除账户失败: {ex.Message}", "ERROR");
            MessageBox.Show($"删除账户失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// 切换用户：通过远程桌面以指定用户身份登录本机，管理员会话保持不变。
    /// 使用 UserStore 中存储的密码自动登录，无需管理员知道密码，也不修改密码。
    /// 凭据通过 Windows 凭据管理器（CredWrite）保存，不经过命令行参数（避免密码被进程列表窥视）。
    /// 异步等待 mstsc 退出，避免阻塞 Admin UI（用户可在 RDP 会话期间继续使用 Admin 面板）。
    /// </summary>
    private async Task SwitchUserAsync()
    {
        const string rdpServer = "127.0.0.1";
        const string credTarget = "TERMSRV/127.0.0.1";
        var username = "";
        var credentialSaved = false;
        try
        {
            username = GetSelectedUser() ?? "";
            if (string.IsNullOrEmpty(username)) return;

            // 从 UserStore 获取存储的密码
            var storedPassword = UserStore.GetStoredPassword(username);
            if (string.IsNullOrEmpty(storedPassword))
            {
                MessageBox.Show(
                    $"无法获取用户 '{username}' 的存储密码。\n" +
                    "该账户可能是通过旧版工具创建的，缺少密码记录。\n" +
                    "请通过Admin面板重置该用户密码后再切换。",
                    "无法切换", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (MessageBox.Show(
                $"将以用户 '{username}' 身份打开远程桌面连接。\n" +
                "管理员会话保持不变，关闭远程桌面窗口即可返回。\n" +
                "Admin 面板在此期间仍可正常使用。",
                "确认切换用户", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
                return;

            // 清除上次切换可能残留的 RDP 凭据（generic + domain_password × 2 个 target）
            CredentialManager.ClearRdpCredentials(rdpServer);

            // 使用 Windows 凭据管理器（CredWrite）保存凭据
            // target 使用 TERMSRV/127.0.0.1（mstsc 实际查找的 target 格式）
            if (!CredentialManager.WriteCredential(credTarget, username, storedPassword))
            {
                var err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                throw new Exception($"CredWrite 失败（Win32 错误码 {err}）");
            }
            credentialSaved = true;

            // 启动 mstsc
            _shell.Log($"启动远程桌面连接（用户 '{username}'）...");
            var mstscPsi = new ProcessStartInfo
            {
                FileName = "mstsc.exe",
                Arguments = $"/v:{rdpServer}",
                UseShellExecute = false
            };
            var mstscProc = Process.Start(mstscPsi);
            if (mstscProc == null)
                throw new Exception("无法启动远程桌面进程 mstsc.exe");

            // 异步等待 mstsc 退出，不阻塞 UI 线程
            await mstscProc.WaitForExitAsync();

            AuditLogger.Write("SWITCH_USER", username, detail: "远程桌面会话已结束");
            _shell.Log($"已结束用户 '{username}' 的远程桌面会话");
        }
        catch (Exception ex)
        {
            _shell.Log($"切换用户失败: {ex.Message}", "ERROR");
            AuditLogger.Write("SWITCH_USER", username, AuditLogger.Result.Failed, ex.Message);
            MessageBox.Show($"切换用户失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            // 清理所有 RDP 相关凭据（generic + domain_password × 2 个 target）
            // 防止残留凭据导致下次切换用户时进入上一次的会话
            if (credentialSaved)
            {
                try { CredentialManager.ClearRdpCredentials(rdpServer); }
                catch { }
            }
        }
    }
}
