using LabWorkstation.Common.Audit;
using LabWorkstation.Common.LocalAccounts;
using LabWorkstation.Common.Notifications;

namespace LabWorkstation.Admin.Tabs;

/// <summary>
/// Tab 7：公告推送。
/// 发送通知（标题/内容/重要程度），查看活跃通知（pending）和历史通知（sent），
/// 归档（将 pending 移至 sent），删除通知。
/// 每个用户的已读状态由 TrayApp 本地追踪，Admin 无需关心。
/// </summary>
public class BroadcastTab : UserControl
{
    private readonly IAppShell _shell;
    private readonly TextBox _bcastTitleBox;
    private readonly ComboBox _bcastImportanceCombo;
    private readonly RichTextBox _bcastMsgBox;
    private readonly ListView _activeList;
    private readonly ListView _historyList;

    public BroadcastTab(IAppShell shell)
    {
        _shell = shell;
        Text = "公告推送";

        var broadcastIntro = new Label
        {
            Text = "向所有用户推送通知。发送后所有在线用户的 TrayApp 将自动弹出自定义弹窗。每个用户独立追踪已读状态。",
            Location = new Point(20, 15),
            MaximumSize = new Size(745, 0),
            AutoSize = true
        };
        Controls.Add(broadcastIntro);

        // -- 编辑通知 --
        var broadcastEditGroup = new GroupBox
        {
            Text = "编辑通知",
            Location = new Point(15, 45),
            Size = new Size(745, 225)
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
            Size = new Size(715, 100),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Microsoft YaHei UI", 9.5F)
        };
        broadcastEditGroup.Controls.Add(_bcastMsgBox);

        var bcastSendBtn = new Button
        {
            Text = "发送通知",
            Location = new Point(580, 195),
            Size = new Size(150, 25),
            BackColor = Color.FromArgb(0, 122, 204),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold)
        };
        bcastSendBtn.Click += (_, _) => SendNotification();
        broadcastEditGroup.Controls.Add(bcastSendBtn);

        // -- 活跃通知（pending） --
        var activeGroup = new GroupBox
        {
            Text = "活跃通知（待推送 / 推送中）",
            Location = new Point(15, 278),
            Size = new Size(745, 160)
        };
        Controls.Add(activeGroup);

        _activeList = new ListView
        {
            Location = new Point(10, 20),
            Size = new Size(590, 100),
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            Font = new Font("Microsoft YaHei UI", 9F)
        };
        _activeList.Columns.Add("标题", 200);
        _activeList.Columns.Add("等级", 50);
        _activeList.Columns.Add("发送时间", 120);
        _activeList.Columns.Add("发送人", 80);
        _activeList.Columns.Add("ID", 80);
        activeGroup.Controls.Add(_activeList);

        var refreshActiveBtn = new Button { Text = "刷新", Location = new Point(610, 20), Size = new Size(60, 28) };
        refreshActiveBtn.Click += (_, _) => RefreshActive();
        activeGroup.Controls.Add(refreshActiveBtn);

        var archiveBtn = new Button
        {
            Text = "归档全部",
            Location = new Point(610, 55),
            Size = new Size(120, 28),
            BackColor = Color.FromArgb(60, 60, 80),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        archiveBtn.Click += (_, _) => ArchiveAll();
        activeGroup.Controls.Add(archiveBtn);

        var deleteActiveBtn = new Button
        {
            Text = "删除选中",
            Location = new Point(610, 90),
            Size = new Size(120, 28),
            BackColor = Color.FromArgb(180, 30, 30),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        deleteActiveBtn.Click += (_, _) => DeleteSelected(_activeList);
        activeGroup.Controls.Add(deleteActiveBtn);

        // -- 历史通知（sent） --
        var historyGroup = new GroupBox
        {
            Text = "历史通知（已归档）",
            Location = new Point(15, 446),
            Size = new Size(745, 130)
        };
        Controls.Add(historyGroup);

        _historyList = new ListView
        {
            Location = new Point(10, 20),
            Size = new Size(590, 95),
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            Font = new Font("Microsoft YaHei UI", 9F)
        };
        _historyList.Columns.Add("标题", 200);
        _historyList.Columns.Add("等级", 50);
        _historyList.Columns.Add("发送时间", 120);
        _historyList.Columns.Add("ID", 80);
        historyGroup.Controls.Add(_historyList);

        var refreshHistoryBtn = new Button { Text = "刷新", Location = new Point(610, 20), Size = new Size(60, 28) };
        refreshHistoryBtn.Click += (_, _) => RefreshHistory();
        historyGroup.Controls.Add(refreshHistoryBtn);

        var deleteHistoryBtn = new Button
        {
            Text = "删除选中",
            Location = new Point(610, 55),
            Size = new Size(120, 28),
            BackColor = Color.FromArgb(180, 30, 30),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        deleteHistoryBtn.Click += (_, _) => DeleteSelected(_historyList);
        historyGroup.Controls.Add(deleteHistoryBtn);
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

            _bcastTitleBox.Text = "";
            _bcastMsgBox.Text = "";
            _bcastImportanceCombo.SelectedIndex = 0;

            MessageBox.Show($"通知已发送！\n\n标题：{title}\n重要程度：{importance}\n\n所有在线用户的 TrayApp 将自动弹出通知弹窗。",
                "成功", MessageBoxButtons.OK, MessageBoxIcon.Information);

            RefreshActive();
        }
        catch (Exception ex)
        {
            _shell.Log($"发送通知失败: {ex.Message}", "ERROR");
            AuditLogger.Write("BROADCAST", title, AuditLogger.Result.Failed, ex.Message);
            MessageBox.Show($"发送通知失败:\n{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    public void RefreshActive()
    {
        _activeList.Items.Clear();
        List<Notification> pending;
        try { pending = NotificationStore.GetPending(); }
        catch (Exception ex) { _shell.Log($"刷新活跃通知失败: {ex.Message}", "WARN"); return; }

        foreach (var n in pending.OrderByDescending(n => n.Timestamp))
        {
            var item = new ListViewItem(n.Title);
            item.SubItems.Add(n.ImportanceText);
            item.SubItems.Add(n.Timestamp.ToString("MM-dd HH:mm:ss"));
            item.SubItems.Add(n.Sender);
            item.SubItems.Add(n.Id);
            item.Tag = n.Id;
            _activeList.Items.Add(item);
        }
    }

    public void RefreshHistory()
    {
        _historyList.Items.Clear();
        List<Notification> sent;
        try { sent = NotificationStore.GetSent(); }
        catch (Exception ex) { _shell.Log($"刷新历史通知失败: {ex.Message}", "WARN"); return; }

        foreach (var n in sent)
        {
            var item = new ListViewItem(n.Title);
            item.SubItems.Add(n.ImportanceText);
            item.SubItems.Add(n.Timestamp.ToString("MM-dd HH:mm:ss"));
            item.SubItems.Add(n.Id);
            item.Tag = n.Id;
            _historyList.Items.Add(item);
        }
    }

    private void ArchiveAll()
    {
        try
        {
            var moved = NotificationStore.ArchivePending();
            if (moved == 0)
            {
                MessageBox.Show("活跃通知为空，无需归档", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _shell.Log($"已归档 {moved} 条通知到历史");
            AuditLogger.Write("BROADCAST_ARCHIVE", $"{moved} notifications", detail: "归档全部活跃通知");
            RefreshActive();
            RefreshHistory();
            MessageBox.Show($"已归档 {moved} 条通知到历史", "成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            _shell.Log($"归档失败: {ex.Message}", "ERROR");
            MessageBox.Show($"归档失败:\n{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void DeleteSelected(ListView list)
    {
        if (list.SelectedItems.Count == 0)
        {
            MessageBox.Show("请先选中要删除的通知", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var ids = list.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag as string).Where(s => s != null).ToList();
        if (ids.Count == 0) return;

        if (MessageBox.Show($"确定删除 {ids.Count} 条通知？此操作不可撤销。",
            "确认删除", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

        var deleted = 0;
        foreach (var id in ids)
        {
            try
            {
                if (NotificationStore.Delete(id!)) deleted++;
            }
            catch { }
        }

        _shell.Log($"已删除 {deleted} 条通知");
        AuditLogger.Write("BROADCAST_DELETE", $"{deleted} notifications", detail: "删除通知");
        RefreshActive();
        RefreshHistory();
    }
}
