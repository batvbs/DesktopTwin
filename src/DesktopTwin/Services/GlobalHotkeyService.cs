using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DesktopTwin.Services;

/// <summary>
/// 全局热键。同时用两条通道接收按键：
///
/// 1. <c>RegisterHotKey</c>——系统热键，正常情况下够用；
/// 2. <c>Raw Input</c>（<c>RIDEV_INPUTSINK</c>）——兜底。
///
/// 为什么需要第二条：焦点落在分身画面里时，RDP 控件会安装键盘钩子，把组合键
/// 转发进远程会话（「发送系统快捷键到分身」开启时尤其如此）。钩子一旦吞掉按键，
/// 系统热键就收不到 <c>WM_HOTKEY</c>——表现就是「在分身里按了没反应」。
/// Raw Input 在系统输入线程里分发，早于键盘钩子，因此还能拿到这次按键。
///
/// 两条通道会对同一次按键各送一次，用时间窗去重。
/// </summary>
internal sealed class GlobalHotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int WmInput = 0x00FF;
    private const int HotkeyId = 0x4454;

    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;

    /// <summary>
    /// 长按不重复触发。没有它，按住热键会连续触发几十次切换。
    /// </summary>
    private const uint ModNoRepeat = 0x4000;

    private const uint RidInput = 0x10000003;
    private const uint RimTypeKeyboard = 1;
    private const uint RidevRemove = 0x00000001;
    private const uint RidevInputSink = 0x00000100;
    private const ushort RiKeyBreak = 0x0001;
    private const ushort HidUsagePageGeneric = 0x01;
    private const ushort HidUsageKeyboard = 0x06;

    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;

    /// <summary>
    /// 两条通道对同一次按键的间隔通常只有几毫秒，250ms 足够去重又不会误伤连按。
    /// </summary>
    private const long DedupeWindowMilliseconds = 250;

    private static readonly Dictionary<string, uint> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["space"] = 0x20,
        ["tab"] = 0x09,
        ["enter"] = 0x0D,
        ["return"] = 0x0D,
        ["esc"] = 0x1B,
        ["escape"] = 0x1B,
        ["backspace"] = 0x08,
        ["delete"] = 0x2E,
        ["insert"] = 0x2D,
        ["home"] = 0x24,
        ["end"] = 0x23,
        ["pageup"] = 0x21,
        ["pagedown"] = 0x22,
        ["left"] = 0x25,
        ["up"] = 0x26,
        ["right"] = 0x27,
        ["down"] = 0x28,
        ["`"] = 0xC0,
        ["-"] = 0xBD,
        ["="] = 0xBB,
        ["["] = 0xDB,
        ["]"] = 0xDD,
        [";"] = 0xBA,
        ["'"] = 0xDE,
        [","] = 0xBC,
        ["."] = 0xBE,
        ["/"] = 0xBF,
        ["\\"] = 0xDC
    };

    /// <summary>
    /// 显示用名称。直接大写化会得到「SPACE」「PAGEUP」这种难看的写法，
    /// 这里只给需要保留固定大小写的键做映射。
    /// </summary>
    private static readonly Dictionary<string, string> DisplayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["space"] = "Space",
        ["tab"] = "Tab",
        ["enter"] = "Enter",
        ["return"] = "Enter",
        ["esc"] = "Esc",
        ["escape"] = "Esc",
        ["backspace"] = "Backspace",
        ["delete"] = "Delete",
        ["insert"] = "Insert",
        ["home"] = "Home",
        ["end"] = "End",
        ["pageup"] = "PageUp",
        ["pagedown"] = "PageDown",
        ["left"] = "Left",
        ["up"] = "Up",
        ["right"] = "Right",
        ["down"] = "Down"
    };

    private HwndSource? _source;
    private bool _registered;
    private bool _rawInputRegistered;
    private bool _disposed;

    private uint _modifiers;
    private uint _virtualKey;
    private long _lastTriggerTicks;

    /// <summary>
    /// 热键被按下。
    /// </summary>
    public event EventHandler? Pressed;

    /// <summary>
    /// 当前是否注册成功。任意一条通道可用即为 true。
    /// </summary>
    public bool IsRegistered => _registered;

    /// <summary>
    /// 是否启用了 Raw Input 兜底通道。
    /// </summary>
    public bool IsRawInputActive => _rawInputRegistered;

    /// <summary>
    /// 注册热键。已注册的会先注销。
    /// </summary>
    /// <param name="window">承载热键消息的窗口。</param>
    /// <param name="combination">形如 <c>Ctrl+Alt+U</c> 的组合键。</param>
    /// <param name="error">失败原因，成功时为空。</param>
    public bool TryRegister(Window window, string combination, out string error)
    {
        error = string.Empty;
        ThrowIfDisposed();

        if (!TryParse(combination, out var modifiers, out var virtualKey))
        {
            error = $"无法识别的组合键：{combination}";
            return false;
        }

        if (!modifiers.HasValue || modifiers.Value == 0)
        {
            // 不带修饰键的全局热键会吃掉整个系统的普通按键，绝对不允许。
            error = "全局热键必须包含 Ctrl / Alt / Shift / Win 之一。";
            return false;
        }

        Unregister();

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            error = "窗口尚未创建，无法注册热键。";
            return false;
        }

        _source ??= HwndSource.FromHwnd(handle);
        if (_source is null)
        {
            error = "无法取得窗口消息源，注册热键失败。";
            return false;
        }

        // 先摘再挂，避免异常路径下重复挂钩子导致一次按键触发两遍。
        _source.RemoveHook(WndProc);
        _source.AddHook(WndProc);

        var hotkeyRegistered = RegisterHotKey(handle, HotkeyId, modifiers.Value | ModNoRepeat, virtualKey);
        var hotkeyError = hotkeyRegistered ? 0 : Marshal.GetLastPInvokeError();

        if (!hotkeyRegistered && hotkeyError == 1409)
        {
            // 被别的程序占了。这时不该偷偷用 Raw Input 去抢——
            // 那会变成两个程序同时响应同一个组合键，用户更难判断。
            _source.RemoveHook(WndProc);
            error = $"组合键 {Format(combination)} 已被其他程序占用，换一个试试。";
            return false;
        }

        if (!hotkeyRegistered)
        {
            AppLog.Warn(
                $"RegisterHotKey 失败（Win32 错误 {hotkeyError}），改由 Raw Input 通道接收按键。");
        }

        _rawInputRegistered = TryRegisterRawInput(handle);

        if (!hotkeyRegistered && !_rawInputRegistered)
        {
            _source.RemoveHook(WndProc);
            error = $"注册热键失败（Win32 错误 {hotkeyError}）。";
            return false;
        }

        _modifiers = modifiers.Value;
        _virtualKey = virtualKey;
        _registered = true;

        AppLog.Info(
            $"全局热键已注册：{Format(combination)}"
            + $"（系统热键={(hotkeyRegistered ? "成功" : "失败")}，"
            + $"Raw Input 兜底={(_rawInputRegistered ? "已启用" : "不可用")}）");

        return true;
    }

    /// <summary>
    /// 注销热键。
    /// </summary>
    public void Unregister()
    {
        if (!_registered && !_rawInputRegistered)
        {
            return;
        }

        _registered = false;
        _virtualKey = 0;
        _modifiers = 0;

        var handle = _source?.Handle ?? IntPtr.Zero;
        if (handle != IntPtr.Zero)
        {
            _ = UnregisterHotKey(handle, HotkeyId);
        }

        UnregisterRawInput();
        _source?.RemoveHook(WndProc);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Unregister();
        _source = null;
    }

    /// <summary>
    /// 解析组合键字符串。修饰键按位或，主键取最后一个可识别项。
    /// </summary>
    /// <param name="combination">形如 <c>Ctrl+Alt+U</c>。</param>
    /// <param name="modifiers">修饰键位掩码。</param>
    /// <param name="virtualKey">主键虚拟码。</param>
    internal static bool TryParse(string? combination, out uint? modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;

        if (string.IsNullOrWhiteSpace(combination))
        {
            return false;
        }

        var parts = combination.Split('+', StringSplitOptions.RemoveEmptyEntries
                                          | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        uint mask = 0;
        uint key = 0;

        foreach (var part in parts)
        {
            var token = part.Trim();
            if (token.Length == 0)
            {
                continue;
            }

            switch (token.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    mask |= ModControl;
                    continue;
                case "alt":
                    mask |= ModAlt;
                    continue;
                case "shift":
                    mask |= ModShift;
                    continue;
                case "win":
                case "super":
                case "windows":
                    mask |= ModWin;
                    continue;
            }

            if (NamedKeys.TryGetValue(token, out var named))
            {
                key = named;
                continue;
            }

            // F1 ~ F24
            if (token.Length is >= 2 and <= 3
                && (token[0] is 'f' or 'F')
                && int.TryParse(token[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var functionKey)
                && functionKey is >= 1 and <= 24)
            {
                key = (uint)(0x70 + functionKey - 1);
                continue;
            }

            if (token.Length == 1)
            {
                var character = char.ToUpperInvariant(token[0]);
                if (character is >= 'A' and <= 'Z' or >= '0' and <= '9')
                {
                    key = character;
                }
            }
        }

        if (key == 0)
        {
            return false;
        }

        modifiers = mask;
        virtualKey = key;
        return true;
    }

    /// <summary>
    /// 把 WPF 的按键转换成组合键里用的记号。不支持的键返回 null。
    /// </summary>
    /// <param name="key">WPF 按键。</param>
    internal static string? KeyToToken(System.Windows.Input.Key key)
    {
        if (key is >= System.Windows.Input.Key.A and <= System.Windows.Input.Key.Z)
        {
            return ((char)('A' + (key - System.Windows.Input.Key.A))).ToString();
        }

        if (key is >= System.Windows.Input.Key.D0 and <= System.Windows.Input.Key.D9)
        {
            return ((char)('0' + (key - System.Windows.Input.Key.D0))).ToString();
        }

        if (key is >= System.Windows.Input.Key.F1 and <= System.Windows.Input.Key.F24)
        {
            return "F" + ((key - System.Windows.Input.Key.F1) + 1).ToString(CultureInfo.InvariantCulture);
        }

        return key switch
        {
            System.Windows.Input.Key.Space => "Space",
            System.Windows.Input.Key.Tab => "Tab",
            System.Windows.Input.Key.Enter => "Enter",
            System.Windows.Input.Key.Escape => "Esc",
            System.Windows.Input.Key.Back => "Backspace",
            System.Windows.Input.Key.Delete => "Delete",
            System.Windows.Input.Key.Insert => "Insert",
            System.Windows.Input.Key.Home => "Home",
            System.Windows.Input.Key.End => "End",
            System.Windows.Input.Key.PageUp => "PageUp",
            System.Windows.Input.Key.PageDown => "PageDown",
            System.Windows.Input.Key.Left => "Left",
            System.Windows.Input.Key.Up => "Up",
            System.Windows.Input.Key.Right => "Right",
            System.Windows.Input.Key.Down => "Down",
            System.Windows.Input.Key.OemMinus => "-",
            System.Windows.Input.Key.OemPlus => "=",
            System.Windows.Input.Key.OemOpenBrackets => "[",
            System.Windows.Input.Key.OemCloseBrackets => "]",
            System.Windows.Input.Key.OemSemicolon => ";",
            System.Windows.Input.Key.OemQuotes => "'",
            System.Windows.Input.Key.OemComma => ",",
            System.Windows.Input.Key.OemPeriod => ".",
            System.Windows.Input.Key.OemQuestion => "/",
            System.Windows.Input.Key.OemPipe => "\\",
            System.Windows.Input.Key.OemTilde => "`",
            _ => null
        };
    }

    /// <summary>
    /// 把组合键规范成适合显示的文本，例如 <c>Ctrl + Alt + U</c>。
    /// </summary>
    internal static string Format(string? combination)
    {
        if (string.IsNullOrWhiteSpace(combination))
        {
            return string.Empty;
        }

        var display = new List<string>();
        foreach (var part in combination.Split('+', StringSplitOptions.RemoveEmptyEntries
                                                      | StringSplitOptions.TrimEntries))
        {
            var token = part.Trim();
            display.Add(token.ToLowerInvariant() switch
            {
                "ctrl" or "control" => "Ctrl",
                "alt" => "Alt",
                "shift" => "Shift",
                "win" or "super" or "windows" => "Win",
                _ => DisplayNames.TryGetValue(token, out var name)
                    ? name
                    : token.ToUpperInvariant()
            });
        }

        return string.Join(" + ", display);
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            RaisePressed();
            return IntPtr.Zero;
        }

        if (message == WmInput
            && _rawInputRegistered
            && _virtualKey != 0
            && TryReadRawKeyboard(lParam, out var virtualKey, out var isKeyUp)
            && virtualKey == _virtualKey
            && !isKeyUp
            && ModifiersMatch())
        {
            RaisePressed();
        }

        return IntPtr.Zero;
    }

    private void RaisePressed()
    {
        // 两条通道对同一次按键会各送一次，这里只认第一次。
        var now = Environment.TickCount64;
        if (now - _lastTriggerTicks < DedupeWindowMilliseconds)
        {
            return;
        }

        _lastTriggerTicks = now;
        Pressed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 用当前键盘状态核对修饰键，而不是自己维护一套按下/抬起的记账。
    ///
    /// Raw Input 只送「哪个键被按了」，不带修饰键状态；自己记账要处理左右 Shift/Ctrl、
    /// 键盘切换、焦点丢失等一堆边界，还容易漏键卡在「按下」状态。直接读实时状态更稳。
    /// </summary>
    private bool ModifiersMatch()
    {
        var control = IsKeyDown(VkControl);
        var alt = IsKeyDown(VkMenu);
        var shift = IsKeyDown(VkShift);
        var win = IsKeyDown(VkLWin) || IsKeyDown(VkRWin);

        return ((_modifiers & ModControl) != 0) == control
            && ((_modifiers & ModAlt) != 0) == alt
            && ((_modifiers & ModShift) != 0) == shift
            && ((_modifiers & ModWin) != 0) == win;
    }

    private static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static bool TryReadRawKeyboard(IntPtr rawInputHandle, out ushort virtualKey, out bool isKeyUp)
    {
        virtualKey = 0;
        isKeyUp = false;

        var headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
        var size = 0u;
        if (GetRawInputData(rawInputHandle, RidInput, IntPtr.Zero, ref size, headerSize) != 0 || size == 0)
        {
            return false;
        }

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(rawInputHandle, RidInput, buffer, ref size, headerSize) != size)
            {
                return false;
            }

            var header = Marshal.PtrToStructure<RawInputHeader>(buffer);
            if (header.Type != RimTypeKeyboard)
            {
                return false;
            }

            var keyboard = Marshal.PtrToStructure<RawKeyboard>(IntPtr.Add(buffer, (int)headerSize));

            // VKey 为 0 或 0xFF 时是伪键（部分键盘的扩展键），拿不到可靠虚拟码，忽略。
            if (keyboard.VKey is 0 or 0xFF)
            {
                return false;
            }

            virtualKey = keyboard.VKey;
            isKeyUp = (keyboard.Flags & RiKeyBreak) != 0;
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool TryRegisterRawInput(IntPtr handle)
    {
        var device = new RawInputDevice
        {
            UsagePage = HidUsagePageGeneric,
            Usage = HidUsageKeyboard,
            Flags = RidevInputSink,
            Target = handle
        };

        if (RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RawInputDevice>()))
        {
            return true;
        }

        AppLog.Warn(
            $"注册 Raw Input 失败（Win32 错误 {Marshal.GetLastPInvokeError()}），"
            + "热键在分身画面内可能不生效。");
        return false;
    }

    private void UnregisterRawInput()
    {
        if (!_rawInputRegistered)
        {
            return;
        }

        _rawInputRegistered = false;

        var device = new RawInputDevice
        {
            UsagePage = HidUsagePageGeneric,
            Usage = HidUsageKeyboard,
            Flags = RidevRemove,
            Target = IntPtr.Zero
        };

        _ = RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RawInputDevice>());
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(GlobalHotkeyService));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        public uint Type;
        public uint Size;
        public IntPtr Device;
        public IntPtr WParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawKeyboard
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VKey;
        public uint Message;
        public uint ExtraInformation;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(
        [In] RawInputDevice[] devices,
        uint numberOfDevices,
        uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(
        IntPtr rawInput,
        uint command,
        IntPtr data,
        ref uint size,
        uint headerSize);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
