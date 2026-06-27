using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using LabWorkstation.Common.Configuration;

namespace LabWorkstation.Common.Kiosk;

/// <summary>
/// Kiosk 公告数据模型。
/// 由 Admin 写入，Kiosk 只读取展示。公告不会自动过期，仅由 Admin 增删改。
/// 与 TrayApp 通知（NotificationStore）独立，不弹窗，以列表形式常驻 Kiosk 界面。
/// </summary>
public sealed class Announcement
{
    /// <summary>公告唯一 ID（8 位 hex）。</summary>
    public string Id { get; set; } = "";

    /// <summary>公告标题。</summary>
    public string Title { get; set; } = "";

    /// <summary>公告正文。</summary>
    public string Content { get; set; } = "";

    /// <summary>是否置顶。置顶公告在 Kiosk 界面单独置顶框展示，普通公告在列表中展示。</summary>
    public bool IsPinned { get; set; }

    /// <summary>创建时间。</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>最后修改时间。</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    /// <summary>创建人（Admin 用户名）。</summary>
    public string CreatedBy { get; set; } = "";
}

/// <summary>
/// Kiosk 公告落盘 Store。数据目录：<see cref="LabConfig.KioskAnnouncementsPath"/>。
/// Admin 端调用 Add/Update/Delete 做增删改；Kiosk 端调用 LoadAll 只读加载。
/// 每条公告一个 JSON 文件（{id}.json），便于原子修改与删除。
/// </summary>
public static class AnnouncementStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = global::System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>加载全部公告。Kiosk 端只读调用，排序：置顶在前，同级别按更新时间倒序。</summary>
    public static List<Announcement> LoadAll()
    {
        var list = new List<Announcement>();
        var dir = LabConfig.KioskAnnouncementsPath;
        if (!Directory.Exists(dir)) return list;

        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file, global::System.Text.Encoding.UTF8);
                var a = JsonSerializer.Deserialize<Announcement>(json, JsonOptions);
                if (a != null)
                {
                    if (string.IsNullOrEmpty(a.Id)) a.Id = Path.GetFileNameWithoutExtension(file);
                    list.Add(a);
                }
            }
            catch { /* 跳过损坏文件 */ }
        }

        return list
            .OrderByDescending(a => a.IsPinned)
            .ThenByDescending(a => a.UpdatedAt)
            .ToList();
    }

    /// <summary>新增公告。生成 8 位 ID，写入 {id}.json。返回新公告。</summary>
    public static Announcement Add(string title, string content, bool isPinned, string createdBy)
    {
        var a = new Announcement
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Title = title,
            Content = content,
            IsPinned = isPinned,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now,
            CreatedBy = createdBy
        };
        Write(a);
        return a;
    }

    /// <summary>更新已有公告（按 Id）。公告不存在返回 false。</summary>
    public static bool Update(string id, string title, string content, bool isPinned)
    {
        var existing = LoadAll().FirstOrDefault(a => a.Id == id);
        if (existing == null) return false;

        existing.Title = title;
        existing.Content = content;
        existing.IsPinned = isPinned;
        existing.UpdatedAt = DateTime.Now;
        Write(existing);
        return true;
    }

    /// <summary>删除指定 Id 的公告。不存在返回 false。</summary>
    public static bool Delete(string id)
    {
        var path = Path.Combine(LabConfig.KioskAnnouncementsPath, $"{id}.json");
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    /// <summary>查指定 Id 的公告，不存在返回 null。</summary>
    public static Announcement? Find(string id) =>
        LoadAll().FirstOrDefault(a => a.Id == id);

    /// <summary>写入单条公告文件（原子替换）。</summary>
    private static void Write(Announcement a)
    {
        var dir = LabConfig.KioskAnnouncementsPath;
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{a.Id}.json");
        var tmp = path + ".tmp";
        var json = JsonSerializer.Serialize(a, JsonOptions);
        File.WriteAllText(tmp, json, global::System.Text.Encoding.UTF8);
        File.Move(tmp, path, overwrite: true);
    }
}
