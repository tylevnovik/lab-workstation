namespace LabWorkstation.Admin.Dialogs;

/// <summary>重置密码对话框。校验两次输入一致且长度≥8。</summary>
public class ResetPasswordDialog : Form
{
    private readonly TextBox _pwdInput;
    private readonly TextBox _confirmInput;

    public string NewPassword { get; private set; } = "";

    public ResetPasswordDialog(string username)
    {
        Text = $"重置密码 - {username}";
        Size = new Size(360, 200);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        Font = new Font("Microsoft YaHei UI", 9F);

        var pwdLabel = new Label
        {
            Text = "新密码：",
            Location = new Point(15, 18),
            AutoSize = true
        };
        Controls.Add(pwdLabel);

        _pwdInput = new TextBox
        {
            Location = new Point(95, 15),
            Size = new Size(230, 25),
            UseSystemPasswordChar = true
        };
        Controls.Add(_pwdInput);

        var confirmLabel = new Label
        {
            Text = "确认密码：",
            Location = new Point(15, 53),
            AutoSize = true
        };
        Controls.Add(confirmLabel);

        _confirmInput = new TextBox
        {
            Location = new Point(95, 50),
            Size = new Size(230, 25),
            UseSystemPasswordChar = true
        };
        Controls.Add(_confirmInput);

        var okBtn = new Button
        {
            Text = "确定",
            Location = new Point(145, 100),
            Size = new Size(80, 30)
        };
        okBtn.Click += OnOk;
        Controls.Add(okBtn);

        AcceptButton = okBtn;
    }

    private void OnOk(object? sender, EventArgs e)
    {
        if (_pwdInput.Text != _confirmInput.Text)
        {
            MessageBox.Show("两次输入的密码不一致", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (_pwdInput.Text.Length < 8)
        {
            MessageBox.Show("密码长度至少 8 位", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        NewPassword = _pwdInput.Text;
        DialogResult = DialogResult.OK;
        Close();
    }
}
