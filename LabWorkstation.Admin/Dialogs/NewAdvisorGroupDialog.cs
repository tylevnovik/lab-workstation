using LabWorkstation.Common.LocalAccounts;

namespace LabWorkstation.Admin.Dialogs;

/// <summary>新建导师组对话框。返回输入的导师名称。</summary>
public class NewAdvisorGroupDialog : Form
{
    private readonly TextBox _nameInput;

    public string AdvisorName { get; private set; } = "";

    public NewAdvisorGroupDialog()
    {
        Text = "新建导师组";
        Size = new Size(350, 160);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        Font = new Font("Microsoft YaHei UI", 9F);

        var label = new Label
        {
            Text = "导师名称（如 张老师）：",
            Location = new Point(15, 18),
            AutoSize = true
        };
        Controls.Add(label);

        _nameInput = new TextBox
        {
            Location = new Point(15, 45),
            Size = new Size(300, 25)
        };
        Controls.Add(_nameInput);

        var okBtn = new Button
        {
            Text = "创建",
            Location = new Point(120, 85),
            Size = new Size(80, 30)
        };
        okBtn.Click += OnOk;
        Controls.Add(okBtn);

        AcceptButton = okBtn;
    }

    private void OnOk(object? sender, EventArgs e)
    {
        var name = _nameInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show("请输入导师名称", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (System.Text.RegularExpressions.Regex.IsMatch(name, @"[^a-zA-Z0-9_\u4e00-\u9fff\-]"))
        {
            MessageBox.Show("导师名称只能包含中英文字符、数字、下划线和短横线", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        AdvisorName = name;
        DialogResult = DialogResult.OK;
        Close();
    }
}
