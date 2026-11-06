using System;
using System.IO;
using System.Threading;
using System.Windows.Threading;
using DesktopTwin.Interop;

namespace DesktopTwin.Services;

/// <summary>
/// 分身内的热键代理：释放、安装并拉起「分身端」。
///
/// 为什么需要它：焦点在分身画面里时，RDP 控件会把组合键转发进远程会话，
/// Console 会话这边的 <c>RegisterHotKey</c> 收不到 <c>WM_HOTKEY</c>，
/// Raw Input 的注册又会被 RDP 控件按「每进程每设备类一个窗口」的规则顶掉。
/// 唯一的干净做法是**在分身会话里也跑一个监听**——那个会话里没有 RDP 客户端，
/// 没有钩子冲突，热键注册一定成功。
///
/// 为什么是原生 exe 而不是 PowerShell 脚本（0.2.3 的教训）：
/// 1) 控制台窗口。powershell.exe 是控制台子系统程序，任务计划程序拉起它必然建一个控制台窗口；
///    用户机器的默认终端是 Windows Terminal，窗口归 WindowsTerminal.exe 所有，
///    <c>-WindowStyle Hidden</c> 与 <c>ShowWindow</c> 都藏不掉——分身桌面上会常驻一个空的
///    PowerShell 窗口。WinExe 不分配控制台，从根上消除这个问题。
/// 2) 启动速度。PowerShell 冷启动 + Add-Type 要 7~15 秒，主程序会把「正在启动」误判成
///    「不在线」而补拉新代理，新代理再杀旧代理——自噬循环，热键永远不稳。
/// 3) 没有脚本文件、执行策略、缓存文件争用这些坑。
///
/// 通信方式照搬社区脚本验证过的做法：分身端命中热键后写一个带时间戳的
/// <c>request.json</c>，主端轮询它。文件通信不需要任何 IPC 权限。
/// </summary>
internal sealed class SessionAgentService : IDisposable
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "DesktopTwinSessionAgent";
    private const string AgentFileName = "DesktopTwin.Agent.exe";
    private const string AgentResourceName = "DesktopTwin.Agent.exe";
    private const string RequestFileName = "uu-switch-request.json";
    private const string HeartbeatFileName = "session-agent-heartbeat.txt";
    private const string AgentLogFileName = "session-agent.log";

    /// <summary>
    /// 请求文件超过这个年龄就当作过期，避免关机前残留的旧请求在下次启动时被误执行。
    /// </summary>
    private static readonly TimeSpan RequestMaxAge = TimeSpan.FromSeconds(12);

    /// <summary>
    /// 「拉起分身端之后」的启动宽限期。
    ///
    /// 分身端冷启动要看进程创建和登录完成，给它一段宽限再判定「不在线」：
    /// 判早了就会补拉新代理，而旧代理正在启动——两个进程互抢热键和互斥体，
    /// 结果一个稳定的都没有。
    /// </summary>
    private static readonly TimeSpan LaunchGracePeriod = TimeSpan.FromSeconds(60);

    private readonly string _dataDirectory;
    private readonly Dispatcher _dispatcher;
    private DispatcherTimer? _pollTimer;
    private DateTime _lastRequestSeen = DateTime.MinValue;
    private long _lastLaunchTickCount = -1;
    private string _hotkeyArgument = "Ctrl+Alt+U";
    private bool _disposed;

    public SessionAgentService(Dispatcher dispatcher, string? dataDirectory = null)
    {
        _dispatcher = dispatcher;
        _dataDirectory = dataDirectory
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DesktopTwin");
    }

    /// <summary>
    /// 分身端请求切换。总在 UI 线程触发。
    /// </summary>
    public event EventHandler? SwitchRequested;

    /// <summary>
    /// 分身端 exe 的落地路径。
    /// </summary>
    public string AgentPath => Path.Combine(_dataDirectory, AgentFileName);

    /// <summary>
    /// 请求文件路径。
    /// </summary>
    public string RequestPath => Path.Combine(_dataDirectory, RequestFileName);

    /// <summary>
    /// 心跳文件路径。
    /// </summary>
    public string HeartbeatPath => Path.Combine(_dataDirectory, HeartbeatFileName);

    /// <summary>
    /// 安装（或更新）分身端：释放 exe、写自启项，并开始轮询请求。
    /// </summary>
    /// <param name="hotkey">当前热键组合，形如 <c>Ctrl+Alt+U</c>。</param>
    /// <param name="error">失败原因。</param>
    public bool EnsureInstalled(string hotkey, out string error)
    {
        error = string.Empty;
        ThrowIfDisposed();

        _hotkeyArgument = hotkey;

        try
        {
            Directory.CreateDirectory(_dataDirectory);
            if (WriteAgentIfChanged())
            {
                // 代理换了，正在跑的还是旧版（或者压根没在跑），下次连上时要重启一次。
                NeedsRestart = true;
            }

            CleanupLegacyScriptFiles();
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or System.Security.SecurityException)
        {
            error = $"释放分身端失败：{exception.Message}";
            AppLog.Warn(error, exception);
            return false;
        }

        try
        {
            WriteRunKey(BuildCommandLine(hotkey));
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
                                              or System.Security.SecurityException
                                              or IOException)
        {
            error = $"写自启项失败：{exception.Message}";
            AppLog.Warn(error, exception);
            return false;
        }

        StartPolling();
        return true;
    }

    /// <summary>
    /// 在分身会话里立刻拉起一份分身端。
    ///
    /// 自启项只在下次登录时生效，用户当前就开着分身，所以必须主动启动一次。
    /// 用任务计划程序以「有限权限 + 交互令牌」启动：代理清单是 asInvoker，
    /// 不会弹 UAC。
    /// </summary>
    /// <returns>是否已下发启动任务。</returns>
    public bool TryStartInChildSession()
    {
        ThrowIfDisposed();

        var sessionId = ChildSessionNativeMethods.TryGetChildSessionId();
        if (sessionId is null)
        {
            AppLog.Info("分身会话尚未创建，分身端会在下次登录时由自启项拉起。");
            return false;
        }

        // 只往「已经登录完成」的会话里拉：RDP 刚连上、桌面还没起来时下发任务，
        // 任务计划程序会把进程起在一个没有交互桌面的会话里，进程活不下来，
        // 却会让主程序以为「已经拉起过」。
        if (!ChildSessionNativeMethods.IsSessionActive(sessionId.Value))
        {
            AppLog.Info($"分身会话 {sessionId} 还没进入 Active 状态，等登录完成后再拉起分身端。");
            return false;
        }

        if (!File.Exists(AgentPath))
        {
            AppLog.Warn($"分身端文件不存在：{AgentPath}，跳过拉起。");
            return false;
        }

        var ok = ChildSessionProcessLauncher.RunInSession(
            sessionId.Value,
            AgentPath,
            $"--hotkey \"{_hotkeyArgument}\"",
            taskNamePrefix: "DesktopTwin-SessionAgent-");

        AppLog.Info(ok
            ? $"已在分身会话 {sessionId} 里启动分身端。"
            : $"在分身会话 {sessionId} 里启动分身端失败，下次登录时由自启项兜底。");

        if (ok)
        {
            NeedsRestart = false;
            // 进入启动宽限期：这段时间内即使没有心跳也按「在线」处理。
            _lastLaunchTickCount = Environment.TickCount64;
        }

        return ok;
    }

    /// <summary>
    /// 分身端文件更新过、但还在用旧版本跑（或压根没跑起来）。
    ///
    /// 用来避免「每次连接状态变化都重启分身端」：那样会陷入
    /// 「新进程杀旧进程、旧进程还没退出、新进程抢不到互斥体又退出」的循环，
    /// 结果一个稳定的分身端都没有，热键彻底失灵。
    /// </summary>
    public bool NeedsRestart { get; private set; }

    /// <summary>
    /// 分身端是否在线（心跳在 20 秒内，或刚拉起还在启动宽限期内）。
    /// </summary>
    public bool IsAgentOnline()
    {
        // 启动宽限：刚拉起就等心跳会把还没完成启动的代理误判成「不在线」，
        // 触发又一次拉起——新代理会杀掉正在启动的旧代理，形成自噬循环。
        if (_lastLaunchTickCount >= 0)
        {
            var sinceLaunch = TimeSpan.FromMilliseconds(
                unchecked(Environment.TickCount64 - _lastLaunchTickCount));
            if (sinceLaunch >= TimeSpan.Zero && sinceLaunch < LaunchGracePeriod)
            {
                return true;
            }
        }

        return IsHeartbeatFresh();
    }

    /// <summary>
    /// 只看心跳文件判断分身端是否在线（不含启动宽限）。
    /// </summary>
    private bool IsHeartbeatFresh()
    {
        try
        {
            if (!File.Exists(HeartbeatPath))
            {
                return false;
            }

            var text = File.ReadAllText(HeartbeatPath).Trim();
            if (!long.TryParse(text, out var unixSeconds))
            {
                return false;
            }

            var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
            return age >= TimeSpan.Zero && age < TimeSpan.FromSeconds(20);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 卸载分身端：删自启项、停轮询、删代理与日志。
    /// </summary>
    public void Uninstall()
    {
        StopPolling();

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
                                              or System.Security.SecurityException
                                              or IOException)
        {
            AppLog.Warn("删除分身端自启项失败。", exception);
        }

        TryDelete(AgentPath);
        TryDelete(RequestPath);
        TryDelete(HeartbeatPath);
        TryDelete(Path.Combine(_dataDirectory, AgentLogFileName));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopPolling();
    }

    private void StartPolling()
    {
        if (_pollTimer is not null)
        {
            return;
        }

        _pollTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            // 切换本身要断开、重连、等 UU 重新挑会话，好几秒起步，
            // 这里没必要做毫秒级响应；1.5 秒一次足够，也更省电。
            Interval = TimeSpan.FromMilliseconds(1500)
        };
        _pollTimer.Tick += OnPollTick;
        _pollTimer.Start();
    }

    private void StopPolling()
    {
        if (_pollTimer is null)
        {
            return;
        }

        _pollTimer.Stop();
        _pollTimer.Tick -= OnPollTick;
        _pollTimer = null;
    }

    private void OnPollTick(object? sender, EventArgs e)
    {
        if (_disposed || !File.Exists(RequestPath))
        {
            return;
        }

        DateTime modified;
        try
        {
            modified = File.GetLastWriteTimeUtc(RequestPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        // 同一个文件只处理一次：分身端每次命中都会重写它，修改时间随之推进。
        if (modified <= _lastRequestSeen)
        {
            return;
        }

        _lastRequestSeen = modified;

        var age = DateTime.UtcNow - modified;
        if (age > RequestMaxAge || age < TimeSpan.FromSeconds(-5))
        {
            // 过期请求（关机前残留）或时钟异常，忽略。
            AppLog.Info($"忽略过期的分身端切换请求（{age.TotalSeconds:N0} 秒前）。");
            return;
        }

        AppLog.Info("收到分身端的热键请求，开始切换。");
        _dispatcher.BeginInvoke(() => SwitchRequested?.Invoke(this, EventArgs.Empty));
    }

    private static string BuildCommandLine(string hotkey)
    {
        return $"\"{Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DesktopTwin",
            AgentFileName)}\" --hotkey \"{hotkey}\"";
    }

    /// <summary>
    /// 把内嵌的分身端 exe 释放到数据目录；内容有变化才重写。
    /// </summary>
    /// <returns>是否真的写了新文件。调用方据此决定要不要重启分身端。</returns>
    private bool WriteAgentIfChanged()
    {
        var assembly = typeof(SessionAgentService).Assembly;
        var resourceName = ResolveAgentResourceName(assembly);
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                "程序内没有找到内嵌的分身端（DesktopTwin.Agent.exe）。这是打包错误，请重新安装。");

        var buffer = new byte[stream.Length];
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read <= 0)
            {
                break;
            }

            offset += read;
        }

        // 内容没变就不重写：重写会推进修改时间，分身端据此判断「自己是不是旧版本」，
        // 平白让所有代理互杀一轮。
        if (File.Exists(AgentPath))
        {
            try
            {
                var existing = File.ReadAllBytes(AgentPath);
                if (existing.Length == buffer.Length && BytesEqual(existing, buffer))
                {
                    return false;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // 读不到就当变了，重写一份。
            }
        }

        // 先写临时文件再替换：代理正在运行时旧文件的句柄可能还在，直接覆盖会失败。
        var tempPath = AgentPath + ".new";
        File.WriteAllBytes(tempPath, buffer);

        if (File.Exists(AgentPath))
        {
            try
            {
                File.Delete(AgentPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn("删除旧分身端失败，改用覆盖写入。", exception);
            }
        }

        File.Move(tempPath, AgentPath);

        AppLog.Info($"分身端已释放：{AgentPath}");
        return true;
    }

    /// <summary>
    /// 找到内嵌分身端的资源名。
    ///
    /// 正常情况下 `LogicalName` 会把它固定成 <c>DesktopTwin.Agent.exe</c>；
    /// 但 MSBuild 的资源命名步骤一旦早于条目添加（比如构建目标挂错了位置），
    /// 名字就会退回默认的「命名空间 + 路径」形式。这里按后缀兜一层，
    /// 免得因为一个打包细节让整个程序起不来——上一次就是这么炸的。
    /// </summary>
    private static string ResolveAgentResourceName(System.Reflection.Assembly assembly)
    {
        if (assembly.GetManifestResourceInfo(AgentResourceName) is not null)
        {
            return AgentResourceName;
        }

        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (name.EndsWith(AgentResourceName, StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Warn($"分身端资源名是 {name}，不是预期的 {AgentResourceName}（已按后缀匹配）。");
                return name;
            }
        }

        throw new InvalidOperationException(
            "程序内没有找到内嵌的分身端（DesktopTwin.Agent.exe）。这是打包错误，请重新安装。"
            + "现有资源：" + string.Join(", ", assembly.GetManifestResourceNames()));
    }

    private static bool BytesEqual(byte[] left, byte[] right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (var index = 0; index < left.Length; index++)
        {
            if (left[index] != right[index])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 清掉 0.2.3 及更早版本留下的 PowerShell 脚本与缓存。
    ///
    /// 这些文件已经不用了；留着只会干扰排障（用户/支持者会打开一份过时的脚本找问题），
    /// 而那个 powershell 代理进程还占着分身里的热键——由分身端自己负责收掉。
    /// </summary>
    private void CleanupLegacyScriptFiles()
    {
        TryDelete(Path.Combine(_dataDirectory, "session-agent.ps1"));
        TryDelete(Path.Combine(_dataDirectory, "session-agent-source.txt"));
        TryDelete(Path.Combine(_dataDirectory, "session-agent.dll"));
        TryDelete(Path.Combine(_dataDirectory, "session-agent-script.txt"));
        TryDelete(Path.Combine(_dataDirectory, "session-agent-hud.log"));
    }

    private static void WriteRunKey(string commandLine)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("无法打开 HKCU 的 Run 键。");
        key.SetValue(RunValueName, commandLine, Microsoft.Win32.RegistryValueKind.String);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"删除文件失败：{path}", exception);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SessionAgentService));
        }
    }
}
