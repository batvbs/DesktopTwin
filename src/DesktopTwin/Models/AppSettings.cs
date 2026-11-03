using System;
using System.Collections.Generic;

namespace DesktopTwin.Models;

/// <summary>
/// 应用全部可持久化设置。整体写入 <c>%AppData%\DesktopTwin\settings.json</c>。
/// </summary>
public sealed class AppSettings
{
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
    /// 分身桌面的逻辑分辨率宽度，默认 1920。
    /// </summary>
    public int DesktopWidth { get; set; } = 1920;

    /// <summary>
    /// 分身桌面的逻辑分辨率高度，默认 1080。
    /// </summary>
    public int DesktopHeight { get; set; } = 1080;

    /// <summary>
    /// RDP 画面是否自适应窗口。关闭时按 1:1 像素显示。
    /// </summary>
    public bool SmartSizing { get; set; } = true;

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
