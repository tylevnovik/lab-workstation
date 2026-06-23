using System.IO;
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
/// 通知文件读写。通知以 JSON 文件落盘到 pending/sent 目录，
/// 由用户端 TrayApp 轮询。对应原 PS 的公告推送 + 通知轮询逻辑。
/// </summary>
public static class NotificationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
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

    /// <summary>读取 pending 目录下所有未处理通知。</summary>
    public static List<Notification> GetPending()
    {
        if (LabConfig.TestMode) return MockState.GetPending();
        var list = new List<Notification>();
        if (!Directory.Exists(LabConfig.NotifyPendingPath)) return list;
        foreach (var file in Directory.EnumerateFiles(LabConfig.NotifyPendingPath, "*.json"))
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

    /// <summary>读取 sent 目录下的历史通知（按时间倒序）。</summary>
    public static List<Notification> GetSent()
    {
        if (LabConfig.TestMode) return MockState.GetSent();
        var list = new List<Notification>();
        if (!Directory.Exists(LabConfig.NotifySentPath)) return list;
        foreach (var file in Directory.EnumerateFiles(LabConfig.NotifySentPath, "*.json")
                     .OrderByDescending(f => File.GetLastWriteTime(f)))
        {
            try
            {
                var json = File.ReadAllText(file, Encoding.UTF8);
                var n = JsonSerializer.Deserialize<Notification>(json, JsonOptions);
                if (n != null) list.Add(n);
            }
            catch { /* 跳过损坏的 JSON */ }
        }
        return list;
    }

    /// <summary>将 pending 目录下所有通知移至 sent（归档）。</summary>
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
}
