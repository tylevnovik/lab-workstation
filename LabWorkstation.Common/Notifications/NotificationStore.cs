using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using LabWorkstation.Common.Configuration;
using LabWorkstation.Common.Mock;

namespace LabWorkstation.Common.Notifications;

/// <summary>通知重要程度。</summary>
public enum Importance { Normal, Important, Urgent }

/// <summary>通知数据模型。对应原 PS 通知 JSON 的结构。</summary>
public sealed class Notification
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public Importance ImportanceLevel { get; set; } = Importance.Normal;
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string Sender { get; set; } = "";

    /// <summary>原 PS 中以中文存储重要程度，此处做映射。</summary>
    public string ImportanceText => ImportanceLevel switch
    {
        Importance.Urgent => "紧急",
        Importance.Important => "重要",
        _ => "普通"
    };

    public static Importance ParseImportance(string? text) => text switch
    {
        "紧急" => Importance.Urgent,
        "重要" => Importance.Important,
        _ => Importance.Normal
    };
}

/// <summary>
/// 通知文件读写。通知以 JSON 文件落盘到 pending/sent 目录。
/// pending 目录由 Admin 写入，所有用户的 TrayApp 只读轮询。
/// 每个用户的"已读"状态由 TrayApp 在本地独立追踪（不修改共享文件），
/// 避免多用户场景下一个用户标记后其他用户看不到的问题。
/// </summary>
public static class NotificationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Encoder = global::System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>发送一条通知（写入 pending 目录）。</summary>
    public static string Send(string title, string message, Importance importance, string sender)
    {
        if (LabConfig.TestMode) return MockState.SendNotification(title, message, importance, sender);
        Directory.CreateDirectory(LabConfig.NotifyPendingPath);
        Directory.CreateDirectory(LabConfig.NotifySentPath);

        var id = Guid.NewGuid().ToString("N")[..8];
        var ts = DateTime.Now.ToString("yyyyMMddHHmmss");
        var fileName = $"{id}_{ts}.json";
        var filePath = Path.Combine(LabConfig.NotifyPendingPath, fileName);

        var n = new Notification
        {
            Id = id,
            Title = title,
            Message = message,
            ImportanceLevel = importance,
            Timestamp = DateTime.Now,
            Sender = sender
        };

        var json = JsonSerializer.Serialize(n, JsonOptions);
        File.WriteAllText(filePath, json, Encoding.UTF8);
        return fileName;
    }

    /// <summary>读取 pending 目录下所有通知（所有用户共享，TrayApp 只读）。</summary>
    public static List<Notification> GetPending()
    {
        if (LabConfig.TestMode) return MockState.GetPending();
        return LoadFromDir(LabConfig.NotifyPendingPath);
    }

    /// <summary>读取 sent 目录下的历史通知（按时间倒序）。</summary>
    public static List<Notification> GetSent()
    {
        if (LabConfig.TestMode) return MockState.GetSent();
        return LoadFromDir(LabConfig.NotifySentPath)
            .OrderByDescending(n => n.Timestamp)
            .ToList();
    }

    /// <summary>将 pending 目录下所有通知移至 sent（归档）。Admin 手动清理操作。</summary>
    public static int ArchivePending()
    {
        if (LabConfig.TestMode) return MockState.ArchivePending();
        if (!Directory.Exists(LabConfig.NotifyPendingPath)) return 0;
        Directory.CreateDirectory(LabConfig.NotifySentPath);

        var moved = 0;
        foreach (var file in Directory.EnumerateFiles(LabConfig.NotifyPendingPath, "*.json"))
        {
            var dest = Path.Combine(LabConfig.NotifySentPath, Path.GetFileName(file));
            File.Move(file, dest, overwrite: true);
            moved++;
        }
        return moved;
    }

    /// <summary>删除指定通知（从 pending 或 sent 目录）。</summary>
    public static bool Delete(string notificationId)
    {
        if (LabConfig.TestMode) return true;
        foreach (var dir in new[] { LabConfig.NotifyPendingPath, LabConfig.NotifySentPath })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                try
                {
                    var json = File.ReadAllText(file, Encoding.UTF8);
                    var n = JsonSerializer.Deserialize<Notification>(json, JsonOptions);
                    if (n != null && n.Id == notificationId)
                    {
                        File.Delete(file);
                        return true;
                    }
                }
                catch { }
            }
        }
        return false;
    }

    // ── 内部方法 ──

    private static List<Notification> LoadFromDir(string dir)
    {
        var list = new List<Notification>();
        if (!Directory.Exists(dir)) return list;
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file, Encoding.UTF8);
                var n = JsonSerializer.Deserialize<Notification>(json, JsonOptions);
                if (n != null)
                {
                    if (string.IsNullOrEmpty(n.Id)) n.Id = Path.GetFileNameWithoutExtension(file);
                    list.Add(n);
                }
            }
            catch { /* 跳过损坏的 JSON */ }
        }
        return list;
    }
}

/// <summary>
/// 每用户已读追踪。每个用户在本地维护一个已看过通知 ID 的列表，
/// 不修改共享的 pending 文件，避免多用户冲突。
/// 文件存储在用户的 LocalApplicationData 目录下。
/// </summary>
public static class NotificationSeenStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    /// <summary>获取当前用户的已读存储文件路径。</summary>
    private static string GetSeenFilePath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LabWorkstation");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "notification_seen.json");
    }

    /// <summary>加载当前用户已看过通知 ID 集合。</summary>
    public static HashSet<string> LoadSeenIds()
    {
        try
        {
            var path = GetSeenFilePath();
            if (!File.Exists(path)) return new HashSet<string>();
            var json = File.ReadAllText(path, Encoding.UTF8);
            var ids = JsonSerializer.Deserialize<List<string>>(json, JsonOptions);
            return ids != null ? new HashSet<string>(ids) : new HashSet<string>();
        }
        catch { return new HashSet<string>(); }
    }

    /// <summary>将通知 ID 添加到当前用户的已看列表并持久化。</summary>
    public static void MarkSeen(string notificationId)
    {
        try
        {
            var seen = LoadSeenIds();
            if (seen.Add(notificationId))
            {
                var path = GetSeenFilePath();
                var json = JsonSerializer.Serialize(seen.ToList(), JsonOptions);
                File.WriteAllText(path, json, Encoding.UTF8);
            }
        }
        catch { /* 持久化失败不影响弹窗 */ }
    }

    /// <summary>清理已读列表中不存在于 pending/sent 的旧 ID（可选维护）。</summary>
    public static void Prune(IEnumerable<string> activeIds)
    {
        try
        {
            var activeSet = new HashSet<string>(activeIds);
            var seen = LoadSeenIds();
            var pruned = seen.Where(id => activeSet.Contains(id)).ToHashSet();
            if (pruned.Count != seen.Count)
            {
                var path = GetSeenFilePath();
                var json = JsonSerializer.Serialize(pruned.ToList(), JsonOptions);
                File.WriteAllText(path, json, Encoding.UTF8);
            }
        }
        catch { }
    }
}
