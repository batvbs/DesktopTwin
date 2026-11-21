using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopTwin.Services;

/// <summary>
/// 主进程到分身代理的控制通道。
///
/// 只有这个客户端负责跨会话通信；键鼠 Win32 调用全部发生在分身端代理进程里，
/// 主桌面会话不会收到自动化输入。
/// </summary>
internal interface IMcpAgentBridge
{
    Task<McpAgentResult> ExecuteAsync(
        string pipeName,
        string operation,
        JsonElement arguments,
        CancellationToken cancellationToken);
}

/// <summary>
/// 代理操作结果。协议错误和工具执行错误分开，便于 MCP 正确标记 <c>isError</c>。
/// </summary>
internal sealed class McpAgentResult
{
    private McpAgentResult(bool ok, JsonElement result, string? errorCode, string? errorMessage)
    {
        Ok = ok;
        Result = result;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
    }

    public bool Ok { get; }

    public JsonElement Result { get; }

    public string? ErrorCode { get; }

    public string? ErrorMessage { get; }

    public static McpAgentResult Success(JsonElement result) =>
        new(true, result, null, null);

    public static McpAgentResult Failure(string code, string message) =>
        new(false, default, code, message);
}

/// <summary>
/// 通过长度前缀 JSON 帧访问分身代理的命名管道。
/// </summary>
internal sealed class SessionAgentControlClient : IMcpAgentBridge
{
    private const int MaxRequestBytes = 8 * 1024 * 1024;
    private const int MaxResponseBytes = 64 * 1024 * 1024;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);

    /// <inheritdoc />
    public async Task<McpAgentResult> ExecuteAsync(
        string pipeName,
        string operation,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var request = new JsonObject
        {
            ["id"] = requestId,
            ["op"] = operation,
            ["args"] = arguments.ValueKind == JsonValueKind.Object
                ? JsonNode.Parse(arguments.GetRawText())
                : new JsonObject()
        };

        var payload = JsonSerializer.SerializeToUtf8Bytes(request);
        if (payload.Length > MaxRequestBytes)
        {
            return McpAgentResult.Failure("request_too_large", "工具参数超过 8 MiB 限制。");
        }

        try
        {
            using var pipe = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectTimeout.CancelAfter(ConnectTimeout);
            await pipe.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);

            var frame = new byte[4 + payload.Length];
            BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
            payload.CopyTo(frame.AsSpan(4));
            await pipe.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);

            var lengthBuffer = new byte[4];
            await ReadExactAsync(pipe, lengthBuffer, cancellationToken).ConfigureAwait(false);
            var responseLength = BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);
            if (responseLength <= 0 || responseLength > MaxResponseBytes)
            {
                return McpAgentResult.Failure(
                    "invalid_agent_response",
                    $"分身代理返回了无效的响应长度：{responseLength}。");
            }

            var responseBytes = new byte[responseLength];
            await ReadExactAsync(pipe, responseBytes, cancellationToken).ConfigureAwait(false);

            var envelope = JsonSerializer.Deserialize<AgentResponseEnvelope>(responseBytes);
            if (envelope is null)
            {
                return McpAgentResult.Failure("invalid_agent_response", "分身代理返回了空响应。");
            }

            if (!envelope.Ok)
            {
                return McpAgentResult.Failure(
                    envelope.Error?.Code ?? "agent_error",
                    envelope.Error?.Message ?? "分身代理执行失败。");
            }

            return McpAgentResult.Success(
                envelope.Result.ValueKind == JsonValueKind.Undefined
                    ? JsonDocument.Parse("{}").RootElement.Clone()
                    : envelope.Result.Clone());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return McpAgentResult.Failure(
                "agent_timeout",
                "连接分身代理超时。请确认分身已登录，且代理正在运行。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
                                              or TimeoutException
                                              or UnauthorizedAccessException
                                              or JsonException)
        {
            AppLog.Warn("MCP 控制通道调用失败。", exception);
            return McpAgentResult.Failure(
                "agent_unavailable",
                "无法连接分身代理。请确认分身已登录并保持运行。");
        }
    }

    private static async Task ReadExactAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("分身代理在响应结束前关闭了管道。");
            }

            offset += read;
        }
    }

    private sealed class AgentResponseEnvelope
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("result")]
        public JsonElement Result { get; set; }

        [JsonPropertyName("error")]
        public AgentError? Error { get; set; }
    }

    private sealed class AgentError
    {
        [JsonPropertyName("code")]
        public string? Code { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }
}
