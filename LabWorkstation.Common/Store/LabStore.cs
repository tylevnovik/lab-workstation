using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using LabWorkstation.Common.Configuration;

namespace LabWorkstation.Common.Store;

/// <summary>
/// JSON 落盘读写的泛型基类。提供原子写入与测试模式支持。
/// 测试模式下 <see cref="Load{T}"/> 返回 null、<see cref="Save{T}"/> 不写文件，
/// 让调用方回退到枚举逻辑（如 GroupManager.GetAllAdvisorGroupsReal）。
/// </summary>
public abstract class LabStore
{
    /// <summary>序列化选项：缩进 + 中文不转义（UnsafeRelaxedJsonEscaping）。</summary>
    protected static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// 从 <paramref name="path"/> 读取并反序列化 JSON。
    /// 文件不存在或测试模式返回 null。
    /// </summary>
    protected static T? Load<T>(string path) where T : class
    {
        if (LabConfig.TestMode) return null;
        if (!File.Exists(path)) return null;
        var json = File.ReadAllText(path, Encoding.UTF8);
        return JsonSerializer.Deserialize<T>(json, JsonOpts);
    }

    /// <summary>
    /// 原子写入 JSON：先确保目录存在，再写 .tmp 临时文件，
    /// 最后 <see cref="File.Move(string, string, bool)"/> 原子替换目标文件（overwrite:true）。
    /// 测试模式直接返回，不落盘。
    /// </summary>
    protected static void Save<T>(string path, T data) where T : class
    {
        if (LabConfig.TestMode) return;

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmp = path + ".tmp";
        var json = JsonSerializer.Serialize(data, JsonOpts);
        File.WriteAllText(tmp, json, Encoding.UTF8);
        File.Move(tmp, path, overwrite: true);
    }
}
