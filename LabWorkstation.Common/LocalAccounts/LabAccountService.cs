using System.IO;
using System.Text;
using LabWorkstation.Common.Audit;
using LabWorkstation.Common.Configuration;

namespace LabWorkstation.Common.LocalAccounts;

/// <summary>
/// 课题组账户业务流程：把账户/组/ACL/归档等多步操作组合成高层流程。
/// 对应原 PS 中散布在 Manage-LabAccounts.ps1 各 Tab 里的业务逻辑。
/// </summary>
public static class LabAccountService
{
    /// <summary>创建导师组：安全组 + 文件夹 + 分类子目录 + NTFS 权限。</summary>
    public static void CreateAdvisorGroup(string advisorName)
    {
        var groupName = LabConfig.AdvisorToGroupName(advisorName);
        if (!GroupManager.GroupExists(groupName))
        {
            GroupManager.CreateGroup(groupName);
        }
        NtfsAclHelper.CreateAdvisorFolder(advisorName);
    }

    /// <summary>
    /// 一键创建账户：建用户 → 加入 Lab_All → 加入导师组 → 建个人目录。
    /// 失败抛 LabOperationException（含 Action/Target）。
    /// </summary>
    public static string CreateLabUser(string username, string password, string displayName, string advisorName)
    {
        // 1. 建用户
        AccountManager.CreateUser(username, password, displayName, displayName);

        // 2. 加入全员组
        if (!GroupManager.GroupExists(LabConfig.AllGroup))
            GroupManager.CreateGroup(LabConfig.AllGroup);
        GroupManager.AddMember(LabConfig.AllGroup, username);

        // 3. 加入导师组（不存在则创建）
        var advisorGroup = LabConfig.AdvisorToGroupName(advisorName);
        if (!GroupManager.GroupExists(advisorGroup))
            CreateAdvisorGroup(advisorName);
        GroupManager.AddMember(advisorGroup, username);

        // 4. 个人目录
        return NtfsAclHelper.CreateUserPersonalDir(username);
    }

    /// <summary>生成 14 位随机密码（与原 PS Generate-RandomPassword 字符集一致）。</summary>
    public static string GenerateRandomPassword(int length = 14)
    {
        const string chars = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789!@#$";
        var sb = new StringBuilder(length);
        var rng = Random.Shared;
        for (var i = 0; i < length; i++)
            sb.Append(chars[rng.Next(chars.Length)]);
        return sb.ToString();
    }

    /// <summary>
    /// 离校流程：禁用账户 → 归档个人数据 → 记录组内数据信息 → 从所有组移除 → 生成交接清单。
    /// 返回归档目录路径。各步骤失败写状态但不中断后续步骤（与原 PS 行为一致）。
    /// </summary>
    public static DepartureResult DepartUser(string username, Action<string>? onProgress = null)
    {
        var advisor = GroupManager.GetUserAdvisorGroup(username);
        var dateStr = DateTime.Now.ToString("yyyyMMdd");
        var archiveDir = Path.Combine(LabConfig.PublicPath, "99_归档", $"离校用户_{username}_{dateStr}");

        var result = new DepartureResult(archiveDir, advisor);

        if (LabConfig.TestMode) return DepartUserMock(username, advisor, archiveDir, onProgress);

        void Log(string msg) => onProgress?.Invoke($"[{DateTime.Now:HH:mm:ss}] {msg}");

        // Step 1: 禁用账户
        try
        {
            Log("步骤 1/5：禁用账户...");
            AccountManager.DisableUser(username);
            AuditLogger.Write("DISABLE_USER", username);
            Log("  -> 账户已禁用");
        }
        catch (Exception ex)
        {
            Log($"  -> 禁用账户失败: {ex.Message}");
            throw new LabOperationException("DEPARTURE", username, "禁用账户失败", ex.Message, ex);
        }

        Directory.CreateDirectory(archiveDir);

        // Step 2: 归档个人数据
        try
        {
            Log("步骤 2/5：归档个人数据...");
            var personalSrc = Path.Combine(LabConfig.UsersRootPath, username);
            if (Directory.Exists(personalSrc))
            {
                var dest = Path.Combine(archiveDir, "个人数据");
                Directory.CreateDirectory(dest);
                CopyDirectory(personalSrc, dest);
                Log("  -> 个人数据已归档");
            }
            else
            {
                Log("  -> 个人数据目录不存在，跳过");
            }
        }
        catch (Exception ex) { Log($"  -> 归档个人数据失败: {ex.Message}"); }

        // Step 3: 记录组内数据信息
        try
        {
            Log("步骤 3/5：记录组内数据信息...");
            if (!string.IsNullOrEmpty(advisor))
            {
                var advisorPath = Path.Combine(LabConfig.SharePath, advisor);
                var info = $"用户 '{username}' 所属导师组: Lab_{advisor}\r\n组内数据路径: {advisorPath}\r\n\r\n注意：组内数据为全组共享，未自动复制到归档目录。\r\n如需保留该用户的特定文件，请手动从上述路径中查找并复制。";
                File.WriteAllText(Path.Combine(archiveDir, "组内数据说明.txt"), info, Encoding.UTF8);
                Log("  -> 已记录组内数据信息到归档目录");
            }
            else
            {
                Log("  -> 用户未分配导师组，跳过");
            }
        }
        catch (Exception ex) { Log($"  -> 步骤 3 异常: {ex.Message}"); }

        // Step 4: 从所有组中移除
        try
        {
            Log("步骤 4/5：从所有组中移除...");
            GroupManager.RemoveMember(LabConfig.AllGroup, username);
            Log($"  -> 已从 {LabConfig.AllGroup} 移除");
            if (!string.IsNullOrEmpty(advisor))
            {
                GroupManager.RemoveMember(LabConfig.AdvisorToGroupName(advisor), username);
                Log($"  -> 已从 Lab_{advisor} 移除");
            }
        }
        catch (Exception ex) { Log($"  -> 从组中移除失败: {ex.Message}"); }

        // Step 5: 生成工作交接清单
        try
        {
            Log("步骤 5/5：生成工作交接清单...");
            var checklist = BuildDepartureChecklist(username, archiveDir, advisor);
            File.WriteAllText(Path.Combine(archiveDir, "工作交接清单.txt"), checklist, Encoding.UTF8);
            Log("  -> 交接清单已生成");
        }
        catch (Exception ex) { Log($"  -> 生成交接清单失败: {ex.Message}"); }

        AuditLogger.Write("DEPARTURE", username, AuditLogger.Result.Success, $"归档路径: {archiveDir}, 导师组: {advisor ?? "无"}");
        Log("离校流程执行完毕！");
        return result;
    }

    /// <summary>测试模式下的离校流程模拟：仅操作内存状态 + 进度回调，不触碰文件系统。</summary>
    private static DepartureResult DepartUserMock(string username, string? advisor, string archiveDir, Action<string>? onProgress)
    {
        void Log(string msg) => onProgress?.Invoke($"[{DateTime.Now:HH:mm:ss}] {msg}");
        Log("【测试模式】离校流程模拟开始（不修改真实系统）");

        Log("步骤 1/5：禁用账户...");
        AccountManager.DisableUser(username);
        AuditLogger.Write("DISABLE_USER", username);
        Log("  -> 账户已禁用（内存）");

        Log("步骤 2/5：归档个人数据...");
        Log("  -> [测试模式] 跳过真实文件复制");

        Log("步骤 3/5：记录组内数据信息...");
        Log(string.IsNullOrEmpty(advisor) ? "  -> 用户未分配导师组，跳过" : $"  -> [测试模式] 组内数据路径: {Path.Combine(LabConfig.SharePath, advisor)}");

        Log("步骤 4/5：从所有组中移除...");
        GroupManager.RemoveMember(LabConfig.AllGroup, username);
        Log($"  -> 已从 {LabConfig.AllGroup} 移除（内存）");
        if (!string.IsNullOrEmpty(advisor))
        {
            GroupManager.RemoveMember(LabConfig.AdvisorToGroupName(advisor), username);
            Log($"  -> 已从 Lab_{advisor} 移除（内存）");
        }

        Log("步骤 5/5：生成工作交接清单...");
        Log("  -> [测试模式] 跳过真实文件写入");

        AuditLogger.Write("DEPARTURE", username, AuditLogger.Result.Success, $"[测试模式] 归档路径: {archiveDir}, 导师组: {advisor ?? "无"}");
        Log("【测试模式】离校流程模拟完毕！");
        return new DepartureResult(archiveDir, advisor);
    }

    private static string BuildDepartureChecklist(string username, string archiveDir, string? advisor)
    {
        var sb = new StringBuilder();
        sb.AppendLine("========================================");
        sb.AppendLine("  课题组工作站 - 离校工作交接清单");
        sb.AppendLine("========================================");
        sb.AppendLine($"用户：{username}");
        sb.AppendLine($"离校日期：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"归档路径：{archiveDir}");
        sb.AppendLine("----------------------------------------");
        sb.AppendLine("交接事项（请逐项确认）：");
        sb.AppendLine("[ ] 1. 实验数据已备份并通知导师");
        sb.AppendLine("[ ] 2. 共享文件夹中的关键文件已交接给接替人");
        sb.AppendLine("[ ] 3. 课题组公共账号/密码已移交");
        sb.AppendLine("[ ] 4. 正在进行的项目/实验已做交接说明");
        sb.AppendLine("[ ] 5. 门禁卡/钥匙等实物已归还");
        sb.AppendLine("[ ] 6. 导师确认签字：___________");
        sb.AppendLine("[ ] 7. 管理员确认签字：___________");
        sb.AppendLine("----------------------------------------");
        sb.AppendLine("备注：");
        sb.AppendLine("- 账户已禁用，数据已归档");
        sb.AppendLine($"- 个人数据归档位置：{archiveDir}\\个人数据");
        sb.AppendLine(advisor != null
            ? $"- 组内数据归档位置：{archiveDir}\\组内数据（原 Lab_{advisor}）"
            : "- 无导师组数据");
        sb.AppendLine("- 如需恢复数据，请联系管理员");
        sb.AppendLine("========================================");
        return sb.ToString();
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            try { File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true); }
            catch { /* 跳过无法复制的文件 */ }
        }
        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            try { CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir))); }
            catch { /* 跳过无法访问的子目录 */ }
        }
    }

    public sealed record DepartureResult(string ArchiveDir, string? Advisor);
}
