using System.Drawing;
using System.Drawing.Drawing2D;
using System.Diagnostics;
using System.Runtime.InteropServices;
using LabWorkstation.Common.Configuration;
using LabWorkstation.Common.LocalAccounts;

namespace LabWorkstation.TrayApp;

/// <summary>
/// 桌面悬浮导航窗。置顶、可拖拽、无标题栏、自绘圆角深色背景。
/// 对应原 PS Lab-TrayApp.ps1 的悬浮窗主体。
/// </summary>
public sealed class FloatingWidget : Form
{
    // ── 配色（与原 PS 一致）──────────────────────────────
    private static readonly Color BgColor = Color.FromArgb(30, 30, 46);
    private static readonly Color AccentColor = Color.FromArgb(137, 180, 250);
    private static readonly Color TextColor = Color.FromArgb(205, 214, 244);
    private static readonly Color SubtleColor = Color.FromArgb(147, 153, 178);
    private static readonly Color BtnBg = Color.FromArgb(49, 50, 68);
    private static readonly Color BtnHover = Color.FromArgb(69, 71, 90);
    private static readonly Color BorderColor = Color.FromArgb(69, 71, 90);

    private static readonly Font FontUser = new("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
    private static readonly Font FontGroup = new("Microsoft YaHei UI", 8f);
    private static readonly Font FontBtn = new("Microsoft YaHei UI", 8f);

    // ── 用户上下文 ────────────────────────────────────────
    public string UserName { get; }
    public string AdvisorName { get; }
    public string UserFolder { get; }
    public string GroupFolder { get; }

    // ── 按钮定义 ──────────────────────────────────────────
    private readonly ButtonDef[] _buttons;
    private int _hoveredBtn = -1;
    private Rectangle _tagRect = Rectangle.Empty;
    private Rectangle _selfServiceRect = Rectangle.Empty;
    private readonly Dictionary<string, bool> _pathExistsCache = new();

    // ── 拖拽 ──────────────────────────────────────────────
    private bool _isDragging;
    private Point _dragStart;

    // ── 事件 ──────────────────────────────────────────────
    public event EventHandler? SelfServiceRequested;
    public event EventHandler? NoticeRequested;

    private readonly NotifyIcon? _notifyIcon;

    public FloatingWidget(NotifyIcon? notifyIcon)
    {
        _notifyIcon = notifyIcon;

        UserName = AccountManager.GetCurrentShortUserName();
        AdvisorName = GroupManager.GetUserAdvisorGroup(UserName);
        UserFolder = Path.Combine(LabConfig.UsersRootPath, UserName);
        GroupFolder = string.IsNullOrEmpty(AdvisorName) ? "" : Path.Combine(LabConfig.SharePath, AdvisorName);

        _buttons = new[]
        {
            new ButtonDef("个人", UserFolder, new Rectangle(10, 60, 70, 32)),
            new ButtonDef("组内", GroupFolder, new Rectangle(90, 60, 70, 32)),
            new ButtonDef("公共", LabConfig.PublicPath, new Rectangle(170, 60, 70, 32))
        };

        // 窗体样式
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(250, 100);
        BackColor = BgColor;
        DoubleBuffered = true;
        KeyPreview = true;

        // 初始位置：屏幕右下角（任务栏上方）
        var screen = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(screen.Right - 260, screen.Bottom - 105);

        // 圆角区域
        Load += (_, _) =>
        {
            Region = new Region(MakeRoundedRect(new Rectangle(0, 0, Width, Height), 8));
        };

        // 阻止外部关闭
        FormClosing += (_, e) =>
        {
            if (e.CloseReason != CloseReason.ApplicationExitCall)
                e.Cancel = true;
        };
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _isDragging = true;
            _dragStart = e.Location;
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        // 悬停高亮
        var oldHover = _hoveredBtn;
        _hoveredBtn = -1;
        for (var i = 0; i < _buttons.Length; i++)
        {
            if (_buttons[i].Rect.Contains(e.Location))
            {
                _hoveredBtn = i;
                break;
            }
        }
        if (oldHover != _hoveredBtn) Invalidate();

        // 拖拽
        if (_isDragging)
        {
            Location = new Point(Location.X + e.X - _dragStart.X, Location.Y + e.Y - _dragStart.Y);
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _isDragging = false;
            // 点击“我的账户”自助服务链接
            if (_selfServiceRect.Contains(e.Location))
            {
                SelfServiceRequested?.Invoke(this, EventArgs.Empty);
                return;
            }
            // 点击“公共工作站”标签
            if (_tagRect.Contains(e.Location))
            {
                NoticeRequested?.Invoke(this, EventArgs.Empty);
                return;
            }
            // 点击按钮
            for (var i = 0; i < _buttons.Length; i++)
            {
                if (_buttons[i].Rect.Contains(e.Location))
                {
                    OpenFolder(_buttons[i].Path);
                    _pathExistsCache.Clear();
                    Invalidate();
                    break;
                }
            }
        }
        base.OnMouseUp(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hoveredBtn != -1)
        {
            _hoveredBtn = -1;
            Invalidate();
        }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right && _notifyIcon != null)
        {
            // 右键显示托盘菜单
            _notifyIcon.ContextMenuStrip?.Show(this, e.Location);
        }
        base.OnMouseClick(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if ((e.Alt && e.KeyCode == Keys.F4) || (e.Control && e.KeyCode == Keys.W))
            e.Handled = true;
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

        // 圆角背景
        var fullRect = new Rectangle(0, 0, Width - 1, Height - 1);
        var roundPath = MakeRoundedRect(fullRect, 8);
        using (var bgBrush = new SolidBrush(BgColor))
            g.FillPath(bgBrush, roundPath);
        using (var borderPen = new Pen(BorderColor, 1))
            g.DrawPath(borderPen, roundPath);

        // 顶部拖拽提示条
        using (var dragBrush = new SolidBrush(Color.FromArgb(88, 91, 112)))
            g.FillRectangle(dragBrush, new Rectangle(115, 4, 20, 3));

        // 用户名
        using (var userBrush = new SolidBrush(AccentColor))
            g.DrawString(UserName, FontUser, userBrush, 14, 14);

        // 组名
        var groupText = string.IsNullOrEmpty(AdvisorName) ? "未分组" : $"{AdvisorName}组";
        using (var groupBrush = new SolidBrush(SubtleColor))
            g.DrawString(groupText, FontGroup, groupBrush, 14, 33);

        // “我的账户 →”自助服务链接
        var ssText = "我的账户 →";
        var ssSize = g.MeasureString(ssText, FontBtn);
        _selfServiceRect = new Rectangle(14, 44, (int)ssSize.Width + 4, 16);
        using (var ssBrush = new SolidBrush(AccentColor))
            g.DrawString(ssText, FontBtn, ssBrush, 14, 44);

        // “公共工作站”标签（测试模式时显示“测试模式”）
        var tagText = LabConfig.TestMode ? "测试模式" : "公共工作站";
        var tagSize = g.MeasureString(tagText, FontBtn);
        _tagRect = new Rectangle(Width - (int)tagSize.Width - 18, 16, (int)tagSize.Width + 8, 18);
        var tagPath = MakeRoundedRect(_tagRect, 4);
        var tagBgColor = LabConfig.TestMode ? Color.FromArgb(203, 166, 247) : Color.FromArgb(49, 50, 68);
        using (var tagBgBrush = new SolidBrush(tagBgColor))
            g.FillPath(tagBgBrush, tagPath);
        using (var tagTextBrush = new SolidBrush(LabConfig.TestMode ? Color.FromArgb(30, 30, 46) : SubtleColor))
            g.DrawString(tagText, FontBtn, tagTextBrush, _tagRect.X + 4, _tagRect.Y + 2);

        // 三个按钮
        for (var i = 0; i < _buttons.Length; i++)
        {
            var btn = _buttons[i];
            var color = i == _hoveredBtn ? BtnHover : BtnBg;
            var btnPath = MakeRoundedRect(btn.Rect, 6);
            using (var btnFillBrush = new SolidBrush(color))
                g.FillPath(btnFillBrush, btnPath);

            // 路径存在性缓存
            if (!_pathExistsCache.TryGetValue(btn.Path ?? "", out var exists))
            {
                exists = !string.IsNullOrEmpty(btn.Path) && Directory.Exists(btn.Path);
                _pathExistsCache[btn.Path ?? ""] = exists;
            }
            var btnTextColor = exists ? TextColor : SubtleColor;
            var textSize = g.MeasureString(btn.Text, FontBtn);
            var tx = btn.Rect.X + (btn.Rect.Width - (int)textSize.Width) / 2;
            var ty = btn.Rect.Y + (btn.Rect.Height - (int)textSize.Height) / 2;
            using (var btnTextBrush = new SolidBrush(btnTextColor))
                g.DrawString(btn.Text, FontBtn, btnTextBrush, tx, ty);
        }
    }

    private static void OpenFolder(string? path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            {
                // 用 UseShellExecute 直接打开文件夹路径（不通过 explorer.exe 传参），
                // 避免提权会话下 explorer.exe 参数丢失的问题。
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true,
                    Verb = "open"
                });
            }
        }
        catch
        {
            // fallback：尝试 explorer.exe
            try
            {
                if (!string.IsNullOrEmpty(path))
                    Process.Start("explorer.exe", $"\"{path}\"");
            }
            catch { /* 静默 */ }
        }
    }

    private static GraphicsPath MakeRoundedRect(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private sealed record ButtonDef(string Text, string? Path, Rectangle Rect);
}
