namespace LabWorkstation.Monitor;

/// <summary>
/// 告警冷却管理。同一 AlertKey 在冷却窗口内不重复告警。
/// 对应原 PowerShell 脚本的 Check-CanAlert / Record-AlertTime。
/// </summary>
public sealed class AlertCooldown
{
    private readonly Dictionary<string, DateTime> _lastAlert = new();
    private readonly object _gate = new();
    private readonly TimeSpan _cooldown;

    public AlertCooldown(TimeSpan cooldown)
    {
        _cooldown = cooldown;
    }

    /// <summary>指定 key 是否仍在冷却期内（true 表示应跳过告警）。</summary>
    public bool IsOnCooldown(string key)
    {
        lock (_gate)
        {
            if (!_lastAlert.TryGetValue(key, out var last))
                return false;
            return DateTime.Now - last < _cooldown;
        }
    }

    /// <summary>记录本次告警时间，开始新一轮冷却。</summary>
    public void RecordAlert(string key)
    {
        lock (_gate)
        {
            _lastAlert[key] = DateTime.Now;
        }
    }
}
