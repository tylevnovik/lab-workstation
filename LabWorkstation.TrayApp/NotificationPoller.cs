using LabWorkstation.Common.Notifications;

namespace LabWorkstation.TrayApp;

/// <summary>
/// 通知轮询器。每15秒读取 pending 通知，与当前用户本地已读列表比对，
/// 只弹窗显示该用户尚未看过的通知。弹窗后将 ID 记入本地已读列表。
/// 同时检测已弹出的通知是否被 Admin 删除，删除后自动关闭对应弹窗。
/// </summary>
public sealed class NotificationPoller : IDisposable
{
    private readonly System.Windows.Forms.Timer _timer;
    private readonly NotifyIcon _notifyIcon;
    private readonly Dictionary<string, Dialogs.NotificationPopup> _activePopups = new();
    private HashSet<string> _seenIds;

    public NotificationPoller(NotifyIcon notifyIcon)
    {
        _notifyIcon = notifyIcon;
        _seenIds = NotificationSeenStore.LoadSeenIds();
        _timer = new System.Windows.Forms.Timer { Interval = 15000 };
        _timer.Tick += (_, _) => Check();
    }

    public void Start()
    {
        Check();
        _timer.Start();
    }

    private void Check()
    {
        try
        {
            var pending = NotificationStore.GetPending();
            var pendingIds = pending.Select(n => n.Id).ToHashSet();

            // 关闭已被 Admin 删除的通知弹窗
            var toClose = _activePopups.Keys.Where(id => !pendingIds.Contains(id)).ToList();
            foreach (var id in toClose)
            {
                if (_activePopups.TryGetValue(id, out var popup) && !popup.IsDisposed)
                {
                    popup.ForceClose();
                    popup.Dispose();
                }
                _activePopups.Remove(id);
            }

            if (pending.Count == 0) return;

            // 筛选当前用户尚未看过的通知
            var newNotifications = pending
                .Where(n => !_seenIds.Contains(n.Id))
                .OrderBy(n => n.Timestamp)
                .ToList();

            if (newNotifications.Count == 0) return;

            foreach (var n in newNotifications)
            {
                NotificationSeenStore.MarkSeen(n.Id);
                _seenIds.Add(n.Id);

                var popup = new Dialogs.NotificationPopup(n);
                _activePopups[n.Id] = popup;
                popup.FormClosed += (_, _) => _activePopups.Remove(n.Id);
                popup.Show();
            }
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
        foreach (var popup in _activePopups.Values)
        {
            if (!popup.IsDisposed) popup.Dispose();
        }
        _activePopups.Clear();
    }
}
