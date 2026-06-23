using LabWorkstation.Common;
using LabWorkstation.Common.Audit;
using LabWorkstation.Common.Configuration;
using LabWorkstation.Common.LocalAccounts;
using LabWorkstation.Admin.Dialogs;

namespace LabWorkstation.Admin.Tabs;

/// <summary>Tab 2：创建账户。单个创建（用户名/密码/姓名/导师），可新建导师组。</summary>
public class CreateAccountTab : UserControl
{
    private readonly IAppShell _shell;
    private readonly TextBox _newUsername;
    private readonly TextBox _newPassword;
    private readonly TextBox _confirmPassword;
    private readonly TextBox _newDisplayName;
    private readonly ComboBox _advisorCombo;

    private const string NewAdvisorItem = "新建导师组...";

    public CreateAccountTab(IAppShell shell)
    {
        _shell = shell;
        Text = "创建账户";

        var infoLabel = new Label
        {
            Text = "为课题组新成员创建 Windows 账户，自动加入全员组和导师组，并在数据盘建立隔离的个人目录。",
            Location = new Point(20, 15),
            AutoSize = true
        };
        Controls.Add(infoLabel);

        var formGroup = new GroupBox
        {
            Text = "账户信息",
            Location = new Point(15, 45),
            Size = new Size(745, 310)
        };
        Controls.Add(formGroup);

        var ul = new Label { Text = "用户名（英文，登录用）：", Location = new Point(20, 32), Size = new Size(190, 25) };
        formGroup.Controls.Add(ul);
        _newUsername = new TextBox { Location = new Point(220, 30), Size = new Size(250, 25) };
        formGroup.Controls.Add(_newUsername);

        var pl = new Label { Text = "密码：", Location = new Point(20, 68), Size = new Size(190, 25) };
        formGroup.Controls.Add(pl);
        _newPassword = new TextBox { Location = new Point(220, 66), Size = new Size(250, 25), UseSystemPasswordChar = true };
        formGroup.Controls.Add(_newPassword);

        var cpl = new Label { Text = "确认密码：", Location = new Point(20, 104), Size = new Size(190, 25) };
        formGroup.Controls.Add(cpl);
        _confirmPassword = new TextBox { Location = new Point(220, 102), Size = new Size(250, 25), UseSystemPasswordChar = true };
        formGroup.Controls.Add(_confirmPassword);

        var genPwdBtn = new Button
        {
            Text = "随机生成密码",
            Location = new Point(490, 66),
            Size = new Size(130, 28)
        };
        genPwdBtn.Click += (_, _) =>
        {
            var pwd = LabAccountService.GenerateRandomPassword();
            _newPassword.Text = pwd;
            _confirmPassword.Text = pwd;
            _shell.Log("已生成随机密码（请记录并告知用户）");
        };
        formGroup.Controls.Add(genPwdBtn);

        var nl = new Label { Text = "显示名称（中文名，可选）：", Location = new Point(20, 140), Size = new Size(190, 25) };
        formGroup.Controls.Add(nl);
        _newDisplayName = new TextBox { Location = new Point(220, 138), Size = new Size(250, 25) };
        formGroup.Controls.Add(_newDisplayName);

        var advLabel = new Label { Text = "导师选择：", Location = new Point(20, 176), Size = new Size(190, 25) };
        formGroup.Controls.Add(advLabel);
        _advisorCombo = new ComboBox
        {
            Location = new Point(220, 174),
            Size = new Size(250, 25),
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        _advisorCombo.SelectedIndexChanged += OnAdvisorSelected;
        formGroup.Controls.Add(_advisorCombo);

        var tipLabel = new Label
        {
            Text = $"提示：创建后，用户可访问 {LabConfig.PublicPath}（公共数据）、{LabConfig.SharePath}\\[导师名]\\（导师组数据）和 {LabConfig.UsersRootPath}\\用户名（个人目录）。",
            ForeColor = Color.Gray,
            Location = new Point(20, 220),
            AutoSize = true
        };
        formGroup.Controls.Add(tipLabel);

        var createBtn = new Button
        {
            Text = "一键创建账户",
            Location = new Point(300, 370),
            Size = new Size(180, 38),
            BackColor = Color.FromArgb(0, 122, 204),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold)
        };
        createBtn.Click += (_, _) => CreateAccount();
        Controls.Add(createBtn);
    }

    public void RefreshAdvisorCombo()
    {
        var current = _advisorCombo.SelectedItem as string;
        _advisorCombo.Items.Clear();
        try
        {
            foreach (var a in GroupManager.GetAllAdvisorGroups())
                _advisorCombo.Items.Add(a);
        }
        catch (Exception ex) { _shell.Log($"获取导师组列表失败: {ex.Message}", "ERROR"); }
        _advisorCombo.Items.Add(NewAdvisorItem);

        if (current != null && _advisorCombo.Items.Contains(current))
            _advisorCombo.SelectedItem = current;
        else if (_advisorCombo.Items.Count > 1)
            _advisorCombo.SelectedIndex = 0;
    }

    private void OnAdvisorSelected(object? sender, EventArgs e)
    {
        if (_advisorCombo.SelectedItem as string == NewAdvisorItem)
        {
            using var dlg = new NewAdvisorGroupDialog();
            if (dlg.ShowDialog() == DialogResult.OK)
            {
                try
                {
                    LabAccountService.CreateAdvisorGroup(dlg.AdvisorName);
                    _shell.Log($"导师组 '{LabConfig.AdvisorToGroupName(dlg.AdvisorName)}' 创建成功");
                    AuditLogger.Write("CREATE_ADVISOR_GROUP", dlg.AdvisorName);
                    RefreshAdvisorCombo();
                    if (_advisorCombo.Items.Contains(dlg.AdvisorName))
                        _advisorCombo.SelectedItem = dlg.AdvisorName;
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
            // 关闭对话框后若仍停留在"新建导师组..."，恢复为第一项
            if (_advisorCombo.SelectedItem as string == NewAdvisorItem && _advisorCombo.Items.Count > 1)
                _advisorCombo.SelectedIndex = 0;
        }
    }

    private void CreateAccount()
    {
        var username = _newUsername.Text.Trim();
        var password = _newPassword.Text;
        var confirmPwd = _confirmPassword.Text;
        var displayName = _newDisplayName.Text.Trim();
        var advisor = _advisorCombo.SelectedItem as string;

        if (string.IsNullOrWhiteSpace(username))
        {
            MessageBox.Show("请输入用户名", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning); return;
        }
        if (System.Text.RegularExpressions.Regex.IsMatch(username, @"[^a-zA-Z0-9_.\-]"))
        {
            MessageBox.Show("用户名只能包含英文字母、数字、下划线、点和短横线", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning); return;
        }
        if (password != confirmPwd)
        {
            MessageBox.Show("两次密码不一致", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error); return;
        }
        if (password.Length < 8)
        {
            MessageBox.Show("密码长度至少 8 位", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning); return;
        }
        if (string.IsNullOrWhiteSpace(advisor) || advisor == NewAdvisorItem)
        {
            MessageBox.Show("请选择导师组", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning); return;
        }

        try
        {
            var personalDir = LabAccountService.CreateLabUser(username, password, displayName, advisor);
            _shell.Log($"账户 '{username}' 创建成功");
            AuditLogger.Write("CREATE_USER", username, detail: $"显示名: {displayName}, 导师组: {advisor}");

            // 清空表单
            _newUsername.Text = "";
            _newPassword.Text = "";
            _confirmPassword.Text = "";
            _newDisplayName.Text = "";

            var advisorGroup = LabConfig.AdvisorToGroupName(advisor);
            var advisorPath = Path.Combine(LabConfig.SharePath, advisor);
            MessageBox.Show(
                $"账户 '{username}' 创建完成！\n\n用户名：{username}\n显示名称：{displayName}\n全员组：{LabConfig.AllGroup}\n导师组：{advisorGroup}\n导师区：{advisorPath}\n个人目录：{personalDir}（仅本人可访问）",
                "成功", MessageBoxButtons.OK, MessageBoxIcon.Information);

            _shell.SelectTab(0);
            _shell.RefreshMembers();
        }
        catch (LabOperationException ex)
        {
            _shell.Log($"创建账户失败: {ex.Message}", "ERROR");
            AuditLogger.Write("CREATE_USER", ex.Target, AuditLogger.Result.Failed, ex.Detail);
            MessageBox.Show($"创建账户失败:\n{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            _shell.Log($"创建账户失败: {ex.Message}", "ERROR");
            AuditLogger.Write("CREATE_USER", username, AuditLogger.Result.Failed, ex.Message);
            MessageBox.Show($"创建账户失败:\n{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
