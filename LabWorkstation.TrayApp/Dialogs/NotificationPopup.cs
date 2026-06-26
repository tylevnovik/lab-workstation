using System.Drawing;
using LabWorkstation.Common.Notifications;

namespace LabWorkstation.TrayApp.Dialogs;

/// <summary>
/// 通知弹窗。紧急通知需手动关闭，非紧急30秒自动关闭。
/// 对应原 PS Show-NotificationPopup。
/// </summary>
public sealed class NotificationPopup : Form
{
    private static readonly Color BgColor = Color.FromArgb(30, 30, 46);
    private static readonly Color AccentColor = Color.FromArgb(137, 180, 250);
    private static readonly Color TextColor = Color.FromArgb(205, 214, 244);

    public NotificationPopup(Notification n)
    {
        Text = n.Title;
        Size = new Size(380, 220);
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        TopMost = true;
        BackColor = BgColor;
        Font = new Font("Microsoft YaHei UI", 9.5f);

        // 位置：右下角
        var screen = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(screen.Right - 400, screen.Bottom - 240);

        // 重要程度颜色
        var impColor = n.ImportanceLevel switch
        {
            Importance.Urgent => Color.FromArgb(243, 139, 168),
            Importance.Important => Color.FromArgb(249, 226, 175),
            _ => Color.FromArgb(147, 153, 178)
        };

        var titleLabel = new Label
        {
            Text = $"[{n.ImportanceText}] {n.Title}",
            ForeColor = impColor,
            Font = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold),
            Location = new Point(15, 15),
            MaximumSize = new Size(350, 0),
            AutoSize = true
        };
        Controls.Add(titleLabel);

        var msgLabel = new Label
        {
            Text = n.Message,
            ForeColor = TextColor,
            Font = new Font("Microsoft YaHei UI", 10f),
            Location = new Point(15, 50),
            MaximumSize = new Size(330, 80),
            AutoSize = true
        };
        Controls.Add(msgLabel);

        var closeBtn = new Button
        {
            Text = "我知道了",
            Size = new Size(100, 30),
            Location = new Point(135, 145),
            BackColor = AccentColor,
            ForeColor = BgColor,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold)
        };
        closeBtn.Click += (_, _) => Close();
        Controls.Add(closeBtn);

        // 非紧急通知30秒自动关闭
        if (n.ImportanceLevel != Importance.Urgent)
        {
            var timer = new System.Windows.Forms.Timer { Interval = 30000 };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                timer.Dispose();
                if (!IsDisposed) Close();
            };
            timer.Start();
        }
    }
}
