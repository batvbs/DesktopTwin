# 架构说明

## 一句话

DesktopTwin 是「Windows RDP 子会话」的一层薄封装：**系统提供能力，本程序负责编排与呈现**。

## 分层

```
View (WPF)              MainWindow / Dialogs / Behaviors
   │  绑定 + 命令
ViewModel               MainWindowViewModel（状态映射、命令、通知）
   │  调用
Services                ChildSessionService      会话生命周期与状态机
                        ChildSessionProcessLauncher  跨会话启动进程
                        ChildSessionEnvironmentCheck 只读环境预检
                        SettingsService / AppLog     持久化与日志
   │  调用
Interop                 ChildSessionNativeMethods  wtsapi32 / user32
                        RdpActiveXHost            AxHost + COM 事件
                        WindowNativeMethods       GetWindowRect / SetWindowPos
```

依赖方向自上而下，没有反向依赖。唯一的例外是 `ChildSessionService` 直接持有 `MainWindow` 引用：
这是一个「一窗口 + 一会话」的单窗口应用，为二者引入接口只会增加阅读成本。若将来需要多窗口，
把 `MainWindow` 换成 `ISessionView` 接口即可，改动集中在 `EnsureDesktopWindow` 一处。

## 关键流程

### 1. 启动与连接

```
StartAsync
  ├─ RefreshEnvironmentCheck()        只读预检，结论缓存给界面与失败诊断
  ├─ EnsureChildSessionsEnabled()     WTSIsChildSessionsEnabled → WTSEnableChildSessions(true)
  └─ ConnectCore()
       ├─ TryGetChildSessionId()      已有会话就复用，不重复创建
       ├─ 创建并显示窗口
       └─ RdpActiveXHost.ConnectToChildSession(1920×1080)
            ├─ Server = "localhost"   ← 只连本机，不接受外部地址
            ├─ SecuredSettings2.KeyboardHookMode / AudioRedirectionMode
            ├─ AdvancedSettings7.RDPPort / EnableCredSspSupport / EnableWindowsKey / SmartSizing
            ├─ IMsRdpExtendedSettings["ConnectToChildSession"] = true
            └─ Connect()
```

连接是**异步完成**的：`StartAsync` 用 `TaskCompletionSource` 等待 `OnLoginComplete` 事件，最多等 60 秒。
首次创建会话时，Windows 要初始化一整个桌面，慢是正常的，因此失败后会按 1s / 2s / 4s 重试 3 次。

### 2. 在分身里启动程序

进程只能在自己所属的会话里创建窗口，所以跨会话启动必须借道任务计划程序：

```
LaunchProgramCoreAsync
  ├─ 校验会话号（可能已被重建）
  ├─ SettingsService.Flush()                 目标程序可能读同一份配置
  ├─ 注册临时任务（InteractiveToken + RunLevel 最高/普通）
  ├─ RunEx(null, TASK_RUN_USE_SESSION_ID, 会话号, null)
  └─ 立即 DeleteTask                         失败也不影响已启动的进程
```

`RunLevel` 由设置决定：默认「普通权限」，这样能减少与反作弊/账号安全机制的冲突；
需要时可在控制中心勾选「以管理员权限启动目标程序」。

### 3. 注销

```
LogoffAndHideAsync
  ├─ 校验 expectedSessionId（防止误注销重建后的新会话）
  ├─ 断开 RDP
  ├─ WTSLogoffSession（TerminateChildSession）
  └─ 隐藏窗口
```

应用退出时同样会尝试注销（`wait: false`），失败只记日志——但会话残留会导致系统无法关机，
所以这一步**必须**尝试。

## 状态与线程

- `ChildSessionService` 的所有状态刷新都在 UI 线程（`DispatcherTimer` 每 500ms 一次），
  因此界面读取状态时不需要额外同步。
- RDP ActiveX 的事件回调（`OnLoginComplete` / `OnDisconnected` / `OnLogonError`）来自 COM，
  已在 `RdpActiveXHost` 内部转换到宿主控件线程再抛事件。
- 启动进程的 COM 操作放在 `Task.Run` 里，用 `SemaphoreSlim` 串行化，避免并发注册同名任务。

## RDP 设置切换为什么要重连

`KeyboardHookMode`（系统组合键发到哪一侧）和 `AudioRedirectionMode`（是否重定向音频）
只能在**连接建立时**设置。因此切换这两个开关时，程序会主动断开并重连一次，
并把「待生效的改动」记在 `_pendingRdpSettingChanges` 里，重连成功后回调通知界面。

## 输入屏蔽（观看模式）

RDP ActiveX 内部有一个标题为 `Input Capture Window` 的子窗口，所有键鼠输入都经过它。
`ChildSessionNativeMethods.SetRdpInputWindowEnabled` 通过 `EnumChildWindows` 找到它并 `EnableWindow(false)`，
即可实现「只显示画面、不接收输入」，画面渲染不受影响。

这个窗口每次连接都会重建，因此观看模式必须在每次 `OnLoginComplete` 后重新施加（见 `ChildSessionService`）。

## 想加「游戏鼠标模式」需要动哪里

这是 ROADMAP 里的第一项，也是唯一需要**双进程**的功能：

1. 本进程在分身窗口获得焦点时捕获鼠标相对位移（Raw Input）；
2. 通过命名管道把位移发给**分身里的一个代理进程**；
3. 代理用 `SendInput` 的相对位移标志注入。

现有代码已经为此留好接口：`ChildSessionProcessLauncher.LaunchAsync` 可以带参数启动任意 exe，
把 `Environment.ProcessPath` 加上 `--session-agent <pipeName>` 传进去即可，不需要改启动路径。
新增部分应放在 `Core/MouseForwarding/`，不要污染 `Interop`。

## 为什么不用 Windows 服务 / 驱动

- 驱动需要签名与内核权限，成本和风险都不成比例；
- 服务无法直接呈现 UI，还要额外处理会话隔离；
- 本程序的目标就是「薄封装」，任何下沉到服务层的设计都会让安装与卸载变复杂。
