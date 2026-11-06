using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DesktopTwin.Interop;
using Microsoft.Win32;
using DesktopTwin.Localization;

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
                return new Result(false, Locale.Get("Fix.NoAutoFix"));
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

        return new Result(true, Locale.Get("Fix.ServiceStarted"));
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
            return new Result(false, Locale.Format("Fix.StartTypeChangedButError", startError));
        }

        return new Result(true, Locale.Get("Fix.StartTypeChanged"));
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
            return new Result(false, Locale.Get("Fix.GroupPolicyOverrides"));
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
            return new Result(false, Locale.Get("Fix.RegistryReadBackNotZero"));
        }

        var restart = RestartTermService(cancellationToken);
        return restart.Success
            ? new Result(true, Locale.Get("Fix.RdpEnabledNeedRestart"))
            : new Result(false, Locale.Format("Fix.RdpRestoreFailed", restart.Message));
    }

    /// <summary>
    /// 还原被补丁接管的 TermService：备份 → 改回原生 DLL → 停用自动更新任务 → 重启服务。
    /// </summary>
    private static Result FixRdpWrapper(CancellationToken cancellationToken)
    {
        var current = ChildSessionEnvironmentCheck.ReadServiceDll();
        if (!ChildSessionEnvironmentCheck.IsServiceDllHijacked(current))
        {
            return new Result(true, Locale.Get("Fix.AlreadyNativeDll"));
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
            return new Result(false, Locale.Get("Fix.RegistryStillForeignDll"));
        }

        var taskDisabled = TryDisableRdpWrapperUpdateTask(out var taskMessage);
        var restart = RestartTermService(cancellationToken);

        if (!restart.Success)
        {
            return new Result(false, Locale.Format("Fix.RestoreFailed", restart.Message));
        }

        var message = Locale.Get("Fix.RestoredNative");
        message += taskDisabled
            ? Locale.Format("Fix.TaskDisabled", RdpWrapperUpdateTaskName)
            : Locale.Format("Fix.TaskNote", taskMessage);
        message += backupPath is null
            ? string.Empty
            : Locale.Format("Fix.BackupSaved", backupPath);
        message += Locale.Get("Fix.ConcurrentGone");

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
            return new Result(false, Locale.Get("Fix.RegistryStillTwo"));
        }

        return new Result(
            true,
            Locale.Get("Fix.HelloOnlyOff"));
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
                Locale.Get("Fix.SessionsRunning"));
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!ServiceNativeMethods.TryStop("TermService", out var stopError))
        {
            return new Result(false, Locale.Format("Fix.StopFailed", stopError));
        }

        if (!ServiceNativeMethods.TryStart("TermService", out var startError))
        {
            return new Result(false, Locale.Format("Fix.RestartFailed", startError));
        }

        return new Result(true, Locale.Get("Fix.ServiceRestarted"));
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
            message = Locale.Get("Fix.SchtasksMissing");
            return false;
        }

        if (!RunSchtasks(schtasks, $"/Query /TN \"{RdpWrapperUpdateTaskName}\"", out _))
        {
            message = Locale.Get("Fix.TaskNotFound");
            return true;
        }

        if (!RunSchtasks(schtasks, $"/Change /TN \"{RdpWrapperUpdateTaskName}\" /DISABLE", out var error))
        {
            message = Locale.Format("Fix.TaskDisableFailed", error);
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
                error = Locale.Format("Fix.RegistryOpenFailed", path);
                return false;
            }

            key.SetValue(name, value, RegistryValueKind.DWord);
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
                                              or System.Security.SecurityException
                                              or IOException)
        {
            error = Locale.Format("Fix.RegistryWriteFailed", exception.Message);
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
                error = Locale.Format("Fix.RegistryOpenFailed", path);
                return false;
            }

            key.SetValue(name, value, kind);
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
                                              or System.Security.SecurityException
                                              or IOException)
        {
            error = Locale.Format("Fix.RegistryWriteFailed", exception.Message);
            return false;
        }
    }
}
