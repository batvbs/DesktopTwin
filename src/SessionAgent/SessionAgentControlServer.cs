using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Web.Script.Serialization;

namespace DesktopTwinSessionAgent
{
    /// <summary>
    /// 主进程 -> 分身代理由长度前缀 JSON 帧组成的命名管道服务。
    ///
    /// 每个请求都在同一条 STA 线程上串行执行，因此剪贴板、SendInput 和窗口操作
    /// 不需要互相争抢输入状态。该管道使用 CurrentUserOnly ACL，只允许当前用户连接。
    /// </summary>
    internal sealed class SessionAgentControlServer
    {
        private const int MaxRequestBytes = 8 * 1024 * 1024;
        private const int MaxResponseBytes = 64 * 1024 * 1024;
        private readonly string _pipeName;
        private readonly Action<string> _log;
        private Thread _thread;

        public SessionAgentControlServer(string pipeName, Action<string> log)
        {
            if (String.IsNullOrWhiteSpace(pipeName) || pipeName.Length > 200)
            {
                throw new ArgumentException("控制管道名称无效。", "pipeName");
            }

            _pipeName = pipeName;
            _log = log;
        }

        public void Start()
        {
            if (_thread != null)
            {
                return;
            }

            _thread = new Thread(Run);
            _thread.IsBackground = true;
            _thread.Name = "DesktopTwin MCP control";
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        private void Run()
        {
            while (true)
            {
                try
                {
                    using (var pipe = CreatePipe())
                    {
                        pipe.WaitForConnection();
                        HandleConnection(pipe);
                    }
                }
                catch (Exception exception)
                {
                    Log("控制管道连接失败: " + exception.Message);
                    Thread.Sleep(500);
                }
            }
        }

        private NamedPipeServerStream CreatePipe()
        {
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            var user = WindowsIdentity.GetCurrent().User;
            if (user == null)
            {
                throw new InvalidOperationException("无法确定当前用户 SID。");
            }

            security.AddAccessRule(new PipeAccessRule(
                user,
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
            return new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                0,
                0,
                security);
        }

        private void HandleConnection(NamedPipeServerStream pipe)
        {
            var requestBytes = ReadFrame(pipe, MaxRequestBytes);
            if (requestBytes == null || requestBytes.Length == 0)
            {
                return;
            }

            var serializer = CreateSerializer();
            Dictionary<string, object> request;
            try
            {
                request = serializer.DeserializeObject(Encoding.UTF8.GetString(requestBytes))
                    as Dictionary<string, object>;
            }
            catch (Exception exception)
            {
                WriteError(pipe, serializer, null, "invalid_request", "请求 JSON 无效: " + exception.Message);
                return;
            }

            if (request == null)
            {
                WriteError(pipe, serializer, null, "invalid_request", "请求必须是 JSON 对象。");
                return;
            }

            object idObject;
            request.TryGetValue("id", out idObject);
            var id = idObject == null ? null : Convert.ToString(idObject, CultureInfo.InvariantCulture);
            try
            {
                var operation = GetString(request, "op", true);
                var arguments = GetDictionary(request, "args", false);
                var result = Execute(operation, arguments ?? new Dictionary<string, object>());
                var response = new Dictionary<string, object>
                {
                    { "ok", true },
                    { "id", id },
                    { "result", result }
                };
                WriteFrame(pipe, serializer.Serialize(response));
            }
            catch (AgentOperationException exception)
            {
                WriteError(pipe, serializer, id, exception.Code, exception.Message);
            }
            catch (Exception exception)
            {
                Log("控制操作失败: " + exception);
                WriteError(pipe, serializer, id, "internal_error", "分身代理执行操作时发生内部错误。");
            }
        }

        private static JavaScriptSerializer CreateSerializer()
        {
            return new JavaScriptSerializer
            {
                MaxJsonLength = MaxResponseBytes,
                RecursionLimit = 64
            };
        }

        private static byte[] ReadFrame(Stream pipe, int maxBytes)
        {
            var lengthBytes = ReadExact(pipe, 4);
            if (lengthBytes == null)
            {
                return null;
            }

            var length = BitConverter.ToInt32(lengthBytes, 0);
            if (length <= 0 || length > maxBytes)
            {
                throw new IOException("控制请求长度无效。");
            }

            return ReadExact(pipe, length);
        }

        private static byte[] ReadExact(Stream stream, int count)
        {
            var buffer = new byte[count];
            var offset = 0;
            while (offset < count)
            {
                var read = stream.Read(buffer, offset, count - offset);
                if (read == 0)
                {
                    return null;
                }

                offset += read;
            }

            return buffer;
        }

        private static void WriteError(
            Stream pipe,
            JavaScriptSerializer serializer,
            string id,
            string code,
            string message)
        {
            var response = new Dictionary<string, object>
            {
                { "ok", false },
                { "id", id },
                {
                    "error",
                    new Dictionary<string, object>
                    {
                        { "code", code },
                        { "message", message }
                    }
                }
            };
            WriteFrame(pipe, serializer.Serialize(response));
        }

        private static void WriteFrame(Stream pipe, string json)
        {
            var payload = Encoding.UTF8.GetBytes(json);
            if (payload.Length > MaxResponseBytes)
            {
                throw new IOException("控制响应超过 64 MiB 上限。");
            }

            var length = BitConverter.GetBytes(payload.Length);
            pipe.Write(length, 0, length.Length);
            pipe.Write(payload, 0, payload.Length);
            pipe.Flush();
        }

        private object Execute(string operation, Dictionary<string, object> args)
        {
            switch (operation)
            {
                case "desktop_get_info":
                    return DesktopOperations.GetInfo();
                case "desktop_screenshot":
                    return DesktopOperations.Screenshot(args);
                case "desktop_mouse_move":
                    return DesktopOperations.MouseMove(args);
                case "desktop_mouse_click":
                    return DesktopOperations.MouseClick(args);
                case "desktop_mouse_drag":
                    return DesktopOperations.MouseDrag(args);
                case "desktop_mouse_scroll":
                    return DesktopOperations.MouseScroll(args);
                case "desktop_keyboard_text":
                    return DesktopOperations.KeyboardText(args);
                case "desktop_keyboard_key":
                    return DesktopOperations.KeyboardKey(args);
                case "desktop_keyboard_hotkey":
                    return DesktopOperations.KeyboardHotkey(args);
                case "desktop_list_windows":
                    return DesktopOperations.ListWindows(args);
                case "desktop_focus_window":
                    return DesktopOperations.FocusWindow(args);
                case "desktop_close_window":
                    return DesktopOperations.CloseWindow(args);
                case "desktop_list_processes":
                    return DesktopOperations.ListProcesses(args);
                case "desktop_launch_process":
                    return DesktopOperations.LaunchProcess(args);
                case "desktop_kill_process":
                    return DesktopOperations.KillProcess(args);
                case "desktop_clipboard_read":
                    return DesktopOperations.ClipboardRead();
                case "desktop_clipboard_write":
                    return DesktopOperations.ClipboardWrite(args);
                default:
                    throw new AgentOperationException("unknown_operation", "未知控制操作: " + operation);
            }
        }

        private static string GetString(Dictionary<string, object> values, string name, bool required)
        {
            object value;
            if (!values.TryGetValue(name, out value) || value == null)
            {
                if (required)
                {
                    throw new AgentOperationException("invalid_arguments", "缺少参数: " + name);
                }

                return null;
            }

            var text = value as string;
            if (text == null)
            {
                throw new AgentOperationException("invalid_arguments", "参数 " + name + " 必须是字符串。");
            }

            return text;
        }

        private static Dictionary<string, object> GetDictionary(
            Dictionary<string, object> values,
            string name,
            bool required)
        {
            object value;
            if (!values.TryGetValue(name, out value) || value == null)
            {
                if (required)
                {
                    throw new AgentOperationException("invalid_arguments", "缺少参数: " + name);
                }

                return null;
            }

            var dictionary = value as Dictionary<string, object>;
            if (dictionary == null)
            {
                throw new AgentOperationException("invalid_arguments", "参数 " + name + " 必须是对象。");
            }

            return dictionary;
        }

        private void Log(string message)
        {
            if (_log != null)
            {
                _log(message);
            }
        }
    }

    internal sealed class AgentOperationException : Exception
    {
        public AgentOperationException(string code, string message)
            : base(message)
        {
            Code = code;
        }

        public string Code { get; private set; }
    }

    /// <summary>
    /// 所有真正触达 Windows 桌面/进程/剪贴板的操作。
    /// </summary>
    internal static class DesktopOperations
    {
        private static readonly int InputSize = Marshal.SizeOf(typeof(DesktopNative.Input));
        private static readonly Dictionary<string, int> NamedKeys = BuildNamedKeys();
        private static readonly HashSet<int> ExtendedKeys = BuildExtendedKeys();

        public static object GetInfo()
        {
            var screen = GetVirtualScreen();
            DesktopNative.Point cursor;
            if (!DesktopNative.GetCursorPos(out cursor))
            {
                cursor = new DesktopNative.Point { X = 0, Y = 0 };
            }

            var process = Process.GetCurrentProcess();
            float dpi = 96f;
            try
            {
                using (var graphics = Graphics.FromHwnd(IntPtr.Zero))
                {
                    dpi = graphics.DpiX;
                }
            }
            catch
            {
                // 某些无桌面环境取不到 DPI，保留标准 96。
            }

            return new Dictionary<string, object>
            {
                { "sessionId", process.SessionId },
                { "sessionName", Native.CurrentSessionName() },
                {
                    "virtualScreen",
                    new Dictionary<string, object>
                    {
                        { "left", screen.Left },
                        { "top", screen.Top },
                        { "width", screen.Width },
                        { "height", screen.Height }
                    }
                },
                {
                    "cursor",
                    new Dictionary<string, object>
                    {
                        { "x", cursor.X },
                        { "y", cursor.Y }
                    }
                },
                { "dpi", Math.Round(dpi, 1) },
                { "foregroundWindowId", DesktopNative.GetForegroundWindow().ToInt64() }
            };
        }

        public static object Screenshot(Dictionary<string, object> args)
        {
            var desktop = GetVirtualScreen();
            Rectangle region = desktop;
            if (args.ContainsKey("left") || args.ContainsKey("top")
                || args.ContainsKey("width") || args.ContainsKey("height"))
            {
                // 任一边界参数出现时启用区域模式；未给出的边沿用整个虚拟屏幕的对应值。
                var left = HasValue(args, "left") ? GetInt(args, "left", null, null) : desktop.Left;
                var top = HasValue(args, "top") ? GetInt(args, "top", null, null) : desktop.Top;
                var width = HasValue(args, "width") ? GetInt(args, "width", 1, 16384) : desktop.Width;
                var height = HasValue(args, "height") ? GetInt(args, "height", 1, 16384) : desktop.Height;
                region = new Rectangle(left, top, width, height);
                if (!desktop.Contains(region))
                {
                    throw new AgentOperationException(
                        "invalid_region",
                        "截图区域必须完全位于分身桌面虚拟屏幕内。");
                }
            }

            if (region.Width <= 0 || region.Height <= 0
                || (long)region.Width * region.Height > 40000000L)
            {
                throw new AgentOperationException("invalid_region", "截图区域过大。");
            }

            try
            {
                using (var bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb))
                {
                    using (var graphics = Graphics.FromImage(bitmap))
                    {
                        graphics.CopyFromScreen(
                            region.Location,
                            Point.Empty,
                            region.Size,
                            CopyPixelOperation.SourceCopy);
                    }

                    using (var memory = new MemoryStream())
                    {
                        bitmap.Save(memory, ImageFormat.Png);
                        return new Dictionary<string, object>
                        {
                            { "width", region.Width },
                            { "height", region.Height },
                            { "left", region.Left },
                            { "top", region.Top },
                            { "mimeType", "image/png" },
                            { "data", Convert.ToBase64String(memory.ToArray()) }
                        };
                    }
                }
            }
            catch (AgentOperationException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new AgentOperationException("screenshot_failed", "截取分身桌面失败: " + exception.Message);
            }
        }

        public static object MouseMove(Dictionary<string, object> args)
        {
            var x = GetInt(args, "x", -100000, 100000);
            var y = GetInt(args, "y", -100000, 100000);
            MoveMouse(x, y);
            return new Dictionary<string, object> { { "x", x }, { "y", y } };
        }

        public static object MouseClick(Dictionary<string, object> args)
        {
            var x = GetInt(args, "x", null, null);
            var y = GetInt(args, "y", null, null);
            var button = GetString(args, "button", false) ?? "left";
            var clicks = HasValue(args, "clicks") ? GetInt(args, "clicks", 1, 3) : 1;
            var flags = GetButtonFlags(button);
            MoveMouse(x, y);
            Thread.Sleep(15);
            for (var i = 0; i < clicks; i++)
            {
                SendMouse(flags.Down);
                Thread.Sleep(20);
                SendMouse(flags.Up);
                if (i + 1 < clicks)
                {
                    Thread.Sleep(70);
                }
            }

            return new Dictionary<string, object>
            {
                { "x", x },
                { "y", y },
                { "button", button.ToLowerInvariant() },
                { "clicks", clicks }
            };
        }

        public static object MouseDrag(Dictionary<string, object> args)
        {
            var fromX = GetInt(args, "fromX", null, null);
            var fromY = GetInt(args, "fromY", null, null);
            var toX = GetInt(args, "toX", null, null);
            var toY = GetInt(args, "toY", null, null);
            var button = GetString(args, "button", false) ?? "left";
            var duration = HasValue(args, "durationMs") ? GetInt(args, "durationMs", 0, 10000) : 400;
            var flags = GetButtonFlags(button);
            MoveMouse(fromX, fromY);
            Thread.Sleep(15);
            SendMouse(flags.Down);
            try
            {
                var steps = Math.Max(1, Math.Min(50, duration / 20));
                for (var i = 1; i <= steps; i++)
                {
                    var x = fromX + ((toX - fromX) * i / steps);
                    var y = fromY + ((toY - fromY) * i / steps);
                    MoveMouse(x, y);
                    if (steps > 1)
                    {
                        Thread.Sleep(Math.Max(1, duration / steps));
                    }
                }
            }
            finally
            {
                SendMouse(flags.Up);
            }

            return new Dictionary<string, object>
            {
                { "fromX", fromX },
                { "fromY", fromY },
                { "toX", toX },
                { "toY", toY },
                { "button", button.ToLowerInvariant() },
                { "durationMs", duration }
            };
        }

        public static object MouseScroll(Dictionary<string, object> args)
        {
            var x = GetInt(args, "x", null, null);
            var y = GetInt(args, "y", null, null);
            var delta = GetInt(args, "delta", -10000, 10000);
            var horizontal = GetBool(args, "horizontal", false);
            MoveMouse(x, y);
            Thread.Sleep(15);
            SendMouseInput(0, 0, delta, DesktopNative.MouseEventWheel
                | (horizontal ? DesktopNative.MouseEventHWheel : 0));
            return new Dictionary<string, object>
            {
                { "x", x },
                { "y", y },
                { "delta", delta },
                { "horizontal", horizontal }
            };
        }

        public static object KeyboardText(Dictionary<string, object> args)
        {
            var text = GetString(args, "text", true);
            if (text.Length > 100000)
            {
                throw new AgentOperationException("invalid_arguments", "文本超过 100000 字符上限。");
            }

            for (var i = 0; i < text.Length; i++)
            {
                var character = text[i];
                if (character == '\r')
                {
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        continue;
                    }

                    SendVirtualKey(0x0D, false);
                    continue;
                }

                if (character == '\n')
                {
                    SendVirtualKey(0x0D, false);
                    continue;
                }

                if (character == '\t')
                {
                    SendVirtualKey(0x09, false);
                    continue;
                }

                SendUnicodeCharacter(character);
            }

            return new Dictionary<string, object>
            {
                { "length", text.Length }
            };
        }

        public static object KeyboardKey(Dictionary<string, object> args)
        {
            var key = GetString(args, "key", true);
            var action = GetString(args, "action", false) ?? "press";
            var repeat = HasValue(args, "repeat") ? GetInt(args, "repeat", 1, 100) : 1;
            var virtualKey = ResolveKey(key, true);
            var extended = ExtendedKeys.Contains(virtualKey);

            if (String.Equals(action, "down", StringComparison.OrdinalIgnoreCase))
            {
                SendVirtualKey(virtualKey, extended, true);
            }
            else if (String.Equals(action, "up", StringComparison.OrdinalIgnoreCase))
            {
                SendVirtualKey(virtualKey, extended, false);
            }
            else if (String.Equals(action, "press", StringComparison.OrdinalIgnoreCase))
            {
                for (var i = 0; i < repeat; i++)
                {
                    SendVirtualKey(virtualKey, extended, true);
                    SendVirtualKey(virtualKey, extended, false);
                    if (i + 1 < repeat)
                    {
                        Thread.Sleep(40);
                    }
                }
            }
            else
            {
                throw new AgentOperationException("invalid_arguments", "action 必须是 press、down 或 up。");
            }

            return new Dictionary<string, object>
            {
                { "key", key },
                { "action", action.ToLowerInvariant() },
                { "repeat", repeat }
            };
        }

        public static object KeyboardHotkey(Dictionary<string, object> args)
        {
            object rawKeys;
            var keysObject = args.TryGetValue("keys", out rawKeys) ? rawKeys as object[] : null;
            if (keysObject == null || keysObject.Length == 0 || keysObject.Length > 8)
            {
                throw new AgentOperationException("invalid_arguments", "keys 必须包含 1-8 个按键。");
            }

            var keys = new List<string>();
            for (var i = 0; i < keysObject.Length; i++)
            {
                var key = keysObject[i] as string;
                if (String.IsNullOrWhiteSpace(key))
                {
                    throw new AgentOperationException("invalid_arguments", "keys 中每一项都必须是字符串。");
                }

                keys.Add(key);
            }

            var modifiers = new List<int>();
            for (var i = 0; i < keys.Count - 1; i++)
            {
                var modifier = ResolveModifier(keys[i]);
                if (modifier == 0)
                {
                    throw new AgentOperationException(
                        "invalid_arguments",
                        "热键最后一项是主键，之前的项必须为 ctrl、alt、shift 或 win。");
                }

                modifiers.Add(modifier);
            }

            var primary = ResolveKey(keys[keys.Count - 1], false);
            if (ResolveModifier(keys[keys.Count - 1]) != 0)
            {
                throw new AgentOperationException("invalid_arguments", "热键最后一项不能是修饰键。");
            }

            var pressed = new List<int>();
            try
            {
                for (var i = 0; i < modifiers.Count; i++)
                {
                    var vk = modifiers[i];
                    SendVirtualKey(vk, ExtendedKeys.Contains(vk), true);
                    pressed.Add(vk);
                }

                SendVirtualKey(primary, ExtendedKeys.Contains(primary), true);
                Thread.Sleep(30);
                SendVirtualKey(primary, ExtendedKeys.Contains(primary), false);
            }
            finally
            {
                for (var i = pressed.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        SendVirtualKey(pressed[i], ExtendedKeys.Contains(pressed[i]), false);
                    }
                    catch
                    {
                        // 已经失败的输入路径不必再遮蔽原始错误。
                    }
                }
            }

            return new Dictionary<string, object> { { "keys", keys } };
        }

        public static object ListWindows(Dictionary<string, object> args)
        {
            var includeHidden = GetBool(args, "includeHidden", false);
            var includeUntitled = GetBool(args, "includeUntitled", false);
            var windows = new List<object>();
            DesktopNative.EnumWindows(delegate(IntPtr window, IntPtr parameter)
            {
                if (!includeHidden && !DesktopNative.IsWindowVisible(window))
                {
                    return true;
                }

                var title = GetWindowTitle(window);
                if (!includeUntitled && String.IsNullOrWhiteSpace(title))
                {
                    return true;
                }

                int processId;
                DesktopNative.GetWindowThreadProcessId(window, out processId);
                var className = GetWindowClass(window);
                DesktopNative.Rect rect;
                var hasRect = DesktopNative.GetWindowRect(window, out rect);
                var item = new Dictionary<string, object>
                {
                    { "windowId", window.ToInt64() },
                    { "processId", processId },
                    { "title", title },
                    { "className", className },
                    { "visible", DesktopNative.IsWindowVisible(window) }
                };

                if (hasRect)
                {
                    item["bounds"] = new Dictionary<string, object>
                    {
                        { "left", rect.Left },
                        { "top", rect.Top },
                        { "width", Math.Max(0, rect.Right - rect.Left) },
                        { "height", Math.Max(0, rect.Bottom - rect.Top) }
                    };
                }

                windows.Add(item);
                return true;
            }, IntPtr.Zero);

            return new Dictionary<string, object>
            {
                { "windows", windows },
                { "count", windows.Count }
            };
        }

        public static object FocusWindow(Dictionary<string, object> args)
        {
            var window = GetWindow(args);
            DesktopNative.ShowWindow(window, DesktopNative.SwRestore);
            var foreground = DesktopNative.GetForegroundWindow();
            int foregroundProcessId;
            var foregroundThread = foreground == IntPtr.Zero
                ? 0
                : DesktopNative.GetWindowThreadProcessId(foreground, out foregroundProcessId);
            var currentThread = DesktopNative.GetCurrentThreadId();
            var attached = false;
            if (foregroundThread != 0 && foregroundThread != currentThread)
            {
                attached = DesktopNative.AttachThreadInput(currentThread, foregroundThread, true);
            }

            try
            {
                DesktopNative.BringWindowToTop(window);
                DesktopNative.SetForegroundWindow(window);
                DesktopNative.SetFocus(window);
            }
            finally
            {
                if (attached)
                {
                    DesktopNative.AttachThreadInput(currentThread, foregroundThread, false);
                }
            }

            if (DesktopNative.GetForegroundWindow() != window)
            {
                throw new AgentOperationException("focus_failed", "Windows 拒绝切换前台窗口。");
            }

            return new Dictionary<string, object>
            {
                { "windowId", window.ToInt64() },
                { "title", GetWindowTitle(window) }
            };
        }

        public static object CloseWindow(Dictionary<string, object> args)
        {
            var window = GetWindow(args);
            if (!DesktopNative.PostMessage(window, DesktopNative.WmClose, IntPtr.Zero, IntPtr.Zero))
            {
                throw new AgentOperationException("close_failed", "无法向窗口发送关闭请求。");
            }

            return new Dictionary<string, object>
            {
                { "windowId", window.ToInt64() },
                { "title", GetWindowTitle(window) }
            };
        }

        public static object ListProcesses(Dictionary<string, object> args)
        {
            var includePath = GetBool(args, "includePath", false);
            var currentSession = Process.GetCurrentProcess().SessionId;
            var items = new List<object>();
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        if (process.SessionId != currentSession)
                        {
                            continue;
                        }

                        var item = new Dictionary<string, object>
                        {
                            { "processId", process.Id },
                            { "name", process.ProcessName }
                        };
                        if (includePath)
                        {
                            try
                            {
                                item["path"] = process.MainModule.FileName;
                            }
                            catch
                            {
                                item["path"] = null;
                            }
                        }

                        items.Add(item);
                    }
                    catch
                    {
                        // 进程可能在枚举时退出，或不允许查询，跳过即可。
                    }
                }
            }

            return new Dictionary<string, object>
            {
                { "processes", items },
                { "count", items.Count }
            };
        }

        public static object LaunchProcess(Dictionary<string, object> args)
        {
            var target = GetString(args, "target", true);
            if (String.IsNullOrWhiteSpace(target))
            {
                throw new AgentOperationException("invalid_arguments", "target 不能为空。");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = target,
                Arguments = GetString(args, "arguments", false) ?? String.Empty,
                UseShellExecute = true
            };
            var workingDirectory = GetString(args, "workingDirectory", false);
            if (!String.IsNullOrWhiteSpace(workingDirectory))
            {
                startInfo.WorkingDirectory = workingDirectory;
            }

            try
            {
                var process = Process.Start(startInfo);
                if (process == null)
                {
                    return new Dictionary<string, object>
                    {
                        { "started", true },
                        { "processId", 0 },
                        { "target", target }
                    };
                }

                using (process)
                {
                    return new Dictionary<string, object>
                    {
                        { "started", true },
                        { "processId", process.Id },
                        { "name", process.ProcessName },
                        { "target", target }
                    };
                }
            }
            catch (Exception exception)
            {
                throw new AgentOperationException("launch_failed", "启动程序失败: " + exception.Message);
            }
        }

        public static object KillProcess(Dictionary<string, object> args)
        {
            var processId = GetInt(args, "processId", 1, Int32.MaxValue);
            if (processId == Process.GetCurrentProcess().Id)
            {
                throw new AgentOperationException("invalid_process", "不能结束后台代理自身。");
            }

            try
            {
                using (var process = Process.GetProcessById(processId))
                {
                    if (process.SessionId != Process.GetCurrentProcess().SessionId)
                    {
                        throw new AgentOperationException("invalid_process", "只允许结束后台分身会话内的进程。");
                    }

                    var name = process.ProcessName;
                    process.Kill();
                    process.WaitForExit(3000);
                    return new Dictionary<string, object>
                    {
                        { "processId", processId },
                        { "name", name },
                        { "terminated", true }
                    };
                }
            }
            catch (AgentOperationException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new AgentOperationException("kill_failed", "结束进程失败: " + exception.Message);
            }
        }

        public static object ClipboardRead()
        {
            return RetryClipboard(delegate
            {
                var text = Clipboard.ContainsText() ? Clipboard.GetText() : String.Empty;
                return new Dictionary<string, object> { { "text", text } };
            });
        }

        public static object ClipboardWrite(Dictionary<string, object> args)
        {
            var text = GetString(args, "text", true);
            if (text.Length > 1000000)
            {
                throw new AgentOperationException("invalid_arguments", "剪贴板文本超过 1000000 字符上限。");
            }

            return RetryClipboard(delegate
            {
                Clipboard.SetDataObject(text, true);
                return new Dictionary<string, object>
                {
                    { "written", true },
                    { "length", text.Length }
                };
            });
        }

        private static object RetryClipboard(Func<object> action)
        {
            Exception last = null;
            for (var attempt = 0; attempt < 6; attempt++)
            {
                try
                {
                    return action();
                }
                catch (ExternalException exception)
                {
                    last = exception;
                    Thread.Sleep(40);
                }
            }

            throw new AgentOperationException(
                "clipboard_failed",
                "分身剪贴板正被其他程序占用: " + (last == null ? "未知错误" : last.Message));
        }

        private static Rectangle GetVirtualScreen()
        {
            return new Rectangle(
                DesktopNative.GetSystemMetrics(DesktopNative.SmXVirtualScreen),
                DesktopNative.GetSystemMetrics(DesktopNative.SmYVirtualScreen),
                DesktopNative.GetSystemMetrics(DesktopNative.SmCxVirtualScreen),
                DesktopNative.GetSystemMetrics(DesktopNative.SmCyVirtualScreen));
        }

        private static void MoveMouse(int x, int y)
        {
            var desktop = GetVirtualScreen();
            if (desktop.Width <= 1 || desktop.Height <= 1
                || x < desktop.Left || x >= desktop.Right
                || y < desktop.Top || y >= desktop.Bottom)
            {
                throw new AgentOperationException("invalid_coordinates", "鼠标坐标位于分身桌面虚拟屏幕之外。");
            }

            DesktopNative.SetCursorPos(x, y);
            var normalizedX = (int)Math.Round((x - desktop.Left) * 65535.0 / (desktop.Width - 1));
            var normalizedY = (int)Math.Round((y - desktop.Top) * 65535.0 / (desktop.Height - 1));
            SendMouseInput(
                normalizedX,
                normalizedY,
                0,
                DesktopNative.MouseEventMove
                    | DesktopNative.MouseEventAbsolute
                    | DesktopNative.MouseEventVirtualDesk);
        }

        private static void SendMouse(uint flags)
        {
            SendMouseInput(0, 0, 0, flags);
        }

        private static void SendMouseInput(int dx, int dy, int data, uint flags)
        {
            var input = new DesktopNative.Input
            {
                Type = DesktopNative.InputMouse,
                Union = new DesktopNative.InputUnion
                {
                    Mouse = new DesktopNative.MouseInput
                    {
                        Dx = dx,
                        Dy = dy,
                        MouseData = data,
                        Flags = flags
                    }
                }
            };
            SendInputs(new[] { input });
        }

        private static void SendUnicodeCharacter(char character)
        {
            var down = new DesktopNative.Input
            {
                Type = DesktopNative.InputKeyboard,
                Union = new DesktopNative.InputUnion
                {
                    Keyboard = new DesktopNative.KeyboardInput
                    {
                        VirtualKey = 0,
                        ScanCode = character,
                        Flags = DesktopNative.KeyEventUnicode
                    }
                }
            };
            var up = down;
            up.Union.Keyboard.Flags = DesktopNative.KeyEventUnicode | DesktopNative.KeyEventKeyUp;
            SendInputs(new[] { down, up });
        }

        private static void SendVirtualKey(int virtualKey, bool extended)
        {
            SendVirtualKey(virtualKey, extended, true);
            SendVirtualKey(virtualKey, extended, false);
        }

        private static void SendVirtualKey(int virtualKey, bool extended, bool keyDown)
        {
            var flags = keyDown ? 0u : DesktopNative.KeyEventKeyUp;
            if (extended)
            {
                flags |= DesktopNative.KeyEventExtendedKey;
            }

            var input = new DesktopNative.Input
            {
                Type = DesktopNative.InputKeyboard,
                Union = new DesktopNative.InputUnion
                {
                    Keyboard = new DesktopNative.KeyboardInput
                    {
                        VirtualKey = (ushort)virtualKey,
                        ScanCode = 0,
                        Flags = flags
                    }
                }
            };
            SendInputs(new[] { input });
        }

        private static void SendInputs(DesktopNative.Input[] inputs)
        {
            var sent = DesktopNative.SendInput((uint)inputs.Length, inputs, InputSize);
            if (sent != inputs.Length)
            {
                throw new AgentOperationException(
                    "input_failed",
                    "分身桌面拒绝了输入注入。请确认分身会话已连接并且没有锁屏。");
            }
        }

        private static KeyButtonFlags GetButtonFlags(string button)
        {
            if (String.Equals(button, "left", StringComparison.OrdinalIgnoreCase))
            {
                return new KeyButtonFlags(DesktopNative.MouseEventLeftDown, DesktopNative.MouseEventLeftUp);
            }

            if (String.Equals(button, "right", StringComparison.OrdinalIgnoreCase))
            {
                return new KeyButtonFlags(DesktopNative.MouseEventRightDown, DesktopNative.MouseEventRightUp);
            }

            if (String.Equals(button, "middle", StringComparison.OrdinalIgnoreCase))
            {
                return new KeyButtonFlags(DesktopNative.MouseEventMiddleDown, DesktopNative.MouseEventMiddleUp);
            }

            throw new AgentOperationException("invalid_arguments", "button 必须是 left、right 或 middle。");
        }

        private static IntPtr GetWindow(Dictionary<string, object> args)
        {
            var value = GetLong(args, "windowId", 1, Int64.MaxValue);
            var window = new IntPtr(value);
            if (!DesktopNative.IsWindow(window))
            {
                throw new AgentOperationException("invalid_window", "窗口不存在或已经关闭。");
            }

            return window;
        }

        private static string GetWindowTitle(IntPtr window)
        {
            var length = DesktopNative.GetWindowTextLength(window);
            if (length <= 0)
            {
                return String.Empty;
            }

            var builder = new StringBuilder(length + 1);
            DesktopNative.GetWindowText(window, builder, builder.Capacity);
            return builder.ToString();
        }

        private static string GetWindowClass(IntPtr window)
        {
            var builder = new StringBuilder(256);
            DesktopNative.GetClassName(window, builder, builder.Capacity);
            return builder.ToString();
        }

        private static int ResolveKey(string name, bool allowModifiers)
        {
            if (String.IsNullOrWhiteSpace(name))
            {
                throw new AgentOperationException("invalid_arguments", "按键名称不能为空。");
            }

            var key = name.Trim().ToLowerInvariant();
            int virtualKey;
            if (NamedKeys.TryGetValue(key, out virtualKey))
            {
                if (!allowModifiers && IsModifierVirtualKey(virtualKey))
                {
                    throw new AgentOperationException("invalid_arguments", "此处不能使用修饰键: " + name);
                }

                return virtualKey;
            }

            if (key.Length == 1 && Char.IsLetter(key[0]))
            {
                return Char.ToUpperInvariant(key[0]);
            }

            if (key.Length == 1 && Char.IsDigit(key[0]))
            {
                return key[0];
            }

            throw new AgentOperationException("invalid_arguments", "不支持的按键名称: " + name);
        }

        private static int ResolveModifier(string name)
        {
            int virtualKey;
            if (!NamedKeys.TryGetValue(name.Trim().ToLowerInvariant(), out virtualKey))
            {
                return 0;
            }

            return IsModifierVirtualKey(virtualKey) ? virtualKey : 0;
        }

        private static bool IsModifierVirtualKey(int virtualKey)
        {
            return virtualKey == 0x10 || virtualKey == 0x11 || virtualKey == 0x12
                || virtualKey == 0x5B || virtualKey == 0x5C;
        }

        private static Dictionary<string, int> BuildNamedKeys()
        {
            var keys = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                { "backspace", 0x08 },
                { "tab", 0x09 },
                { "enter", 0x0D },
                { "return", 0x0D },
                { "shift", 0x10 },
                { "ctrl", 0x11 },
                { "control", 0x11 },
                { "alt", 0x12 },
                { "menu", 0x12 },
                { "pause", 0x13 },
                { "capslock", 0x14 },
                { "escape", 0x1B },
                { "esc", 0x1B },
                { "space", 0x20 },
                { "pageup", 0x21 },
                { "pagedown", 0x22 },
                { "end", 0x23 },
                { "home", 0x24 },
                { "left", 0x25 },
                { "up", 0x26 },
                { "right", 0x27 },
                { "down", 0x28 },
                { "printscreen", 0x2C },
                { "insert", 0x2D },
                { "delete", 0x2E },
                { "del", 0x2E },
                { "win", 0x5B },
                { "lwin", 0x5B },
                { "rwin", 0x5C },
                { "apps", 0x5D },
                { "numlock", 0x90 },
                { "scrolllock", 0x91 },
                { "semicolon", 0xBA },
                { "equal", 0xBB },
                { "comma", 0xBC },
                { "minus", 0xBD },
                { "period", 0xBE },
                { "slash", 0xBF },
                { "backtick", 0xC0 },
                { "leftbracket", 0xDB },
                { "backslash", 0xDC },
                { "rightbracket", 0xDD },
                { "quote", 0xDE }
            };

            for (var i = 1; i <= 24; i++)
            {
                keys.Add("f" + i.ToString(CultureInfo.InvariantCulture), 0x6F + i);
            }

            for (var i = 0; i <= 9; i++)
            {
                keys.Add("numpad" + i.ToString(CultureInfo.InvariantCulture), 0x60 + i);
            }

            keys.Add("multiply", 0x6A);
            keys.Add("add", 0x6B);
            keys.Add("subtract", 0x6D);
            keys.Add("decimal", 0x6E);
            keys.Add("divide", 0x6F);
            return keys;
        }

        private static HashSet<int> BuildExtendedKeys()
        {
            return new HashSet<int>
            {
                0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28,
                0x2D, 0x2E, 0x5B, 0x5C, 0x5D, 0x6F, 0x90
            };
        }

        private static bool HasValue(Dictionary<string, object> args, string name)
        {
            object raw;
            return args.TryGetValue(name, out raw) && raw != null;
        }

        private static string GetString(Dictionary<string, object> args, string name, bool required)
        {
            object raw;
            if (!args.TryGetValue(name, out raw) || raw == null)
            {
                if (required)
                {
                    throw new AgentOperationException("invalid_arguments", "缺少参数: " + name);
                }

                return null;
            }

            var value = raw as string;
            if (value == null)
            {
                throw new AgentOperationException("invalid_arguments", "参数 " + name + " 必须是字符串。");
            }

            return value;
        }

        private static bool GetBool(Dictionary<string, object> args, string name, bool defaultValue)
        {
            object raw;
            if (!args.TryGetValue(name, out raw) || raw == null)
            {
                return defaultValue;
            }

            if (raw is bool)
            {
                return (bool)raw;
            }

            throw new AgentOperationException("invalid_arguments", "参数 " + name + " 必须是布尔值。");
        }

        private static int GetInt(
            Dictionary<string, object> args,
            string name,
            int? minimum,
            int? maximum)
        {
            var value = GetLong(args, name, minimum, maximum);
            if (value < Int32.MinValue || value > Int32.MaxValue)
            {
                throw new AgentOperationException("invalid_arguments", "参数 " + name + " 超出整数范围。");
            }

            return (int)value;
        }

        private static long GetLong(
            Dictionary<string, object> args,
            string name,
            long? minimum,
            long? maximum)
        {
            object raw;
            if (!args.TryGetValue(name, out raw) || raw == null)
            {
                throw new AgentOperationException("invalid_arguments", "缺少参数: " + name);
            }

            long value;
            try
            {
                value = Convert.ToInt64(raw, CultureInfo.InvariantCulture);
            }
            catch
            {
                throw new AgentOperationException("invalid_arguments", "参数 " + name + " 必须是整数。");
            }

            if (minimum.HasValue && value < minimum.Value)
            {
                throw new AgentOperationException(
                    "invalid_arguments",
                    "参数 " + name + " 不能小于 " + minimum.Value.ToString(CultureInfo.InvariantCulture) + "。");
            }

            if (maximum.HasValue && value > maximum.Value)
            {
                throw new AgentOperationException(
                    "invalid_arguments",
                    "参数 " + name + " 不能大于 " + maximum.Value.ToString(CultureInfo.InvariantCulture) + "。");
            }

            return value;
        }

        private struct KeyButtonFlags
        {
            public KeyButtonFlags(uint down, uint up)
            {
                Down = down;
                Up = up;
            }

            public uint Down;
            public uint Up;
        }
    }
}
