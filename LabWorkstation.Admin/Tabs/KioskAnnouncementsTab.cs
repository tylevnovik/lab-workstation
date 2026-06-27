using LabWorkstation.Common.Audit;
using LabWorkstation.Common.Kiosk;
using LabWorkstation.Common.LocalAccounts;

namespace LabWorkstation.Admin.Tabs;

/// <summary>
/// Tab：Kiosk 公告管理。
/// 编辑/发布/修改/删除 Kiosk 界面常驻公告。公告不会自动过期，仅由管理员维护。
/// 与 TrayApp 通知（BroadcastTab）独立：Kiosk 公告以列表形式常驻 Kiosk 创建用户界面，
/// 不弹窗、不推送，kiosk 用户进入界面即可看到。
/// 支持置顶：置顶公告在 Kiosk 顶部独立框展示，普通公告在列表中展示。
/// </summary>
public class KioskAnnouncementsTab : UserControl
{
    private readonly IAppShell _shell;

    // 编辑区
    private readonly TextBox _titleBox;
    private readonly CheckBox _pinnedCheck;
    private readonly RichTextBox _contentBox;
    private readonly Button _publishBtn;
    private readonly Button _updateBtn;
    private readonly Button _cancelEditBtn;
    private string? _editingId; // 非 null 表示当前在编辑已有公告

    // 列表区
    private readonly ListView _list;
    private readonly Button _refreshBtn;
    private readonly Button _editBtn;
    private readonly Button _deleteBtn;
    private readonly Button _togglePinBtn;

    public KioskAnnouncementsTab(IAppShell shell)
    {
        _shell = shell;
        Text = "Kiosk 公告";

        var intro = new Label
        {
            Text = "管理 Kiosk 自助开户界面的常驻公告。公告不会自动过期，仅由管理员增删改。" +
                   "置顶公告在 Kiosk 顶部独立框展示，普通公告在列表中展示。",
            Location = new Point(20, 15),
            MaximumSize = new Size(745, 0),
            AutoSize = true
        };
        Controls.Add(intro);

        // -- 编辑区 --
        var editGroup = new GroupBox
        {
            Text = "编辑公告",
            Location = new Point(15, 60),
            Size = new Size(745, 230)
        };
        Controls.Add(editGroup);

        var titleLabel = new Label { Text = "标题：", Location = new Point(15, 28), AutoSize = true };
        editGroup.Controls.Add(titleLabel);

        _titleBox = new TextBox { Location = new Point(70, 25), Size = new Size(420, 25) };
        editGroup.Controls.Add(_titleBox);

        _pinnedCheck = new CheckBox
        {
            Text = "置顶",
            Location = new Point(510, 27),
            AutoSize = true
        };
        editGroup.Controls.Add(_pinnedCheck);

        var contentLabel = new Label { Text = "内容：", Location = new Point(15, 62), AutoSize = true };
        editGroup.Controls.Add(contentLabel);

        _contentBox = new RichTextBox
        {
            Location = new Point(15, 88),
            Size = new Size(715, 100),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Microsoft YaHei UI", 9.5F)
        };
        editGroup.Controls.Add(_contentBox);

        _publishBtn = new Button
        {
            Text = "发布新公告",
            Location = new Point(440, 198),
            Size = new Size(130, 25),
            BackColor = Color.FromArgb(0, 122, 204),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold)
        };
        _publishBtn.Click += (_, _) => Publish();
        editGroup.Controls.Add(_publishBtn);

        _updateBtn = new Button
        {
            Text = "保存修改",
            Location = new Point(440, 198),
            Size = new Size(130, 25),
            BackColor = Color.FromArgb(60, 140, 60),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold),
            Visible = false
        };
        _updateBtn.Click += (_, _) => SaveUpdate();
        editGroup.Controls.Add(_updateBtn);

        _cancelEditBtn = new Button
        {
            Text = "取消编辑",
            Location = new Point(580, 198),
            Size = new Size(110, 25),
            FlatStyle = FlatStyle.Flat,
            Visible = false
        };
        _cancelEditBtn.Click += (_, _) => ExitEditMode();
        editGroup.Controls.Add(_cancelEditBtn);

        // -- 列表区 --
        var listGroup = new GroupBox
        {
            Text = "已发布公告",
            Location = new Point(15, 300),
            Size = new Size(745, 290)
        };
        Controls.Add(listGroup);

        _list = new ListView
        {
            Location = new Point(10, 22),
            Size = new Size(590, 255),
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            Font = new Font("Microsoft YaHei UI", 9F)
        };
        _list.Columns.Add("标题", 220);
        _list.Columns.Add("置顶", 50);
        _list.Columns.Add("创建时间", 130);
        _list.Columns.Add("更新时间", 130);
        _list.Columns.Add("创建人", 60);
        _list.Columns.Add("ID", 70);
        _list.MultiSelect = false;
        listGroup.Controls.Add(_list);

        _refreshBtn = new Button { Text = "刷新", Location = new Point(610, 22), Size = new Size(120, 28) };
        _refreshBtn.Click += (_, _) => RefreshList();
        listGroup.Controls.Add(_refreshBtn);

        _editBtn = new Button
        {
            Text = "编辑选中",
            Location = new Point(610, 56),
            Size = new Size(120, 28),
            FlatStyle = FlatStyle.Flat
        };
        _editBtn.Click += (_, _) => BeginEdit();
        listGroup.Controls.Add(_editBtn);

        _togglePinBtn = new Button
        {
            Text = "切换置顶",
            Location = new Point(610, 90),
            Size = new Size(120, 28),
            FlatStyle = FlatStyle.Flat
        };
        _togglePinBtn.Click += (_, _) => TogglePin();
        listGroup.Controls.Add(_togglePinBtn);

        _deleteBtn = new Button
        {
            Text = "删除选中",
            Location = new Point(610, 124),
            Size = new Size(120, 28),
            BackColor = Color.FromArgb(180, 30, 30),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        _deleteBtn.Click += (_, _) => DeleteSelected();
        listGroup.Controls.Add(_deleteBtn);
    }

    /// <summary>发布新公告。</summary>
    private void Publish()
    {
        var title = _titleBox.Text.Trim();
        var content = _contentBox.Text.Trim();
        if (string.IsNullOrEmpty(title))
        {
            MessageBox.Show("请输入标题", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (string.IsNullOrEmpty(content))
        {
            MessageBox.Show("请输入内容", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            var createdBy = AccountManager.GetCurrentShortUserName();
            var a = AnnouncementStore.Add(title, content, _pinnedCheck.Checked, createdBy);
            _shell.Log($"Kiosk 公告已发布：'{title}'（置顶={a.IsPinned}, ID={a.Id}）");
            AuditLogger.Write("KIOSK_ANNOUNCEMENT_ADD", title,
                detail: $"置顶={a.IsPinned}, ID={a.Id}");

            ClearEditor();
            RefreshList();
            MessageBox.Show("公告已发布，Kiosk 界面将在 30 秒内自动刷新显示。",
                "成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            _shell.Log($"发布 Kiosk 公告失败: {ex.Message}", "ERROR");
            MessageBox.Show($"发布失败:\n{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>进入编辑模式：将选中公告填入编辑区。</summary>
    private void BeginEdit()
    {
        if (_list.SelectedItems.Count == 0)
        {
            MessageBox.Show("请先选中要编辑的公告", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var id = _list.SelectedItems[0].Tag as string;
        if (id == null) return;

        var a = AnnouncementStore.Find(id);
        if (a == null)
        {
            MessageBox.Show("公告不存在，可能已被删除", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            RefreshList();
            return;
        }

        _editingId = a.Id;
        _titleBox.Text = a.Title;
        _contentBox.Text = a.Content;
        _pinnedCheck.Checked = a.IsPinned;

        // 切换按钮可见性
        _publishBtn.Visible = false;
        _updateBtn.Visible = true;
        _cancelEditBtn.Visible = true;
    }

    /// <summary>保存对已有公告的修改。</summary>
    private void SaveUpdate()
    {
        if (_editingId == null) return;
        var title = _titleBox.Text.Trim();
        var content = _contentBox.Text.Trim();
        if (string.IsNullOrEmpty(title))
        {
            MessageBox.Show("请输入标题", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (string.IsNullOrEmpty(content))
        {
            MessageBox.Show("请输入内容", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            if (AnnouncementStore.Update(_editingId, title, content, _pinnedCheck.Checked))
            {
                _shell.Log($"Kiosk 公告已修改：'{title}'（ID={_editingId}）");
                AuditLogger.Write("KIOSK_ANNOUNCEMENT_UPDATE", title,
                    detail: $"置顶={_pinnedCheck.Checked}, ID={_editingId}");
                ExitEditMode();
                RefreshList();
            }
            else
            {
                MessageBox.Show("公告不存在，可能已被删除", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                ExitEditMode();
                RefreshList();
            }
        }
        catch (Exception ex)
        {
            _shell.Log($"修改 Kiosk 公告失败: {ex.Message}", "ERROR");
            MessageBox.Show($"修改失败:\n{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>退出编辑模式，清空编辑区并恢复发布按钮。</summary>
    private void ExitEditMode()
    {
        _editingId = null;
        ClearEditor();
        _publishBtn.Visible = true;
        _updateBtn.Visible = false;
        _cancelEditBtn.Visible = false;
    }

    /// <summary>清空编辑区输入。</summary>
    private void ClearEditor()
    {
        _titleBox.Text = "";
        _contentBox.Text = "";
        _pinnedCheck.Checked = false;
    }

    /// <summary>切换选中公告的置顶状态。</summary>
    private void TogglePin()
    {
        if (_list.SelectedItems.Count == 0)
        {
            MessageBox.Show("请先选中公告", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var id = _list.SelectedItems[0].Tag as string;
        if (id == null) return;

        var a = AnnouncementStore.Find(id);
        if (a == null)
        {
            MessageBox.Show("公告不存在，可能已被删除", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            RefreshList();
            return;
        }

        try
        {
            AnnouncementStore.Update(a.Id, a.Title, a.Content, !a.IsPinned);
            _shell.Log($"Kiosk 公告置顶状态已切换：'{a.Title}'（{a.IsPinned} → {!a.IsPinned}）");
            AuditLogger.Write("KIOSK_ANNOUNCEMENT_TOGGLE_PIN", a.Title,
                detail: $"{a.IsPinned} -> {!a.IsPinned}, ID={a.Id}");
            RefreshList();
        }
        catch (Exception ex)
        {
            _shell.Log($"切换置顶失败: {ex.Message}", "ERROR");
            MessageBox.Show($"切换失败:\n{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>删除选中的公告。</summary>
    private void DeleteSelected()
    {
        if (_list.SelectedItems.Count == 0)
        {
            MessageBox.Show("请先选中要删除的公告", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var id = _list.SelectedItems[0].Tag as string;
        var title = _list.SelectedItems[0].Text;
        if (id == null) return;

        if (MessageBox.Show($"确定删除公告 '{title}'？此操作不可撤销。",
            "确认删除", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

        try
        {
            if (AnnouncementStore.Delete(id))
            {
                _shell.Log($"Kiosk 公告已删除：'{title}'（ID={id}）");
                AuditLogger.Write("KIOSK_ANNOUNCEMENT_DELETE", title, detail: $"ID={id}");
                RefreshList();
                // 如果正在编辑被删除的公告，退出编辑模式
                if (_editingId == id) ExitEditMode();
            }
            else
            {
                MessageBox.Show("公告不存在，可能已被删除", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                RefreshList();
            }
        }
        catch (Exception ex)
        {
            _shell.Log($"删除 Kiosk 公告失败: {ex.Message}", "ERROR");
            MessageBox.Show($"删除失败:\n{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>刷新公告列表。</summary>
    public void RefreshList()
    {
        _list.Items.Clear();
        List<Announcement> list;
        try { list = AnnouncementStore.LoadAll(); }
        catch (Exception ex)
        {
            _shell.Log($"刷新 Kiosk 公告列表失败: {ex.Message}", "WARN");
            return;
        }

        foreach (var a in list)
        {
            var item = new ListViewItem(a.Title);
            item.SubItems.Add(a.IsPinned ? "是" : "");
            item.SubItems.Add(a.CreatedAt.ToString("MM-dd HH:mm"));
            item.SubItems.Add(a.UpdatedAt.ToString("MM-dd HH:mm"));
            item.SubItems.Add(a.CreatedBy);
            item.SubItems.Add(a.Id);
            item.Tag = a.Id;
            _list.Items.Add(item);
        }
    }
}
