using LabWorkstation.Common;
using LabWorkstation.Common.Audit;
using LabWorkstation.Common.Configuration;
using LabWorkstation.Common.LocalAccounts;

namespace LabWorkstation.Admin.Tabs;

/// <summary>Tab 5：批量操作。从多行文本解析"用户名,密码,姓名,导师"批量创建账户。</summary>
public class BatchTab : UserControl
{
    private readonly IAppShell _shell;
    private readonly TextBox _batchInput;
    private readonly TextBox _batchPassword;
    private readonly RichTextBox _batchProgress;

    public BatchTab(IAppShell shell)
    {
        _shell = shell;
        Text = "批量操作";

        var batchIntro = new Label
        {
            Text = "批量创建账户：每行一个，格式为 \"用户名,密码,姓名,导师\"（导师名必须匹配已有导师组）。",
            Location = new Point(20, 15),
            AutoSize = true
        };
        Controls.Add(batchIntro);

        var batchExample = new Label
        {
            Text = "示例：zhangsan,Pass123!,张三,张老师    （密码可省略，省略时使用右侧默认密码）",
            ForeColor = Color.Gray,
            Location = new Point(20, 38),
            AutoSize = true
        };
        Controls.Add(batchExample);

        var batchGroup = new GroupBox
        {
            Text = "用户列表",
            Location = new Point(15, 60),
            Size = new Size(460, 290)
        };
        Controls.Add(batchGroup);

        _batchInput = new TextBox
        {
            Location = new Point(12, 22),
            Size = new Size(435, 255),
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font("Consolas", 10F)
        };
        batchGroup.Controls.Add(_batchInput);

        var batchLabel2 = new Label
        {
            Text = "默认密码（行未指定密码时使用）：",
            Location = new Point(490, 75),
            AutoSize = true
        };
        Controls.Add(batchLabel2);

        _batchPassword = new TextBox
        {
            Location = new Point(490, 100),
            Size = new Size(200, 25),
            UseSystemPasswordChar = true
        };
        Controls.Add(_batchPassword);

        var batchGenBtn = new Button
        {
            Text = "随机生成",
            Location = new Point(700, 100),
            Size = new Size(65, 25)
        };
        batchGenBtn.Click += (_, _) =>
        {
            _batchPassword.Text = LabAccountService.GenerateRandomPassword();
            _shell.Log("已为批量操作生成随机密码");
        };
        Controls.Add(batchGenBtn);

        var batchNote = new Label
        {
            Text = "注意：创建后建议提醒各用户首次登录后修改密码。\n导师名必须与已有导师组匹配，否则该行将被跳过。",
            ForeColor = Color.Red,
            Location = new Point(490, 135),
            AutoSize = true
        };
        Controls.Add(batchNote);

        var progressLabel = new Label
        {
            Text = "执行进度：",
            Location = new Point(490, 180),
            AutoSize = true
        };
        Controls.Add(progressLabel);

        _batchProgress = new RichTextBox
        {
            Location = new Point(490, 200),
            Size = new Size(275, 100),
            ReadOnly = true,
            BackColor = Color.FromArgb(250, 250, 250),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 8.5F)
        };
        Controls.Add(_batchProgress);

        var batchBtn = new Button
        {
            Text = "批量创建",
            Location = new Point(540, 310),
            Size = new Size(140, 38),
            BackColor = Color.FromArgb(0, 122, 204),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold)
        };
        batchBtn.Click += (_, _) => BatchCreate();
        Controls.Add(batchBtn);
    }

    private void AppendProgress(string msg)
    {
        _batchProgress.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}\r\n");
        _batchProgress.ScrollToCaret();
        Application.DoEvents();
    }

    private void BatchCreate()
    {
        try
        {
            var lines = _batchInput.Text.Split('\n')
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrEmpty(l))
                .ToList();
            if (lines.Count == 0)
            {
                MessageBox.Show("请输入用户列表", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning); return;
            }
            var defaultPwd = _batchPassword.Text;
            if (defaultPwd.Length < 8)
            {
                MessageBox.Show("默认密码长度至少 8 位", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error); return;
            }

            // 确保全员组存在
            if (!GroupManager.GroupExists(LabConfig.AllGroup))
            {
                GroupManager.CreateGroup(LabConfig.AllGroup);
                _shell.Log($"全员组 '{LabConfig.AllGroup}' 已自动创建");
            }

            // 当前导师组列表
            var currentAdvisors = GroupManager.GetAllAdvisorGroups();

            _batchProgress.Clear();
            int successCount = 0, failCount = 0;

            foreach (var line in lines)
            {
                var parts = line.Split(',').Select(p => p.Trim()).ToArray();
                var uname = parts.Length > 0 ? parts[0] : "";
                // 4 字段：用户名,密码,姓名,导师；3 字段：用户名,姓名,导师（用默认密码）
                string pwd, dname, advisorNm;
                if (parts.Length >= 4)
                {
                    pwd = parts[1];
                    dname = parts[2];
                    advisorNm = parts[3];
                }
                else if (parts.Length >= 3)
                {
                    pwd = defaultPwd;
                    dname = parts[1];
                    advisorNm = parts[2];
                }
                else
                {
                    AppendProgress($"跳过非法行（字段不足）: {line}");
                    failCount++;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(uname))
                {
                    AppendProgress("跳过：用户名为空");
                    failCount++;
                    continue;
                }
                if (System.Text.RegularExpressions.Regex.IsMatch(uname, @"[^a-zA-Z0-9_.\-]"))
                {
                    AppendProgress($"跳过非法用户名: {uname}");
                    _shell.Log($"跳过非法用户名: {uname}", "WARN");
                    failCount++;
                    continue;
                }
                if (pwd.Length < 8)
                {
                    AppendProgress($"跳过 '{uname}'：密码长度不足 8 位");
                    _shell.Log($"跳过 '{uname}'：密码长度不足 8 位", "WARN");
                    failCount++;
                    continue;
                }
                if (string.IsNullOrWhiteSpace(advisorNm))
                {
                    AppendProgress($"跳过 '{uname}'：未指定导师名");
                    _shell.Log($"跳过 '{uname}'：未指定导师名", "WARN");
                    failCount++;
                    continue;
                }
                if (!currentAdvisors.Contains(advisorNm))
                {
                    AppendProgress($"跳过 '{uname}'：导师组 '{LabConfig.AdvisorToGroupName(advisorNm)}' 不存在（请先创建）");
                    _shell.Log($"跳过 '{uname}'：导师组 '{LabConfig.AdvisorToGroupName(advisorNm)}' 不存在（请先创建）", "WARN");
                    failCount++;
                    continue;
                }

                try
                {
                    if (AccountManager.UserExists(uname))
                    {
                        AppendProgress($"用户 '{uname}' 已存在，跳过创建，仅加入分组");
                        _shell.Log($"用户 '{uname}' 已存在，跳过创建", "WARN");
                    }
                    else
                    {
                        LabAccountService.CreateLabUser(uname, pwd, dname, advisorNm);
                        AppendProgress($"已创建账户: {uname} ({dname}) -> {LabConfig.AdvisorToGroupName(advisorNm)}");
                        _shell.Log($"已创建账户: {uname} ({dname})");
                    }
                    successCount++;
                }
                catch (LabOperationException ex)
                {
                    AppendProgress($"创建 '{uname}' 失败: {ex.Message}");
                    _shell.Log($"创建 '{uname}' 失败: {ex.Message}", "ERROR");
                    failCount++;
                }
                catch (Exception ex)
                {
                    AppendProgress($"创建 '{uname}' 失败: {ex.Message}");
                    _shell.Log($"创建 '{uname}' 失败: {ex.Message}", "ERROR");
                    failCount++;
                }
            }

            AppendProgress($"完成：成功 {successCount} 人，失败 {failCount} 人");
            _shell.Log($"批量创建完成：成功 {successCount} 人，失败 {failCount} 人");
            AuditLogger.Write("BATCH_CREATE", $"{successCount} users", detail: $"成功: {successCount}, 失败: {failCount}");
            MessageBox.Show($"批量创建完成\n成功：{successCount} 人\n失败：{failCount} 人\n\n详见操作日志与进度框。",
                "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);

            _batchInput.Text = "";
            _shell.SelectTab(0);
            _shell.RefreshMembers();
        }
        catch (Exception ex)
        {
            _shell.Log($"批量创建失败: {ex.Message}", "ERROR");
            MessageBox.Show($"批量创建失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
