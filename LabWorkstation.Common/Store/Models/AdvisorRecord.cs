using System.IO;
using LabWorkstation.Common.Configuration;

namespace LabWorkstation.Common.Store.Models;

/// <summary>
/// 导师组的权威落盘记录。对应一个 Lab_ 导师组及其共享文件夹。
/// 由 <see cref="LabWorkstation.Common.Store.AdvisorStore"/> 持久化到 advisors.json。
/// </summary>
public sealed record AdvisorRecord
{
    /// <summary>导师名，如"张老师"（组名去掉 Lab_ 前缀）。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>组名，如"Lab_张老师"。</summary>
    public string GroupName { get; init; } = string.Empty;

    /// <summary>记录创建时间。</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>创建人（管理员账号）。</summary>
    public string CreatedBy { get; init; } = string.Empty;

    /// <summary>描述/备注。</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>导师共享文件夹路径，如 D:\GroupData\张老师。</summary>
    public string FolderPath { get; init; } = string.Empty;

    /// <summary>
    /// 工厂方法：由导师名与创建人构造记录，
    /// 自动填充 GroupName（LabConfig.AdvisorToGroupName）与 FolderPath（SharePath\导师名）。
    /// </summary>
    public static AdvisorRecord Create(string advisorName, string createdBy) => new()
    {
        Name = advisorName,
        GroupName = LabConfig.AdvisorToGroupName(advisorName),
        CreatedAt = DateTime.Now,
        CreatedBy = createdBy,
        Description = string.Empty,
        FolderPath = Path.Combine(LabConfig.SharePath, advisorName)
    };
}
