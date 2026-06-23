using LabWorkstation.Common.Notifications;

namespace LabWorkstation.TrayApp;

/// <summary>
/// 通知轮询器。每30秒读取 pending 通知，每个通知每会话只弹一次。
/// 对应原 PS Check-Notifications。紧急通知额外弹窗。
/// </summary>
public sealed class NotificationPoller : IDisposable
{
    private readonly System.Windows.Forms.Timer _timer;
    private readonly NotifyIcon _notifyIcon;
    private readonly HashSet<string> _shownIds = new();

    public NotificationPoller(NotifyIcon notifyIcon)
    {
        _notifyIcon = notifyIcon;
        _timer = new System.Windows.Forms.Timer { Interval = 30000 };
        _timer.Tick += (_, _) => Check();
    }

    public void Start()
    {
        Check(); // 启动时立即检查一次
        _timer.Start();
    }

    private void Check()
    {
        try
        {
            var pending = NotificationStore.GetPending();
            foreach (var n in pending)
            {
                if (_shownIds.Contains(n.Id)) continue;

                var tipIcon = n.ImportanceLevel switch
                {
                    Importance.Urgent => ToolTipIcon.Error,
                    Importance.Important => ToolTipIcon.Warning,
                    _ => ToolTipIcon.Info
                };
                _notifyIcon.ShowBalloonTip(5000, n.Title, n.Message, tipIcon);

                // 紧急通知额外弹窗
                if (n.ImportanceLevel == Importance.Urgent)
                {
                    var popup = new Dialogs.NotificationPopup(n);
                    popup.Show(); // 非阻塞，避免阻塞轮询
                }

                _shownIds.Add(n.Id);
            }
        }
        catch
        {
            // 静默处理错误（文件夹不存在等）
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }
}
