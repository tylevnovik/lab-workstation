using System.Drawing;
using System.Drawing.Drawing2D;
using LabWorkstation.Common.Notifications;

namespace LabWorkstation.TrayApp.Dialogs;

/// <summary>
/// 自定义通知弹窗。根据重要程度有不同的展示效果：
/// - 紧急(Urgent)：红色边框，不可手动关闭，持久显示，置顶，闪烁提醒
/// - 重要(Important)：橙色边框，需手动点击关闭，不自动消失
/// - 普通(Normal)：蓝色边框，30秒后自动关闭
/// 不使用系统通知，完全自绘弹窗。
/// </summary>
public sealed class NotificationPopup : Form
{
    private static readonly Color BgColor = Color.FromArgb(30, 30, 46);
    private static readonly Color TextColor = Color.FromArgb(205, 214, 244);
    private static readonly Color AccentBlue = Color.FromArgb(137, 180, 250);
    private static readonly Color UrgentRed = Color.FromArgb(243, 139, 168);
    private static readonly Color ImportantOrange = Color.FromArgb(249, 226, 175);

    private readonly Notification _notification;
    private System.Windows.Forms.Timer? _autoCloseTimer;
    private System.Windows.Forms.Timer? _flashTimer;
    private bool _flashOn;

    // ── 拖拽支持 ──
    private bool _isDragging;
    private Point _dragStart;

    public NotificationPopup(Notification n)
    {
        _notification = n;
        Text = n.Title;
        Size = new Size(420, 260);
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        TopMost = true;
        BackColor = BgColor;
        Font = new Font("Microsoft YaHei UI", 9.5f);
        ShowInTaskbar = false;

        // 位置：右下角，多个通知向上堆叠
        var screen = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(screen.Right - 440, screen.Bottom - 280);

        // 圆角
        Region = CreateRoundedRegion(ClientRectangle, 12);

        // 等级配色
        var borderColor = n.ImportanceLevel switch
        {
            Importance.Urgent => UrgentRed,
            Importance.Important => ImportantOrange,
            _ => AccentBlue
        };

        // ── 顶部色条（同时作为拖拽手柄）──
        var topBar = new Panel
        {
            Location = new Point(0, 0),
            Size = new Size(420, 5),
            BackColor = borderColor,
            Cursor = Cursors.SizeAll
        };
        // 顶部色条支持拖拽
        topBar.MouseDown += (_, e) => StartDrag(e);
        topBar.MouseMove += (_, e) => DoDrag(e);
        topBar.MouseUp += (_, _) => _isDragging = false;
        Controls.Add(topBar);

        // ── 关闭按钮（紧急不显示）──
        if (n.ImportanceLevel != Importance.Urgent)
        {
            var closeBtn = new Label
            {
                Text = "x",
                Font = new Font("Arial", 12f, FontStyle.Bold),
                ForeColor = Color.FromArgb(150, 150, 170),
                Location = new Point(390, 8),
                Size = new Size(25, 25),
                Cursor = Cursors.Hand,
                TextAlign = ContentAlignment.MiddleCenter
            };
            closeBtn.Click += (_, _) => Close();
            closeBtn.MouseEnter += (_, _) => closeBtn.ForeColor = Color.White;
            closeBtn.MouseLeave += (_, _) => closeBtn.ForeColor = Color.FromArgb(150, 150, 170);
            Controls.Add(closeBtn);
        }

        // ── 重要程度标签 ──
        var impLabel = new Label
        {
            Text = $"[{n.ImportanceText}]",
            ForeColor = borderColor,
            Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold),
            Location = new Point(20, 18),
            AutoSize = true,
            Cursor = Cursors.SizeAll
        };
        MakeDraggable(impLabel);
        Controls.Add(impLabel);

        // ── 时间 ──
        var timeLabel = new Label
        {
            Text = n.Timestamp.ToString("MM-dd HH:mm"),
            ForeColor = Color.FromArgb(120, 130, 150),
            Font = new Font("Microsoft YaHei UI", 8f),
            Location = new Point(100, 20),
            AutoSize = true,
            Cursor = Cursors.SizeAll
        };
        MakeDraggable(timeLabel);
        Controls.Add(timeLabel);

        // ── 标题（显式测量高度，支持长标题换行）──
        var titleFont = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold);
        var titleSize = TextRenderer.MeasureText(
            n.Title, titleFont,
            new Size(370, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
        var titleLabel = new Label
        {
            Text = n.Title,
            ForeColor = TextColor,
            Font = titleFont,
            Location = new Point(20, 45),
            Size = new Size(370, titleSize.Height),
            AutoSize = false,
            Cursor = Cursors.SizeAll
        };
        MakeDraggable(titleLabel);
        Controls.Add(titleLabel);

        // 消息内容 Y 坐标：基于标题实际底部 + 间距
        var msgTop = titleLabel.Bottom + 15;
        // 更新 msgLabel 的 Y 坐标（之前硬编码 95）
        // 重新计算 msgHeight 并设置 msgLabel.Location
        int msgY = msgTop;

        // ── 消息内容（自动换行 + 显式测量高度，超长时内部滚动）──
        // 用 RichTextBox 实现长文本滚动，避免顶掉底部按钮
        // 拖拽通过顶部色条 + 标题 + 重要程度标签实现，消息框不参与拖拽（避免与文本选择冲突）
        var msgFont = new Font("Microsoft YaHei UI", 10f);

        // 先计算完整文本高度
        var msgSize = TextRenderer.MeasureText(
            n.Message, msgFont,
            new Size(370, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
        var fullMsgHeight = msgSize.Height;

        // 计算消息框可用最大高度：
        // 屏幕工作区 80% - 顶部 95px（标题区） - 底部 60px（按钮 + 留白）
        var maxScreenHeight = (int)(Screen.PrimaryScreen!.WorkingArea.Height * 0.8);
        var maxMsgHeight = maxScreenHeight - msgY - 60;
        if (maxMsgHeight < 80) maxMsgHeight = 80;
        // 消息框实际高度：不超过可用最大高度
        var msgHeight = Math.Min(fullMsgHeight, maxMsgHeight);

        var msgBox = new RichTextBox
        {
            Text = n.Message,
            ForeColor = Color.FromArgb(180, 190, 210),
            BackColor = BgColor,
            Font = msgFont,
            Location = new Point(20, msgY),
            Size = new Size(370, msgHeight),
            BorderStyle = BorderStyle.None,
            ReadOnly = true,
            WordWrap = true,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            Cursor = Cursors.Default
        };
        Controls.Add(msgBox);

        // 底部操作区 Y 坐标：基于 msgBox 实际底部 + 间距
        var bottomY = msgBox.Bottom + 15;

        // ── 底部操作区 ──
        if (n.ImportanceLevel == Importance.Urgent)
        {
            // 紧急：显示提示，不可关闭（但可拖动）
            var lockLabel = new Label
            {
                Text = "此为紧急通知，需管理员处理后方可关闭（可拖动顶部条移动位置）",
                ForeColor = UrgentRed,
                Font = new Font("Microsoft YaHei UI", 8.5f),
                Location = new Point(20, bottomY),
                AutoSize = true,
                Cursor = Cursors.SizeAll
            };
            MakeDraggable(lockLabel);
            Controls.Add(lockLabel);

            // 闪烁效果
            _flashTimer = new System.Windows.Forms.Timer { Interval = 800 };
            _flashTimer.Tick += (_, _) =>
            {
                _flashOn = !_flashOn;
                topBar.BackColor = _flashOn ? Color.FromArgb(255, 200, 200) : UrgentRed;
                impLabel.ForeColor = _flashOn ? Color.White : UrgentRed;
            };
            _flashTimer.Start();
        }
        else
        {
            // 重要和普通：显示"我知道了"按钮
            var ackBtn = new Button
            {
                Text = "我知道了",
                Size = new Size(100, 32),
                Location = new Point(290, bottomY),
                BackColor = borderColor,
                ForeColor = BgColor,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            ackBtn.FlatAppearance.BorderSize = 0;
            ackBtn.Click += (_, _) => Close();
            Controls.Add(ackBtn);
        }

        // 普通通知30秒自动关闭
        if (n.ImportanceLevel == Importance.Normal)
        {
            _autoCloseTimer = new System.Windows.Forms.Timer { Interval = 30000 };
            _autoCloseTimer.Tick += (_, _) =>
            {
                _autoCloseTimer.Stop();
                _autoCloseTimer.Dispose();
                if (!IsDisposed) Close();
            };
            _autoCloseTimer.Start();
        }

        // ── 弹窗高度自适应内容 ──
        // 计算所有子控件的最低边界，加上底部留白，作为弹窗高度
        var maxBottom = 0;
        foreach (Control c in Controls)
        {
            var b = c.Bottom;
            if (b > maxBottom) maxBottom = b;
        }
        var newHeight = maxBottom + 20;  // 底部留白 20px
        if (newHeight < 200) newHeight = 200;  // 最小高度
        // 最大高度限制为屏幕工作区高度的 80%（避免超出屏幕）
        // msgBox 的高度已在前面限制，所以这里 newHeight 不会超过屏幕 80%
        if (newHeight > maxScreenHeight) newHeight = maxScreenHeight;
        if (newHeight != Height)
        {
            // 高度增加时，位置上移以保持右下角对齐
            var dy = newHeight - Height;
            Location = new Point(Location.X, Location.Y - dy);
            Size = new Size(Width, newHeight);
            // 重新生成圆角区域
            Region = CreateRoundedRegion(ClientRectangle, 12);
        }

        // 绘制边框
        Paint += (_, e) =>
        {
            using var pen = new Pen(borderColor, 1);
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            e.Graphics.DrawPath(pen, CreateRoundedPath(rect, 12));
        };
    }

    // ── 拖拽实现 ──────────────────────────────────────────────

    /// <summary>
    /// 为指定控件附加拖拽事件，使其能拖动整个弹窗。
    /// 用于标题/重要程度标签等文本区域，扩大可拖拽范围。
    /// </summary>
    private void MakeDraggable(Control c)
    {
        c.MouseDown += (_, e) => StartDrag(e);
        c.MouseMove += (_, e) => DoDrag(e);
        c.MouseUp += (_, _) => _isDragging = false;
    }

    /// <summary>开始拖拽：记录鼠标按下位置。</summary>
    private void StartDrag(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _isDragging = true;
            _dragStart = e.Location;
        }
    }

    /// <summary>执行拖拽：按鼠标偏移移动弹窗位置。</summary>
    private void DoDrag(MouseEventArgs e)
    {
        if (_isDragging)
        {
            Location = new Point(Location.X + e.X - _dragStart.X, Location.Y + e.Y - _dragStart.Y);
        }
    }

    /// <summary>阻止 Alt+F4 和窗口关闭按钮（紧急通知）。</summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // ForceClose 方法可设置 _forceClose 标志来绕过阻止
        if (!_forceClose &&
            _notification.ImportanceLevel == Importance.Urgent &&
            e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
        }
        base.OnFormClosing(e);
    }

    private bool _forceClose;

    /// <summary>
    /// 强制关闭弹窗（即使紧急通知也被阻止的情况下）。
    /// 供 NotificationPoller 在 Admin 删除通知后调用。
    /// </summary>
    public void ForceClose()
    {
        _forceClose = true;
        _flashTimer?.Stop();
        if (!IsDisposed) Close();
    }

    private static Region CreateRoundedRegion(Rectangle rect, int radius)
    {
        using var path = CreateRoundedPath(rect, radius);
        return new Region(path);
    }

    private static GraphicsPath CreateRoundedPath(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _autoCloseTimer?.Dispose();
            _flashTimer?.Dispose();
        }
        base.Dispose(disposing);
    }
}
