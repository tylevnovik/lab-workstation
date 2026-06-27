using System.Linq;
using LabWorkstation.Common.Configuration;
using LabWorkstation.Common.Store.Models;

namespace LabWorkstation.Common.Store;

/// <summary>
/// 导师组权威清单的 JSON 落盘 Store。
/// 把"靠脚本枚举系统 Lab_ 组"改为"本地有权威记录"，数据文件：advisors.json。
/// </summary>
public class AdvisorStore : LabStore
{
    /// <summary>advisors.json 的根结构。</summary>
    private class AdvisorStoreData
    {
        public int Version { get; set; }
        public DateTime UpdatedAt { get; set; }
        public List<AdvisorRecord> Advisors { get; set; } = new();
    }

    /// <summary>
    /// 加载全部导师记录。
    /// 文件不存在或测试模式返回空列表（调用方可回退到 GroupManager 枚举）。
    /// </summary>
    public static List<AdvisorRecord> LoadAll()
    {
        var data = Load<AdvisorStoreData>(LabConfig.AdvisorStorePath);
        return data?.Advisors ?? new List<AdvisorRecord>();
    }

    /// <summary>
    /// 添加或替换一条导师记录（按 <see cref="AdvisorRecord.Name"/> 去重：移除同名旧记录后追加新记录）。
    /// 测试模式跳过。
    /// </summary>
    public static void Add(AdvisorRecord record)
    {
        if (LabConfig.TestMode) return;
        var data = LoadOrCreate();
        data.Advisors.RemoveAll(a => a.Name == record.Name);
        data.Advisors.Add(record);
        data.UpdatedAt = DateTime.Now;
        Save(LabConfig.AdvisorStorePath, data);
    }

    /// <summary>同 <see cref="Add"/>：按 Name 去重添加或更新一条导师记录。</summary>
    public static void AddOrUpdate(AdvisorRecord record) => Add(record);

    /// <summary>
    /// 移除指定导师名的记录。测试模式跳过。
    /// </summary>
    public static void Remove(string advisorName)
    {
        if (LabConfig.TestMode) return;
        var data = LoadOrCreate();
        data.Advisors.RemoveAll(a => a.Name == advisorName);
        data.UpdatedAt = DateTime.Now;
        Save(LabConfig.AdvisorStorePath, data);
    }

    /// <summary>判断指定导师名的记录是否存在。</summary>
    public static bool Exists(string advisorName) =>
        LoadAll().Any(a => a.Name == advisorName);

    /// <summary>加载已有数据；文件不存在或测试模式返回新的空结构。</summary>
    private static AdvisorStoreData LoadOrCreate()
    {
        var data = Load<AdvisorStoreData>(LabConfig.AdvisorStorePath);
        return data ?? new AdvisorStoreData { Version = 1 };
    }
}
