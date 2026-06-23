using LabWorkstation.Common.Audit;
using LabWorkstation.Common.LocalAccounts;
using LabWorkstation.Common.Notifications;

namespace LabWorkstation.Admin.Tabs;

/// <summary>Tab 7：公告推送。发送通知（标题/内容/重要程度），查看历史，归档待发送。</summary>
public class BroadcastTab : UserControl
{
    private readonly IAppShell _shell;
    private readonly TextBox _bcastTitleBox;
    private readonly ComboBox _bcastImportanceCombo;
    private readonly RichTextBox _bcastMsgBox;
    private readonly ComboBox _bcastHistoryCombo;

    public BroadcastTab(IAppShell shell)
    {
        _shell = shell;
        Text = "公告推送";

        var broadcastIntro = new Label
        {
            Text = "向所有在线用户推送通知。通知保存后由用户端的 Lab-TrayApp 轮询并弹出提醒。",
            Location = new Point(20, 15),
            AutoSize = true
        };
        Controls.Add(broadcastIntro);

        // -- 编辑通知 --
        var broadcastEditGroup = new GroupBox
        {
            Text = "编辑通知",
            Location = new Point(15, 45),
            Size = new Size(745, 330)
        };
        Controls.Add(broadcastEditGroup);

        var bcastTitleLabel = new Label { Text = "通知标题：", Location = new Point(15, 28), AutoSize = true };
        broadcastEditGroup.Controls.Add(bcastTitleLabel);

        _bcastTitleBox = new TextBox { Location = new Point(95, 25), Size = new Size(400, 25) };
        broadcastEditGroup.Controls.Add(_bcastTitleBox);

        var bcastImpLabel = new Label { Text = "重要程度：", Location = new Point(520, 28), AutoSize = true };
        broadcastEditGroup.Controls.Add(bcastImpLabel);

        _bcastImportanceCombo = new ComboBox
        {
            Location = new Point(600, 25),
            Size = new Size(100, 25),
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        _bcastImportanceCombo.Items.AddRange(new object[] { "普通", "重要", "紧急" });
        _bcastImportanceCombo.SelectedIndex = 0;
        broadcastEditGroup.Controls.Add(_bcastImportanceCombo);

        var bcastMsgLabel = new Label { Text = "通知内容：", Location = new Point(15, 62), AutoSize = true };
        broadcastEditGroup.Controls.Add(bcastMsgLabel);

        _bcastMsgBox = new RichTextBox
        {
            Location = new Point(15, 88),
            Size = new Size(715, 200),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Microsoft YaHei UI", 9.5F)
        };
        broadcastEditGroup.Controls.Add(_bcastMsgBox);

        var bcastSendBtn = new Button
        {
            Text = "发送通知",
            Location = new Point(580, 300),
            Size = new Size(150, 30),
            BackColor = Color.FromArgb(0, 122, 204),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold)
        };
        bcastSendBtn.Click += (_, _) => SendNotification();
        broadcastEditGroup.Controls.Add(bcastSendBtn);

        // -- 历史通知 --
        var bcastHistoryGroup = new GroupBox
        {
            Text = "历史通知",
            Location = new Point(15, 383),
            Size = new Size(745, 48)
        };
        Controls.Add(bcastHistoryGroup);

        _bcastHistoryCombo = new ComboBox
        {
            Location = new Point(15, 17),
            Size = new Size(530, 25),
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        bcastHistoryGroup.Controls.Add(_bcastHistoryCombo);

        var bcastHistoryRefreshBtn = new Button { Text = "刷新", Location = new Point(555, 15), Size = new Size(75, 28) };
        bcastHistoryRefreshBtn.Click += (_, _) => RefreshHistory();
        bcastHistoryGroup.Controls.Add(bcastHistoryRefreshBtn);

        var bcastArchiveBtn = new Button { Text = "移至历史", Location = new Point(640, 15), Size = new Size(90, 28) };
        bcastArchiveBtn.Click += (_, _) => ArchivePending();
        bcastHistoryGroup.Controls.Add(bcastArchiveBtn);
    }

    private void SendNotification()
    {
        var title = "";
        try
        {
            title = _bcastTitleBox.Text.Trim();
            var message = _bcastMsgBox.Text.Trim();
            var importance = _bcastImportanceCombo.SelectedItem as string ?? "普通";

            if (string.IsNullOrWhiteSpace(title))
            {
                MessageBox.Show("请输入通知标题", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning); return;
            }
            if (string.IsNullOrWhiteSpace(message))
            {
                MessageBox.Show("请输入通知内容", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning); return;
            }

            var imp = Notification.ParseImportance(importance);
            var sender = AccountManager.GetCurrentShortUserName();

            var fileName = NotificationStore.Send(title, message, imp, sender);
            _shell.Log($"通知已发送：'{title}' ({importance}) -> {fileName}");
            AuditLogger.Write("BROADCAST", title, detail: $"重要程度: {importance}, 文件: {fileName}");

            // 清空输入
            _bcastTitleBox.Text = "";
            _bcastMsgBox.Text = "";
            _bcastImportanceCombo.SelectedIndex = 0;

            MessageBox.Show($"通知已发送！\n\n标题：{title}\n重要程度：{importance}\n文件：{fileName}\n\n用户端将自动轮询并弹出提醒。",
                "成功", MessageBoxButtons.OK, MessageBoxIcon.Information);

            RefreshHistory();
        }
        catch (Exception ex)
        {
            _shell.Log($"发送通知失败: {ex.Message}", "ERROR");
            AuditLogger.Write("BROADCAST", title, AuditLogger.Result.Failed, ex.Message);
            MessageBox.Show($"发送通知失败:\n{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    public void RefreshHistory()
    {
        _bcastHistoryCombo.Items.Clear();
        List<Notification> sent;
        try { sent = NotificationStore.GetSent(); }
        catch (Exception ex) { _shell.Log($"刷新历史通知失败: {ex.Message}", "WARN"); return; }

        foreach (var n in sent)
        {
            var display = $"[{n.Timestamp:yyyy-MM-dd HH:mm:ss}] {n.Title} ({n.ImportanceText})";
            _bcastHistoryCombo.Items.Add(display);
        }
        if (_bcastHistoryCombo.Items.Count > 0) _bcastHistoryCombo.SelectedIndex = 0;
    }

    private void ArchivePending()
    {
        try
        {
            int moved;
            try { moved = NotificationStore.ArchivePending(); }
            catch (Exception ex)
            {
                _shell.Log($"移至历史失败: {ex.Message}", "ERROR");
                MessageBox.Show($"移至历史失败:\n{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (moved == 0)
            {
                MessageBox.Show("待发送目录为空", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information); return;
            }

            _shell.Log($"已将 {moved} 条通知移至历史");
            AuditLogger.Write("BROADCAST_ARCHIVE", $"{moved} notifications", detail: "移至历史目录");
            RefreshHistory();
            MessageBox.Show($"已将 {moved} 条通知移至历史", "成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            _shell.Log($"移至历史失败: {ex.Message}", "ERROR");
            AuditLogger.Write("BROADCAST_ARCHIVE", "notifications", AuditLogger.Result.Failed, ex.Message);
            MessageBox.Show($"移至历史失败:\n{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
