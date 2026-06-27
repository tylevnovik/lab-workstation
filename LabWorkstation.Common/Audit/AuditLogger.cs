using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using LabWorkstation.Common.Configuration;
using LabWorkstation.Common.Logging;
using LabWorkstation.Common.Mock;

namespace LabWorkstation.Common.Audit;

/// <summary>
/// 持久化审计日志。仅记录改变系统状态的操作。
/// 对应原 PS 的 Write-AuditLog / Rotate-AuditLog。
/// 测试模式下仅写入内存，不触碰真实日志文件。
/// </summary>
public static class AuditLogger
{
    private static readonly RotatingFileWriter Writer = new(
        LabConfig.AuditLogPath,
        LabConfig.AuditLogMaxSizeBytes,
        LabConfig.AuditLogMaxArchives);

    public enum Result { Success, Failed }

    /// <summary>写入一条审计日志。</summary>
    public static void Write(string action, string target, Result result = Result.Success, string detail = "")
    {
        if (LabConfig.TestMode) { MockState.WriteAudit(action, target, result.ToString(), detail); return; }
        try
        {
            var operatorName = WindowsIdentity.GetCurrent().Name;
            var ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var detailPart = string.IsNullOrEmpty(detail) ? "" : $" | 详情: {detail}";
            var line = $"[{ts}] 操作人: {operatorName} | 操作: {action} | 对象: {target} | 结果: {result}{detailPart}";
            Writer.AppendLine(line);
        }
        catch
        {
            // 审计日志写入失败不应阻塞主流程
        }
    }

    /// <summary>
    /// 读取与某用户相关的最近 N 条审计记录（供 TrayApp 自助服务展示）。
    /// 精确匹配规则（避免子串误匹配，如 "zhang" 不应匹配 "zhangsan"）：
    /// - 对象字段值等于 username：`| 对象: {username} |`
    /// - 操作人字段以 `\username` 结尾：`操作人: DOMAIN\{username} |`
    /// </summary>
    public static List<string> ReadUserLines(string username, int count = 20)
    {
        if (LabConfig.TestMode) return MockState.ReadUserLines(username, count);
        var result = new List<string>();
        try
        {
            if (!File.Exists(LabConfig.AuditLogPath)) return result;

            // 构造精确匹配正则（OR 关系，任一命中即算相关）：
            // 1. 对象字段：| 对象: {username} |（字段值前后均有 " | " 定界，避免子串匹配）
            // 2. 操作人字段：操作人: DOMAIN\{username} |（WindowsIdentity.Name 返回 DOMAIN\user 格式）
            var escaped = Regex.Escape(username);
            var targetPattern = $@"\| 对象: {escaped} \|";
            var operatorPattern = $@"操作人: [^\|]*\\{escaped} \|";
            var regex = new Regex($"({targetPattern}|{operatorPattern})", RegexOptions.Compiled);

            var allLines = File.ReadAllLines(LabConfig.AuditLogPath, Encoding.UTF8);
            foreach (var line in allLines)
            {
                if (regex.IsMatch(line))
                    result.Add(line);
            }
            return result.TakeLast(count).ToList();
        }
        catch
        {
            return result;
        }
    }
}
