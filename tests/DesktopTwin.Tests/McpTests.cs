using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DesktopTwin.Models;
using DesktopTwin.Services;

namespace DesktopTwin.Tests;

public sealed class McpProtocolHandlerTests
{
    [Fact]
    public async Task 非字符串字段返回无效请求而不是抛异常()
    {
        var handler = CreateHandler();
        var message = JsonNode.Parse("""
            {"jsonrpc":2,"id":1,"method":"ping","params":{}}
            """);

        var response = await handler.HandleAsync(message, CancellationToken.None);

        Assert.Equal(-32600, response!["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task 通知不产生响应()
    {
        var handler = CreateHandler();
        var message = JsonNode.Parse("""
            {"jsonrpc":"2.0","method":"notifications/initialized","params":{}}
            """);

        var response = await handler.HandleAsync(message, CancellationToken.None);

        Assert.Null(response);
    }

    [Fact]
    public async Task 初始化协商已知版本并回退未知版本()
    {
        var handler = CreateHandler();
        var known = await handler.HandleAsync(JsonNode.Parse("""
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26"}}
            """), CancellationToken.None);
        var unknown = await handler.HandleAsync(JsonNode.Parse("""
            {"jsonrpc":"2.0","id":2,"method":"initialize","params":{"protocolVersion":"1900-01-01"}}
            """), CancellationToken.None);

        Assert.Equal("2025-03-26", known!["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.Equal(McpProtocolHandler.LatestProtocolVersion, unknown!["result"]!["protocolVersion"]!.GetValue<string>());
    }

    [Fact]
    public async Task 工具清单包含完整桌面控制能力()
    {
        var handler = CreateHandler();
        var response = await handler.HandleAsync(JsonNode.Parse("""
            {"jsonrpc":"2.0","id":1,"method":"tools/list"}
            """), CancellationToken.None);
        var tools = response!["result"]!["tools"]!.AsArray();

        Assert.Equal(17, tools.Count);
        Assert.Contains(tools, node => node!["name"]!.GetValue<string>() == "desktop_screenshot");
        Assert.Contains(tools, node => node!["name"]!.GetValue<string>() == "desktop_keyboard_hotkey");
        Assert.Contains(tools, node => node!["name"]!.GetValue<string>() == "desktop_clipboard_write");
    }

    [Fact]
    public async Task 工具调用转发管道和对象参数()
    {
        var bridge = new RecordingBridge();
        var handler = new McpProtocolHandler(bridge, () => "DesktopTwin.AgentControl.test");

        var response = await handler.HandleAsync(JsonNode.Parse("""
            {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"desktop_mouse_move","arguments":{"x":12,"y":34}}}
            """), CancellationToken.None);

        Assert.Equal("DesktopTwin.AgentControl.test", bridge.LastPipeName);
        Assert.Equal("desktop_mouse_move", bridge.LastOperation);
        Assert.Equal(12, bridge.LastArguments.GetProperty("x").GetInt32());
        Assert.False(response!["result"]!["isError"]!.GetValue<bool>());
    }

    [Fact]
    public async Task 非法工具参数返回工具错误()
    {
        var bridge = new RecordingBridge();
        var handler = new McpProtocolHandler(bridge, () => "DesktopTwin.AgentControl.test");

        var response = await handler.HandleAsync(JsonNode.Parse("""
            {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"desktop_mouse_move","arguments":[1,2]}}
            """), CancellationToken.None);

        Assert.True(response!["result"]!["isError"]!.GetValue<bool>());
        Assert.Null(bridge.LastOperation);
    }

    private static McpProtocolHandler CreateHandler()
    {
        return new McpProtocolHandler(new RecordingBridge(), () => null);
    }

    private sealed class RecordingBridge : IMcpAgentBridge
    {
        public string? LastPipeName { get; private set; }

        public string? LastOperation { get; private set; }

        public JsonElement LastArguments { get; private set; }

        public Task<McpAgentResult> ExecuteAsync(
            string pipeName,
            string operation,
            JsonElement arguments,
            CancellationToken cancellationToken)
        {
            LastPipeName = pipeName;
            LastOperation = operation;
            LastArguments = arguments.Clone();
            return Task.FromResult(McpAgentResult.Success(
                JsonSerializer.SerializeToElement(new { ok = true })));
        }
    }
}

public sealed class McpSecretStoreTests
{
    [Fact]
    public void 首次启用会生成随机且安全的令牌和管道名()
    {
        var settings = new McpSettings();

        Assert.True(McpSecretStore.Ensure(settings));
        Assert.True(McpSecretStore.IsSafeToken(settings.AccessToken));
        Assert.True(McpSecretStore.IsSafePipeName(settings.PipeName));
        Assert.StartsWith("DesktopTwin.AgentControl.", settings.PipeName);
        Assert.Equal(McpSettings.DefaultPort, settings.Port);
    }

    [Fact]
    public void 已有安全值时不会重复生成()
    {
        var settings = new McpSettings
        {
            AccessToken = McpSecretStore.CreateBase64Url(32),
            PipeName = "DesktopTwin.AgentControl." + McpSecretStore.CreateHex(16)
        };
        var token = settings.AccessToken;
        var pipe = settings.PipeName;

        Assert.False(McpSecretStore.Ensure(settings));
        Assert.Equal(token, settings.AccessToken);
        Assert.Equal(pipe, settings.PipeName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("has spaces and must not fit in a URL path at all")]
    [InlineData("invalid+base64/token-with-illegal-characters")]
    public void 不安全令牌会被拒绝(string token)
    {
        Assert.False(McpSecretStore.IsSafeToken(token));
    }

    [Fact]
    public void 每次生成的秘密都不同()
    {
        Assert.NotEqual(McpSecretStore.CreateBase64Url(32), McpSecretStore.CreateBase64Url(32));
        Assert.NotEqual(McpSecretStore.CreateHex(16), McpSecretStore.CreateHex(16));
    }

    [Fact]
    public void 分身端参数支持只开控制通道()
    {
        var arguments = SessionAgentService.BuildAgentArguments(
            null,
            "DesktopTwin.AgentControl.0123456789abcdef0123456789abcdef");

        Assert.Equal(
            "--no-hotkey --control-pipe \"DesktopTwin.AgentControl.0123456789abcdef0123456789abcdef\"",
            arguments);
    }
}

public sealed class McpLoopbackServerTests
{
    [Fact]
    public async Task 带路径令牌的请求可完成初始化()
    {
        using var server = CreateServer();
        server.Start();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        var response = await client.PostAsync(
            server.Endpoint,
            JsonContent("""
                {"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}
                """));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("DesktopTwin", body!["result"]!["serverInfo"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task 错误的认证头会被拒绝()
    {
        using var server = CreateServer();
        server.Start();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var request = new HttpRequestMessage(HttpMethod.Post, server.Endpoint)
        {
            Content = JsonContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""")
        };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer wrong");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task 非本机主机名和外部来源会被拒绝()
    {
        using var server = CreateServer();
        server.Start();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var hostRequest = new HttpRequestMessage(HttpMethod.Post, server.Endpoint)
        {
            Content = JsonContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""")
        };
        hostRequest.Headers.Host = "evil.example";

        var hostResponse = await client.SendAsync(hostRequest);

        using var originRequest = new HttpRequestMessage(HttpMethod.Post, server.Endpoint)
        {
            Content = JsonContent("""{"jsonrpc":"2.0","id":2,"method":"ping"}""")
        };
        originRequest.Headers.TryAddWithoutValidation("Origin", "https://evil.example");

        var originResponse = await client.SendAsync(originRequest);

        Assert.Equal(HttpStatusCode.Forbidden, hostResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, originResponse.StatusCode);
    }

    [Fact]
    public async Task 错误路径返回未找到()
    {
        using var server = CreateServer();
        server.Start();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        var response = await client.PostAsync(
            $"http://127.0.0.1:{server.Port}/mcp/wrong-token",
            JsonContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}"""));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("Content-Length: 8388609\r\n")]
    [InlineData("Transfer-Encoding: chunked\r\n")]
    public async Task 超限请求体返回413(string framingHeader)
    {
        using var server = CreateServer();
        server.Start();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        await using var stream = client.GetStream();
        var path = new Uri(server.Endpoint).PathAndQuery;
        var chunk = framingHeader.StartsWith("Transfer-Encoding", StringComparison.Ordinal)
            ? "800001\r\n"
            : string.Empty;
        var request = $"POST {path} HTTP/1.1\r\nHost: 127.0.0.1:{server.Port}\r\nContent-Type: application/json\r\n{framingHeader}Connection: close\r\n\r\n{chunk}";
        var bytes = Encoding.ASCII.GetBytes(request);
        await stream.WriteAsync(bytes);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        var response = await reader.ReadToEndAsync(timeout.Token);

        Assert.StartsWith("HTTP/1.1 413", response, StringComparison.Ordinal);
    }

    private static McpLoopbackServer CreateServer()
    {
        return new McpLoopbackServer(
            McpSecretStore.CreateBase64Url(32),
            0,
            () => "DesktopTwin.AgentControl.test",
            new StubBridge());
    }

    private static StringContent JsonContent(string json)
    {
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    private sealed class StubBridge : IMcpAgentBridge
    {
        public Task<McpAgentResult> ExecuteAsync(
            string pipeName,
            string operation,
            JsonElement arguments,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(McpAgentResult.Success(
                JsonSerializer.SerializeToElement(new { ok = true })));
        }
    }
}
