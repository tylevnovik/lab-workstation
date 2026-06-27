using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace LabWorkstation.Common.Native;

/// <summary>
/// userenv.dll 的 CreateProfile 函数封装。
/// 用于在用户首次登录前预生成 Profile 目录，避免首次登录延迟。
/// 对应原 PS 中 New-LocalUser 后调用 CreateProfile 的步骤。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class UserenvNative
{
    private const int S_OK = 0;

    /// <summary>Profile 已存在时的返回码（HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS)）。</summary>
    private const int E_ALREADY_EXISTS = unchecked((int)0x800700B7);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int CreateProfile(
        string pszUserSid,
        string pszUserName,
        StringBuilder pszProfilePath,
        uint cchProfilePath);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteProfile(
        string lpSidString,
        string? lpProfilePath,
        string? lpComputerName);

    /// <summary>
    /// 尝试为指定用户预生成 Profile。幂等：返回 S_OK 或 E_ALREADY_EXISTS 均视为成功。
    /// </summary>
    /// <param name="sid">用户 SID（如 S-1-5-21-...）。</param>
    /// <param name="username">用户名。</param>
    /// <param name="profilePath">成功时输出 Profile 目录路径；失败时为空字符串。</param>
    /// <returns>成功（含已存在）返回 true；失败返回 false。</returns>
    public static bool TryCreateProfile(string sid, string username, out string profilePath)
    {
        const uint BufferSize = 260;
        var sb = new StringBuilder((int)BufferSize);
        var hr = CreateProfile(sid, username, sb, BufferSize);

        if (hr == S_OK || hr == E_ALREADY_EXISTS)
        {
            profilePath = sb.ToString();
            return true;
        }

        profilePath = string.Empty;
        return false;
    }

    /// <summary>
    /// 删除指定用户的 Profile：删除 Profile 目录并清理 ProfileList 注册表项。
    /// 幂等：用户无 Profile 时返回 true。
    /// </summary>
    /// <param name="sid">用户 SID（如 S-1-5-21-...）。</param>
    /// <param name="profilePath">Profile 目录路径（可为 null，API 自行从注册表读取）。</param>
    /// <returns>成功返回 true；失败返回 false（可用 GetLastError 获取错误码）。</returns>
    public static bool TryDeleteProfile(string sid, string? profilePath = null)
    {
        return DeleteProfile(sid, profilePath, null);
    }
}
