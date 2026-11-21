using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopTwin.Services;

/// <summary>
/// 无状态 Streamable HTTP MCP 的 JSON-RPC 处理核心。
/// </summary>
internal sealed class McpProtocolHandler
{
    public const string LatestProtocolVersion = "2025-06-18";
    private static readonly HashSet<string> SupportedProtocolVersions = new(StringComparer.Ordinal)
    {
        "2024-11-05",
        "2025-03-26",
        LatestProtocolVersion
    };

    private readonly IMcpAgentBridge _agentBridge;
    private readonly Func<string?> _pipeNameProvider;

    public McpProtocolHandler(IMcpAgentBridge agentBridge, Func<string?> pipeNameProvider)
    {
        _agentBridge = agentBridge;
        _pipeNameProvider = pipeNameProvider;
    }

    public async Task<JsonNode?> HandleAsync(JsonNode? message, CancellationToken cancellationToken)
    {
        if (message is JsonArray batch)
        {
            if (batch.Count == 0)
            {
                return Error(null, -32600, "Invalid Request");
            }

            var responses = new JsonArray();
            foreach (var item in batch)
            {
                if (item is not JsonObject request)
                {
                    responses.Add(Error(null, -32600, "Invalid Request"));
                    continue;
                }

                var response = await HandleObjectAsync(request, cancellationToken).ConfigureAwait(false);
                if (response is not null)
                {
                    responses.Add(response);
                }
            }

            return responses.Count == 0 ? null : responses;
        }

        return message is JsonObject requestObject
            ? await HandleObjectAsync(requestObject, cancellationToken).ConfigureAwait(false)
            : Error(null, -32600, "Invalid Request");
    }

    private async Task<JsonObject?> HandleObjectAsync(
        JsonObject request,
        CancellationToken cancellationToken)
    {
        var hasId = request.TryGetPropertyValue("id", out var idNode);
        var id = hasId ? idNode?.DeepClone() : null;

        if (!string.Equals(ReadString(request["jsonrpc"]), "2.0", StringComparison.Ordinal))
        {
            return Error(id, -32600, "Invalid Request");
        }

        var method = ReadString(request["method"]);
        if (string.IsNullOrWhiteSpace(method))
        {
            return Error(id, -32600, "Invalid Request");
        }

        // JSON-RPC 通知没有响应；MCP 的 notifications/initialized 就属于这一类。
        if (!hasId)
        {
            return null;
        }

        var parametersNode = request["params"];
        if (parametersNode is not null && parametersNode is not JsonObject)
        {
            return Error(id, -32602, "Invalid params");
        }

        var parameters = parametersNode as JsonObject ?? new JsonObject();
        return method switch
        {
            "initialize" => Result(id, BuildInitializeResult(parameters)),
            "ping" => Result(id, new JsonObject()),
            "tools/list" => Result(id, new JsonObject
            {
                ["tools"] = McpToolCatalog.ToJsonArray()
            }),
            "tools/call" => Result(id, await CallToolAsync(parameters, cancellationToken).ConfigureAwait(false)),
            "resources/list" => Result(id, new JsonObject { ["resources"] = new JsonArray() }),
            "prompts/list" => Result(id, new JsonObject { ["prompts"] = new JsonArray() }),
            _ => Error(id, -32601, "Method not found")
        };
    }

    private JsonObject BuildInitializeResult(JsonObject parameters)
    {
        var requested = ReadString(parameters["protocolVersion"]);
        var protocolVersion = requested is not null && SupportedProtocolVersions.Contains(requested)
            ? requested
            : LatestProtocolVersion;

        return new JsonObject
        {
            ["protocolVersion"] = protocolVersion,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject { ["listChanged"] = false }
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "DesktopTwin",
                ["version"] = "1.0.0"
            },
            ["instructions"] =
                "Controls the isolated Windows child-session desktop hosted by DesktopTwin. "
                + "Start with desktop_get_info and desktop_screenshot. Use desktop_list_windows before focusing or closing a window. "
                + "Coordinates are child-desktop screen coordinates; mouse and keyboard input never target the host desktop."
        };
    }

    private async Task<JsonObject> CallToolAsync(
        JsonObject parameters,
        CancellationToken cancellationToken)
    {
        var toolName = ReadString(parameters["name"]);
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return ToolError("invalid_arguments", "tools/call 缺少 name。");
        }

        if (!McpToolCatalog.Contains(toolName))
        {
            return ToolError("unknown_tool", $"未知工具：{toolName}");
        }

        var argumentsNode = parameters["arguments"];
        if (argumentsNode is not null && argumentsNode is not JsonObject)
        {
            return ToolError("invalid_arguments", "tools/call 的 arguments 必须是 JSON 对象。");
        }

        var arguments = argumentsNode as JsonObject ?? new JsonObject();
        var pipeName = _pipeNameProvider();
        if (string.IsNullOrWhiteSpace(pipeName))
        {
            return ToolError("agent_unavailable", "分身控制通道尚未启动。");
        }

        McpAgentResult agentResult;
        try
        {
            var argumentsElement = JsonDocument.Parse(arguments.ToJsonString()).RootElement.Clone();
            agentResult = await _agentBridge.ExecuteAsync(
                pipeName,
                toolName,
                argumentsElement,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            AppLog.Warn($"MCP 工具 {toolName} 执行失败。", exception);
            return ToolError("agent_error", exception.Message);
        }

        if (!agentResult.Ok)
        {
            return ToolError(
                agentResult.ErrorCode ?? "agent_error",
                agentResult.ErrorMessage ?? "分身代理执行失败。");
        }

        if (agentResult.Result.ValueKind != JsonValueKind.Object)
        {
            return ToolError("invalid_agent_response", "分身代理返回的结果不是 JSON 对象。");
        }

        return toolName == "desktop_screenshot"
            ? BuildScreenshotResult(agentResult.Result)
            : BuildJsonResult(agentResult.Result);
    }

    private static JsonObject BuildScreenshotResult(JsonElement result)
    {
        var data = result.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.String
            ? dataElement.GetString()
            : null;
        var mimeType = result.TryGetProperty("mimeType", out var mimeElement) && mimeElement.ValueKind == JsonValueKind.String
            ? mimeElement.GetString()
            : "image/png";
        var width = result.TryGetProperty("width", out var widthElement) && widthElement.TryGetInt32(out var parsedWidth)
            ? parsedWidth
            : 0;
        var height = result.TryGetProperty("height", out var heightElement) && heightElement.TryGetInt32(out var parsedHeight)
            ? parsedHeight
            : 0;

        if (string.IsNullOrWhiteSpace(data))
        {
            return ToolError("invalid_screenshot", "截图结果缺少 PNG 数据。");
        }

        return new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "image",
                    ["data"] = data,
                    ["mimeType"] = string.IsNullOrWhiteSpace(mimeType) ? "image/png" : mimeType
                },
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = $"Child desktop screenshot captured ({width} x {height})."
                }
            },
            ["isError"] = false
        };
    }

    private static JsonObject BuildJsonResult(JsonElement result)
    {
        return new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = JsonSerializer.Serialize(result, new JsonSerializerOptions
                    {
                        WriteIndented = true
                    })
                }
            },
            ["isError"] = false
        };
    }

    private static JsonObject ToolError(string code, string message)
    {
        return new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = $"{code}: {message}"
                }
            },
            ["isError"] = true
        };
    }

    private static string? ReadString(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }

    private static JsonObject Result(JsonNode? id, JsonObject result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["result"] = result
    };

    internal static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject
        {
            ["code"] = code,
            ["message"] = message
        }
    };
}
