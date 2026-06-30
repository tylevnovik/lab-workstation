namespace LabWorkstation.Common.Kiosk;

/// <summary>
/// Kiosk 应用提交的开户请求。Monitor 服务以 SYSTEM 身份轮询
/// <c>kiosk_queue/requests</c> 目录，读取此模型并处理。
/// </summary>
public sealed class KioskRequest
{
    public string RequestId { get; set; } = "";
    public string Type { get; set; } = "";
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string AdvisorName { get; set; } = "";
    /// <summary>用户在 Kiosk 端自行设置的密码（明文，仅经队列文件传递给 Monitor）。</summary>
    public string Password { get; set; } = "";
    public DateTime Timestamp { get; set; }
}

/// <summary>
/// Monitor 处理完 Kiosk 请求后写入 <c>kiosk_queue/responses</c> 的响应。
/// Kiosk 应用通过 RequestId 关联请求与响应。
/// </summary>
public sealed class KioskResponse
{
    public string RequestId { get; set; } = "";
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string? Password { get; set; }
}
