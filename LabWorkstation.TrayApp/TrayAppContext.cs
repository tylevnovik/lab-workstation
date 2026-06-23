using System.Drawing;
using System.Drawing.Drawing2D;
using LabWorkstation.Common.Configuration;
using LabWorkstation.Common.LocalAccounts;

namespace LabWorkstation.TrayApp;

/// <summary>
/// 托盘图标 + 右键菜单 + 应用生命周期管理。
/// 对应原 PS 的托盘图标构建与菜单逻辑。
/// </summary>
public sealed class TrayAppContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly FloatingWidget _widget;
    private readonly NotificationPoller _poller;

    public TrayAppContext()
    {
        _notifyIcon = new NotifyIcon
        {
            Icon = CreateTrayIcon(),
            Visible = true
        };

        _widget = new FloatingWidget(_notifyIcon);
        _widget.SelfServiceRequested += (_, _) => ShowSelfService();
        _widget.NoticeRequested += (_, _) => ShowNotice();

        _notifyIcon.Text = $"课题组工作站 · {_widget.UserName}";
        _notifyIcon.ContextMenuStrip = BuildMenu();
        _notifyIcon.DoubleClick += (_, _) => _widget.Visible = !_widget.Visible;

        _poller = new NotificationPoller(_notifyIcon);
        _poller.Start();

        // 启动气泡提示
        var advisorSuffix = string.IsNullOrEmpty(_widget.AdvisorName) ? "" : $" · {_widget.AdvisorName}组";
        var testPrefix = LabConfig.TestMode ? "【测试模式】" : "";
        _notifyIcon.ShowBalloonTip(3000,
            $"{testPrefix}课题组工作站",
            $"{testPrefix}悬浮导航已就绪。拖拽移动，点击按钮打开文件夹。\n{_widget.UserName}{advisorSuffix}",
            ToolTipIcon.Info);

        _widget.Show();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip { Font = new Font("Microsoft YaHei UI", 9f) };

        var groupText = string.IsNullOrEmpty(_widget.AdvisorName) ? "未分组" : $"{_widget.AdvisorName}组";
        var header = menu.Items.Add($"{_widget.UserName} · {groupText}");
        header.Enabled = false;
        header.ForeColor = Color.Gray;

        menu.Items.Add("-");

        var mToggle = menu.Items.Add("显示/隐藏悬浮窗");
        mToggle.Click += (_, _) => _widget.Visible = !_widget.Visible;

        var mSelf = menu.Items.Add("我的账户");
        mSelf.Click += (_, _) => ShowSelfService();

        menu.Items.Add("-");

        var mNotice = menu.Items.Add("查看使用须知");
        mNotice.Click += (_, _) => ShowNotice();

        var mAbout = menu.Items.Add("关于此工作站");
        mAbout.Click += (_, _) =>
        {
            var groupText2 = string.IsNullOrEmpty(_widget.AdvisorName) ? "未分组" : _widget.AdvisorName;
            MessageBox.Show(
                $"课题组公共工作站\n\n" +
                $"当前用户：{_widget.UserName}\n" +
                $"所属组：{groupText2}\n\n" +
                "数据存放规则：\n  个人 → D:\\Users\\你的用户名\\\n  组内 → D:\\GroupData\\导师名\\\n  跨组 → D:\\GroupData\\_公共\\\n\n如有疑问请联系管理员。",
                "课题组工作站", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };

        menu.Items.Add("-");

        var mExit = menu.Items.Add("退出导航工具");
        mExit.ForeColor = Color.FromArgb(180, 0, 0);
        mExit.Click += (_, _) => ExitApp();

        return menu;
    }

    private void ShowSelfService()
    {
        using var dlg = new Dialogs.SelfServiceDialog(_widget.UserName, _widget.AdvisorName, _widget.GroupFolder);
        dlg.ShowDialog(_widget);
    }

    private void ShowNotice()
    {
        using var popup = new Dialogs.NoticePopup();
        popup.ShowDialog(_widget);
    }

    private void ExitApp()
    {
        _poller.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        // Application.Exit 会以 CloseReason.ApplicationExitCall 关闭悬浮窗，
        // 通过 FloatingWidget 的 FormClosing 检查。
        Application.Exit();
    }

    /// <summary>绘制与原 PS 一致的托盘图标（深色底 + 蓝色文件夹形状）。</summary>
    private static Icon CreateTrayIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.FillRectangle(new SolidBrush(Color.FromArgb(30, 30, 46)), 0, 0, 32, 32);
            var fb = new SolidBrush(Color.FromArgb(137, 180, 250));
            var pts = new[]
            {
                new PointF(5, 11), new PointF(13, 11),
                new PointF(15, 8), new PointF(27, 8),
                new PointF(27, 24), new PointF(5, 24)
            };
            g.FillPolygon(fb, pts);
        }
        var hicon = bmp.GetHicon();
        return Icon.FromHandle(hicon);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _poller.Dispose();
            _notifyIcon.Dispose();
            _widget.Dispose();
        }
        base.Dispose(disposing);
    }
}
