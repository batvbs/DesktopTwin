using System.Collections.Generic;

namespace DesktopTwin.Localization;

/// <summary>
/// 界面文字的简体中文原文（同时也是回退值）。
///
/// 这里是**唯一来源**：程序里所有可本地化的文字都从这里取，
/// 导出的 <c>.lng</c> 模板也由它生成。新增界面文字时只需要在这里加一条，
/// 再在界面里用 <c>{loc:Loc 键名}</c> 或 <see cref="Localization.Get"/> 取用。
///
/// 键名约定：<c>区域.用途</c>，例如 <c>Main.Start</c>、<c>Menu.Diagnostics.Open</c>。
/// 键名一旦发布就不要改——改了会让已有的语言文件失效。
/// </summary>
internal static class Strings
{
    /// <summary>
    /// 全部内置文字，按界面上出现的顺序排列（导出模板的顺序就是这个顺序）。
    /// </summary>
    public static readonly IReadOnlyList<KeyValuePair<string, string>> All =
    [
        // ---- 主窗口 ----
        Pair("Main.Title", "桌面分身"),
        Pair("Main.Start", "启动"),
        Pair("Main.Start.ToolTip", "启动并连接桌面分身"),
        Pair("Main.Hide", "隐藏"),
        Pair("Main.Hide.ToolTip", "隐藏窗口并保持 RDP 连接"),
        Pair("Main.SwitchWindow", "切换窗口"),
        Pair("Main.SwitchWindow.ToolTip", "向桌面分身发送 Win+Tab"),
        Pair("Main.ControlCenter", "控制中心"),
        Pair("Main.Status.NotStarted", "桌面分身尚未启动"),
        Pair("Main.Status.NotStarted.Description", "点击“启动并连接”开始。"),

        // ---- 欢迎/连接引导 ----
        Pair("Startup.Connect", "启动并连接"),
        Pair("Startup.CopyDiagnostics", "复制环境信息"),
        Pair("Startup.Closing", "请稍候，关闭完成后窗口会自动隐藏。"),
        Pair("Startup.Hint.Switch", "连接成功后，此处会自动切换为分身桌面画面。"),
        Pair("Startup.Hint.FirstLogin", "第一次连接会显示 Windows 登录界面，请用当前账户登录；本地账户没有密码时请先设置密码。"),
        Pair("Startup.Hint.Close", "关闭窗口会注销分身会话，其中的程序都会被关闭。"),

        // ---- 控制中心菜单 ----
        Pair("Menu.Start", "启动"),
        Pair("Menu.Hide", "隐藏"),
        Pair("Menu.SwitchWindow", "切换窗口"),
        Pair("Menu.SelectTarget", "选择要在分身中运行的程序…"),
        Pair("Menu.LaunchTarget", "立即在分身中启动"),
        Pair("Menu.TargetProgram", "目标程序：{0}"),
        Pair("Menu.AutoLaunch", "连接成功后自动启动"),
        Pair("Menu.RunAsAdmin", "以管理员权限启动目标程序"),
        Pair("Menu.RunAsAdmin.ToolTip", "默认不勾选：以普通权限运行可以减少与反作弊机制的冲突。只有目标程序自身要求提权时才需要勾选。"),
        Pair("Menu.Resolution", "桌面分辨率"),
        Pair("Menu.Resolution.Custom", "自定义…"),
        Pair("Menu.Resolution.Custom.ToolTip", "输入任意宽高。当前分辨率会在状态栏显示。"),
        Pair("Menu.Window", "窗口"),
        Pair("Menu.Adaptive", "自适应缩放"),
        Pair("Menu.OneToOne", "1 : 1"),
        Pair("Menu.KeepAspectRatio", "保持宽高比"),
        Pair("Menu.Shortcuts", "快捷键"),
        Pair("Menu.SendSystemShortcuts", "系统组合键发送到桌面分身"),
        Pair("Menu.SendSystemShortcuts.ToolTip", "勾选后，Alt+Tab、Windows 键等系统组合键发送到桌面分身；取消勾选后在本机生效。切换时会自动重新连接 RDP。"),
        Pair("Menu.SendWinD", "发送 Win+D 显示桌面"),
        Pair("Menu.SendWinTab", "发送 Win+Tab 切换窗口"),

        // ---- UU 视角切换 ----
        Pair("Menu.UuSwitch.Enabled", "UU 视角切换（已启用 · {0}）"),
        Pair("Menu.UuSwitch.Disabled", "UU 视角切换"),
        Pair("Menu.UuSwitch", "启用 UU 视角切换（{0}）"),
        Pair("Menu.UuSwitch.ToolTip", "启用后可用全局热键在「UU 控制整台电脑」与「UU 控制桌面分身」之间切换。切换时会结束 UU 的串流端进程，让 UU 服务重新挑选会话；分身只是断开，不会注销。"),
        Pair("Menu.UuHotkey", "切换快捷键：{0}"),
        Pair("Menu.UuTrigger", "立即切换一次"),
        Pair("Menu.UuRepair", "修复 UU 串流（重启 UU 服务）"),
        Pair("Menu.UuRepair.ToolTip", "快速连续切换后，手机上的 UU 可能显示设备离线：切换需要结束串流端，短时间内连续结束会被 UU 服务当成连续崩溃而暂停重拉。点这里重启 UU 服务恢复，然后在手机上重新连接一次。"),

        // ---- 自动登录 ----
        Pair("Menu.AutoLogin.Enabled", "自动登录分身（已启用）"),
        Pair("Menu.AutoLogin.Disabled", "自动登录分身"),
        Pair("Menu.TargetProgram.None", "未选择"),
        Pair("Menu.AutoLogin", "自动登录（{0}）"),
        Pair("Menu.AutoLogin.Unnamed", "自动登录"),
        Pair("Menu.AutoLogin.Settings", "自动登录设置…"),
        Pair("Menu.AutoLogin.Settings.ToolTip", "在「本程序自动登录」与「Windows 系统自动登录」之间选择，并查看各自的步骤与影响范围。"),
        Pair("Menu.AutoLogin.Enable", "启用自动登录（本程序）"),
        Pair("Menu.AutoLogin.Enable.ToolTip", "连接分身时自动填入 Windows 登录凭据。密码用 DPAPI 加密保存在本机，只有当前账户能解开；若凭据框仍出现，程序最多尝试自动填写两次，之后交回手动输入。"),
        Pair("Menu.AutoLogin.SetPassword", "设置登录密码…"),
        Pair("Menu.AutoLogin.ClearPassword", "清除已保存的密码"),
        Pair("Menu.AutoLogin.System", "打开系统自动登录设置…"),
        Pair("Menu.AutoLogin.System.ToolTip", "等同于 Win+R 输入 netplwiz。取消勾选「要使用本计算机，用户必须输入用户名和密码」，点应用后输入账户密码（不是 PIN），重启生效。注意这会让本机开机即进桌面。"),

        // ---- 诊断 ----
        Pair("Menu.Diagnostics", "诊断"),
        Pair("Menu.Diagnostics.Open", "诊断与修复…"),
        Pair("Menu.Diagnostics.Open.ToolTip", "列出所有可能影响启动的环境问题，可自动修复的直接在面板里修。"),
        Pair("Menu.Diagnostics.CopyEnv", "复制环境信息"),
        Pair("Menu.Diagnostics.Logs", "打开日志目录"),
        Pair("Menu.Diagnostics.Settings", "打开设置目录"),
        Pair("Menu.Diagnostics.DisableChildSessions", "关闭 Child Sessions（恢复系统原状）"),

        // ---- 全屏顶栏 ----
        Pair("Fullscreen.Title", "桌面分身"),
        Pair("Fullscreen.Minimize", "最小化"),
        Pair("Fullscreen.Restore", "还原"),
        Pair("Fullscreen.Restore.ToolTip", "退出全屏，回到窗口模式"),
        Pair("Fullscreen.Close", "关闭"),
        Pair("Fullscreen.Pin", "固定显示这条工具栏（会记住）"),
        Pair("Fullscreen.Unpin", "取消固定（会记住）"),

        // ---- 托盘 ----
        Pair("Tray.Show", "显示主窗口"),
        Pair("Tray.Hide", "隐藏窗口"),
        Pair("Tray.Fullscreen", "全屏显示"),
        Pair("Tray.Exit", "退出"),
        Pair("Tray.ToolTip.Visible", "桌面分身（窗口已显示）"),
        Pair("Tray.ToolTip.Hidden", "桌面分身（窗口已隐藏）"),

        // ---- 通用按钮 ----
        Pair("Common.Ok", "确定"),
        Pair("Common.Continue", "继续"),
        Pair("Common.GotIt", "知道了"),
        Pair("Common.Cancel", "取消"),
        Pair("Common.Close", "关闭"),

        // ---- 视角切换的提示与结果 ----
        Pair("Uu.Switching.Title", "正在切换视角"),
        Pair("Uu.Switching.Message", "正在调整会话落点，请稍候…"),
        Pair("Uu.Done.Title", "视角切换完成"),
        Pair("Uu.Failed.Title", "视角切换失败"),
        Pair("Uu.Recovered.Title", "UU 串流已恢复"),
        Pair("Uu.Recovered.Message", "串流端已经重新拉起，在手机上重新连接一次即可进桌面。"),
        Pair("Uu.Repair.Title", "正在修复 UU 串流"),
        Pair("Uu.Repair.Message", "正在重启 UU 服务…"),
        Pair("Uu.Repair.Done", "UU 串流已修复"),
        Pair("Uu.Repair.Failed", "UU 串流修复失败"),
        Pair("Uu.Result.Busy", "上一次切换还没结束，已忽略本次。"),
        Pair("Uu.Result.Cancelled", "切换已取消。"),
        Pair("Uu.Result.Failed", "切换失败：{0}"),
        Pair("Uu.Result.DisconnectFailed", "分身会话没能断开，请稍后重试。"),
        Pair("Uu.Result.NotConnected", "分身没能连上。请在窗口里手动点「启动并连接」。"),
        Pair("Uu.Result.NoSession", "分身会话不存在，请在窗口里手动点「启动并连接」。"),
        Pair("Uu.Result.ToMain", "已切到「整台电脑」，分身已挂回后台，下次切回是秒切。"),
        Pair("Uu.Result.ToMain.NotRestored", "已切到「整台电脑」，但分身没能挂回后台，切回时会重新连接。"),
        Pair("Uu.Result.ToMain.NoStreamer", "已切到「整台电脑」。当前没有 UU 串流端，下次连入即是主桌面。"),
        Pair("Uu.Result.ToMain.StreamerStuck", "UU 串流端没能落到主桌面。程序会在后台自动重试拉起 UU 服务，也可以稍后在手机上重连一次。"),
        Pair("Uu.Result.ToChild", "已切回「桌面分身」，重新连一下 UU 就进游戏桌面。"),
        Pair("Uu.Result.ToChild.NoStreamer", "已切回「桌面分身」。当前没有 UU 串流端，下次连入即是游戏桌面。"),
        Pair("Uu.Result.ToChild.StreamerStuck", "UU 串流端还没落到分身。程序会在后台自动重试拉起 UU 服务，稍候在手机上重连一次即可。"),
        Pair("Uu.Result.ServiceRestarted", "UU 服务已重启，串流端已重新拉起。"),
        Pair("Uu.Result.ServiceRestarted.NoStreamer", "UU 服务已重启。请在手机上重新连接一次 UU（设备应该已经回到在线）。"),
        Pair("Uu.Result.ServiceRestartFailed", "重启 UU 服务失败（停止{0}、启动失败）。请在服务管理器里手动重启 GameViewerService。"),
    ];

    /// <summary>
    /// 内置文字条数。
    /// </summary>
    public static int Count => All.Count;

    /// <summary>
    /// 查内置中文原文。
    /// </summary>
    /// <param name="key">键。</param>
    /// <param name="text">原文。</param>
    public static bool TryGet(string key, out string text)
    {
        foreach (var entry in All)
        {
            if (string.Equals(entry.Key, key, System.StringComparison.OrdinalIgnoreCase))
            {
                text = entry.Value;
                return true;
            }
        }

        text = key;
        return false;
    }

    private static KeyValuePair<string, string> Pair(string key, string text) => new(key, text);
}
