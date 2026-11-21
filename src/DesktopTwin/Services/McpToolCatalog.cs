using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace DesktopTwin.Services;

/// <summary>
/// 暴露给 MCP 客户端的工具清单。
/// </summary>
internal static class McpToolCatalog
{
    private static readonly IReadOnlyList<McpToolDefinition> _tools = Build();

    public static IReadOnlyList<McpToolDefinition> Tools => _tools;

    public static bool Contains(string name) =>
        _tools.Any(tool => string.Equals(tool.Name, name, StringComparison.Ordinal));

    public static JsonArray ToJsonArray()
    {
        var array = new JsonArray();
        foreach (var tool in _tools)
        {
            array.Add(tool.ToJson());
        }

        return array;
    }

    private static IReadOnlyList<McpToolDefinition> Build()
    {
        return new List<McpToolDefinition>
        {
            Tool(
                "desktop_get_info",
                "Get the child desktop geometry, virtual screen bounds, cursor position, DPI and current session id.",
                """
                {"type":"object","properties":{},"additionalProperties":false}
                """),
            Tool(
                "desktop_screenshot",
                "Capture the child desktop as PNG. Optional coordinates capture a region. Coordinates are child-desktop screen coordinates.",
                """
                {"type":"object","properties":{"left":{"type":"integer"},"top":{"type":"integer"},"width":{"type":"integer","minimum":1},"height":{"type":"integer","minimum":1}},"additionalProperties":false}
                """),
            Tool(
                "desktop_mouse_move",
                "Move the mouse pointer on the child desktop. x and y are absolute child-desktop screen coordinates.",
                """
                {"type":"object","properties":{"x":{"type":"integer"},"y":{"type":"integer"}},"required":["x","y"],"additionalProperties":false}
                """),
            Tool(
                "desktop_mouse_click",
                "Move to x,y and click a mouse button on the child desktop. clicks may be 1-3.",
                """
                {"type":"object","properties":{"x":{"type":"integer"},"y":{"type":"integer"},"button":{"type":"string","enum":["left","right","middle"],"default":"left"},"clicks":{"type":"integer","minimum":1,"maximum":3,"default":1}},"required":["x","y"],"additionalProperties":false}
                """),
            Tool(
                "desktop_mouse_drag",
                "Drag from one child-desktop coordinate to another with a mouse button held down.",
                """
                {"type":"object","properties":{"fromX":{"type":"integer"},"fromY":{"type":"integer"},"toX":{"type":"integer"},"toY":{"type":"integer"},"button":{"type":"string","enum":["left","right","middle"],"default":"left"},"durationMs":{"type":"integer","minimum":0,"maximum":10000,"default":400}},"required":["fromX","fromY","toX","toY"],"additionalProperties":false}
                """),
            Tool(
                "desktop_mouse_scroll",
                "Scroll at x,y on the child desktop. Positive delta scrolls up; horizontal scrolling uses the horizontal flag.",
                """
                {"type":"object","properties":{"x":{"type":"integer"},"y":{"type":"integer"},"delta":{"type":"integer"},"horizontal":{"type":"boolean","default":false}},"required":["x","y","delta"],"additionalProperties":false}
                """),
            Tool(
                "desktop_keyboard_text",
                "Type Unicode text into the focused child-desktop window using keyboard input.",
                """
                {"type":"object","properties":{"text":{"type":"string","maxLength":100000}},"required":["text"],"additionalProperties":false}
                """),
            Tool(
                "desktop_keyboard_key",
                "Press, release or hold a keyboard key in the child desktop. Common key names such as Enter, Tab, Escape, F5, Left and letters are accepted.",
                """
                {"type":"object","properties":{"key":{"type":"string"},"action":{"type":"string","enum":["press","down","up"],"default":"press"},"repeat":{"type":"integer","minimum":1,"maximum":100,"default":1}},"required":["key"],"additionalProperties":false}
                """),
            Tool(
                "desktop_keyboard_hotkey",
                "Press a key combination in the child desktop, for example [\"ctrl\",\"shift\",\"esc\"] or [\"alt\",\"f4\"]. The last key is the primary key.",
                """
                {"type":"object","properties":{"keys":{"type":"array","minItems":1,"maxItems":8,"items":{"type":"string"}}},"required":["keys"],"additionalProperties":false}
                """),
            Tool(
                "desktop_list_windows",
                "List top-level windows on the child desktop with windowId, process id, title, class name and bounds.",
                """
                {"type":"object","properties":{"includeHidden":{"type":"boolean","default":false},"includeUntitled":{"type":"boolean","default":false}},"additionalProperties":false}
                """),
            Tool(
                "desktop_focus_window",
                "Restore and focus a child-desktop window returned by desktop_list_windows.",
                """
                {"type":"object","properties":{"windowId":{"type":"integer"}},"required":["windowId"],"additionalProperties":false}
                """),
            Tool(
                "desktop_close_window",
                "Ask a child-desktop window to close by posting WM_CLOSE. The application may prompt to save data.",
                """
                {"type":"object","properties":{"windowId":{"type":"integer"}},"required":["windowId"],"additionalProperties":false}
                """),
            Tool(
                "desktop_list_processes",
                "List processes running in the child Windows session. Set includePath to include executable paths when accessible.",
                """
                {"type":"object","properties":{"includePath":{"type":"boolean","default":false}},"additionalProperties":false}
                """),
            Tool(
                "desktop_launch_process",
                "Launch an application or document inside the child session. target is an executable path, shell URI, or document path.",
                """
                {"type":"object","properties":{"target":{"type":"string","minLength":1},"arguments":{"type":"string"},"workingDirectory":{"type":"string"}},"required":["target"],"additionalProperties":false}
                """),
            Tool(
                "desktop_kill_process",
                "Terminate a process by process id. Only processes in the child session may be terminated.",
                """
                {"type":"object","properties":{"processId":{"type":"integer","minimum":1}},"required":["processId"],"additionalProperties":false}
                """),
            Tool(
                "desktop_clipboard_read",
                "Read Unicode text from the child session clipboard.",
                """
                {"type":"object","properties":{},"additionalProperties":false}
                """),
            Tool(
                "desktop_clipboard_write",
                "Replace the child session clipboard with Unicode text.",
                """
                {"type":"object","properties":{"text":{"type":"string","maxLength":1000000}},"required":["text"],"additionalProperties":false}
                """)
        };
    }

    private static McpToolDefinition Tool(string name, string description, string schema)
    {
        var node = JsonNode.Parse(schema) as JsonObject
            ?? throw new InvalidOperationException($"Invalid MCP tool schema: {name}");
        return new McpToolDefinition(name, description, node);
    }
}

internal sealed class McpToolDefinition
{
    public McpToolDefinition(string name, string description, JsonObject inputSchema)
    {
        Name = name;
        Description = description;
        InputSchema = inputSchema;
    }

    public string Name { get; }

    public string Description { get; }

    public JsonObject InputSchema { get; }

    public JsonObject ToJson() => new()
    {
        ["name"] = Name,
        ["description"] = Description,
        ["inputSchema"] = InputSchema.DeepClone()
    };
}
