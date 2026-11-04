# 桌面分身 · DesktopTwin

把 Windows 自带的**第二个桌面会话**变成桌面上的一扇窗口。

同一台电脑、同一个 Windows 用户，同时开两份全屏程序——一份在你原来的桌面上，一份在窗口里。
窗口里的那一份有自己的任务栏、自己的桌面、自己的键鼠焦点，不会抢走你正在用的鼠标。

> 本项目把开源项目 [BetterGI](https://github.com/babalae/better-genshin-impact) 中的「桌面分身」功能抽取为独立软件，
> 只保留与分身相关的部分，不包含任何游戏自动化功能。代码源自 BetterGI，遵循 GPL-3.0。

---

## 它到底做了什么

> **定位先说清楚**：本工具面向「让第二个账号在分身里挂着跑」这类场景，
> 而不是「两只手同时玩两个号」。原因是 RDP 协议不传递鼠标相对位移，依赖相对鼠标的游戏在分身里转不了视角。
> 这一点与原版 BetterGI 的差异见 [已知限制](#已知限制) 与 [ROADMAP](ROADMAP.md)。

Windows 本身支持「子会话」（Child Session）：在同一个登录用户下，额外开一个隐藏的桌面会话。
RDP 客户端可以把某个会话的画面渲染到自己的窗口里，而 `ConnectToChildSession` 这个接口允许**连接本机自己的子会话**。

DesktopTwin 做的事情就是：

1. 调用 `WTSEnableChildSessions(true)` 打开系统的子会话能力；
2. 用 RDP ActiveX 控件连接本机子会话，把画面嵌进 WPF 窗口；
3. 你在这个窗口里登录、启动程序，就像在另一台电脑上一样。

**所以它不是虚拟机、不是沙盒、不做任何图形 hook。** 它只是把 Windows 已经具备的能力做成一个顺手的窗口。
绝大部分「用不了」的情况，根源都在 Windows 的 RDP / 用户 / 组策略设置上，而不是本程序。

---

## 环境要求

| 项目 | 要求 |
| --- | --- |
| 系统 | Windows 10 / 11（专业版、企业版、教育版为官方支持范围） |
| 家庭版 | 微软文档标注为不支持，但**实测多数家庭版可用**，程序会提示但不阻止 |
| 运行权限 | **必须以管理员权限运行**（系统级 API 要求） |
| 账号 | 需要一个**有密码**的 Windows 账户（Microsoft 账户或本地账户均可） |
| 冲突软件 | 不能安装 RDP Wrapper；不建议使用精简版/优化版系统 |

一个 Windows 用户同时只能有**一个**桌面分身。

---

## 快速开始

### 使用

1. 从 [Releases](../../releases) 下载 `DesktopTwin-<版本>-win-x64.zip`，解压到任意目录
   （**不要放在 `Downloads` 这类多人可写的目录里**，本程序以管理员权限运行）；
2. 以管理员权限运行 `DesktopTwin.exe`；
3. 点标题栏的 **启动**；
4. 首次会显示 Windows 登录界面，输入**当前账户的用户名和密码**
   （用户名以 `C:\Users` 下属于你的那个文件夹名为准）；
5. 登录完成后窗口里就是一个完整的桌面，可以像平时一样启动程序；
6. 想让某个程序每次自动出现在分身里：**控制中心 → 选择要在分身中运行的程序…**。

关闭窗口会**注销分身会话**，里面所有程序都会被关闭，请提前保存。

> **校验下载文件**：每个 Release 都附带 `SHA256SUMS.txt`，解压前先核对
> `Get-FileHash .\DesktopTwin-<版本>-win-x64.zip -Algorithm SHA256`，一致再运行。
> 本项目目前**没有代码签名证书**，Windows SmartScreen 会提示「未知发布者」，
> 这是预期行为——校验哈希比点「仍要运行」更可靠。

### 从源码构建

需要 .NET SDK 10.0 或更高版本。

```powershell
git clone <this-repo>
cd desktop-twin
dotnet build -c Release
dotnet publish src/DesktopTwin -c Release -r win-x64 --self-contained false -o publish
```

产物在 `publish\DesktopTwin.exe`（框架依赖，需要目标机安装 .NET 10 桌面运行时）。
如需免安装运行时，把 `--self-contained false` 换成 `--self-contained true`。

---

## 功能

| 功能 | 说明 |
| --- | --- |
| 启动 / 注销分身 | 一键创建、连接、注销第二个桌面会话 |
| 在分身中启动程序 | 通过任务计划程序把任意 exe 启动到分身会话；可选是否提权 |
| 连接后自动启动 | 记住目标程序，连接成功自动拉起 |
| 观看模式 | 只显示画面，本机键鼠不进入分身 |
| 系统组合键 | 可选 Alt+Tab / Windows 键在本机生效还是发到分身 |
| 音频 | 可单独静音分身，不影响主桌面 |
| 分辨率 | 1920×1080 / 2560×1440 / 1600×900 / 1280×720，或自定义（640×480 ~ 7680×4320） |
| 窗口 | 自适应或 1:1、保持 16:9、置顶、小窗模式、位置记忆 |
| 快捷键注入 | 向分身发送 Win+D / Win+Tab |
| 环境预检 | 启动前检查版本、RDP 开关、Windows Hello 限制、RDP Wrapper |
| 诊断 | 一键复制环境快照、打开日志目录 |

---

## 已知限制

这些限制来自 Windows 本身，不是本程序的 bug：

- **鼠标相对移动**：RDP 协议不传递鼠标的「相对位移」。射击类游戏需要相对鼠标，本程序目前**不提供**该能力。
  BetterGI 是通过在主桌面捕获相对位移、转发给分身里的另一个 BetterGI 进程注入实现的，属于双进程架构，本项目暂未实现（见 ROADMAP）。
- **分身内提权**：在分身里对程序点「以管理员运行」是否成功**取决于环境**——有人能正常弹出 UAC，有人会失败。
  失败时请用 **控制中心 → 以管理员权限启动目标程序**，由本程序从外部启动。
- **不能双开的软件**：Chrome、部分启动器等只允许单实例的程序，在两个桌面里同时运行会互相抢占（通常表现为其中一个一直等待）。
- **分身运行期间无法关机 / 重启**：请先关闭桌面分身。若程序异常退出导致会话残留，
  在「任务管理器 → 用户」里注销同名用户即可。
- **开机启动项会重复启动**：分身会话登录后，部分自启动软件会再启动一份，可能冲突。
- **远程控制软件**：UU 远程等不支持多用户的工具，可能会连到分身桌面上去。

---

## 常见问题

**连接失败 / 提示 516（RDP 连接已断开）**
这是一个通用错误，原因很多。依次尝试：重启本程序 → 重启系统 → 检查 `fDenyTSConnections` 是否为 1 → 确认没有装 RDP Wrapper。
程序在连接失败时会把环境预检结论一起写进日志和提示，先看那里。

**密码怎么输都不对**
1. 打开「设置 → 账户 → 登录选项」，**关闭**「为了提高安全性，仅允许对此设备上的 Microsoft 帐户使用 Windows Hello 登录」；
2. 用 `runas /u:MicrosoftAccount\<你的微软账户> winver` 刷新一次本地凭据缓存；
3. 仍然不行时，改用本地账户登录 Windows。

**每次启动都要输密码**
用 PIN 登录时，系统每次都会要求凭据。可以让 Windows 自动登录（`netplwiz`，取消勾选「要使用本计算机，用户必须输入用户名和密码」，
密码填 Microsoft 账户密码而不是 PIN）。

**提示「不支持该请求」**
目标程序要求提权，而分身内的 UAC 在当前环境不可用。请用 **控制中心 → 以管理员权限启动目标程序**。

**关不掉、也关不了机**
任务管理器 → 用户 → 右键注销进程数较少的同名用户。

更多排查见 [docs/troubleshooting.md](docs/troubleshooting.md)。

---

## 安全声明

- 本程序**以管理员权限运行**，并会调用 `WTSEnableChildSessions(true)` 修改系统的子会话开关。
  该开关是系统级持久设置，可用 **控制中心 → 诊断 → 关闭 Child Sessions（恢复系统原状）** 还原；
  程序**不会**在退出时自动改回去（那会悄悄改掉用户可能已经适应的系统状态）。
- 在分身中启动程序时，本程序会临时注册一个计划任务（启动后立即删除），
  这是把进程送进另一个会话的**唯一**系统支持方式。异常退出可能残留任务，下次启动会自动清扫。
- 本程序不联网、不监听端口、不上传任何数据；RDP 连接目标固定为本机回环地址。
- 日志与设置只写在 `%AppData%\DesktopTwin\`。
- **它不是沙盒，也不是安全边界。** 分身会话与你的主桌面属于同一个 Windows 账户、同一个用户配置文件、
  同一套文件权限，分身里能做的事在主桌面同样能做。请不要把桌面分身当成隔离不可信程序的手段。
- **设置文件按当前用户授权。** `%AppData%\DesktopTwin\settings.json` 记录了「要启动哪个程序」。
  如果你把这个文件交给别人、或用共享账户运行本程序，对方就可以让本程序在管理员权限下启动任意本地 exe。
  提权启动因此**永远需要你手动确认一次**，不会自动执行。
- **关于杀毒软件误报**：本程序的行为组合（要求管理员权限、修改 RDP 相关设置、跨会话提权启动进程、
  屏蔽输入窗口）与部分恶意软件的行为特征重合，存在被杀软拦截或误报的可能。
  请以源码为准，或自行从源码构建。遇到拦截时，请把日志与拦截截图提交到 Issue。
- 请在**你自己拥有或已获授权**的设备上使用。绕过软件的多开限制可能违反其服务条款，风险由使用者承担。

---

## 与 BetterGI 的关系

- 代码来源：BetterGI 的 `ChildSession` 相关实现（`ChildSessionService` / `ChildSessionNativeMethods` /
  `ChildSessionProcessLauncher` / `ChildSessionEnvironmentCheck` / `RdpActiveXHost` 与相关窗口、行为类）。
- 本项目只做抽取与解耦：去掉配置中心、i18n、日志框架、游戏自动化等依赖，换成独立的设置服务与文件日志。
- BetterGI 是 GPL-3.0，因此本项目同样使用 GPL-3.0，详见 [LICENSE](LICENSE) 与 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
- 与 BetterGI 团队无隶属关系，本项目为其功能的独立衍生实现。

---

## 项目结构

```
src/DesktopTwin/
├─ Interop/          与 Windows 打交道的部分（wtsapi32、user32、RDP ActiveX）
├─ Services/         会话编排、进程启动、环境预检、设置、日志
├─ Models/           设置模型与事件参数
├─ View/             主窗口、对话框、行为（拖拽 / 宽高比 / 尺寸记忆）
├─ ViewModel/        界面状态与命令
└─ app.manifest      requireAdministrator
docs/                架构说明、排查手册、路线图
```

设计说明见 [docs/architecture.md](docs/architecture.md)。

---

## English

**DesktopTwin** turns the Windows RDP *Child Session* into a normal window, so you can run a second full desktop
— with its own taskbar, focus and input — on the same machine and the same user account.

It is an extraction of the "desktop clone" feature from [BetterGI](https://github.com/babalae/better-genshin-impact),
with all game automation removed. Requires Windows 10/11 (Pro/Enterprise/Education officially supported; Home usually
works but is unsupported by Microsoft), administrator privileges, and an account with a password.

Known limitation: relative mouse movement is not forwarded (RDP protocol limitation), so first-person games that
capture the cursor are not supported yet. See [ROADMAP.md](ROADMAP.md).

Licensed under GPL-3.0. Not affiliated with the BetterGI team.

---

## 许可证

GPL-3.0-or-later，见 [LICENSE](LICENSE)。
