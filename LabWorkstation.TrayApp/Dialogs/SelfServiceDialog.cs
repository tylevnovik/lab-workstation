using System.Drawing;
using System.Diagnostics;
using LabWorkstation.Common.Audit;
using LabWorkstation.Common.Configuration;
using LabWorkstation.Common.LocalAccounts;
using LabWorkstation.Common.Storage;

namespace LabWorkstation.TrayApp.Dialogs;

/// <summary>
/// 自助服务弹窗：修改密码、查看存储用量、查看自己的审计日志。
/// 对应原 PS Show-SelfServiceDialog。
/// </summary>
public sealed class SelfServiceDialog : Form
{
    private static readonly Color BgColor = Color.FromArgb(30, 30, 46);
    private static readonly Color PanelColor = Color.FromArgb(40, 42, 58);
    private static readonly Color InputColor = Color.FromArgb(49, 50, 68);
    private static readonly Color AccentColor = Color.FromArgb(137, 180, 250);
    private static readonly Color TextColor = Color.FromArgb(205, 214, 244);
    private static readonly Color SubtleColor = Color.FromArgb(147, 153, 178);
    private static readonly Color ErrorColor = Color.FromArgb(243, 139, 168);
    private static readonly Color SuccessColor = Color.FromArgb(166, 227, 161);

    private readonly string _userName;
    private readonly string _advisorName;
    private readonly string _groupFolder;

    public SelfServiceDialog(string userName, string advisorName, string groupFolder)
    {
        _userName = userName;
        _advisorName = advisorName;
        _groupFolder = groupFolder;

        Text = "我的账户";
        Size = new Size(500, 620);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        TopMost = true;
        BackColor = BgColor;
        Font = new Font("Microsoft YaHei UI", 9f);

        BuildUi();
    }

    private void BuildUi()
    {
        var yPos = 15;

        // 标题
        var title = new Label
        {
            Text = "我的账户",
            ForeColor = AccentColor,
            Font = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold),
            Location = new Point(20, yPos),
            AutoSize = true
        };
        Controls.Add(title);
        yPos += 35;

        // 用户信息区
        var infoPanel = new Panel
        {
            Location = new Point(20, yPos),
            Size = new Size(440, 65),
            BackColor = PanelColor
        };

        var lblUser = new Label
        {
            Text = $"用户名：{_userName}",
            ForeColor = TextColor,
            Font = new Font("Microsoft YaHei UI", 10f),
            Location = new Point(12, 8),
            AutoSize = true
        };
        infoPanel.Controls.Add(lblUser);

        var groupText = string.IsNullOrEmpty(_advisorName) ? "未分组" : $"{_advisorName}组";
        var lblAdvisor = new Label
        {
            Text = $"导师组：{groupText}",
            ForeColor = SubtleColor,
            Font = new Font("Microsoft YaHei UI", 9f),
            Location = new Point(12, 30),
            AutoSize = true
        };
        infoPanel.Controls.Add(lblAdvisor);

        var lastLogon = GetLastLogon();
        var lblLogon = new Label
        {
            Text = $"最近登录：{lastLogon}",
            ForeColor = SubtleColor,
            Font = new Font("Microsoft YaHei UI", 9f),
            Location = new Point(230, 8),
            AutoSize = true
        };
        infoPanel.Controls.Add(lblLogon);

        Controls.Add(infoPanel);
        yPos += 75;

        // 修改密码区
        var secPwd = new Label
        {
            Text = "修改密码",
            ForeColor = AccentColor,
            Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold),
            Location = new Point(20, yPos),
            AutoSize = true
        };
        Controls.Add(secPwd);
        yPos += 25;

        var txtOldPwd = MakePwdBox(out var lblOld, "原密码：", yPos);
        Controls.Add(lblOld); Controls.Add(txtOldPwd);
        yPos += 30;

        var txtNewPwd = MakePwdBox(out var lblNew, "新密码：", yPos);
        Controls.Add(lblNew); Controls.Add(txtNewPwd);
        yPos += 30;

        var txtConfirmPwd = MakePwdBox(out var lblConfirm, "确认密码：", yPos);
        Controls.Add(lblConfirm); Controls.Add(txtConfirmPwd);
        yPos += 30;

        var lblPwdStatus = new Label
        {
            Text = "",
            ForeColor = Color.FromArgb(250, 179, 135),
            Location = new Point(90, yPos),
            AutoSize = true
        };
        Controls.Add(lblPwdStatus);

        var btnChangePwd = new Button
        {
            Text = "修改密码",
            Size = new Size(90, 28),
            Location = new Point(350, yPos - 5),
            BackColor = AccentColor,
            ForeColor = BgColor,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold)
        };
        btnChangePwd.Click += (_, _) =>
        {
            var newPwd = txtNewPwd.Text;
            var confirmPwd = txtConfirmPwd.Text;
            if (string.IsNullOrWhiteSpace(newPwd))
            {
                lblPwdStatus.ForeColor = ErrorColor;
                lblPwdStatus.Text = "请输入新密码";
                return;
            }
            if (newPwd.Length < 8)
            {
                lblPwdStatus.ForeColor = ErrorColor;
                lblPwdStatus.Text = "密码长度至少8位";
                return;
            }
            if (newPwd != confirmPwd)
            {
                lblPwdStatus.ForeColor = ErrorColor;
                lblPwdStatus.Text = "两次输入的密码不一致";
                return;
            }
            try
            {
                AccountManager.ChangePassword(_userName, txtOldPwd.Text, newPwd);
                lblPwdStatus.ForeColor = SuccessColor;
                lblPwdStatus.Text = "密码修改成功";
                txtOldPwd.Text = "";
                txtNewPwd.Text = "";
                txtConfirmPwd.Text = "";
                AuditLogger.Write("CHANGE_PASSWORD", _userName);
            }
            catch (Exception ex)
            {
                lblPwdStatus.ForeColor = ErrorColor;
                lblPwdStatus.Text = $"修改失败：{ex.Message}";
            }
        };
        Controls.Add(btnChangePwd);
        yPos += 40;

        // 存储用量区
        var secStorage = new Label
        {
            Text = "存储用量",
            ForeColor = AccentColor,
            Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold),
            Location = new Point(20, yPos),
            AutoSize = true
        };
        Controls.Add(secStorage);
        yPos += 25;

        var personalPath = $"D:\\Users\\{_userName}\\";
        var lblPersonalSize = new Label
        {
            Text = $"个人文件夹 ({personalPath})：{FolderSizer.GetSizeDisplay(Path.Combine(LabConfig.UsersRootPath, _userName))}",
            ForeColor = TextColor,
            Location = new Point(30, yPos),
            AutoSize = true
        };
        Controls.Add(lblPersonalSize);
        yPos += 22;

        var lblGroupSize = new Label
        {
            ForeColor = TextColor,
            Location = new Point(30, yPos),
            AutoSize = true
        };
        if (!string.IsNullOrEmpty(_groupFolder) && Directory.Exists(_groupFolder))
            lblGroupSize.Text = $"组内文件夹 ({_groupFolder}\\)：{FolderSizer.GetSizeDisplay(_groupFolder)}";
        else
            lblGroupSize.Text = "组内文件夹：未分配";
        Controls.Add(lblGroupSize);
        yPos += 28;

        var btnRefresh = new Button
        {
            Text = "刷新",
            Size = new Size(70, 26),
            Location = new Point(40, yPos),
            BackColor = InputColor,
            ForeColor = TextColor,
            FlatStyle = FlatStyle.Flat
        };
        btnRefresh.Click += (_, _) =>
        {
            lblPersonalSize.Text = $"个人文件夹 ({personalPath})：{FolderSizer.GetSizeDisplay(Path.Combine(LabConfig.UsersRootPath, _userName))}";
            if (!string.IsNullOrEmpty(_groupFolder) && Directory.Exists(_groupFolder))
                lblGroupSize.Text = $"组内文件夹 ({_groupFolder}\\)：{FolderSizer.GetSizeDisplay(_groupFolder)}";
        };
        Controls.Add(btnRefresh);
        yPos += 38;

        // 最近审计日志
        var secAudit = new Label
        {
            Text = "最近操作记录",
            ForeColor = AccentColor,
            Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold),
            Location = new Point(20, yPos),
            AutoSize = true
        };
        Controls.Add(secAudit);
        yPos += 25;

        var auditBox = new RichTextBox
        {
            Location = new Point(20, yPos),
            Size = new Size(440, 100),
            ReadOnly = true,
            BackColor = PanelColor,
            ForeColor = TextColor,
            BorderStyle = BorderStyle.None,
            Font = new Font("Microsoft YaHei UI", 8.5f),
            ScrollBars = RichTextBoxScrollBars.Vertical
        };
        try
        {
            var lines = AuditLogger.ReadUserLines(_userName, 20);
            auditBox.Text = lines.Count > 0 ? string.Join("\r\n", lines) : "暂无操作记录";
        }
        catch
        {
            auditBox.Text = "无法读取日志";
        }
        Controls.Add(auditBox);
        yPos += 110;

        // 关闭按钮
        var closeBtn = new Button
        {
            Text = "关闭",
            Size = new Size(100, 32),
            Location = new Point(195, yPos),
            BackColor = AccentColor,
            ForeColor = BgColor,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold)
        };
        closeBtn.Click += (_, _) => Close();
        Controls.Add(closeBtn);
    }

    private TextBox MakePwdBox(out Label label, string text, int yPos)
    {
        label = new Label
        {
            Text = text,
            ForeColor = TextColor,
            Location = new Point(20, yPos),
            AutoSize = true
        };
        var box = new TextBox
        {
            Location = new Point(90, yPos),
            Size = new Size(250, 24),
            UseSystemPasswordChar = true,
            BackColor = Color.FromArgb(49, 50, 68),
            ForeColor = TextColor
        };
        return box;
    }

    private string GetLastLogon()
    {
        try
        {
            // 取该用户最早启动的进程时间作为登录时间近似
            var procs = Process.GetProcesses();
            DateTime? earliest = null;
            foreach (var p in procs)
            {
                try
                {
                    if (p.StartTime < earliest || earliest == null)
                        earliest = p.StartTime;
                }
                catch { /* 跳过无权限的进程 */ }
            }
            return earliest?.ToString("yyyy-MM-dd HH:mm:ss") ?? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }
        catch
        {
            return "未知";
        }
    }
}
