using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DesktopTwin.Localization;

namespace DesktopTwin.Services;

/// <summary>
/// 全局热键。用 <c>RegisterHotKey</c> 注册到主窗口的 HWND 上。
///
/// 挂在主窗口而不是自己建消息窗口：窗口被隐藏时 HWND 依然存在，
/// 热键照常生效，不需要额外的生命周期管理。
/// </summary>
internal sealed class GlobalHotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 0x4454;

    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;

    /// <summary>
    /// 长按不重复触发。没有它，按住热键会连续触发几十次切换。
    /// </summary>
    private const uint ModNoRepeat = 0x4000;

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

    private HwndSource? _source;    private bool _registered;
    private bool _disposed;

    /// <summary>
    /// 热键被按下。
    /// </summary>
    public event EventHandler? Pressed;

    /// <summary>
    /// 当前是否注册成功。
    /// </summary>
    public bool IsRegistered => _registered;

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
            error = Locale.Format("Hotkey.Error.Unrecognized", combination);
            return false;
        }

        if (!modifiers.HasValue || modifiers.Value == 0)
        {
            // 不带修饰键的全局热键会吃掉整个系统的普通按键，绝对不允许。
            error = Locale.Get("Hotkey.Error.NeedModifier");
            return false;
        }

        Unregister();

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            error = Locale.Get("Hotkey.Error.NoWindow");
            return false;
        }

        _source ??= HwndSource.FromHwnd(handle);
        if (_source is null)
        {
            error = Locale.Get("Hotkey.Error.NoMessageSource");
            return false;
        }

        _source.AddHook(WndProc);

        if (!RegisterHotKey(handle, HotkeyId, modifiers.Value | ModNoRepeat, virtualKey))
        {
            var code = Marshal.GetLastPInvokeError();
            _source.RemoveHook(WndProc);
            error = code == 1409
                ? Locale.Format("Hotkey.Error.Occupied", Format(combination))
                : Locale.Format("Hotkey.Error.RegisterFailed", code);
            return false;
        }

        _registered = true;
        return true;
    }

    /// <summary>
    /// 注销热键。
    /// </summary>
    public void Unregister()
    {
        if (!_registered)
        {
            return;
        }

        _registered = false;
        var handle = _source?.Handle ?? IntPtr.Zero;
        if (handle != IntPtr.Zero)
        {
            _ = UnregisterHotKey(handle, HotkeyId);
        }

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
            Pressed?.Invoke(this, EventArgs.Empty);
        }

        return IntPtr.Zero;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(GlobalHotkeyService));
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
}
