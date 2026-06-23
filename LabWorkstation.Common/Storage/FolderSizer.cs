using System.IO;
using LabWorkstation.Common.Configuration;
using LabWorkstation.Common.Mock;

namespace LabWorkstation.Common.Storage;

/// <summary>
/// 文件夹大小计算与人性化显示。对应原 PS 的 Get-FolderSizeDisplay。
/// 测试模式下返回模拟大小，不扫描真实磁盘。
/// </summary>
public static class FolderSizer
{
    /// <summary>递归计算文件夹内所有文件总字节数。</summary>
    public static long GetSizeBytes(string path)
    {
        if (LabConfig.TestMode) return MockState.GetSizeBytes(path);
        if (!Directory.Exists(path)) return 0;
        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; }
                catch { /* 跳过无法访问的文件 */ }
            }
        }
        catch { /* 跳过无法访问的目录 */ }
        return total;
    }

    /// <summary>人性化显示（GB/MB/KB/B）。</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):N2} GB";
        if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):N2} MB";
        if (bytes >= 1L << 10) return $"{bytes / (double)(1L << 10):N2} KB";
        return $"{bytes} B";
    }

    /// <summary>获取文件夹大小并格式化（路径不存在返回提示）。</summary>
    public static string GetSizeDisplay(string path)
    {
        if (LabConfig.TestMode) return MockState.GetSizeDisplay(path);
        if (!Directory.Exists(path)) return "(路径不存在)";
        return FormatSize(GetSizeBytes(path));
    }
}
