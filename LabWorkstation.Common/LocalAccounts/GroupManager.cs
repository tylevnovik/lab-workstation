using System.DirectoryServices.AccountManagement;
using System.Runtime.Versioning;
using LabWorkstation.Common.Configuration;
using LabWorkstation.Common.Mock;

namespace LabWorkstation.Common.LocalAccounts;

/// <summary>
/// 本地安全组操作。统一使用 System.DirectoryServices.AccountManagement，
/// 替代原 PS 中混用的 net localgroup / [ADSI]"WinNT://..."。
/// 测试模式下所有操作仅作用于内存模拟状态。
/// </summary>
[SupportedOSPlatform("windows")]
public static class GroupManager
{
    private static PrincipalContext CreateContext() =>
        new(ContextType.Machine, Environment.MachineName);

    /// <summary>组是否存在。</summary>
    public static bool GroupExists(string groupName) =>
        LabConfig.TestMode ? MockState.GroupExists(groupName) : GroupExistsReal(groupName);

    private static bool GroupExistsReal(string groupName)
    {
        using var ctx = CreateContext();
        using var gp = GroupPrincipal.FindByIdentity(ctx, IdentityType.SamAccountName, groupName);
        return gp != null;
    }

    /// <summary>获取组主体（调用方负责 Dispose）。仅供内部使用。</summary>
    public static GroupPrincipal? FindGroup(string groupName)
    {
        var ctx = CreateContext();
        var gp = GroupPrincipal.FindByIdentity(ctx, IdentityType.SamAccountName, groupName);
        if (gp == null) ctx.Dispose();
        return gp;
    }

    /// <summary>创建安全组。</summary>
    public static void CreateGroup(string groupName)
    {
        if (LabConfig.TestMode) { MockState.CreateGroup(groupName); return; }
        using var ctx = CreateContext();
        using var gp = new GroupPrincipal(ctx, groupName) { Description = "课题组工作站自动创建" };
        gp.Save();
    }

    /// <summary>删除安全组。</summary>
    public static void DeleteGroup(string groupName)
    {
        if (LabConfig.TestMode) { MockState.DeleteGroup(groupName); return; }
        using var gp = FindGroup(groupName) ?? throw new LabOperationException("DELETE_GROUP", groupName, $"组 '{groupName}' 不存在");
        gp.Delete();
    }

    /// <summary>获取组成员用户名列表。</summary>
    public static List<string> GetMembers(string groupName) =>
        LabConfig.TestMode ? MockState.GetMembers(groupName) : GetMembersReal(groupName);

    private static List<string> GetMembersReal(string groupName)
    {
        var result = new List<string>();
        using var gp = FindGroup(groupName);
        if (gp == null) return result;
        foreach (var m in gp.GetMembers(recursive: false))
        {
            if (m is UserPrincipal up && up.SamAccountName != null)
                result.Add(up.SamAccountName);
            m.Dispose();
        }
        return result;
    }

    /// <summary>将用户加入组（用户/组不存在则抛异常）。</summary>
    public static void AddMember(string groupName, string username)
    {
        if (LabConfig.TestMode) { MockState.AddMember(groupName, username); return; }
        using var gp = FindGroup(groupName) ?? throw new LabOperationException("ADD_TO_GROUP", username, $"组 '{groupName}' 不存在");
        using var ctx = gp.Context;
        using var up = UserPrincipal.FindByIdentity(ctx, IdentityType.SamAccountName, username)
            ?? throw new LabOperationException("ADD_TO_GROUP", username, $"用户 '{username}' 不存在");
        if (!gp.Members.Contains(up))
            gp.Members.Add(up);
        gp.Save();
    }

    /// <summary>将用户从组中移除（不存在则忽略）。</summary>
    public static void RemoveMember(string groupName, string username)
    {
        if (LabConfig.TestMode) { MockState.RemoveMember(groupName, username); return; }
        using var gp = FindGroup(groupName);
        if (gp == null) return;
        using var ctx = gp.Context;
        using var up = UserPrincipal.FindByIdentity(ctx, IdentityType.SamAccountName, username);
        if (up != null && gp.Members.Contains(up))
        {
            gp.Members.Remove(up);
            gp.Save();
        }
    }

    /// <summary>判断用户是否属于某组。</summary>
    public static bool IsMember(string groupName, string username) =>
        LabConfig.TestMode ? MockState.IsMember(groupName, username) : IsMemberReal(groupName, username);

    private static bool IsMemberReal(string groupName, string username)
    {
        using var gp = FindGroup(groupName);
        if (gp == null) return false;
        using var ctx = gp.Context;
        using var up = UserPrincipal.FindByIdentity(ctx, IdentityType.SamAccountName, username);
        return up != null && gp.Members.Contains(up);
    }

    /// <summary>获取所有导师组名（Lab_ 开头且非 Lab_All）。</summary>
    public static List<string> GetAllAdvisorGroups() =>
        LabConfig.TestMode ? MockState.GetAllAdvisorGroups() : GetAllAdvisorGroupsReal();

    private static List<string> GetAllAdvisorGroupsReal()
    {
        var result = new List<string>();
        using var ctx = CreateContext();
        using var searcher = new PrincipalSearcher(new GroupPrincipal(ctx));
        foreach (var found in searcher.FindAll())
        {
            if (found is GroupPrincipal gp && gp.SamAccountName != null
                && LabConfig.IsAdvisorGroup(gp.SamAccountName))
            {
                result.Add(LabConfig.GroupNameToAdvisor(gp.SamAccountName));
            }
            found.Dispose();
        }
        return result;
    }

    /// <summary>查询某用户所属的导师组名（未分配返回空字符串）。</summary>
    public static string GetUserAdvisorGroup(string username) =>
        LabConfig.TestMode ? MockState.GetUserAdvisorGroup(username) : GetUserAdvisorGroupReal(username);

    private static string GetUserAdvisorGroupReal(string username)
    {
        foreach (var advisor in GetAllAdvisorGroupsReal())
        {
            if (IsMemberReal(LabConfig.AdvisorToGroupName(advisor), username))
                return advisor;
        }
        return string.Empty;
    }
}
