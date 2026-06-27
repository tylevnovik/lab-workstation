using System.Security.Cryptography;
using System.Text;

namespace LabWorkstation.Common.Security;

/// <summary>
/// 使用 Windows DPAPI（Data Protection API）对敏感数据进行加解密。
/// 采用 <see cref="DataProtectionScope.LocalMachine"/> 作用域，允许同一台机器上
/// 任何本地进程（含 SYSTEM、Administrators）解密，但文件被复制到其他机器后无法解密。
/// 用于保护 users.json 中的 StoredPassword 字段，防止离线读取攻击。
/// </summary>
public static class PasswordProtector
{
    /// <summary>
    /// 可选熵值（额外密钥材料），加解密必须一致。使用固定字节以使 Administrators 与 SYSTEM
    /// 两个身份都能解密同一个加密块（LocalMachine 作用域不绑定具体用户身份）。
    /// </summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LabWorkstation.UserStore.v1");

    /// <summary>
    /// 加密明文密码，返回 Base64 编码的密文（适合写入 JSON）。
    /// 输入为 null/空时返回 null，保持字段可空语义。
    /// </summary>
    public static string? Protect(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return null;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(plainText);
            var cipher = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.LocalMachine);
            return Convert.ToBase64String(cipher);
        }
        catch
        {
            // DPAPI 加密失败（极少见），返回 null 让调用方决定是否报错
            return null;
        }
    }

    /// <summary>
    /// 解密 Base64 编码的密文，返回明文。
    /// 解密失败（密文损坏、跨机器复制、作用域不匹配）返回 null。
    /// </summary>
    public static string? Unprotect(string? cipherBase64)
    {
        if (string.IsNullOrEmpty(cipherBase64)) return null;
        try
        {
            var cipher = Convert.FromBase64String(cipherBase64);
            var bytes = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            // 解密失败：可能是旧版明文密码（未加密），或跨机器复制
            return null;
        }
    }

    /// <summary>
    /// 尝试解密；若解密失败则将输入视为明文并返回（向后兼容旧版未加密的 StoredPassword）。
    /// 调用方拿到明文后应考虑重新加密落盘以完成迁移。
    /// </summary>
    public static string? UnprotectOrFallback(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;

        // 先尝试按密文解密
        var plain = Unprotect(stored);
        if (plain != null) return plain;

        // 解密失败：可能是明文（旧版数据）。判断是否为合法 Base64 来减少误判
        // 若不是合法 Base64，几乎肯定是明文
        try { Convert.FromBase64String(stored); }
        catch { return stored; } // 不是 Base64，按明文返回

        // 是合法 Base64 但解密失败：可能是其他熵加密的或损坏的数据，按明文返回作为兜底
        return stored;
    }
}
