using System.IO;

namespace LabWorkstation.Common.System;

/// <summary>
/// 定位项目构建产物（bin 目录）的共享工具。
/// Admin 自部署到 C:\Scripts\LabWorkstation.Admin 后，AppContext.BaseDirectory 不再指向源码树，
/// 各部署器（TrayApp/Monitor/Kiosk）需要回溯到解决方案根查找其他项目的 bin 目录。
/// 本类统一处理候选路径构造与查找逻辑。
/// </summary>
public static class BuildArtifactLocator
{
    /// <summary>解决方案根的候选路径（按优先级）。</summary>
    private static readonly string[] SolutionRootCandidates =
    {
        // 1. 从源码运行 Admin：bin\<Config>\net8.0-windows 回溯 4 层到解决方案根
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")),
        // 2. 已知开发路径 fallback（与项目位置一致）
        @"d:\lab-workstation"
    };

    /// <summary>配置名候选（优先 Release，生产部署应使用 Release）。</summary>
    private static readonly string[] ConfigCandidates =
    {
        "Release",
        "Debug"
    };

    /// <summary>目标框架。</summary>
    private const string TargetFramework = "net8.0-windows";

    /// <summary>
    /// 查找指定项目的构建产物目录。按 SolutionRoot × Config 笛卡尔积尝试，
    /// 返回第一个存在的目录；均不存在时抛 LabOperationException。
    /// </summary>
    /// <param name="projectName">项目名（如 "LabWorkstation.TrayApp"）。</param>
    public static string ResolveProjectBinDir(string projectName)
    {
        foreach (var root in SolutionRootCandidates)
        {
            foreach (var config in ConfigCandidates)
            {
                var candidate = Path.Combine(root, projectName, "bin", config, TargetFramework);
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new LabOperationException(
            "RESOLVE_BUILD_ARTIFACT",
            projectName,
            detail: $"Searched: {string.Join(" | ", AllCandidates(projectName))}",
            message: $"未找到 {projectName} 的构建产物目录，请先 dotnet build LabWorkstation.slnx -c Release");
    }

    /// <summary>枚举所有候选路径（用于错误信息展示）。</summary>
    private static IEnumerable<string> AllCandidates(string projectName)
    {
        foreach (var root in SolutionRootCandidates)
            foreach (var config in ConfigCandidates)
                yield return Path.Combine(root, projectName, "bin", config, TargetFramework);
    }
}
