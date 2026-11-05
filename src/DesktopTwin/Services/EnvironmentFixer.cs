using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DesktopTwin.Interop;
using Microsoft.Win32;

namespace DesktopTwin.Services;

/// <summary>
/// 环境问题的一键修复。
///
/// 与检测（<see cref="ChildSessionEnvironmentCheck"/>）分开实现，是为了让「看」和「改」
/// 各自可以被独立审查——这里的每个方法都会真实改动系统设置。
///
/// 几条硬约束（改这里之前请先读一遍）：
/// 1. 只改注册表 / 服务，不替用户重启或注销机器；
/// 2. 改之前备份原值，改之后回读确认；
/// 3. 重启服务之前检查有没有活动会话，有就先拒绝，避免把用户正在用的分身打断；
/// 4. 需要重启才生效的修复，必须在返回消息里说清楚。
/// </summary>
public static class EnvironmentFixer
{
    /// <summary>
    /// RDP 补丁自动更新任务名。实测由 autoupdate.bat -taskadd 以
    /// <c>/sc ONSTART /ru SYSTEM</c> 创建，不停用的话开机会把补丁写回去。
    /// </summary>
    private const string RdpWrapperUpdateTaskName = "RDP Wrapper Autoupdate";

    /// <summary>
    /// 修复结果。
    /// </summary>
    /// <param name="Success">是否成功。</param>
    /// <param name="Message">给用户看的一句话说明。</param>
    public sealed record Result(bool Success, string Message);

    /// <summary>
    /// 对指定问题执行一键修复。不支持自动修复的问题返回失败并说明原因。
    /// </summary>
    /// <param name="issueId">问题标识，见 <see cref="ChildSessionEnvironmentCheck.Collect"/>。</param>
    /// <param name="cancellationToken">取消标记。</param>
    public static Task<Result> FixAsync(string issueId, CancellationToken cancellationToken)
    {
        return Task.Run(() => Fix(issueId, cancellationToken), cancellationToken);
    }

    private static Result Fix(string issueId, CancellationToken cancellationToken)
    {
        switch (issueId)
        {
            case "termservice-stopped":
                return FixTermServiceStopped();

            case "termservice-disabled":
                return FixTermServiceDisabled();

            case "rdp-disabled":
                return FixRdpDisabled(cancellationToken);

            case "rdp-wrapper":
                return FixRdpWrapper(cancellationToken);

            case "hello-only":
                return FixHelloOnly();

            default:
                return new Result(false, "这个问题没有提供自动修复，请按展开后的说明手动处理。");
        }
    }

    /// <summary>
    /// 启动停止的 TermService。
    /// </summary>
    private static Result FixTermServiceStopped()
    {
        if (!ServiceNativeMethods.TryStart("TermService", out var error))
        {
            return new Result(false, error);
        }

        return new Result(true, "远程桌面服务已启动。");
    }

    /// <summary>
    /// 把被禁用的 TermService 改回「手动」并启动。
    /// </summary>
    private static Result FixTermServiceDisabled()
    {
        if (!ServiceNativeMethods.TrySetStartTypeToManual("TermService", out var configError))
        {
            return new Result(false, configError);
        }

        if (!ServiceNativeMethods.TryStart("TermService", out var startError))
        {
            return new Result(false, $"启动类型已改为手动，但{startError}");
        }

        return new Result(true, "TermService 启动类型已改为「手动」并已启动。");
    }

    /// <summary>
    /// 打开远程桌面主机：fDenyTSConnections 改 0，然后重启 TermService 让监听器生效。
    /// </summary>
    private static Result FixRdpDisabled(CancellationToken cancellationToken)
    {
        // 组策略下发时改本机值没有意义，直接拒绝，避免造成「改过了但没用」的错觉。
        var policyValue = ChildSessionEnvironmentCheck.ReadInt(
            RegistryHive.LocalMachine,
            ChildSessionEnvironmentCheck.TerminalServerPolicyRegistryPath,
            "fDenyTSConnections");
        if (policyValue is not null)
        {
            return new Result(false, "该设置由组策略下发，改本机注册表会被覆盖。请改用 gpedit.msc 或联系管理员。");
        }

        if (!TryWriteDword(
                ChildSessionEnvironmentCheck.TerminalServerRegistryPath,
                "fDenyTSConnections",
                0,
                out var writeError))
        {
            return new Result(false, writeError);
        }

        // 回读确认：写入成功不等于生效。
        var after = ChildSessionEnvironmentCheck.ReadInt(
            RegistryHive.LocalMachine,
            ChildSessionEnvironmentCheck.TerminalServerRegistryPath,
            "fDenyTSConnections");
        if (after != 0)
        {
            return new Result(false, "注册表写入后回读不是 0，修复未生效。");
        }

        var restart = RestartTermService(cancellationToken);
        return restart.Success
            ? new Result(true, "已开启远程桌面并重启服务。如果正在使用分身，需要重新点一次「启动」。")
            : new Result(false, "注册表已改回 0，但" + restart.Message);
    }

    /// <summary>
    /// 还原被补丁接管的 TermService：备份 → 改回原生 DLL → 停用自动更新任务 → 重启服务。
    /// </summary>
    private static Result FixRdpWrapper(CancellationToken cancellationToken)
    {
        var current = ChildSessionEnvironmentCheck.ReadServiceDll();
        if (!ChildSessionEnvironmentCheck.IsServiceDllHijacked(current))
        {
            return new Result(true, "当前已经是原生 termsrv.dll，无需修复。");
        }

        var backupPath = WriteBackup(current);

        if (!TryWriteString(
                ChildSessionEnvironmentCheck.TermServiceParametersRegistryPath,
                "ServiceDll",
                ChildSessionEnvironmentCheck.GetNativeServiceDllPath(),
                RegistryValueKind.ExpandString,
                out var writeError))
        {
            return new Result(false, writeError);
        }

        var after = ChildSessionEnvironmentCheck.ReadServiceDll();
        if (ChildSessionEnvironmentCheck.IsServiceDllHijacked(after))
        {
            return new Result(false, "注册表写入后回读仍是第三方 DLL，修复未生效。");
        }

        var taskDisabled = TryDisableRdpWrapperUpdateTask(out var taskMessage);
        var restart = RestartTermService(cancellationToken);

        if (!restart.Success)
        {
            return new Result(false, "ServiceDll 已还原，但" + restart.Message);
        }

        var message = "已还原为原生 termsrv.dll 并重启远程桌面服务。";
        message += taskDisabled
            ? "同时已停用计划任务「" + RdpWrapperUpdateTaskName + "」。"
            : "（" + taskMessage + "）";
        message += backupPath is null
            ? string.Empty
            : $"原值已备份到 {backupPath}。";
        message += "注意：多用户并发远程桌面的能力已随之失效。";

        return new Result(true, message);
    }

    /// <summary>
    /// 关闭「仅允许 Windows Hello 登录」。改完需要用户自己注销或重启才生效。
    /// </summary>
    private static Result FixHelloOnly()
    {
        if (!TryWriteDword(
                ChildSessionEnvironmentCheck.PasswordLessRegistryPath,
                "DevicePasswordLessBuildVersion",
                0,
                out var error))
        {
            return new Result(false, error);
        }

        var after = ChildSessionEnvironmentCheck.ReadInt(
            RegistryHive.LocalMachine,
            ChildSessionEnvironmentCheck.PasswordLessRegistryPath,
            "DevicePasswordLessBuildVersion");
        if (after == ChildSessionEnvironmentCheck.PasswordLessHelloOnlyValue)
        {
            return new Result(false, "注册表写入后回读仍是 2，修复未生效。");
        }

        return new Result(
            true,
            "已关闭「仅允许 Windows Hello 登录」。**必须注销或重启**才会生效——"
            + "凭据提供程序在登录时就已经确定，不重新登录不会刷新。");
    }

    /// <summary>
    /// 重启 TermService。先确认没有活动会话，避免打断正在用的分身。
    /// </summary>
    private static Result RestartTermService(CancellationToken cancellationToken)
    {
        if (HasActiveNonConsoleSession())
        {
            return new Result(
                false,
                "检测到还有正在运行的分身（或远程桌面）会话。请先关闭分身再重试，"
                + "否则重启服务会把里面的程序一起关掉。");
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!ServiceNativeMethods.TryStop("TermService", out var stopError))
        {
            return new Result(false, "停止远程桌面服务失败：" + stopError);
        }

        if (!ServiceNativeMethods.TryStart("TermService", out var startError))
        {
            return new Result(false, "服务已停止但重新启动失败：" + startError);
        }

        return new Result(true, "远程桌面服务已重启。");
    }

    /// <summary>
    /// 是否存在除 Console 之外的活动会话。
    /// </summary>
    private static bool HasActiveNonConsoleSession()
    {
        var console = ChildSessionNativeMethods.TryGetConsoleSessionId();

        foreach (var session in ChildSessionNativeMethods.EnumerateSessions())
        {
            if (session.SessionId == console)
            {
                continue;
            }

            if (session.SessionId == 0)
            {
                continue;
            }

            if (session.State == 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 停用 RDP 补丁的自动更新任务。
    /// </summary>
    /// <param name="message">失败原因或「任务不存在」的说明。</param>
    private static bool TryDisableRdpWrapperUpdateTask(out string message)
    {
        message = string.Empty;

        var schtasks = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "schtasks.exe");

        if (!File.Exists(schtasks))
        {
            message = "找不到 schtasks.exe，请手动删除计划任务";
            return false;
        }

        if (!RunSchtasks(schtasks, $"/Query /TN \"{RdpWrapperUpdateTaskName}\"", out _))
        {
            message = "未发现该计划任务，无需停用";
            return true;
        }

        if (!RunSchtasks(schtasks, $"/Change /TN \"{RdpWrapperUpdateTaskName}\" /DISABLE", out var error))
        {
            message = "停用计划任务失败：" + error;
            return false;
        }

        return true;
    }

    private static bool RunSchtasks(string executablePath, string arguments, out string output)
    {
        output = string.Empty;

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            if (process is null)
            {
                return false;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(15000);

            output = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            return process.ExitCode == 0;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
                                              or InvalidOperationException
                                              or IOException)
        {
            output = exception.Message;
            return false;
        }
    }

    /// <summary>
    /// 把当前的 ServiceDll 备份到设置目录。返回备份文件路径，失败返回 null。
    /// </summary>
    private static string? WriteBackup(string? serviceDll)
    {
        if (string.IsNullOrWhiteSpace(serviceDll))
        {
            return null;
        }

        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DesktopTwin");
            Directory.CreateDirectory(directory);

            var path = Path.Combine(directory, "service-dll-backup.txt");
            File.WriteAllText(
                path,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}{serviceDll}{Environment.NewLine}");
            return path;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("备份 ServiceDll 失败。", exception);
            return null;
        }
    }

    private static bool TryWriteDword(string path, string name, int value, out string error)
    {
        error = string.Empty;

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(path, writable: true);
            if (key is null)
            {
                error = $"无法打开注册表项 HKLM\\{path}（权限不足或路径不存在）。";
                return false;
            }

            key.SetValue(name, value, RegistryValueKind.DWord);
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
                                              or System.Security.SecurityException
                                              or IOException)
        {
            error = $"写入注册表失败：{exception.Message}";
            return false;
        }
    }

    private static bool TryWriteString(
        string path,
        string name,
        string value,
        RegistryValueKind kind,
        out string error)
    {
        error = string.Empty;

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(path, writable: true);
            if (key is null)
            {
                error = $"无法打开注册表项 HKLM\\{path}（权限不足或路径不存在）。";
                return false;
            }

            key.SetValue(name, value, kind);
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
                                              or System.Security.SecurityException
                                              or IOException)
        {
            error = $"写入注册表失败：{exception.Message}";
            return false;
        }
    }
}
