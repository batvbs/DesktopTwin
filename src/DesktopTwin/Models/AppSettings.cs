using System;
using System.Collections.Generic;

namespace DesktopTwin.Models;

/// <summary>
/// 应用全部可持久化设置。整体写入 <c>%AppData%\DesktopTwin\settings.json</c>。
/// </summary>
public sealed class AppSettings
{
    /// <summary>
    /// 当前设置文件的结构版本。新增需要迁移的默认值变更时递增。
    /// </summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// 设置文件写入时的结构版本。旧文件里没有这一项，反序列化后为 0。
    /// </summary>
    public int SettingsVersion { get; set; }

    /// <summary>
    /// 桌面分身会话相关设置。
    /// </summary>
    public SessionSettings Session { get; set; } = new();

    /// <summary>
    /// 在分身中启动程序相关设置。
    /// </summary>
    public LaunchSettings Launch { get; set; } = new();

    /// <summary>
    /// 窗口外观相关设置。
    /// </summary>
    public WindowSettings Window { get; set; } = new();

    /// <summary>
    /// 与外部工具联动、以及自动登录相关的可选设置。
    /// </summary>
    public AutomationSettings Automation { get; set; } = new();

    /// <summary>
    /// 用户是否已确认过「本程序会修改系统级 Child Sessions 开关」的首次运行提示。
    /// 未确认前不会真正启动分身，避免用户在不了解后果的情况下改动系统设置。
    /// </summary>
    public bool ChildSessionNoticeAccepted { get; set; }
}

/// <summary>
/// 桌面分身会话设置。
/// </summary>
public sealed class SessionSettings
{
    /// <summary>
    /// 分身桌面宽度的下限。再小的话 Windows 桌面和登录界面会显示不全。
    /// </summary>
    public const int MinDesktopWidth = 640;

    /// <summary>
    /// 分身桌面宽度的上限。8K 已经远超 RDP 的实用范围，再大只会拖慢编码。
    /// </summary>
    public const int MaxDesktopWidth = 7680;

    /// <summary>
    /// 分身桌面高度的下限。
    /// </summary>
    public const int MinDesktopHeight = 480;

    /// <summary>
    /// 分身桌面高度的上限。
    /// </summary>
    public const int MaxDesktopHeight = 4320;

    /// <summary>
    /// 判断一组尺寸是否在允许范围内。
    /// </summary>
    public static bool IsValidDesktopSize(int width, int height) =>
        width >= MinDesktopWidth && width <= MaxDesktopWidth
        && height >= MinDesktopHeight && height <= MaxDesktopHeight;

    /// <summary>
    /// 分身桌面的逻辑分辨率宽度，默认 1920。
    /// </summary>
    public int DesktopWidth { get; set; } = 1920;

    /// <summary>
    /// 分身桌面的逻辑分辨率高度，默认 1080。
    /// </summary>
    public int DesktopHeight { get; set; } = 1080;

    /// <summary>
    /// RDP 画面是否自适应窗口。关闭时按 1:1 像素显示。
    ///
    /// 默认关闭（1:1）：分身桌面通常是 1920×1080，而窗口大小取决于屏幕与用户拖动，
    /// 自适应缩放会把画面重采样，文字发虚。1:1 是清晰的那一档，需要铺满时再手动切。
    /// </summary>
    public bool SmartSizing { get; set; }

    /// <summary>
    /// 拖动窗口边缘时是否保持 16:9 宽高比。
    /// </summary>
    public bool KeepAspectRatio { get; set; } = true;

    /// <summary>
    /// Alt+Tab、Windows 键等系统组合键是否发送到分身。切换需重连 RDP。
    /// </summary>
    public bool SendSystemShortcutsToRemote { get; set; } = true;

    /// <summary>
    /// 是否关闭分身声音。切换需重连 RDP。
    /// </summary>
    public bool AudioMuted { get; set; }

    /// <summary>
    /// 观看模式：仅显示画面，本机键鼠不会进入分身。
    /// </summary>
    public bool WatchMode { get; set; }

    /// <summary>
    /// 窗口是否置顶。
    /// </summary>
    public bool Topmost { get; set; }
}

/// <summary>
/// 在分身会话中启动目标程序的设置。
/// </summary>
public sealed class LaunchSettings
{
    /// <summary>
    /// 连接成功后自动启动的程序路径。
    /// </summary>
    public string? TargetPath { get; set; }

    /// <summary>
    /// 启动目标程序时使用的命令行参数。
    /// </summary>
    public string TargetArguments { get; set; } = string.Empty;

    /// <summary>
    /// 连接成功后是否自动启动目标程序。
    /// </summary>
    public bool AutoLaunchOnConnect { get; set; } = true;

    /// <summary>
    /// 是否以管理员权限启动目标程序。
    ///
    /// 默认关闭：以普通权限运行能减少与反作弊、账号安全机制的冲突。
    /// 只有当目标程序自身要求提权（例如需要写入受保护目录）时才需要打开。
    /// </summary>
    public bool RunAsAdministrator { get; set; }

    /// <summary>
    /// 「连接成功后自动启动 + 以管理员权限启动」的一次性授权凭据（DPAPI 加密的 Base64）。
    ///
    /// 自动 + 提权等于「只要改一下 settings.json 就能在本机管理员上下文里执行任意程序」，
    /// 所以默认不自动执行。但这两个开关同时勾选本身就是用户的明确意图——
    /// 于是改成「用户在界面上确认一次，程序记住这次授权」：授权与目标路径、参数绑定，
    /// 之后改了目标程序就要重新确认，不会变成一张可以随 settings.json 一起改的白条。
    /// </summary>
    public string? ElevatedAutoLaunchConsent { get; set; }

    /// <summary>
    /// 最近使用过的目标程序路径，最新的排在最前。
    /// </summary>
    public List<string> RecentTargets { get; set; } = [];
}

/// <summary>
/// 窗口外观设置。
/// </summary>
public sealed class WindowSettings
{
    /// <summary>
    /// 普通窗口模式下的窗口位置（物理像素）。
    /// </summary>
    public WindowPosition? NormalPosition { get; set; }

    /// <summary>
    /// 小窗模式下的窗口位置（物理像素）。
    /// </summary>
    public WindowPosition? SmallPosition { get; set; }

    /// <summary>
    /// 上次退出时是否处于小窗模式。
    /// </summary>
    public bool SmallWindowMode { get; set; }

    /// <summary>
    /// 全屏模式下顶部的悬浮按钮栏是否固定显示。
    ///
    /// 默认 false：只在鼠标移到屏幕顶端、以及刚进入全屏时显示 10 秒。
    /// 用户点过图钉后记下来，之后每次进全屏都保持固定。
    /// </summary>
    public bool FullscreenBarPinned { get; set; }
}

/// <summary>
/// 与外部工具联动、以及自动登录相关的可选设置。
///
/// 这里的每一项都默认关闭：它们要么会结束本机别的进程（UU 视角切换），
/// 要么会保存登录凭据（自动登录），必须由用户主动打开。
/// </summary>
public sealed class AutomationSettings
{
    /// <summary>
    /// 是否启用 UU 视角切换热键。
    /// </summary>
    public bool UuViewSwitchEnabled { get; set; }

    /// <summary>
    /// UU 视角切换的全局热键，形如 <c>Ctrl+Alt+U</c>。
    /// </summary>
    public string UuViewSwitchHotkey { get; set; } = "Ctrl+Alt+U";

    /// <summary>
    /// 是否启用分身自动登录。
    /// </summary>
    public bool AutoLoginEnabled { get; set; }

    /// <summary>
    /// 自动登录使用的用户名。留空表示使用当前 Windows 账户。
    /// </summary>
    public string? AutoLoginUserName { get; set; }

    /// <summary>
    /// 各账户的自动登录密码：键是用户名（不区分大小写），值是 DPAPI 加密后的 Base64。
    ///
    /// 为什么是字典而不是单个密码：本机的「桌面分身」可以换不同账户登录
    /// （例如主号挂游戏、小号跑脚本）。每个账户的密码都要单独存一份，
    /// 连接时按「当前要登录的账户」取对应那一份——否则换账户后要么登录失败，
    /// 要么被迫把上一个账户的密码覆盖掉。
    ///
    /// DPAPI 是 CurrentUser 作用域：这份数据只有当前 Windows 账户能解开，
    /// 拷到别的机器或别的账户下都无效。
    /// </summary>
    public Dictionary<string, string> AutoLoginPasswords { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 仅用于把 0.2.3 测试版的「单账户密码」搬到 <see cref="AutoLoginPasswords"/>。
    ///
    /// 读出来就立刻迁移并清空，之后不再写。测试版用户升级时不必重新输入一次密码。
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("AutoLoginPassword")]
    public string? LegacyAutoLoginPassword { get; set; }
}

/// <summary>
/// 窗口左上角坐标。
/// </summary>
public sealed class WindowPosition
{
    /// <summary>
    /// 创建空位置。
    /// </summary>
    public WindowPosition()
    {
    }

    /// <summary>
    /// 创建指定坐标的位置。
    /// </summary>
    public WindowPosition(int left, int top)
    {
        Left = left;
        Top = top;
    }

    /// <summary>
    /// 水平坐标。
    /// </summary>
    public int Left { get; set; }

    /// <summary>
    /// 垂直坐标。
    /// </summary>
    public int Top { get; set; }

    /// <summary>
    /// 判断两个位置是否相同。
    /// </summary>
    public bool Matches(WindowPosition? other)
    {
        return other is not null && other.Left == Left && other.Top == Top;
    }
}
