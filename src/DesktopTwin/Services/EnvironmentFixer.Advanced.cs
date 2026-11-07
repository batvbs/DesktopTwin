using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text;
using System.Threading;
using DesktopTwin.Interop;
using DesktopTwin.Localization;
using Microsoft.Win32;

namespace DesktopTwin.Services;

/// <summary>
/// 环境修复的第二组：处理 RDP 破解工具留下的残渣，以及 termsrv.dll 本体被改的情况。
///
/// 与主实现一致的三条约束：改前备份、改后回读、重启服务前检查有没有活动会话。
/// 额外两条针对本组的约束：
/// 4. 依赖服务只**追加**不覆盖（整键覆盖的还原脚本就是在这里把 Win11 的 StateRepository 冲掉的）；
/// 5. 安全参数只**补到默认**，绝不下调（已经加固到 3/4 的保持原样）。
/// </summary>
public static partial class EnvironmentFixer
{
    /// <summary>
    /// 把 ServiceDll 写回「原生路径 + REG_EXPAND_SZ」。
    ///
    /// 与 rdp-wrapper 的区别：那边是**路径被换成第三方 DLL**，这里是**值本身写坏了**
    /// （缺失，或类型被写成 REG_SZ）。后者的症状更隐蔽——服务起不来，
    /// 但 DLL 名字看起来完全正常。
    /// </summary>
    private static Result FixServiceDllType(CancellationToken cancellationToken)
    {
        var entry = ChildSessionEnvironmentCheck.GetServiceDllEntry();
        if (entry.Exists
            && entry.Kind == RegistryValueKind.ExpandString
            && !ChildSessionEnvironmentCheck.IsServiceDllHijacked(entry.Value))
        {
            return new Result(true, Locale.Get("Fix.DllType.AlreadyOk"));
        }

        var backupPath = WriteServiceDllState(entry);

        if (!TryWriteString(
                ChildSessionEnvironmentCheck.TermServiceParametersRegistryPath,
                "ServiceDll",
                ChildSessionEnvironmentCheck.GetNativeServiceDllPath(),
                RegistryValueKind.ExpandString,
                out var writeError))
        {
            return new Result(false, writeError);
        }

        var after = ChildSessionEnvironmentCheck.GetServiceDllEntry();
        if (!after.Exists || after.Kind != RegistryValueKind.ExpandString)
        {
            return new Result(false, Locale.Get("Fix.DllType.StillWrong"));
        }

        // 模块在服务启动时加载，改完必须重启服务才生效。
        var restart = RestartTermService(cancellationToken);
        if (!restart.Success)
        {
            return new Result(false, Locale.Format("Fix.DllType.NeedRestart", restart.Message));
        }

        var message = Locale.Get("Fix.DllType.Restored");
        if (backupPath is not null)
        {
            message += Locale.Format("Fix.BackupSaved", backupPath);
        }

        return new Result(true, message);
    }

    /// <summary>
    /// 恢复被整键覆盖的 TermService 服务键字段。
    /// </summary>
    private static Result FixServiceKey(CancellationToken cancellationToken)
    {
        var deviations = ChildSessionEnvironmentCheck.GetServiceKeyDeviations();
        if (deviations.Count == 0)
        {
            return new Result(true, Locale.Get("Fix.ServiceKey.AlreadyOk"));
        }

        var backupPath = WriteServiceKeySnapshot(deviations);
        var failures = new List<string>();

        foreach (var deviation in deviations)
        {
            var ok = deviation.Field switch
            {
                "ImagePath" => TryWriteString(
                    ChildSessionEnvironmentCheck.TermServiceKeyPath,
                    "ImagePath",
                    @"%SystemRoot%\System32\svchost.exe -k NetworkService",
                    RegistryValueKind.ExpandString,
                    out _),

                "ObjectName" => TryWriteString(
                    ChildSessionEnvironmentCheck.TermServiceKeyPath,
                    "ObjectName",
                    @"NT AUTHORITY\NetworkService",
                    RegistryValueKind.ExpandString,
                    out _),

                "DependOnService" => TryAppendServiceDependencies(out _),

                _ => true
            };

            if (!ok)
            {
                failures.Add(deviation.Field);
            }
        }

        if (failures.Count > 0)
        {
            return new Result(false, Locale.Format("Fix.ServiceKey.PartialFailed", string.Join(", ", failures)));
        }

        var restart = RestartTermService(cancellationToken);
        if (!restart.Success)
        {
            return new Result(false, Locale.Format("Fix.ServiceKey.NeedRestart", restart.Message));
        }

        var message = Locale.Get("Fix.ServiceKey.Restored");
        if (backupPath is not null)
        {
            message += Locale.Format("Fix.BackupSaved", backupPath);
        }

        return new Result(true, message);
    }

    /// <summary>
    /// 清理 RDP 破解工具的持久化项：计划任务导出 XML 后删除，自启项备份后删值。
    ///
    /// 先断持久化再改配置，否则这些任务会在下次开机把 DLL 写回去——
    /// 这是「修好又坏」的唯一成因。
    /// </summary>
    private static Result FixToolPersistence()
    {
        var hits = ChildSessionEnvironmentCheck.GetToolPersistenceHits();
        if (hits.Count == 0)
        {
            return new Result(true, Locale.Get("Fix.Persistence.Nothing"));
        }

        var backupDirectory = CreateToolBackupDirectory();
        var removedTasks = 0;
        var removedValues = 0;
        var failures = new List<string>();

        foreach (var hit in hits)
        {
            if (hit.Kind == "task")
            {
                if (RemoveScheduledTask(hit.TaskPath, backupDirectory))
                {
                    removedTasks++;
                }
                else
                {
                    failures.Add(hit.Target);
                }

                continue;
            }

            if (RemoveRunValue(hit, backupDirectory))
            {
                removedValues++;
            }
            else
            {
                failures.Add(hit.Target);
            }
        }

        ChildSessionEnvironmentCheck.InvalidateScanCache();

        if (failures.Count > 0)
        {
            return new Result(
                false,
                Locale.Format("Fix.Persistence.PartialFailed", string.Join("；", failures)));
        }

        return new Result(
            true,
            Locale.Format("Fix.Persistence.Removed", removedTasks, removedValues, backupDirectory));
    }

    /// <summary>
    /// 把被降低的 RDP 安全参数补回系统默认值。只补不降。
    /// </summary>
    private static Result FixRdpSecurity()
    {
        var downgrades = ChildSessionEnvironmentCheck.GetRdpSecurityDowngrades();
        if (downgrades.Count == 0)
        {
            return new Result(true, Locale.Get("Fix.RdpSecurity.AlreadyOk"));
        }

        var failures = new List<string>();
        foreach (var downgrade in downgrades)
        {
            if (!TryWriteDword(
                    ChildSessionEnvironmentCheck.RdpTcpRegistryPath,
                    downgrade.Name,
                    downgrade.Expect,
                    out _))
            {
                failures.Add(downgrade.Name);
            }
        }

        ChildSessionEnvironmentCheck.InvalidateScanCache();

        if (failures.Count > 0)
        {
            return new Result(false, Locale.Format("Fix.RdpSecurity.PartialFailed", string.Join(", ", failures)));
        }

        // 这几项在远程桌面服务下次启动时生效；这里刻意不重启服务，
        // 免得为了一个安全参数的调整把用户正在用的分身打断。
        return new Result(true, Locale.Get("Fix.RdpSecurity.Restored"));
    }

    /// <summary>
    /// 修复被直接打过补丁（或丢失）的 termsrv.dll。
    ///
    /// 判定用 <c>sfc /verifyfile</c>（见 ChildSessionEnvironmentCheck.Termsrv.cs），
    /// 所以修复也用同一把尺子验收：**复制完必须复核通过才算成功**，
    /// 否则回滚原文件再交给 sfc —— 不能因为「看起来复制成功了」就宣布修好。
    ///
    /// 顺序：先试工具自己留下的原版备份（秒级），没有可用备份才跑 sfc /scannow（5~15 分钟）。
    /// </summary>
    private static Result FixTermsrvDll(CancellationToken cancellationToken)
    {
        var state = ChildSessionEnvironmentCheck.GetTermsrvState();
        if (state.Exists && state.Verified && state.Intact)
        {
            return new Result(true, Locale.Get("Fix.Termsrv.AlreadyOk"));
        }

        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var termsrvPath = Path.Combine(system32, "termsrv.dll");

        // 1) 用工具自己的备份还原，但必须复核通过
        foreach (var backupName in state.Backups)
        {
            var backupPath = Path.Combine(system32, backupName);
            if (!File.Exists(backupPath))
            {
                continue;
            }

            var restore = TryRestoreTermsrvFromBackup(termsrvPath, backupPath, cancellationToken);
            if (restore is not null)
            {
                return restore;
            }
        }

        // 2) 交给 sfc
        cancellationToken.ThrowIfCancellationRequested();

        var sfcResult = RunSfcScannow(cancellationToken);
        var after = ChildSessionEnvironmentCheck.VerifyTermsrvNow();

        if (after.Exists && after.Verified && after.Intact)
        {
            return new Result(true, Locale.Format("Fix.Termsrv.RepairedBySfc", sfcResult));
        }

        return new Result(false, Locale.Format("Fix.Termsrv.SfcNotFixed", sfcResult));
    }

    /// <summary>
    /// 用备份覆盖 termsrv.dll，覆盖后用 sfc 复核。复核不通过则回滚并返回 null，让调用方继续走 sfc。
    /// </summary>
    private static Result? TryRestoreTermsrvFromBackup(
        string termsrvPath,
        string backupPath,
        CancellationToken cancellationToken)
    {
        if (HasActiveNonConsoleSession())
        {
            // 覆盖正在被服务使用的 DLL 要停服务；有活动会话时宁可不动。
            return new Result(false, Locale.Get("Fix.SessionsRunning"));
        }

        var safetyCopy = Path.Combine(
            GetDataDirectory(),
            "termsrv.dll.replaced-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));

        try
        {
            if (File.Exists(termsrvPath))
            {
                File.Copy(termsrvPath, safetyCopy, overwrite: true);
            }

            ServiceNativeMethods.TryStop("TermService", out _);
            cancellationToken.ThrowIfCancellationRequested();
            File.Copy(backupPath, termsrvPath, overwrite: true);
            ServiceNativeMethods.TryStart("TermService", out _);

            var after = ChildSessionEnvironmentCheck.VerifyTermsrvNow();
            if (after.Exists && after.Verified && after.Intact)
            {
                return new Result(
                    true,
                    Locale.Format("Fix.Termsrv.RestoredFromBackup", Path.GetFileName(backupPath), safetyCopy));
            }

            // 这个备份不是原版：回滚，别把系统留在更糟的状态。
            if (File.Exists(safetyCopy))
            {
                ServiceNativeMethods.TryStop("TermService", out _);
                File.Copy(safetyCopy, termsrvPath, overwrite: true);
                ServiceNativeMethods.TryStart("TermService", out _);
            }

            AppLog.Warn($"备份 {Path.GetFileName(backupPath)} 未通过 sfc 校验，已回滚。");
            return null;
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or SecurityException)
        {
            AppLog.Warn("从备份还原 termsrv.dll 失败。", exception);
            ServiceNativeMethods.TryStart("TermService", out _);
            return null;
        }
    }

    /// <summary>
    /// 运行 <c>sfc /scannow</c>，返回给用户看的简短结果。
    /// </summary>
    private static string RunSfcScannow(CancellationToken cancellationToken)
    {
        try
        {
            var sfc = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "sfc.exe");

            if (!File.Exists(sfc))
            {
                return Locale.Get("Fix.Termsrv.SfcMissing");
            }

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = sfc,
                Arguments = "/scannow",
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process is null)
            {
                return Locale.Get("Fix.Termsrv.SfcFailed");
            }

            // sfc 通常 5~15 分钟；给足时间，期间允许取消。
            var timeout = TimeSpan.FromMinutes(30);
            var deadline = DateTime.UtcNow + timeout;
            while (!process.WaitForExit(2000))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (Exception exception) when (exception is InvalidOperationException
                                                          or System.ComponentModel.Win32Exception)
                    {
                        // 进程刚好退出。
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (DateTime.UtcNow > deadline)
                {
                    return Locale.Get("Fix.Termsrv.SfcTimeout");
                }
            }

            return process.ExitCode switch
            {
                0 => Locale.Get("Fix.Termsrv.SfcClean"),
                1 => Locale.Get("Fix.Termsrv.SfcRepaired"),
                _ => Locale.Get("Fix.Termsrv.SfcCouldNotRepair")
            };
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                              or System.ComponentModel.Win32Exception
                                              or IOException)
        {
            AppLog.Warn("运行 sfc 失败。", exception);
            return Locale.Get("Fix.Termsrv.SfcFailed");
        }
    }

    // ---------------- 辅助 ----------------

    /// <summary>
    /// 追加缺失的服务依赖（RPCSS / StateRepository），保留本机已有的其它依赖。
    /// </summary>
    private static bool TryAppendServiceDependencies(out string error)
    {
        error = string.Empty;

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(ChildSessionEnvironmentCheck.TermServiceKeyPath, writable: true);
            if (key is null)
            {
                error = Locale.Format("Fix.RegistryOpenFailed", ChildSessionEnvironmentCheck.TermServiceKeyPath);
                return false;
            }

            var current = key.GetValue("DependOnService") as string[] ?? Array.Empty<string>();
            var wanted = new List<string>(current);

            void Ensure(string name)
            {
                foreach (var existing in wanted)
                {
                    if (string.Equals(existing, name, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }

                wanted.Add(name);
            }

            Ensure("RPCSS");

            using (var stateRepository = baseKey.OpenSubKey(
                       @"SYSTEM\CurrentControlSet\Services\StateRepository"))
            {
                if (stateRepository is not null)
                {
                    Ensure("StateRepository");
                }
            }

            if (wanted.Count == current.Length)
            {
                return true;
            }

            key.SetValue("DependOnService", wanted.ToArray(), RegistryValueKind.MultiString);
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
                                              or SecurityException
                                              or IOException)
        {
            error = Locale.Format("Fix.RegistryWriteFailed", exception.Message);
            return false;
        }
    }

    /// <summary>
    /// 把当前 ServiceDll 的状态（含类型）写进备份文件。
    /// </summary>
    private static string? WriteServiceDllState(ChildSessionEnvironmentCheck.ServiceDllEntry entry)
    {
        try
        {
            var directory = GetDataDirectory();
            var path = Path.Combine(directory, "service-dll-backup.txt");
            File.WriteAllText(
                path,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}" +
                (entry.Exists
                    ? $"Value={entry.Value}{Environment.NewLine}Kind={entry.Kind}{Environment.NewLine}"
                    : "<值不存在>" + Environment.NewLine));
            return path;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("备份 ServiceDll 状态失败。", exception);
            return null;
        }
    }

    /// <summary>
    /// 把服务键当前值导出成文本，便于手工回滚。
    /// </summary>
    private static string? WriteServiceKeySnapshot(IReadOnlyList<ChildSessionEnvironmentCheck.ServiceKeyDeviation> deviations)
    {
        try
        {
            var directory = GetDataDirectory();
            var path = Path.Combine(directory, "termservice-key-backup.txt");
            var builder = new StringBuilder();
            builder.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            foreach (var deviation in deviations)
            {
                builder.AppendLine($"{deviation.Field}: {deviation.Current}  ->  {deviation.Expect}");
            }

            File.WriteAllText(path, builder.ToString());
            return path;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("备份 TermService 服务键失败。", exception);
            return null;
        }
    }

    private static string GetDataDirectory()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DesktopTwin");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string CreateToolBackupDirectory()
    {
        var directory = Path.Combine(
            GetDataDirectory(),
            "rdp-tool-backup",
            DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>
    /// 导出一份任务 XML 再删除任务。
    /// </summary>
    private static bool RemoveScheduledTask(string taskPath, string backupDirectory)
    {
        if (string.IsNullOrWhiteSpace(taskPath))
        {
            return false;
        }

        var schtasks = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "schtasks.exe");

        if (!File.Exists(schtasks))
        {
            return false;
        }

        try
        {
            var query = RunProcess(schtasks, $"/Query /TN \"{taskPath}\" /XML");
            if (query.Output is not null)
            {
                var fileName = taskPath.Trim('\\').Replace('\\', '_') + ".xml";
                File.WriteAllText(Path.Combine(backupDirectory, fileName), query.Output, Encoding.UTF8);
            }

            // 删除必须看退出码：不看的话删失败也报「已清理」，面板刷新后问题还在，
            // 用户会以为修复本身是坏的。备份失败不拦删除（XML 已经尽力导出了）。
            var delete = RunProcess(schtasks, $"/Delete /TN \"{taskPath}\" /F");
            return delete.ExitCode == 0;
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                              or System.ComponentModel.Win32Exception
                                              or IOException
                                              or UnauthorizedAccessException)
        {
            AppLog.Warn($"删除计划任务 {taskPath} 失败。", exception);
            return false;
        }
    }

    /// <summary>
    /// 备份并删除一处自启项。
    /// </summary>
    private static bool RemoveRunValue(
        ChildSessionEnvironmentCheck.ToolPersistenceHit hit,
        string backupDirectory)
    {
        try
        {
            var root = hit.RegistryRoot switch
            {
                "HKCU" => Registry.CurrentUser,
                "HKU" => Registry.Users,
                _ => Registry.LocalMachine
            };

            using var key = root.OpenSubKey(hit.RegistryPath, writable: true);
            if (key is null)
            {
                return false;
            }

            var data = key.GetValue(hit.ValueName) as string ?? string.Empty;

            // 原文留一份，万一删错了还能手工加回去。
            var fileName = MakeSafeFileName(hit.Target) + ".txt";
            File.WriteAllText(
                Path.Combine(backupDirectory, fileName),
                hit.Target + Environment.NewLine + data + Environment.NewLine,
                Encoding.UTF8);

            key.DeleteValue(hit.ValueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception exception) when (exception is SecurityException
                                              or UnauthorizedAccessException
                                              or IOException)
        {
            AppLog.Warn($"删除自启项 {hit.Target} 失败。", exception);
            return false;
        }
    }

    private static string MakeSafeFileName(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            builder.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), ch) >= 0 ? '_' : ch);
        }

        return builder.ToString();
    }

    /// <summary>
    /// 跑一个外部命令，返回（标准输出，退出码）。跑不起来或超时返回退出码 -1。
    /// </summary>
    private static (string? Output, int ExitCode) RunProcess(string fileName, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            });

            if (process is null)
            {
                return (null, -1);
            }

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(30_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception exception) when (exception is InvalidOperationException
                                                      or System.ComponentModel.Win32Exception)
                {
                    // 进程刚好退出。
                }

                return (output, -1);
            }

            return (output, process.ExitCode);
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                              or System.ComponentModel.Win32Exception
                                              or IOException)
        {
            AppLog.Warn($"运行 {Path.GetFileName(fileName)} 失败。", exception);
            return (null, -1);
        }
    }
}
