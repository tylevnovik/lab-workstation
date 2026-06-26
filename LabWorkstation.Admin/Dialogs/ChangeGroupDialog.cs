using LabWorkstation.Common.LocalAccounts;

namespace LabWorkstation.Admin.Dialogs;

/// <summary>修改分组对话框。选择新的导师组。</summary>
public class ChangeGroupDialog : Form
{
    private readonly ComboBox _combo;

    public string SelectedAdvisor { get; private set; } = "";

    public ChangeGroupDialog(string username, string currentAdvisor)
    {
        Text = $"修改分组 - {username}";
        Size = new Size(380, 200);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        Font = new Font("Microsoft YaHei UI", 9F);

        var currentDisplay = string.IsNullOrEmpty(currentAdvisor) ? "(未分配)" : $"Lab_{currentAdvisor}";
        var infoLabel = new Label
        {
            Text = $"用户：{username}    当前导师组：{currentDisplay}",
            Location = new Point(15, 15),
            MaximumSize = new Size(350, 0),
            AutoSize = true
        };
        Controls.Add(infoLabel);

        var label = new Label
        {
            Text = "新导师组：",
            Location = new Point(15, 55),
            AutoSize = true
        };
        Controls.Add(label);

        _combo = new ComboBox
        {
            Location = new Point(100, 52),
            Size = new Size(240, 25),
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        Controls.Add(_combo);

        var okBtn = new Button
        {
            Text = "确定",
            Location = new Point(110, 105),
            Size = new Size(80, 30)
        };
        okBtn.Click += OnOk;
        Controls.Add(okBtn);

        var cancelBtn = new Button
        {
            Text = "取消",
            Location = new Point(200, 105),
            Size = new Size(80, 30)
        };
        cancelBtn.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(cancelBtn);

        AcceptButton = okBtn;
        CancelButton = cancelBtn;

        Load += (_, _) => PopulateAdvisors(currentAdvisor);
    }

    private void PopulateAdvisors(string currentAdvisor)
    {
        _combo.Items.Clear();
        var advisors = GroupManager.GetAllAdvisorGroups();
        foreach (var a in advisors)
            _combo.Items.Add(a);

        if (currentAdvisor != null && _combo.Items.Contains(currentAdvisor))
            _combo.SelectedItem = currentAdvisor;
        else if (_combo.Items.Count > 0)
            _combo.SelectedIndex = 0;
    }

    private void OnOk(object? sender, EventArgs e)
    {
        var sel = _combo.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(sel))
        {
            MessageBox.Show("请选择新的导师组", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        SelectedAdvisor = sel;
        DialogResult = DialogResult.OK;
        Close();
    }
}
