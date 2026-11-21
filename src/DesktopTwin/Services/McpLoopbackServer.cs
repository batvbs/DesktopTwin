using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopTwin.Services;

/// <summary>
/// 只监听 IPv4 回环地址的 MCP Streamable HTTP 服务。
///
/// 没有引入第三方 Web 框架：端口只在本机可见，请求体也有严格上限。这样既避开
/// MCP NuGet 依赖，又不把控制面暴露到局域网。
/// </summary>
internal sealed class McpLoopbackServer : IDisposable
{
    private const int MaxHeaderBytes = 32 * 1024;
    private const int MaxRequestBytes = 8 * 1024 * 1024;
    private const int MaxRequestBodyReadTimeoutSeconds = 30;
    private const int RequestLifetimeSeconds = 60;

    private readonly object _gate = new();
    private readonly string _accessToken;
    private readonly string _expectedPath;
    private readonly Func<string?> _pipeNameProvider;
    private readonly McpProtocolHandler _handler;
    private TcpListener? _listener;
    private CancellationTokenSource? _cancellation;
    private Task? _acceptLoop;
    private bool _disposed;

    public McpLoopbackServer(
        string accessToken,
        int preferredPort,
        Func<string?> pipeNameProvider,
        IMcpAgentBridge? agentBridge = null)
    {
        if (!McpSecretStore.IsSafeToken(accessToken))
        {
            throw new ArgumentException("MCP 访问令牌格式无效。", nameof(accessToken));
        }

        _accessToken = accessToken;
        _expectedPath = "/mcp/" + accessToken;
        _pipeNameProvider = pipeNameProvider;
        _handler = new McpProtocolHandler(agentBridge ?? new SessionAgentControlClient(), pipeNameProvider);
        PreferredPort = preferredPort;
    }

    public int PreferredPort { get; }

    public int Port { get; private set; }

    public bool IsListening => _listener is not null;

    public string? LastError { get; private set; }

    public string Endpoint => $"http://127.0.0.1:{Port}/mcp/{_accessToken}";

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_listener is not null)
            {
                return;
            }

            LastError = null;
            try
            {
                StartListener(PreferredPort);
            }
            catch (SocketException) when (PreferredPort != 0)
            {
                var fallbackMessage = $"端口 {PreferredPort} 已被占用，已改用系统分配的端口。";
                AppLog.Warn(fallbackMessage);
                StartListener(0);
                LastError = fallbackMessage;
            }

            _cancellation = new CancellationTokenSource();
            _acceptLoop = Task.Run(() => AcceptLoopAsync(_cancellation.Token));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _cancellation?.Cancel();
            _listener?.Stop();
            _listener = null;
            var loop = _acceptLoop;
            _acceptLoop = null;
            _cancellation?.Dispose();
            _cancellation = null;

            if (loop is not null)
            {
                try
                {
                    loop.Wait(TimeSpan.FromSeconds(2));
                }
                catch (AggregateException)
                {
                    // 停止监听时 Accept 会以 SocketException/操作取消结束，属正常。
                }
            }
        }
    }

    private void StartListener(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start(64);
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        AppLog.Info($"MCP 控制服务已监听 http://127.0.0.1:{Port}");
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException exception)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                AppLog.Warn("MCP 监听器接受连接失败。", exception);
                continue;
            }

            _ = HandleClientSafelyAsync(client, cancellationToken);
        }
    }

    private async Task HandleClientSafelyAsync(TcpClient client, CancellationToken serverCancellation)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(serverCancellation);
                timeout.CancelAfter(TimeSpan.FromSeconds(RequestLifetimeSeconds));
                await HandleClientAsync(client, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!serverCancellation.IsCancellationRequested)
            {
                await TryWriteErrorAsync(client.GetStream(), 408, "Request Timeout").ConfigureAwait(false);
            }
            catch (RequestTooLargeException)
            {
                await TryWriteErrorAsync(client.GetStream(), 413, "Request body too large.").ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException
                                                  or SocketException
                                                  or ObjectDisposedException
                                                  or JsonException)
            {
                // 客户端提前断开很常见，不把连接异常升级成服务级故障。
                AppLog.Info($"MCP 客户端连接结束：{exception.Message}");
            }
            catch (Exception exception)
            {
                AppLog.Warn("MCP 请求处理失败。", exception);
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var stream = client.GetStream();
        using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readTimeout.CancelAfter(TimeSpan.FromSeconds(MaxRequestBodyReadTimeoutSeconds));
        var request = await ReadRequestAsync(stream, readTimeout.Token).ConfigureAwait(false);
        if (request is null)
        {
            await WriteResponseAsync(stream, 431, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!IsExpectedHost(request.Value.Headers, out var hostError))
        {
            await WriteResponseAsync(stream, 403, ErrorBody(hostError), cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!IsExpectedPath(request.Value.Target))
        {
            await WriteResponseAsync(stream, 404, ErrorBody("Not Found"), cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!IsAllowedOrigin(request.Value.Headers))
        {
            await WriteResponseAsync(stream, 403, ErrorBody("Origin is not allowed."), cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!IsAuthorized(request.Value.Headers))
        {
            await WriteResponseAsync(
                stream,
                401,
                ErrorBody("Invalid bearer token."),
                cancellationToken,
                "WWW-Authenticate: Bearer\r\n").ConfigureAwait(false);
            return;
        }

        if (!string.Equals(request.Value.Method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            await WriteResponseAsync(
                stream,
                405,
                ErrorBody("Method Not Allowed"),
                cancellationToken,
                "Allow: POST\r\n").ConfigureAwait(false);
            return;
        }

        if (!IsJsonContentType(request.Value.Headers))
        {
            await WriteResponseAsync(
                stream,
                415,
                ErrorBody("Content-Type must be application/json."),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Value.Body.Length > MaxRequestBytes)
        {
            await WriteResponseAsync(stream, 413, ErrorBody("Request body too large."), cancellationToken).ConfigureAwait(false);
            return;
        }

        JsonNode? message;
        try
        {
            message = JsonNode.Parse(request.Value.Body);
        }
        catch (JsonException)
        {
            var parseError = McpProtocolHandler.Error(null, -32700, "Parse error");
            await WriteJsonAsync(stream, 400, parseError, cancellationToken).ConfigureAwait(false);
            return;
        }

        JsonNode? response;
        try
        {
            response = await _handler.HandleAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            AppLog.Warn("MCP 方法处理失败。", exception);
            var internalError = McpProtocolHandler.Error(null, -32603, "Internal error");
            await WriteJsonAsync(stream, 500, internalError, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (response is null)
        {
            await WriteResponseAsync(stream, 202, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        await WriteJsonAsync(stream, 200, response, cancellationToken).ConfigureAwait(false);
    }

    private bool IsExpectedPath(string target)
    {
        if (string.IsNullOrWhiteSpace(target) || !target.StartsWith('/'))
        {
            return false;
        }

        var question = target.IndexOf('?');
        var path = question >= 0 ? target[..question] : target;
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(path));
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(_expectedPath));
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private bool IsExpectedHost(Dictionary<string, string> headers, out string error)
    {
        error = "Host is not allowed.";
        if (!headers.TryGetValue("host", out var host) || string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var allowed = new[]
        {
            $"127.0.0.1:{Port}",
            $"localhost:{Port}",
            $"[::1]:{Port}"
        };

        foreach (var candidate in allowed)
        {
            if (string.Equals(host.Trim(), candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsAllowedOrigin(Dictionary<string, string> headers)
    {
        if (!headers.TryGetValue("origin", out var origin) || string.IsNullOrWhiteSpace(origin))
        {
            return true;
        }

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || uri.Port != Port)
        {
            return false;
        }

        return string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Host, "::1", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsAuthorized(Dictionary<string, string> headers)
    {
        if (!headers.TryGetValue("authorization", out var authorization)
            || string.IsNullOrWhiteSpace(authorization))
        {
            // 路径本身已经包含 256 位随机令牌；Bearer 头是可选加固，不强制客户端必须发送。
            return true;
        }

        const string prefix = "Bearer ";
        if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var provided = authorization[prefix.Length..].Trim();
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(_accessToken));
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static bool IsJsonContentType(Dictionary<string, string> headers)
    {
        return headers.TryGetValue("content-type", out var contentType)
            && contentType.TrimStart().StartsWith("application/json", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<HttpRequest?> ReadRequestAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        using var headerBuffer = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        var headerEnd = -1;
        try
        {
            while (headerBuffer.Length < MaxHeaderBytes)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return null;
                }

                var previousLength = (int)headerBuffer.Length;
                headerBuffer.Write(buffer, 0, read);
                var bytes = headerBuffer.GetBuffer();
                var searchStart = Math.Max(0, previousLength - 3);
                for (var i = searchStart; i <= headerBuffer.Length - 4; i++)
                {
                    if (bytes[i] == '\r' && bytes[i + 1] == '\n'
                        && bytes[i + 2] == '\r' && bytes[i + 3] == '\n')
                    {
                        headerEnd = i;
                        break;
                    }
                }

                if (headerEnd >= 0)
                {
                    break;
                }
            }

            if (headerEnd < 0)
            {
                return null;
            }

            var allBytes = headerBuffer.ToArray();
            var headerText = Encoding.Latin1.GetString(allBytes, 0, headerEnd);
            var lines = headerText.Split("\r\n", StringSplitOptions.None);
            if (lines.Length == 0)
            {
                return null;
            }

            var requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (requestLine.Length < 2)
            {
                return null;
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 1; i < lines.Length; i++)
            {
                var colon = lines[i].IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }

                headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
            }

            var host = headers.TryGetValue("host", out var hostValue) ? hostValue : string.Empty;
            var target = requestLine[1];
            if (Uri.TryCreate(target, UriKind.Absolute, out var absoluteUri))
            {
                target = absoluteUri.PathAndQuery;
            }

            if (headers.TryGetValue("expect", out var expect)
                && expect.Contains("100-continue", StringComparison.OrdinalIgnoreCase))
            {
                var continueBytes = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
                await stream.WriteAsync(continueBytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            var bodyPrefixLength = allBytes.Length - headerEnd - 4;
            byte[] body;
            if (headers.TryGetValue("transfer-encoding", out var transferEncoding)
                && transferEncoding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            {
                using var bodyStream = new MemoryStream();
                var reader = new PrefixedStreamReader(stream, allBytes.AsMemory(headerEnd + 4, bodyPrefixLength));
                await ReadChunkedBodyAsync(reader, bodyStream, cancellationToken).ConfigureAwait(false);
                body = bodyStream.ToArray();
            }
            else
            {
                if (!headers.TryGetValue("content-length", out var contentLengthText)
                    || !int.TryParse(contentLengthText, out var contentLength)
                    || contentLength < 0)
                {
                    return null;
                }

                if (contentLength > MaxRequestBytes)
                {
                    // 不读取超限请求体，直接让连接层返回 413。
                    throw new RequestTooLargeException();
                }

                body = new byte[contentLength];
                var copied = Math.Min(bodyPrefixLength, contentLength);
                if (copied > 0)
                {
                    Array.Copy(allBytes, headerEnd + 4, body, 0, copied);
                }

                await ReadExactAsync(stream, body.AsMemory(copied), cancellationToken).ConfigureAwait(false);
            }

            return new HttpRequest(requestLine[0], target, host, headers, body);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task ReadChunkedBodyAsync(
        PrefixedStreamReader reader,
        Stream destination,
        CancellationToken cancellationToken)
    {
        while (destination.Length <= MaxRequestBytes)
        {
            var sizeLine = await reader.ReadLineAsync(8192, cancellationToken).ConfigureAwait(false);
            if (sizeLine is null)
            {
                throw new IOException("分块请求体提前结束。");
            }

            var separator = sizeLine.IndexOf(';');
            var sizeText = separator >= 0 ? sizeLine[..separator] : sizeLine;
            if (!int.TryParse(sizeText.Trim(), System.Globalization.NumberStyles.HexNumber, null, out var size)
                || size < 0)
            {
                throw new IOException("分块请求体长度无效。");
            }

            if (size == 0)
            {
                string? trailer;
                do
                {
                    trailer = await reader.ReadLineAsync(8192, cancellationToken).ConfigureAwait(false);
                }
                while (!string.IsNullOrEmpty(trailer));

                return;
            }

            if (destination.Length + size > MaxRequestBytes)
            {
                throw new RequestTooLargeException();
            }

            var chunk = new byte[size];
            await reader.ReadExactlyAsync(chunk, cancellationToken).ConfigureAwait(false);
            await destination.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
            var crlf = new byte[2];
            await reader.ReadExactlyAsync(crlf, cancellationToken).ConfigureAwait(false);
            if (crlf[0] != '\r' || crlf[1] != '\n')
            {
                throw new IOException("分块请求体缺少分隔符。");
            }
        }

        throw new IOException("分块请求体超过上限。");
    }

    private static async Task ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("请求体提前结束。");
            }

            offset += read;
        }
    }

    private static async Task WriteJsonAsync(
        NetworkStream stream,
        int status,
        JsonNode body,
        CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(body.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = false
        }));
        await WriteResponseAsync(stream, status, payload, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        int status,
        byte[]? body,
        CancellationToken cancellationToken,
        string extraHeaders = "")
    {
        body ??= Array.Empty<byte>();
        var reason = status switch
        {
            200 => "OK",
            202 => "Accepted",
            400 => "Bad Request",
            401 => "Unauthorized",
            403 => "Forbidden",
            404 => "Not Found",
            405 => "Method Not Allowed",
            408 => "Request Timeout",
            413 => "Payload Too Large",
            415 => "Unsupported Media Type",
            431 => "Request Header Fields Too Large",
            500 => "Internal Server Error",
            _ => "Error"
        };

        var header = new StringBuilder()
            .Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n")
            .Append("Content-Type: application/json; charset=utf-8\r\n")
            .Append("Content-Length: ").Append(body.Length).Append("\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("X-Content-Type-Options: nosniff\r\n")
            .Append("Connection: close\r\n")
            .Append(extraHeaders)
            .Append("\r\n");
        var headerBytes = Encoding.ASCII.GetBytes(header.ToString());
        await stream.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);
        if (body.Length > 0)
        {
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task TryWriteErrorAsync(NetworkStream stream, int status, string message)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await WriteResponseAsync(stream, status, ErrorBody(message), timeout.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 连接若已经断了，错误响应本身也没有意义。
        }
    }

    private static byte[] ErrorBody(string message) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { error = message }));

    private sealed class RequestTooLargeException : IOException
    {
    }

    private readonly record struct HttpRequest(
        string Method,
        string Target,
        string Host,
        Dictionary<string, string> Headers,
        byte[] Body);

    /// <summary>
    /// 让分块解码器先消费“已跟随请求头读进内存”的字节，再继续读网络流。
    /// </summary>
    private sealed class PrefixedStreamReader
    {
        private readonly Stream _stream;
        private readonly Memory<byte> _prefix;
        private int _offset;

        public PrefixedStreamReader(Stream stream, Memory<byte> prefix)
        {
            _stream = stream;
            _prefix = prefix;
        }

        public async Task<string?> ReadLineAsync(int maxBytes, CancellationToken cancellationToken)
        {
            using var line = new MemoryStream();
            var one = new byte[1];
            while (line.Length <= maxBytes)
            {
                var read = await ReadAsync(one, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return line.Length == 0 ? null : Encoding.ASCII.GetString(line.ToArray());
                }

                if (one[0] == '\n')
                {
                    var bytes = line.ToArray();
                    var length = bytes.Length > 0 && bytes[^1] == '\r' ? bytes.Length - 1 : bytes.Length;
                    return Encoding.ASCII.GetString(bytes, 0, length);
                }

                line.WriteByte(one[0]);
            }

            throw new IOException("请求头行过长。");
        }

        public async Task ReadExactlyAsync(Memory<byte> destination, CancellationToken cancellationToken)
        {
            var offset = 0;
            while (offset < destination.Length)
            {
                var read = await ReadAsync(destination[offset..], cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new IOException("请求体提前结束。");
                }

                offset += read;
            }
        }

        private async Task<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken)
        {
            if (_offset < _prefix.Length)
            {
                var count = Math.Min(destination.Length, _prefix.Length - _offset);
                _prefix.Slice(_offset, count).CopyTo(destination);
                _offset += count;
                return count;
            }

            return await _stream.ReadAsync(destination, cancellationToken).ConfigureAwait(false);
        }
    }
}
