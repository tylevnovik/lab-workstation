using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using LabWorkstation.Common.Configuration;
using LabWorkstation.Common.Kiosk;
using LabWorkstation.Common.LocalAccounts;

namespace LabWorkstation.Monitor.Kiosk;

/// <summary>
/// Kiosk 开户请求队列处理器。
/// 轮询 <see cref="LabConfig.KioskRequestsPath"/> 目录中的 <c>req_*.json</c> 文件，
/// 反序列化为 <see cref="KioskRequest"/> 后调用 <see cref="LabAccountService"/> 创建账户，
/// 将结果写入 <see cref="LabConfig.KioskResponsesPath"/> 目录的 <c>resp_{RequestId}.json</c>。
/// 单个请求处理失败不影响其他请求。
/// </summary>
public sealed class KioskQueueProcessor
{
    /// <summary>响应文件最大保留时长，超过后自动清理。</summary>
    private const int ResponseMaxAgeMinutes = 10;

    private const string RequestFilePrefix = "req_";
    private const string ResponseFilePrefix = "resp_";

    /// <summary>Monitor 心跳文件名（写入 responses/ 目录，kiosk 有 Read 权限）。</summary>
    private const string HeartbeatFileName = "monitor_heartbeat.json";

    private readonly Action<string>? _log;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        // 不转义中文等非 ASCII 字符，使响应文件可读
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // 属性名大小写不敏感，兼容 Kiosk 端不同序列化设置
        PropertyNameCaseInsensitive = true
    };

    /// <param name="log">可选的日志回调（输出到控制台等）。</param>
    public KioskQueueProcessor(Action<string>? log = null)
    {
        _log = log;
    }

    /// <summary>
    /// 扫描请求目录并处理所有待处理请求，然后清理过期响应文件。
    /// 该方法对每个文件单独 try/catch，保证一个请求失败不影响其他请求。
    /// </summary>
    public void ProcessPendingRequests()
    {
        // 确保目录存在（首次运行或被清理后自动创建）
        Directory.CreateDirectory(LabConfig.KioskRequestsPath);
        Directory.CreateDirectory(LabConfig.KioskResponsesPath);

        // 遍历所有 req_*.json 请求文件
        IEnumerable<string> requestFiles;
        try
        {
            requestFiles = Directory.EnumerateFiles(
                LabConfig.KioskRequestsPath,
                RequestFilePrefix + "*.json");
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[Kiosk] 枚举请求目录失败: {ex.Message}");
            return;
        }

        foreach (var reqFile in requestFiles)
        {
            try
            {
                ProcessSingleRequest(reqFile);
            }
            catch (Exception ex)
            {
                // 单个请求处理异常不影响其他请求
                _log?.Invoke($"[Kiosk] 处理请求文件 {Path.GetFileName(reqFile)} 时发生未捕获异常: {ex.Message}");
            }
        }

        // 清理超过保留时长的旧响应文件
        CleanupOldResponses();

        // 写入心跳文件，供 Kiosk 端检测 Monitor 是否在线
        WriteHeartbeat();
    }

    /// <summary>
    /// 写入心跳文件到 responses/ 目录。Kiosk 端通过读取该文件的时间戳判断
    /// Monitor 是否在运行。心跳文件与响应文件同目录，复用 kiosk 的 Read 权限，
    /// 无需额外调整 ACL。
    /// </summary>
    private void WriteHeartbeat()
    {
        try
        {
            var heartbeat = new
            {
                Timestamp = DateTime.Now,
                Hostname = Environment.MachineName,
                Version = 1
            };
            var path = Path.Combine(LabConfig.KioskResponsesPath, HeartbeatFileName);
            var json = JsonSerializer.Serialize(heartbeat, _jsonOptions);
            File.WriteAllText(path, json, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[Kiosk] 写入心跳文件失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 处理单个请求文件：读取 → 反序列化 → 执行操作 → 写响应 → 删除请求文件。
    /// </summary>
    private void ProcessSingleRequest(string reqFilePath)
    {
        var fileName = Path.GetFileName(reqFilePath);

        // ── 1. 读取并反序列化请求 ─────────────────────────────
        KioskRequest? request;
        try
        {
            var json = File.ReadAllText(reqFilePath, Encoding.UTF8);
            request = JsonSerializer.Deserialize<KioskRequest>(json, _jsonOptions);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[Kiosk] 解析请求文件 {fileName} 失败: {ex.Message}");
            TryDeleteFile(reqFilePath);
            return;
        }

        if (request is null || string.IsNullOrWhiteSpace(request.RequestId))
        {
            _log?.Invoke($"[Kiosk] 请求文件 {fileName} 内容无效（RequestId 为空），跳过");
            TryDeleteFile(reqFilePath);
            return;
        }

        var requestId = request.RequestId;
        _log?.Invoke($"[Kiosk] 开始处理请求 {requestId}（类型: {request.Type}，用户: {request.Username}）");

        // ── 2. 分发处理 ──────────────────────────────────────
        KioskResponse response;
        try
        {
            response = request.Type switch
            {
                "CreateAccount" => HandleCreateAccount(request),
                _ => new KioskResponse
                {
                    RequestId = requestId,
                    Success = false,
                    Message = $"不支持的操作类型: {request.Type}",
                    Password = null
                }
            };
        }
        catch (Exception ex)
        {
            // LabAccountService.CreateLabUser 失败时抛出异常
            response = new KioskResponse
            {
                RequestId = requestId,
                Success = false,
                Message = $"错误信息: {ex.Message}",
                Password = null
            };
            _log?.Invoke($"[Kiosk] 请求 {requestId} 处理失败: {ex.Message}");
        }

        // ── 3. 写入响应文件 ──────────────────────────────────
        try
        {
            WriteResponse(response);
            _log?.Invoke($"[Kiosk] 请求 {requestId} 处理完成（成功: {response.Success}）");
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[Kiosk] 写入响应文件 {requestId} 失败: {ex.Message}");
        }

        // ── 4. 删除请求文件（无论成功失败都删除，避免重复处理）──
        TryDeleteFile(reqFilePath);
    }

    /// <summary>
    /// 处理 CreateAccount 请求：使用用户在 Kiosk 端自行设置的密码创建账户。
    /// 密码复杂度校验在 Kiosk 端完成（SubmitRequest），此处不再生成随机密码。
    /// 创建失败时抛出异常，由调用方捕获并生成失败响应。
    /// </summary>
    private static KioskResponse HandleCreateAccount(KioskRequest request)
    {
        // 调用账户服务创建用户（失败时抛 LabOperationException）
        LabAccountService.CreateLabUser(
            request.Username,
            request.Password,
            request.DisplayName,
            request.AdvisorName);

        return new KioskResponse
        {
            RequestId = request.RequestId,
            Success = true,
            Message = "账户创建成功",
            Password = null // 用户自设密码，不再回传
        };
    }

    /// <summary>将响应序列化为 JSON 并写入 responses 目录。</summary>
    private static void WriteResponse(KioskResponse response)
    {
        var respPath = Path.Combine(
            LabConfig.KioskResponsesPath,
            $"{ResponseFilePrefix}{response.RequestId}.json");

        var json = JsonSerializer.Serialize(response, _jsonOptions);
        File.WriteAllText(respPath, json, Encoding.UTF8);
    }

    /// <summary>清理超过 <see cref="ResponseMaxAgeMinutes"/> 分钟的旧响应文件，防止堆积。</summary>
    private void CleanupOldResponses()
    {
        try
        {
            var cutoff = DateTime.Now.AddMinutes(-ResponseMaxAgeMinutes);
            var pattern = ResponseFilePrefix + "*.json";

            foreach (var respFile in Directory.EnumerateFiles(LabConfig.KioskResponsesPath, pattern))
            {
                try
                {
                    var fi = new FileInfo(respFile);
                    if (fi.LastWriteTime < cutoff)
                    {
                        fi.Delete();
                    }
                }
                catch
                {
                    // 单个文件清理失败跳过，不影响其他文件
                }
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[Kiosk] 清理旧响应文件失败: {ex.Message}");
        }
    }

    /// <summary>安全删除文件（忽略不存在或无权限等异常）。</summary>
    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 忽略删除失败
        }
    }
}
