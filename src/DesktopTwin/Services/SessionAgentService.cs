using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Threading;
using DesktopTwin.Interop;
using DesktopTwin.Localization;

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

    /// <summary>
    /// 心跳超过这个年龄就当代理卡住了。代理每 2 秒写一次，容错 15 次。
    /// </summary>
    private static readonly TimeSpan HeartbeatFreshWindow = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 心跳最多每 2 秒读一次：状态轮询是 500ms 一次，每次开文件既浪费又增加冲突概率。
    /// </summary>
    private static readonly TimeSpan HeartbeatCheckInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 连续读不到心跳多少次才认定离线。单次失败只记日志、按「未知」跳过。
    /// </summary>
    private const int HeartbeatReadFailureLimit = 3;

    /// <summary>
    /// 进程探测结果的缓存时长。枚举进程约 1~3ms，没必要每 500ms 做一次。
    /// </summary>
    private static readonly TimeSpan AgentProbeInterval = TimeSpan.FromSeconds(3);

    private readonly string _dataDirectory;
    private readonly Dispatcher _dispatcher;
    private DispatcherTimer? _pollTimer;
    private DateTime _lastRequestSeen = DateTime.MinValue;
    private long _lastLaunchTickCount = -1;
    private string _hotkeyArgument = "Ctrl+Alt+U";

    /// <summary>连续读心跳失败的次数；成功一次就清零。</summary>
    private int _consecutiveHeartbeatReadFailures;

    /// <summary>心跳读取结果缓存（2 秒）。</summary>
    private long _lastHeartbeatCheckTicks = -100_000;
    private bool _lastHeartbeatFresh;

    /// <summary>进程探测结果缓存（3 秒）。</summary>
    private long _lastAgentProbeTicks = -100_000;
    private bool _lastAgentProbeResult;
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
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or System.Security.SecurityException)
        {
            error = Locale.Format("Agent.Error.ReleaseFailed", exception.Message);
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
            error = Locale.Format("Agent.Error.RunKeyFailed", exception.Message);
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

        // 已经有一个活着的分身端就不要重复拉起：每次拉起都会新建进程、抢一次热键，
        // 然后因为互斥体被占立刻退出——日志里那几十次「重新拉起」就是这样空转的。
        // NeedsRestart 为真时是例外：那说明代理文件更新过，需要新进程去替换旧的。
        if (!NeedsRestart && HasLiveAgentProcess())
        {
            AppLog.Info("分身端已在运行，跳过本次拉起。");
            return false;
        }

        var ok = ChildSessionProcessLauncher.RunInSession(
            sessionId.Value,
            AgentPath,
            $"--hotkey \"{_hotkeyArgument}\"",
            taskNamePrefix: "DesktopTwin-SessionAgent-");

        AppLog.Info(ok
            ? Locale.Format("Agent.Started", sessionId)
            : Locale.Format("Agent.StartFailed", sessionId));

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
    /// 分身端是否在线。
    ///
    /// 两道判断，各管一件事：
    /// 1. 先看**进程**在不在（同会话）——它回答「代理有没有被启动或被收掉」；
    /// 2. 进程在再看**心跳**——它回答「代理还活着在干活，还是卡住了」。
    ///
    /// 只看心跳会把「读文件失败」误判成掉线（历史上一天误报 60 多次）；
    /// 只看进程又发现不了卡死，所以两者缺一不可。
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

        if (!HasLiveAgentProcess())
        {
            return false;
        }

        return IsHeartbeatFresh();
    }

    /// <summary>
    /// 分身会话里是否已经有分身端进程。
    ///
    /// 不能靠互斥体判断：代理用的是 <c>Local\</c> 命名空间，会话内可见，
    /// 主程序所在的父会话根本打不开它。改成 <c>Global\</c> 又会让多个分身会话互相打架。
    ///
    /// 必须按会话过滤：控制台登录时自启项也会跑一次代理，那个进程会立刻退出，
    /// 不过滤就会把它当成「分身端还活着」。
    /// </summary>
    public bool HasLiveAgentProcess()
    {
        var now = Environment.TickCount64;
        if (now - _lastAgentProbeTicks < (long)AgentProbeInterval.TotalMilliseconds)
        {
            return _lastAgentProbeResult;
        }

        _lastAgentProbeTicks = now;
        _lastAgentProbeResult = ProbeAgentProcess();
        return _lastAgentProbeResult;
    }

    private bool ProbeAgentProcess()
    {
        var sessionId = ChildSessionNativeMethods.TryGetChildSessionId();
        if (sessionId is null)
        {
            return false;
        }

        var processName = Path.GetFileNameWithoutExtension(AgentFileName);
        foreach (var process in Process.GetProcessesByName(processName))
        {
            try
            {
                if ((uint)process.SessionId == sessionId.Value)
                {
                    return true;
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException
                                                  or System.ComponentModel.Win32Exception
                                                  or NotSupportedException)
            {
                // 进程刚好退出或无权读取：跳过，不是错误。
            }
            finally
            {
                process.Dispose();
            }
        }

        return false;
    }

    /// <summary>
    /// 只看心跳文件判断分身端是否还在写心跳（不含启动宽限）。
    ///
    /// 读文件时会撞上代理正在写：两边都用 <see cref="FileShare.ReadWrite"/> 打开就不会互斥。
    /// 即便这样仍可能失败（杀软扫描等），因此**单次失败不算掉线**：
    /// 连续 <see cref="HeartbeatReadFailureLimit"/> 次读不到才认，中间那次按「未知」跳过。
    /// </summary>
    private bool IsHeartbeatFresh()
    {
        var now = Environment.TickCount64;
        if (now - _lastHeartbeatCheckTicks < (long)HeartbeatCheckInterval.TotalMilliseconds)
        {
            return _lastHeartbeatFresh;
        }

        _lastHeartbeatCheckTicks = now;
        _lastHeartbeatFresh = ProbeHeartbeat();
        return _lastHeartbeatFresh;
    }

    private bool ProbeHeartbeat()
    {
        long unixSeconds;
        try
        {
            using var stream = new FileStream(
                HeartbeatPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            if (!long.TryParse(reader.ReadToEnd().Trim(), out unixSeconds))
            {
                _consecutiveHeartbeatReadFailures = 0;
                return false;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _consecutiveHeartbeatReadFailures++;

            // 单次冲突不当掉线：这一轮按「未知」处理，留给下一轮再确认。
            AppLog.Info($"读取分身端心跳失败（第 {_consecutiveHeartbeatReadFailures} 次）：{exception.Message}");
            return _consecutiveHeartbeatReadFailures < HeartbeatReadFailureLimit;
        }

        _consecutiveHeartbeatReadFailures = 0;

        var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        return age >= TimeSpan.Zero && age < HeartbeatFreshWindow;
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
            ?? throw new InvalidOperationException(Locale.Get("Agent.EmbeddedMissing"));

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

        // 先写临时文件，再把旧文件「改名让位」，最后把新文件搬到正式名字上。
        //
        // 不能直接覆盖或删除旧文件：正在运行的代理把自己的 exe 映射住了，
        // 删除/覆盖都会失败（Device or resource busy）。但 Windows 允许**重命名**
        // 正在运行的可执行文件——旧进程继续用改名后的文件跑，新文件占回原名字。
        var tempPath = AgentPath + ".new";
        var retiredPath = AgentPath + ".old";
        File.WriteAllBytes(tempPath, buffer);

        if (File.Exists(AgentPath))
        {
            TryDelete(retiredPath);
            try
            {
                File.Move(AgentPath, retiredPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // 连改名都不行（极少见）：留着旧代理继续跑，下次启动再换。
                AppLog.Warn("旧分身端无法改名让位，本次沿用旧版本。", exception);
                TryDelete(tempPath);
                return false;
            }
        }

        try
        {
            File.Move(tempPath, AgentPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 旧文件刚才已经被改名让位，这里再失败，AgentPath 上就什么都没有了：
            // 自启项指着这个路径，代理再也起不来。先把旧版本放回去。
            AppLog.Warn("释放新分身端失败，回滚到旧版本。", exception);

            try
            {
                if (!File.Exists(AgentPath) && File.Exists(retiredPath))
                {
                    File.Move(retiredPath, AgentPath);
                }
            }
            catch (Exception rollbackException) when (rollbackException is IOException
                                                          or UnauthorizedAccessException)
            {
                AppLog.Error("回滚旧分身端失败，代理可能无法启动。", rollbackException);
            }

            TryDelete(tempPath);

            if (!File.Exists(AgentPath))
            {
                // 回滚也没能留下可执行文件：这次安装是失败的。返回 false 会被
                // 调用方当成「内容没变」，接着写自启项并报成功，而路径上是空的。
                throw new IOException(
                    "分身端安装失败：目标路径没有可用的可执行文件。", exception);
            }

            return false;
        }

        // 旧文件此刻可能还被旧进程占着，删不掉也无所谓：下次启动还会再试一次。
        TryDelete(retiredPath);

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
