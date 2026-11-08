using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace DesktopTwinSessionAgent
{
    /// <summary>
    /// 桌面分身 · 分身端热键代理（原生 WinExe）。
    ///
    /// 为什么是原生 exe 而不是 PowerShell 脚本：
    /// 1) 控制台窗口。powerShell.exe 是控制台子系统程序，任务计划程序拉起时必然建一个控制台窗口；
    ///    用户机器的默认终端是 Windows Terminal，窗口归 WindowsTerminal.exe 所有，
    ///    -WindowStyle Hidden 和 ShowWindow 都藏不掉它——分身桌面上会一直挂着一个空的 PowerShell 窗口。
    ///    WinExe 根本不分配控制台，从根上消除这个问题。
    /// 2) 启动速度。PowerShell 冷启动 + Add-Type 要 7~15 秒，主程序会误判「不在线」而补拉新代理，
    ///    新代理再杀旧代理，形成自噬循环。WinExe 启动在 100 毫秒级。
    /// 3) 无脚本文件、无执行策略、无缓存文件争用。
    ///
    /// 协议与之前完全一致（文件 IPC）：
    ///   命中热键 → 写 uu-switch-request.json（延迟 1.6 秒，先让用户看到提示）
    ///   每 5 秒写 session-agent-heartbeat.txt（Unix 秒），启动时立刻写一次
    ///   日志 session-agent.log
    ///
    /// 注意：用 .NET Framework 4.x 的 csc 编译（Windows 自带，无需另装运行库），
    /// 因此代码保持 C# 5 语法（不能用字符串插值、nameof 等）。
    /// </summary>
    internal static class Program
    {
        private const string MutexName = @"Local\DesktopTwin.SessionAgent";
        private const string DefaultHotkey = "Ctrl+Alt+U";
        private const int HotkeyId = 1;
        private const int WmHotkey = 0x0312;
        private const uint ModNoRepeat = 0x4000;
        private const int HudCloseLogThreshold = 0;

        private static string _dataDirectory;
        private static string _requestPath;
        private static string _heartbeatPath;
        private static string _logPath;

        private static Mutex _mutex;
        private static HotkeyWindow _hotkeyWindow;
        private static readonly object LogLock = new object();
        private static int _logLines;
        private static Form _hud;
        private static System.Windows.Forms.Timer _hudTimer;
        private static bool _registered;

        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Native.SetProcessDPIAware();

            _dataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DesktopTwin");
            _requestPath = Path.Combine(_dataDirectory, "uu-switch-request.json");
            _heartbeatPath = Path.Combine(_dataDirectory, "session-agent-heartbeat.txt");
            _logPath = Path.Combine(_dataDirectory, "session-agent.log");

            string hotkey = ReadHotkeyArgument(args);
            bool testMode = HasFlag(args, "--test");

            Log("代理启动 pid=" + Process.GetCurrentProcess().Id + " 版本=" + typeof(Program).Assembly.GetName().Version);

            // 只在分身会话里工作。自启项是 HKCU 的，Console 登录时也会执行本程序。
            // 子会话的 WinStation 名固定形如 7a78855482a04fa781dc#0；
            // Console 是 console，远程桌面是 rdp-tcp#N，都不会误匹配。
            string sessionName = Native.CurrentSessionName();
            if (!Regex.IsMatch(sessionName, "^[0-9a-fA-F]{16,}#[0-9]+$"))
            {
                Log("会话名 '" + sessionName + "' 不是分身会话，退出（Console 登录时自启项也会执行本程序，属正常）");
                return 0;
            }

            // 会话判定一过就写心跳：主程序据此判断「代理是否在线」。
            WriteHeartbeat();

            KillStaleAgents();

            bool createdNew;
            _mutex = new Mutex(false, MutexName, out createdNew);
            bool owned = false;
            try
            {
                owned = _mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                // 上一个持有者被强杀：所有权其实已经交给本进程，照常运行。
                owned = true;
            }

            if (!owned)
            {
                Log("已有分身端在运行，本进程退出");
                return 0;
            }

            uint mods;
            uint vk;
            ParseHotkey(hotkey, out mods, out vk);

            _hotkeyWindow = new HotkeyWindow();
            _hotkeyWindow.Pressed += OnHotkeyPressed;
            _registered = Native.RegisterHotKey(_hotkeyWindow.Handle, HotkeyId, mods | ModNoRepeat, vk);
            Log("热键注册 " + hotkey + ": " + (_registered ? "成功" : "失败"));

            if (testMode)
            {
                // 自检模式：只验证会话判定与热键注册。排障时手动跑一次。
                Log("自检: SESSION_NAME=" + sessionName + " HOTKEY_REGISTERED=" + _registered);
                if (_registered)
                {
                    Native.UnregisterHotKey(_hotkeyWindow.Handle, HotkeyId);
                }

                return 0;
            }

            var beat = new System.Windows.Forms.Timer();
            beat.Interval = 2000;
            beat.Tick += delegate { WriteHeartbeat(); };
            beat.Start();

            // 守夜：热键被别的进程占用时每 3 秒重试一次，对方让出后自动接管。
            // 只在状态变化时写日志，避免每 3 秒刷一行。
            var guard = new System.Windows.Forms.Timer();
            guard.Interval = 3000;
            guard.Tick += delegate
            {
                if (_registered)
                {
                    return;
                }

                _registered = Native.RegisterHotKey(_hotkeyWindow.Handle, HotkeyId, mods | ModNoRepeat, vk);
                if (_registered)
                {
                    Log("热键补注册成功");
                }
            };
            guard.Start();

            Log("进入消息循环");
            Application.Run(new AgentContext());
            Log("消息循环退出，分身端结束");
            return 0;
        }

        /// <summary>
        /// 命中热键：先在分身画面里弹提示，再延迟 1.6 秒写请求。
        ///
        /// 延迟是必须的：主端一收到请求就断开会话，分身画面连同提示一起消失，
        /// 用户看到的只会是「按了没反应」。
        /// </summary>
        private static void OnHotkeyPressed()
        {
            Log("热键命中，弹提示并延迟通知主端");
            ShowHud("已收到热键，正在切换视角…", 2500);

            var delay = new System.Windows.Forms.Timer();
            delay.Interval = 1600;
            delay.Tick += delegate
            {
                delay.Stop();
                delay.Dispose();
                try
                {
                    long ts = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                    string json = "{\"ts\": " + ts.ToString() + ", \"source\": \"session-agent\"}";
                    File.WriteAllText(_requestPath, json, new UTF8Encoding(false));
                    Log("切换请求已写入");
                }
                catch (Exception ex)
                {
                    Log("写请求失败: " + ex.Message);
                }
            };
            delay.Start();
        }

        /// <summary>
        /// 在分身画面中央弹一个黑色提示条。
        /// </summary>
        private static void ShowHud(string text, int milliseconds)
        {
            try
            {
                if (_hud != null)
                {
                    try
                    {
                        _hud.Close();
                    }
                    catch (Exception)
                    {
                        // 上一个提示已经关掉了，忽略。
                    }

                    _hud = null;
                }

                if (_hudTimer != null)
                {
                    _hudTimer.Stop();
                    _hudTimer.Dispose();
                    _hudTimer = null;
                }

                var hud = new Form();
                hud.FormBorderStyle = FormBorderStyle.None;
                hud.StartPosition = FormStartPosition.CenterScreen;
                hud.TopMost = true;
                hud.ShowInTaskbar = false;
                hud.BackColor = Color.FromArgb(28, 28, 30);
                hud.Size = new Size(480, 92);
                hud.Opacity = 0.95;

                var label = new Label();
                label.Text = text;
                label.ForeColor = Color.White;
                label.Font = new Font("Microsoft YaHei UI", 13f);
                label.Dock = DockStyle.Fill;
                label.TextAlign = ContentAlignment.MiddleCenter;
                hud.Controls.Add(label);

                _hud = hud;
                hud.Show();

                var timer = new System.Windows.Forms.Timer();
                timer.Interval = milliseconds;
                timer.Tick += delegate
                {
                    timer.Stop();
                    timer.Dispose();
                    if (_hud != null)
                    {
                        try
                        {
                            _hud.Close();
                        }
                        catch (Exception)
                        {
                        }

                        _hud = null;
                    }
                };
                _hudTimer = timer;
                timer.Start();
            }
            catch (Exception ex)
            {
                Log("HUD 失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 收掉「过期」的旧代理：启动时间早于本程序文件修改时间的那种。
        ///
        /// 只杀过期的，不无条件杀同会话所有代理：主程序在心跳超时时会补拉一个新的，
        /// 无差别互杀 + 补拉会形成「新代理杀旧代理、下一个补拉再杀它」的循环，
        /// 表现就是分身桌面反复闪窗口、热键时灵时不灵。
        /// </summary>
        private static void KillStaleAgents()
        {
            try
            {
                string self = Process.GetCurrentProcess().MainModule.FileName;
                DateTime scriptTime = File.GetLastWriteTime(self);
                int mySession = Process.GetCurrentProcess().SessionId;
                int myPid = Process.GetCurrentProcess().Id;

                foreach (Process process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(self)))
                {
                    try
                    {
                        if (process.Id == myPid || process.SessionId != mySession)
                        {
                            continue;
                        }

                        if (process.StartTime < scriptTime)
                        {
                            Log("收掉过期旧代理 pid=" + process.Id);
                            process.Kill();
                        }
                    }
                    catch (Exception)
                    {
                        // 进程刚好退出或权限不足：跳过。
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                Log("清理旧代理失败: " + ex.Message);
            }
        }

        private static void WriteHeartbeat()
        {
            try
            {
                long now = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                byte[] payload = new UTF8Encoding(false).GetBytes(now.ToString());

                // 用 FileShare.ReadWrite 打开：主程序那边同时可能在读，
                // 默认共享模式（FileShare.Read）会把「读」挡在外面，反之亦然。
                using (FileStream stream = new FileStream(
                           _heartbeatPath,
                           FileMode.Create,
                           FileAccess.Write,
                           FileShare.ReadWrite | FileShare.Delete))
                {
                    stream.Write(payload, 0, payload.Length);
                }
            }
            catch (Exception ex)
            {
                Log("心跳写入失败: " + ex.Message);
            }
        }

        private static void Log(string message)
        {
            try
            {
                lock (LogLock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_logPath));
                    string line = DateTime.Now.ToString("MM-dd HH:mm:ss") + "  pid="
                        + Process.GetCurrentProcess().Id + "  " + message;
                    File.AppendAllText(_logPath, line + "\r\n", new UTF8Encoding(false));

                    // 涨到 1 MB 就转存，防止异常循环把磁盘写满。
                    _logLines++;
                    if (_logLines % 50 == 0)
                    {
                        var info = new FileInfo(_logPath);
                        if (info.Exists && info.Length > 1024 * 1024)
                        {
                            string backup = _logPath + ".1";
                            if (File.Exists(backup))
                            {
                                File.Delete(backup);
                            }

                            File.Move(_logPath, backup);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // 日志失败不能影响热键本身。
            }
        }

        private static string ReadHotkeyArgument(string[] args)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "--hotkey", StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }

            return DefaultHotkey;
        }

        private static bool HasFlag(string[] args, string name)
        {
            foreach (string arg in args)
            {
                if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static void ParseHotkey(string text, out uint mods, out uint vk)
        {
            mods = 0;
            vk = 0;

            foreach (string raw in text.Split('+'))
            {
                string part = raw.Trim().ToLowerInvariant();
                if (part.Length == 0)
                {
                    continue;
                }

                if (part == "ctrl" || part == "control")
                {
                    mods |= 0x2;
                }
                else if (part == "alt")
                {
                    mods |= 0x1;
                }
                else if (part == "shift")
                {
                    mods |= 0x4;
                }
                else if (part == "win" || part == "super")
                {
                    mods |= 0x8;
                }
                else if (part.Length == 1)
                {
                    vk = (uint)char.ToUpperInvariant(part[0]);
                }
            }

            if (vk == 0)
            {
                vk = (uint)'U';
            }
        }

        /// <summary>
        /// 承载 WM_HOTKEY 的隐藏窗口（无样式、不进任务栏、不抢焦点、不可见）。
        /// </summary>
        private sealed class HotkeyWindow : NativeWindow
        {
            public event Action Pressed;

            public HotkeyWindow()
            {
                CreateParams cp = new CreateParams();
                cp.Caption = "DesktopTwinSessionAgent";
                cp.Style = 0;
                cp.ExStyle = 0x00000080;
                CreateHandle(cp);
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WmHotkey)
                {
                    Action handler = Pressed;
                    if (handler != null)
                    {
                        handler();
                    }

                    return;
                }

                base.WndProc(ref m);
            }
        }

        /// <summary>
        /// 无窗体的应用上下文：只维持消息循环。
        /// </summary>
        private sealed class AgentContext : ApplicationContext
        {
        }
    }

    /// <summary>
    /// 需要的 Win32 调用。
    /// </summary>
    internal static class Native
    {
        [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool WTSQuerySessionInformationW(
            IntPtr hServer,
            int sessionId,
            int infoClass,
            out IntPtr ppBuffer,
            out int pBytesReturned);

        [DllImport("wtsapi32.dll")]
        private static extern void WTSFreeMemory(IntPtr p);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        public static string CurrentSessionName()
        {
            IntPtr buffer;
            int bytes;

            // WTSWinStationName = 6，WTS_CURRENT_SESSION = -1
            if (!WTSQuerySessionInformationW(IntPtr.Zero, -1, 6, out buffer, out bytes)
                || buffer == IntPtr.Zero)
            {
                return string.Empty;
            }

            try
            {
                return Marshal.PtrToStringUni(buffer) ?? string.Empty;
            }
            finally
            {
                WTSFreeMemory(buffer);
            }
        }
    }
}
