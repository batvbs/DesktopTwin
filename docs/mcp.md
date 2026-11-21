# MCP 桌面控制

DesktopTwin 可以把「分身桌面」包装成一个本机 MCP 服务，让支持 Streamable HTTP 的 MCP 客户端截图并操作 RDP 子会话。

核心保证：MCP 服务不向主桌面发送任何鼠标或键盘输入。移动鼠标、点击、拖拽、滚轮、文本输入、按键和热键全部由运行在 RDP 子会话里的 `SessionAgent` 执行；服务没有主会话 `SendInput` 回退路径。

> 这里的「分身桌面」指 DesktopTwin 用 `WTSEnableChildSessions` 创建、再通过内置 RDP 控件连接的 Windows 子会话，不是同一个登录会话里的普通虚拟桌面。

## 启用

1. 启动 DesktopTwin，并先让分身会话连接成功。
2. 打开 **控制中心 → MCP 桌面控制**。
3. 勾选 **启用 MCP 桌面控制**。
4. 菜单下方会显示监听状态；显示「MCP 已就绪」后，点击 **复制 MCP 客户端配置**。
5. 把复制出的 JSON 粘贴到受信任的 MCP 客户端配置中，然后重启或刷新客户端。

MCP 默认关闭。端口默认是 `39721`；如果该端口已被占用，DesktopTwin 会自动使用系统分配的端口，复制出的配置里始终是实际地址。下次启动会优先复用已保存的端口。

如果只打开了 MCP、没有启用 UU 视角切换，DesktopTwin 仍会自动安装并在分身会话里启动 `SessionAgent`。MCP 监听本身可以先启动，但分身未连接或代理未就绪时，工具调用会返回 `agent_unavailable`。

## 客户端配置

「复制 MCP 客户端配置」生成的格式如下，地址、令牌和端口以实际复制结果为准：

```json
{
  "mcpServers": {
    "desktop-twin": {
      "type": "http",
      "url": "http://127.0.0.1:39721/mcp/<随机令牌>",
      "headers": {
        "Authorization": "Bearer <随机令牌>"
      }
    }
  }
}
```

不要把令牌写入公开仓库、截图或聊天记录。令牌等同于本机分身桌面的控制权限。

服务只接受发往 `127.0.0.1` 的 IPv4 环回请求，不是局域网或公网服务。端点接收 JSON-RPC 2.0 的 HTTP `POST`，支持：

- `initialize`
- `notifications/initialized`
- `ping`
- `tools/list`
- `tools/call`
- `resources/list`、`prompts/list`（返回空列表）

服务按请求—响应方式返回 JSON，不提供服务器主动推送的 SSE 事件流。若本机代理软件拦截了环回请求，请把 `127.0.0.1` 加入 `NO_PROXY`。

手动检查端点时可以使用（把两个占位符替换成配置中的值）：

```powershell
curl.exe -s http://127.0.0.1:39721/mcp/<随机令牌> ^
  -H "Content-Type: application/json" ^
  -H "Authorization: Bearer <随机令牌>" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}"
```

## 工具

共 17 个工具。截图区域、鼠标坐标和窗口坐标都属于分身桌面的坐标系。

### 桌面与截图

| 工具 | 作用 | 主要参数 |
| --- | --- | --- |
| `desktop_get_info` | 获取分身桌面尺寸、虚拟屏幕边界、光标位置、DPI 和会话 ID | 无 |
| `desktop_screenshot` | 截取整个分身桌面或指定区域，返回 PNG | `left`、`top`、`width`、`height`，均为可选 |

`desktop_screenshot` 的 MCP 结果是 `image` 内容（`image/png`），同时附带宽高文字说明。单次截图最多 4000 万像素，区域必须完整落在分身虚拟屏幕内。

### 鼠标

| 工具 | 作用 | 主要参数 |
| --- | --- | --- |
| `desktop_mouse_move` | 移动分身桌面的鼠标指针 | `x`、`y` |
| `desktop_mouse_click` | 移动到坐标并点击 | `x`、`y`；可选 `button`、`clicks` |
| `desktop_mouse_drag` | 按住按钮从一点拖到另一点 | `fromX`、`fromY`、`toX`、`toY`；可选 `button`、`durationMs` |
| `desktop_mouse_scroll` | 在指定位置滚动 | `x`、`y`、`delta`；可选 `horizontal` |

鼠标按钮为 `left`、`right` 或 `middle`。点击次数为 1–3 次，拖拽时长为 0–10000 毫秒。

### 键盘

| 工具 | 作用 | 主要参数 |
| --- | --- | --- |
| `desktop_keyboard_text` | 向分身桌面当前焦点窗口输入 Unicode 文本 | `text` |
| `desktop_keyboard_key` | 按下、松开或持续按住一个键 | `key`；可选 `action`、`repeat` |
| `desktop_keyboard_hotkey` | 发送组合键 | `keys` |

`desktop_keyboard_key` 的 `action` 为 `press`（默认）、`down` 或 `up`，`repeat` 为 1–100 次。热键示例：`["ctrl", "shift", "esc"]`、`["alt", "f4"]`；最后一项是主键，不能是修饰键。`desktop_keyboard_text` 单次最多 100,000 个字符。

### 窗口与进程

| 工具 | 作用 | 主要参数 |
| --- | --- | --- |
| `desktop_list_windows` | 列出分身会话的顶层窗口 | 可选 `includeHidden`、`includeUntitled` |
| `desktop_focus_window` | 还原并聚焦窗口 | `windowId` |
| `desktop_close_window` | 向窗口发送 `WM_CLOSE` | `windowId` |
| `desktop_list_processes` | 列出分身会话内的进程 | 可选 `includePath` |
| `desktop_launch_process` | 在分身会话内启动程序或文档 | `target`；可选 `arguments`、`workingDirectory` |
| `desktop_kill_process` | 结束分身会话内的进程 | `processId` |

窗口完成后应用可能弹出保存确认，`desktop_close_window` 不会强制结束它。进程列表按当前子会话的 session ID 过滤；结束进程时也会再次校验 session ID，并且拒绝结束后台代理自身。

### 剪贴板

| 工具 | 作用 | 主要参数 |
| --- | --- | --- |
| `desktop_clipboard_read` | 读取分身会话剪贴板中的 Unicode 文本 | 无 |
| `desktop_clipboard_write` | 用 Unicode 文本替换分身会话剪贴板 | `text` |

只支持文本，不读取或写入图片、文件列表和其他剪贴板格式。写入文本最多 1,000,000 个字符；剪贴板读写固定在同一条 STA 控制线程上按顺序执行。

## 工作方式

```text
MCP 客户端
  │  POST http://127.0.0.1:<端口>/mcp/<随机令牌>
  ▼
DesktopTwin 主进程
  │  环回、Host/Origin、路径令牌、可选 Bearer 校验
  ▼
当前用户专属命名管道
  │  4 字节长度 + JSON 请求帧
  ▼
RDP 子会话中的 SessionAgent
  │  子会话桌面上的 SendInput / PostMessage / Clipboard / Process API
  ▼
分身桌面
```

`SessionAgent` 只在名称符合子会话格式的会话中启动；在普通主会话里运行时它会自行退出。鼠标坐标、截图、窗口枚举和进程操作都在子会话的进程与桌面环境中完成。

## 安全模型

- **默认关闭**，必须由用户在 DesktopTwin 菜单中显式启用。
- **仅 IPv4 环回**：监听地址是 `127.0.0.1`，不监听 `0.0.0.0`、IPv6 或局域网地址。
- **随机路径令牌**：每次需要生成时使用 256 位随机令牌；不知道 URL 就不能进入 MCP 处理流程。
- **精确 Bearer 校验**：配置会附带 `Authorization: Bearer <令牌>`。请求带了错误的认证头会被拒绝；不带认证头时仍由随机 URL 路径承担主要认证，便于只配置 URL 的客户端使用。
- **Host / Origin 校验**：拒绝非环回主机名和外部网页来源，降低 DNS rebinding 与浏览器跨站调用风险。
- **命名管道 ACL**：只允许当前 Windows 用户访问；管道名也包含随机部分。
- **资源限制**：HTTP 请求体与主进程到分身的控制请求各最多 8 MiB，管道响应最多 64 MiB；请求头、执行时间和单次截图也都有独立上限，超限请求会被拒绝。
- **不暴露令牌**：状态栏只显示监听地址和端口，「复制 MCP 客户端配置」才会把令牌写入剪贴板。

这些措施保护 MCP 端点，但**不是安全沙盒**。MCP 客户端和分身会话仍与主桌面使用同一个 Windows 账户、用户配置文件和文件权限；获得令牌的受信任客户端本来就能在分身里启动程序并访问同一用户的文件。只把令牌配置给可信客户端。

## 隔离边界

- **键鼠隔离**：所有注入调用都发生在子会话的 `SessionAgent` 进程中，不会调用主会话的输入 API。主桌面当前的鼠标位置、键盘焦点和正在输入的内容不会被 MCP 工具改变。
- **窗口和进程隔离**：列表、启动、聚焦、关闭与结束操作以子会话为运行上下文；进程操作额外按 `SessionId` 校验。
- **RDP 画面**：MCP 截图读取子会话桌面，不截取主桌面。
- **剪贴板**：读写目标为子会话剪贴板。若 RDP 客户端或系统策略启用了剪贴板重定向，Windows 可能按该策略同步剪贴板内容；这不属于键鼠注入路径。
- **可操作范围**：分身锁屏、安全桌面、UAC 安全提示或受保护窗口可能拒绝输入或无法截图，这是 Windows 的桌面安全机制。

## 排查

| 现象 | 处理 |
| --- | --- |
| 菜单显示「MCP 控制未启用」 | 勾选 **启用 MCP 桌面控制** |
| 显示「MCP 服务未运行」 | 查看 DesktopTwin 日志和端口冲突；重新勾选一次以重新监听 |
| 显示「等待分身桌面连接」 | 先连接分身会话；MCP 监听不需要窗口位于前台，但子会话必须存在且已登录 |
| 显示「正在启动分身代理」 | 等待几秒；仍不消失时重新连接分身，或检查日志中 `SessionAgent` 的安装与启动错误 |
| 工具返回 `agent_unavailable` | 子会话或控制管道尚未就绪，确认分身已连接且状态变为「MCP 已就绪」 |
| 工具返回 `invalid_coordinates` | 坐标超出分身桌面虚拟屏幕；先调用 `desktop_get_info` 获取边界 |
| 工具返回截图或输入失败 | 确认分身没有锁屏、不是 UAC 安全桌面，且目标窗口没有以更高完整性级别阻止注入 |
| 客户端连不上但地址正确 | 检查客户端是否支持 `type: "http"`；确认没有代理拦截 `127.0.0.1`；确认地址中的令牌没有被截断 |
| 修改启用状态后工具失效 | DesktopTwin 会同步并可能重启 `SessionAgent`；等待状态恢复为「MCP 已就绪」后重试 |

相关实现见：

- `src/DesktopTwin/Services/McpLoopbackServer.cs`
- `src/DesktopTwin/Services/McpProtocolHandler.cs`
- `src/DesktopTwin/Services/McpToolCatalog.cs`
- `src/SessionAgent/SessionAgentControlServer.cs`
- `src/SessionAgent/SessionAgentDesktopNative.cs`
