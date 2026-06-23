using System.Text;

namespace LabWorkstation.Common.Logging;

/// <summary>
/// 按大小轮转的文件写入器。超过阈值时归档为 .bak（或按月归档），
/// 保留指定数量的历史归档。线程安全（简单锁）。
/// 对应原 PS 的 Rotate-AuditLog / Rotate-LogFile 逻辑。
/// </summary>
public sealed class RotatingFileWriter
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly long _maxSizeBytes;
    private readonly int _maxArchives;

    public RotatingFileWriter(string path, long maxSizeBytes, int maxArchives)
    {
        _path = path;
        _maxSizeBytes = maxSizeBytes;
        _maxArchives = maxArchives;
    }

    /// <summary>追加一行 UTF-8 文本，必要时先轮转。</summary>
    public void AppendLine(string line)
    {
        lock (_gate)
        {
            try
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                RotateIfNeeded();

                File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // 日志写入失败不应阻塞主流程
            }
        }
    }

    private void RotateIfNeeded()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var fi = new FileInfo(_path);
            if (fi.Length < _maxSizeBytes) return;

            var dir = Path.GetDirectoryName(_path) ?? string.Empty;
            var baseName = Path.GetFileNameWithoutExtension(_path);
            var ext = Path.GetExtension(_path);
            var archiveName = $"{baseName}_{DateTime.Now:yyyyMMdd}{ext}";
            var archivePath = Path.Combine(dir, archiveName);

            if (File.Exists(archivePath))
            {
                // 追加到已有月度归档后删除当前文件
                File.AppendAllText(archivePath, File.ReadAllText(_path, Encoding.UTF8), Encoding.UTF8);
                File.Delete(_path);
            }
            else
            {
                File.Move(_path, archivePath);
            }

            File.Create(_path).Dispose();

            // 清理过期归档
            var pattern = $"{baseName}_*{ext}";
            var archives = Directory.GetFiles(dir, pattern)
                .OrderBy(f => File.GetLastWriteTime(f))
                .ToArray();
            while (archives.Length > _maxArchives)
            {
                File.Delete(archives[0]);
                archives = archives.Skip(1).ToArray();
            }
        }
        catch
        {
            // 轮转失败不阻塞
        }
    }
}
