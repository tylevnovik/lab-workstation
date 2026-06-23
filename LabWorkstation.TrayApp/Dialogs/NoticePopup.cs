using System.Drawing;

namespace LabWorkstation.TrayApp.Dialogs;

/// <summary>
/// 使用须知弹窗（数据存放规则）。对应原 PS Show-NoticePopup。
/// </summary>
public sealed class NoticePopup : Form
{
    private static readonly Color BgColor = Color.FromArgb(30, 30, 46);
    private static readonly Color PanelColor = Color.FromArgb(40, 42, 58);
    private static readonly Color AccentColor = Color.FromArgb(137, 180, 250);
    private static readonly Color TextColor = Color.FromArgb(205, 214, 244);

    public NoticePopup()
    {
        Text = "课题组工作站 · 使用须知";
        Size = new Size(480, 420);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        TopMost = true;
        BackColor = BgColor;
        Font = new Font("Microsoft YaHei UI", 9.5f);

        var title = new Label
        {
            Text = "数据存放规则",
            ForeColor = AccentColor,
            Font = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold),
            Location = new Point(20, 18),
            AutoSize = true
        };
        Controls.Add(title);

        var content = new RichTextBox
        {
            Location = new Point(20, 55),
            Size = new Size(430, 290),
            ReadOnly = true,
            BackColor = PanelColor,
            ForeColor = TextColor,
            BorderStyle = BorderStyle.None,
            Font = new Font("Microsoft YaHei UI", 10f),
            ScrollBars = RichTextBoxScrollBars.Vertical,
            Text = @"个人数据（草稿、私人文件）
  D:\Users\你的用户名\
  仅自己可见，其他人无法访问

组内数据（本组的项目、报告）
  D:\GroupData\你的导师名\对应类别\
  仅本组成员可见

跨组数据（需要多组共享的数据）
  D:\GroupData\_公共\
  所有成员可见

软件安装
  公共软件 → 找管理员装到 Program Files
  个人工具 → 装到 D:\Users\你的用户名\Tools\
  禁止往 GroupData 里装程序

注意事项
  不要把私人数据放在 GroupData 里
  不要在 C 盘存大文件
  用完远程桌面请注销
  跑耗时任务请限制资源占用"
        };
        Controls.Add(content);

        var closeBtn = new Button
        {
            Text = "我知道了",
            Size = new Size(120, 35),
            Location = new Point(175, 360),
            BackColor = AccentColor,
            ForeColor = BgColor,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold)
        };
        closeBtn.Click += (_, _) => Close();
        Controls.Add(closeBtn);
    }
}
