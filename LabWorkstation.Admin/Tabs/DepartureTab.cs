using LabWorkstation.Common;
using LabWorkstation.Common.Audit;
using LabWorkstation.Common.Configuration;
using LabWorkstation.Common.LocalAccounts;
using LabWorkstation.Common.Storage;

namespace LabWorkstation.Admin.Tabs;

/// <summary>Tab 6：离校管理。禁用账户、归档数据、从组移除、生成交接清单。</summary>
public class DepartureTab : UserControl
{
    private readonly IAppShell _shell;
    private readonly ComboBox _departUserCombo;
    private readonly RichTextBox _departInfoBox;
    private readonly RichTextBox _departStatusBox;
    private readonly CheckBox _deleteAccountCheck;
    private Button _departExecBtn;

    public DepartureTab(IAppShell shell)
    {
        _shell = shell;
        Text = "离校管理";

        var departIntro = new Label
        {
            Text = "学生离校时，执行此流程：禁用账户、归档个人数据和组内数据、从所有组中移除，并生成工作交接清单。",
            Location = new Point(20, 15),
            AutoSize = true
        };
        Controls.Add(departIntro);

        // -- 选择用户 --
        var departUserGroup = new GroupBox
        {
            Text = "选择用户",
            Location = new Point(15, 50),
            Size = new Size(745, 60)
        };
        Controls.Add(departUserGroup);

        var departUserLabel = new Label { Text = "离校用户：", Location = new Point(15, 22), AutoSize = true };
        departUserGroup.Controls.Add(departUserLabel);

        _departUserCombo = new ComboBox
        {
            Location = new Point(95, 19),
            Size = new Size(250, 25),
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        departUserGroup.Controls.Add(_departUserCombo);

        var departRefreshBtn = new Button { Text = "刷新列表", Location = new Point(360, 17), Size = new Size(90, 28) };
        departRefreshBtn.Click += (_, _) => RefreshDepartUserCombo();
        departUserGroup.Controls.Add(departRefreshBtn);

        var departInfoBtn = new Button { Text = "查看用户信息", Location = new Point(465, 17), Size = new Size(120, 28) };
        departInfoBtn.Click += (_, _) => ShowUserInfo();
        departUserGroup.Controls.Add(departInfoBtn);

        // -- 用户信息显示区 --
        var departInfoGroup = new GroupBox
        {
            Text = "用户信息",
            Location = new Point(15, 118),
            Size = new Size(745, 140)
        };
        Controls.Add(departInfoGroup);

        _departInfoBox = new RichTextBox
        {
            Location = new Point(12, 22),
            Size = new Size(720, 108),
            ReadOnly = true,
            BackColor = Color.FromArgb(245, 245, 245),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Microsoft YaHei UI", 9F)
        };
        departInfoGroup.Controls.Add(_departInfoBox);

        // -- 步骤状态显示 --
        var departStatusGroup = new GroupBox
        {
            Text = "执行进度",
            Location = new Point(15, 266),
            Size = new Size(745, 100)
        };
        Controls.Add(departStatusGroup);

        _departStatusBox = new RichTextBox
        {
            Location = new Point(12, 22),
            Size = new Size(720, 68),
            ReadOnly = true,
            BackColor = Color.FromArgb(250, 250, 250),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 8.5F)
        };
        departStatusGroup.Controls.Add(_departStatusBox);

        // -- 彻底删除账户选项 --
        _deleteAccountCheck = new CheckBox
        {
            Text = "归档后彻底删除账户（删除Profile+个人目录+Windows账户，删除后可用同名重建）",
            Location = new Point(15, 378),
            Size = new Size(745, 24),
            ForeColor = Color.FromArgb(180, 30, 30),
            Checked = false
        };
        Controls.Add(_deleteAccountCheck);

        // -- 执行离校按钮 --
        _departExecBtn = new Button
        {
            Text = "执行离校流程",
            Location = new Point(300, 408),
            Size = new Size(180, 38),
            BackColor = Color.FromArgb(180, 30, 30),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold)
        };
        _departExecBtn.Click += (_, _) => ExecuteDeparture();
        Controls.Add(_departExecBtn);
    }

    public void RefreshDepartUserCombo()
    {
        _departUserCombo.Items.Clear();
        try
        {
            if (GroupManager.GroupExists(LabConfig.AllGroup))
            {
                foreach (var m in GroupManager.GetMembers(LabConfig.AllGroup))
                    _departUserCombo.Items.Add(m);
            }
        }
        catch (Exception ex) { _shell.Log($"刷新离校用户列表失败: {ex.Message}", "ERROR"); }
        if (_departUserCombo.Items.Count > 0) _departUserCombo.SelectedIndex = 0;
        _shell.Log($"离校用户列表已刷新，共 {_departUserCombo.Items.Count} 人");
    }

    private void ShowUserInfo()
    {
        try
        {
            var username = _departUserCombo.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(username))
            {
                MessageBox.Show("请先选择一个用户", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning); return;
            }

            _departInfoBox.Clear();

            string displayName = "(未知)";
            string enabledText = "(未知)";
            try
            {
                var info = AccountManager.GetUserInfo(username);
                if (info != null)
                {
                    displayName = info.DisplayName;
                    enabledText = info.Enabled ? "已启用" : "已禁用";
                }
            }
            catch { }

            var advisor = GroupManager.GetUserAdvisorGroup(username);
            var advisorDisplay = string.IsNullOrEmpty(advisor) ? "(未分配)" : LabConfig.AdvisorToGroupName(advisor);

            var personalPath = Path.Combine(LabConfig.UsersRootPath, username);
            var personalSize = FolderSizer.GetSizeDisplay(personalPath);

            string groupPathDisplay = "(无)";
            string groupSizeDisplay = "(未分配导师组)";
            if (!string.IsNullOrEmpty(advisor))
            {
                var advisorPath = Path.Combine(LabConfig.SharePath, advisor);
                groupPathDisplay = advisorPath;
                groupSizeDisplay = FolderSizer.GetSizeDisplay(advisorPath);
            }

            var info2 = $"用户名：{username}\r\n" +
                        $"显示名称：{displayName}\r\n" +
                        $"账户状态：{enabledText}\r\n" +
                        $"导师组：{advisorDisplay}\r\n" +
                        $"个人文件夹：{personalPath} ({personalSize})\r\n" +
                        $"组内数据区：{groupPathDisplay} ({groupSizeDisplay})";
            _departInfoBox.Text = info2;
            _shell.Log($"已查看用户 '{username}' 的信息");
        }
        catch (Exception ex)
        {
            _shell.Log($"查看用户信息失败: {ex.Message}", "ERROR");
            MessageBox.Show($"查看用户信息失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ExecuteDeparture()
    {
        var username = _departUserCombo.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(username))
        {
            MessageBox.Show("请先选择一个用户", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning); return;
        }

        var advisor = GroupManager.GetUserAdvisorGroup(username);
        var advisorDisplay = string.IsNullOrEmpty(advisor) ? "(未分配)" : LabConfig.AdvisorToGroupName(advisor);

        var deleteAccount = _deleteAccountCheck.Checked;

        var confirmMsg = $"确定要对用户 '{username}' 执行离校流程吗？\n\n" +
                         "即将执行以下操作：\n" +
                         (deleteAccount ? "1. 禁用账户\n" : "1. 禁用账户（用户将无法登录）\n") +
                         $"2. 归档个人数据到 {Path.Combine(LabConfig.PublicPath, "99_归档")}\\\n";
        if (!string.IsNullOrEmpty(advisor))
            confirmMsg += $"3. 归档组内数据（{advisorDisplay}）\n";
        confirmMsg += "4. 从所有组中移除\n" +
                      "5. 生成工作交接清单\n";
        if (deleteAccount)
        {
            confirmMsg += "6. 删除 Profile 和个人数据目录\n" +
                          "7. 删除 Windows 账户（删除后可用同名重建）\n";
        }
        confirmMsg += "\n此操作不可撤销，请确认。";

        if (deleteAccount)
        {
            confirmMsg += "\n\n注意：已勾选彻底删除，归档后将永久删除账户、Profile和个人目录，不可撤销。";
        }

        if (MessageBox.Show(confirmMsg, "确认离校", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

        _departExecBtn.Enabled = false;
        _departStatusBox.Clear();

        try
        {
            var result = LabAccountService.DepartUser(username, msg =>
            {
                _departStatusBox.AppendText($"{msg}\r\n");
                _departStatusBox.ScrollToCaret();
                Application.DoEvents();
                _shell.Log(msg);
            }, deleteAccount: _deleteAccountCheck.Checked);

            _departStatusBox.AppendText($"\n归档路径：{result.ArchiveDir}\r\n");
            _departStatusBox.ScrollToCaret();

            _shell.Log($"离校流程执行完毕！归档路径：{result.ArchiveDir}");

            _departExecBtn.Enabled = true;

            var successMsg = deleteAccount
                ? $"离校流程已执行完毕！\n\n用户：{username}\n归档路径：{result.ArchiveDir}\n\n账户、Profile 和个人数据目录已彻底删除，Windows 账户已删除（可用同名重建）。\n\n请通知相关人员完成纸质交接清单签字。"
                : $"离校流程已执行完毕！\n\n用户：{username}\n归档路径：{result.ArchiveDir}\n\n请通知相关人员完成纸质交接清单签字。";

            MessageBox.Show(successMsg, "离校完成", MessageBoxButtons.OK, MessageBoxIcon.Information);

            RefreshDepartUserCombo();
            _shell.RefreshMembers();
        }
        catch (LabOperationException ex)
        {
            _shell.Log($"离校流程执行失败: {ex.Message}", "ERROR");
            AuditLogger.Write("DEPARTURE", ex.Target, AuditLogger.Result.Failed, ex.Detail);
            _departExecBtn.Enabled = true;
            MessageBox.Show($"离校流程执行失败:\n{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            _shell.Log($"离校流程执行失败: {ex.Message}", "ERROR");
            _departExecBtn.Enabled = true;
            MessageBox.Show($"离校流程执行失败:\n{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
