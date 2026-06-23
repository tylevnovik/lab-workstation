namespace LabWorkstation.Common;

/// <summary>
/// 账户/组/ACL 等系统操作失败时抛出。携带审计所需上下文，
/// 供上层统一记录审计日志并提示用户。
/// </summary>
public class LabOperationException : Exception
{
    /// <summary>操作类型，如 CREATE_USER、CHANGE_GROUP。</summary>
    public string Action { get; }

    /// <summary>操作对象（用户名、组名等）。</summary>
    public string Target { get; }

    /// <summary>补充说明。</summary>
    public string Detail { get; }

    public LabOperationException(string action, string target, string message, Exception? inner = null)
        : this(action, target, detail: "", message: message, inner: inner)
    {
    }

    public LabOperationException(string action, string target, string detail, string message, Exception? inner = null)
        : base(message, inner)
    {
        Action = action;
        Target = target;
        Detail = detail ?? string.Empty;
    }
}
