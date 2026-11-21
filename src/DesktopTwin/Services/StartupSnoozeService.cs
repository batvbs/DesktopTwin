using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace DesktopTwin.Services;

/// <summary>
/// 关停 / 恢复「开机自启动项」的结果。
/// </summary>
/// <param name="Success">流程是否走完（个别项关不掉不算失败，会出现在 <see cref="Warnings"/> 里）。</param>
/// <param name="Message">给用户看的结论。</param>
/// <param name="Warnings">关不掉的项、以及需要注意的提示。</param>
public sealed record StartupSnoozeResult(
    bool Success,
    string Message,
    IReadOnlyList<string> Warnings)
{
    /// <summary>创建一个成功结果。</summary>
    public static StartupSnoozeResult Ok(string message, IReadOnlyList<string>? warnings = null) =>
        new(true, message, warnings ?? Array.Empty<string>());

    /// <summary>创建一个失败结果。</summary>
    public static StartupSnoozeResult Fail(string message, IReadOnlyList<string>? warnings = null) =>
        new(false, message, warnings ?? Array.Empty<string>());
}

/// <summary>
/// 在分身连接期间临时关掉本机的开机自启动项，断开后再按字节原样恢复。
///
/// 为什么需要它：子会话用同一个账户登录，Windows 会在子会话里把用户和公共启动项
/// 再跑一遍；单实例软件（Steam、Clash 等）收到协议或 IPC 调用后会把窗口拉回主桌面，
/// 于是「分身里启动」变成「主桌面又冒出来一堆窗口」。
///
/// 只动 Windows 自己的开关，不碰文件、不碰权限、不杀进程：
/// <list type="bullet">
/// <item><c>Explorer\StartupApproved</c> 的二进制值（Run / Run32 / StartupFolder）</item>
/// <item>计划任务的启用状态（<c>schtasks /Change</c>）</item>
/// <item>UWP 启动任务的 <c>State</c> 值</item>
/// </list>
///
/// 先写日志再改系统，日志里存的是原值字节；中途崩溃时下次启动会照着日志恢复。
/// </summary>
public sealed class StartupSnoozeService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedRootPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";
    private const string StartupFolderApprovedPath = ApprovedRootPath + @"\StartupFolder";
    private const string StartMenuPrograms = @"Microsoft\Windows\Start Menu\Programs";
    private const string TaskCacheTreePath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tree";
    private const string UwpRootPath =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";

    private static readonly JsonSerializerOptions JournalJsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly object _gate = new();

    /// <summary>
    /// 创建服务。
    /// </summary>
    /// <param name="directory">数据目录，默认 <c>%AppData%\DesktopTwin</c>；测试里传临时目录。</param>
    public StartupSnoozeService(string? directory = null)
    {
        Directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DesktopTwin");
        JournalPath = Path.Combine(Directory, "startup-snooze.json");
        ExceptionsPath = Path.Combine(Directory, "startup-snooze-exceptions.json");
    }

    /// <summary>数据目录。</summary>
    public string Directory { get; }

    /// <summary>关停日志路径。</summary>
    public string JournalPath { get; }

    /// <summary>例外清单路径（写入被系统拒绝的项，用户明确知情后可放行）。</summary>
    public string ExceptionsPath { get; }

    /// <summary>当前是否处于「已关停」状态（以日志文件是否存在为准）。</summary>
    public bool IsSnoozed => File.Exists(JournalPath);

    /// <summary>
    /// 上次退出时没来得及恢复（日志还在）——下次启动必须先恢复再连分身。
    /// </summary>
    public bool HasPendingJournal => IsSnoozed;

    /// <summary>
    /// 关停本机所有能覆盖的第三方启动源。
    /// </summary>
    public StartupSnoozeResult EnsureSnoozed()
    {
        lock (_gate)
        {
            if (IsSnoozed)
            {
                return StartupSnoozeResult.Ok("启动项已处于关停状态。");
            }

            var refusals = GetRefusalReasons();
            if (refusals.Count > 0)
            {
                AppLog.Warn("存在未覆盖的启动源，已拒绝关停：" + string.Join("；", refusals));
                return StartupSnoozeResult.Fail(
                    "存在无法安全覆盖的启动源，已拒绝关停启动项：" + string.Join("；", refusals),
                    refusals);
            }

            var journal = new StartupSnoozeJournal
            {
                CreatedAt = DateTimeOffset.Now,
                Machine = Environment.MachineName,
                User = Environment.UserName
            };

            try
            {
                journal.Registry = CollectRegistryEntries();
                journal.Tasks = CollectScheduledTasks();
                journal.Uwp = CollectUwpTasks();
            }
            catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException
                                                  or IOException or InvalidOperationException)
            {
                AppLog.Warn("枚举启动项失败，未做任何修改。", exception);
                return StartupSnoozeResult.Fail("读取启动项失败，未做任何修改：" + exception.Message);
            }

            var warnings = new List<string>();
            DropUncoveredEntries(journal, warnings);
            DropExceptionEntries(journal, warnings);

            if (journal.Registry.Count == 0 && journal.Tasks.Count == 0 && journal.Uwp.Count == 0)
            {
                // 没有可关的东西也要落一份日志：否则「已关停」状态无法表示，
                // 断开时也就不会走恢复流程。
                journal.Warnings = warnings;
                journal.Completed = true;
                SaveJournal(journal);
                return StartupSnoozeResult.Ok("没有需要关停的启动项。", warnings);
            }

            journal.Warnings = warnings;

            // 先落盘再改系统：任何一步崩溃都能靠这份日志恢复。
            SaveJournal(journal);

            try
            {
                ApplyRegistryEntries(journal, warnings);
                ApplyScheduledTasks(journal, warnings);
                ApplyUwpTasks(journal, warnings);
            }
            catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException
                                                  or IOException or InvalidOperationException)
            {
                AppLog.Warn("关停启动项时出错，正在回滚。", exception);
                var rollback = RestoreCore();
                return StartupSnoozeResult.Fail(
                    "关停启动项失败，已回滚：" + exception.Message + "；" + rollback.Message,
                    warnings);
            }

            journal.Warnings = warnings;
            journal.Completed = true;
            SaveJournal(journal);

            var summary = string.Format(
                CultureInfo.CurrentCulture,
                "已关停启动项：注册表 {0} 项、计划任务 {1} 项、UWP {2} 项。",
                journal.Registry.Count(entry => entry.Applied),
                journal.Tasks.Count(entry => entry.ConfirmedDisabled),
                journal.Uwp.Count(entry => entry.WrittenState == 0));
            AppLog.Info(summary + (warnings.Count == 0 ? string.Empty : "；未能关停：" + string.Join("；", warnings)));
            return StartupSnoozeResult.Ok(summary, warnings);
        }
    }

    /// <summary>
    /// 按日志把启动项原样恢复。
    /// </summary>
    public StartupSnoozeResult Restore()
    {
        lock (_gate)
        {
            return RestoreCore();
        }
    }

    private StartupSnoozeResult RestoreCore()
    {
        if (!IsSnoozed)
        {
            return StartupSnoozeResult.Ok("没有需要恢复的启动项。");
        }

        StartupSnoozeJournal journal;
        try
        {
            journal = LoadJournal();
        }
        catch (Exception exception) when (exception is IOException or JsonException
                                              or UnauthorizedAccessException)
        {
            AppLog.Warn("读取关停日志失败，未能恢复启动项。", exception);
            return StartupSnoozeResult.Fail("读取关停日志失败：" + exception.Message);
        }

        var problems = new List<string>();
        var restored = 0;

        foreach (var entry in journal.Registry.Where(entry => entry.Applied))
        {
            try
            {
                RestoreRegistryEntry(entry);
                entry.Applied = false;
                restored++;
            }
            catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException
                                                  or IOException or ArgumentException)
            {
                problems.Add(string.Format(
                    CultureInfo.CurrentCulture,
                    "{0}\\{1}：{2}",
                    entry.Hive,
                    entry.ValueName,
                    exception.Message));
            }
        }

        foreach (var task in journal.Tasks.Where(task => task.ConfirmedDisabled && task.OriginalEnabled))
        {
            var (exitCode, output) = RunSchtasks("/Change", "/TN", task.Relative, "/Enable");
            if (exitCode != 0)
            {
                problems.Add(task.Relative + "：" + output);
                continue;
            }

            if (!WaitForTaskEnabled(task.Relative, expected: true))
            {
                problems.Add(task.Relative + "：启用后回读未确认");
                continue;
            }

            task.ConfirmedDisabled = false;
            restored++;
        }

        foreach (var uwp in journal.Uwp.Where(uwp => uwp.WrittenState == 0))
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(uwp.KeyPath, writable: true);
                if (key is null)
                {
                    problems.Add(uwp.KeyPath + "：键不存在");
                    continue;
                }

                key.SetValue("State", uwp.OriginalState, RegistryValueKind.DWord);
                uwp.WrittenState = uwp.OriginalState;
                restored++;
            }
            catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException
                                                  or IOException)
            {
                problems.Add(uwp.KeyPath + "：" + exception.Message);
            }
        }

        if (problems.Count == 0)
        {
            try
            {
                File.Delete(JournalPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                problems.Add("删除日志失败：" + exception.Message);
            }
        }
        else
        {
            // 已经恢复的项把标记清掉，避免下次重复恢复时又去写一遍。
            try
            {
                SaveJournal(journal);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn("保存恢复进度失败。", exception);
            }
        }

        if (problems.Count > 0)
        {
            AppLog.Warn("恢复启动项未全部成功：" + string.Join("；", problems));
            return StartupSnoozeResult.Fail(
                "恢复启动项未全部成功：" + string.Join("；", problems),
                problems);
        }

        var message = string.Format(CultureInfo.CurrentCulture, "已恢复 {0} 个启动项。", restored);
        AppLog.Info(message);
        return StartupSnoozeResult.Ok(message);
    }

    /// <summary>
    /// 启动时调用：上次没恢复完就补一次，返回给用户的提示（无需提示时为 null）。
    /// </summary>
    public string? RecoverOnStartup()
    {
        if (!HasPendingJournal)
        {
            return null;
        }

        AppLog.Warn("发现上次未恢复的启动项关停日志，正在恢复。");
        var result = Restore();
        return result.Success
            ? "上次退出时未能恢复开机启动项，已按日志自动恢复：" + result.Message
            : "上次关停的开机启动项未能全部恢复，请检查：" + result.Message;
    }

    // ===== 枚举 =====

    private sealed record RunTarget(
        RegistryHive Hive,
        RegistryView RunView,
        RegistryView ApprovedView,
        string HiveName,
        string[] ApprovedPaths);

    private static IReadOnlyList<RunTarget> RunTargets() => new[]
    {
        new RunTarget(RegistryHive.CurrentUser, RegistryView.Registry64, RegistryView.Registry64,
            "CurrentUser", new[] { ApprovedRootPath + @"\Run", ApprovedRootPath + @"\Run32" }),
        new RunTarget(RegistryHive.LocalMachine, RegistryView.Registry64, RegistryView.Registry64,
            "LocalMachine", new[] { ApprovedRootPath + @"\Run" }),
        // HKLM 的 32 位启动项写在 WOW6432Node 下，但 StartupApproved 整棵子树也被重定向，
        // 所以「关掉 HKLM 32 位项」必须用 64 位视图去写 Run32——否则会写到
        // WOW6432Node\...\StartupApproved\Run32 这个不存在的镜像上，写了也没用。
        new RunTarget(RegistryHive.LocalMachine, RegistryView.Registry32, RegistryView.Registry64,
            "LocalMachine", new[] { ApprovedRootPath + @"\Run32" })
    };

    private static List<StartupSnoozeRegistryEntry> CollectRegistryEntries()
    {
        var entries = new List<StartupSnoozeRegistryEntry>();

        foreach (var target in RunTargets())
        {
            using var baseKey = RegistryKey.OpenBaseKey(target.Hive, target.RunView);
            using var runKey = baseKey.OpenSubKey(RunKeyPath, writable: false);
            if (runKey is null)
            {
                continue;
            }

            foreach (var valueName in runKey.GetValueNames())
            {
                if (string.IsNullOrWhiteSpace(valueName))
                {
                    continue;
                }

                var commandLine = Convert.ToString(
                    runKey.GetValue(valueName, string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames),
                    CultureInfo.InvariantCulture);

                var approvalPath = FindApprovalPath(target, valueName);
                var originalBytes = ReadApprovalBytes(
                    target.Hive, target.ApprovedView, approvalPath, valueName, out var exists);

                // 已经关掉的不要重复关：恢复时也就不用动它。
                if (exists && StartupApproval.IsDisabled(originalBytes))
                {
                    continue;
                }

                // 命令行的可执行文件不存在时 Windows 本来就不会启动它（启动项「已失效」），
                // 这种情况不需要写开关，否则反而会把它变成「启用」。
                if (!exists && !ExecutableExists(commandLine))
                {
                    continue;
                }

                entries.Add(new StartupSnoozeRegistryEntry
                {
                    Kind = "RunValue",
                    Hive = target.HiveName,
                    RunView = target.RunView.ToString(),
                    ApprovedView = target.ApprovedView.ToString(),
                    KeyPath = approvalPath,
                    ValueName = valueName,
                    CommandLine = commandLine,
                    OriginalExists = exists,
                    OriginalBase64 = exists && originalBytes is not null
                        ? Convert.ToBase64String(originalBytes)
                        : null
                });
            }
        }

        entries.AddRange(CollectStartupFolderEntries());
        return entries;
    }

    private static string FindApprovalPath(RunTarget target, string valueName)
    {
        foreach (var path in target.ApprovedPaths)
        {
            using var baseKey = RegistryKey.OpenBaseKey(target.Hive, target.ApprovedView);
            using var key = baseKey.OpenSubKey(path, writable: false);
            if (key is not null && key.GetValueNames().Contains(valueName, StringComparer.OrdinalIgnoreCase))
            {
                return path;
            }
        }

        return target.ApprovedPaths[0];
    }

    private static byte[]? ReadApprovalBytes(
        RegistryHive hive,
        RegistryView view,
        string keyPath,
        string valueName,
        out bool exists)
    {
        exists = false;
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var key = baseKey.OpenSubKey(keyPath, writable: false);
        if (key?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is byte[] raw)
        {
            exists = true;
            return raw;
        }

        return null;
    }

    private static List<StartupSnoozeRegistryEntry> CollectStartupFolderEntries()
    {
        var entries = new List<StartupSnoozeRegistryEntry>();
        var folders = new (string Path, RegistryHive Hive, string HiveName)[]
        {
            (System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                StartMenuPrograms,
                "Startup"), RegistryHive.CurrentUser, "CurrentUser"),
            (System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                StartMenuPrograms,
                "Startup"), RegistryHive.LocalMachine, "LocalMachine")
        };

        foreach (var folder in folders)
        {
            if (!System.IO.Directory.Exists(folder.Path))
            {
                continue;
            }

            foreach (var file in System.IO.Directory.EnumerateFiles(folder.Path))
            {
                var fileName = System.IO.Path.GetFileName(file);
                if (string.Equals(fileName, "desktop.ini", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var bytes = ReadFolderApprovalBytes(folder.Hive, fileName, out var exists);
                if (exists && StartupApproval.IsDisabled(bytes))
                {
                    continue;
                }

                entries.Add(new StartupSnoozeRegistryEntry
                {
                    Kind = "StartupFolderItem",
                    Hive = folder.HiveName,
                    RunView = "Registry64",
                    ApprovedView = "Registry64",
                    KeyPath = StartupFolderApprovedPath,
                    ValueName = fileName,
                    CommandLine = file,
                    OriginalExists = exists,
                    OriginalBase64 = exists && bytes is not null ? Convert.ToBase64String(bytes) : null
                });
            }
        }

        return entries;
    }

    private static byte[]? ReadFolderApprovalBytes(RegistryHive hive, string valueName, out bool exists)
    {
        exists = false;
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(StartupFolderApprovedPath, writable: false);
        if (key?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is byte[] raw)
        {
            exists = true;
            return raw;
        }

        return null;
    }

    private static List<StartupSnoozeTaskEntry> CollectScheduledTasks()
    {
        var entries = new List<StartupSnoozeTaskEntry>();
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var tree = baseKey.OpenSubKey(TaskCacheTreePath, writable: false);
        if (tree is null)
        {
            return entries;
        }

        Collect(tree, string.Empty, depth: 0);
        return entries;

        void Collect(RegistryKey node, string relative, int depth)
        {
            if (depth > 6)
            {
                return;
            }

            var isTask = node.GetValue("Id") is not null;
            if (isTask && !relative.StartsWith(@"\Microsoft", StringComparison.OrdinalIgnoreCase))
            {
                var xml = QueryTaskXml(relative);
                if (xml is not null && ScheduledTaskXml.IsEnabled(xml) && ScheduledTaskXml.HasSessionStartTrigger(xml))
                {
                    var trimmed = relative.TrimStart('\\');
                    var slash = trimmed.LastIndexOf('\\');
                    entries.Add(new StartupSnoozeTaskEntry
                    {
                        Relative = relative,
                        TaskName = slash < 0 ? trimmed : trimmed.Substring(slash + 1),
                        TaskPath = slash < 0 ? "\\" : "\\" + trimmed.Substring(0, slash) + "\\",
                        OriginalEnabled = true
                    });
                }
            }

            foreach (var childName in node.GetSubKeyNames())
            {
                using var child = node.OpenSubKey(childName, writable: false);
                if (child is not null)
                {
                    Collect(child, relative + "\\" + childName, depth + 1);
                }
            }
        }
    }

    private static string? QueryTaskXml(string relative)
    {
        var (exitCode, output) = RunSchtasks("/Query", "/XML", "ONE", "/TN", relative);
        return exitCode == 0 && output.Contains('<') ? output : null;
    }

    private static List<StartupSnoozeUwpEntry> CollectUwpTasks()
    {
        var entries = new List<StartupSnoozeUwpEntry>();
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var root = baseKey.OpenSubKey(UwpRootPath, writable: false);
        if (root is null)
        {
            return entries;
        }

        foreach (var family in root.GetSubKeyNames())
        {
            if (family.StartsWith("MicrosoftWindows.", StringComparison.OrdinalIgnoreCase)
                || family.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var packageKey = root.OpenSubKey(family, writable: false);
            if (packageKey is null)
            {
                continue;
            }

            Walk(packageKey, family, string.Empty, depth: 0);
        }

        return entries;

        void Walk(RegistryKey node, string family, string relative, int depth)
        {
            if (depth > 8)
            {
                return;
            }

            if (node.GetValue("State", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is int state
                && state == UwpStartupState.EnabledConfirmed)
            {
                entries.Add(new StartupSnoozeUwpEntry
                {
                    KeyPath = UwpRootPath + "\\" + family + (relative.Length == 0 ? string.Empty : "\\" + relative),
                    PackageFamilyName = family,
                    Task = relative.Length == 0 ? family : relative.Substring(relative.LastIndexOf('\\') + 1),
                    OriginalState = state,
                    WrittenState = state
                });
            }

            foreach (var childName in node.GetSubKeyNames())
            {
                using var child = node.OpenSubKey(childName, writable: false);
                if (child is not null)
                {
                    Walk(child, family, relative.Length == 0 ? childName : relative + "\\" + childName, depth + 1);
                }
            }
        }
    }

    // ===== 写入 =====

    private void ApplyRegistryEntries(StartupSnoozeJournal journal, List<string> warnings)
    {
        var disabled = Convert.ToBase64String(StartupApproval.CreateDisabled(DateTime.UtcNow));
        foreach (var entry in journal.Registry)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(ParseHive(entry.Hive), ParseView(entry.ApprovedView));
                using var key = baseKey.CreateSubKey(entry.KeyPath, writable: true)
                    ?? throw new InvalidOperationException("无法创建键 " + entry.KeyPath);

                key.SetValue(entry.ValueName, Convert.FromBase64String(disabled), RegistryValueKind.Binary);
                entry.WrittenBase64 = disabled;
                entry.Applied = true;
                SaveJournal(journal);
            }
            catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException
                                                  or IOException or InvalidOperationException
                                                  or ArgumentException)
            {
                var message = string.Format(
                    CultureInfo.CurrentCulture,
                    "{0}：{1}",
                    entry.ValueName,
                    exception.Message);
                warnings.Add(message);
                AppLog.Warn("无法关闭启动项 " + entry.ValueName, exception);
                TryRestoreRegistryEntryQuietly(entry);
            }
        }
    }

    private void ApplyScheduledTasks(StartupSnoozeJournal journal, List<string> warnings)
    {
        foreach (var task in journal.Tasks)
        {
            var (exitCode, output) = RunSchtasks("/Change", "/TN", task.Relative, "/Disable");
            if (exitCode != 0)
            {
                warnings.Add(task.Relative + "：" + output);
                AppLog.Warn("禁用计划任务失败：" + task.Relative + " -> " + output);
                continue;
            }

            if (!WaitForTaskEnabled(task.Relative, expected: false))
            {
                warnings.Add(task.Relative + "：禁用后回读未确认");
                continue;
            }

            task.ConfirmedDisabled = true;
            SaveJournal(journal);
        }
    }

    private void ApplyUwpTasks(StartupSnoozeJournal journal, List<string> warnings)
    {
        foreach (var uwp in journal.Uwp)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(uwp.KeyPath, writable: true)
                    ?? throw new InvalidOperationException("键不存在");
                key.SetValue("State", 0, RegistryValueKind.DWord);
                uwp.WrittenState = 0;
                SaveJournal(journal);
            }
            catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException
                                                  or IOException or InvalidOperationException)
            {
                warnings.Add(uwp.Task + "：" + exception.Message);
                AppLog.Warn("关闭 UWP 启动任务失败：" + uwp.Task, exception);
            }
        }
    }

    private static void RestoreRegistryEntry(StartupSnoozeRegistryEntry entry)
    {
        using var baseKey = RegistryKey.OpenBaseKey(ParseHive(entry.Hive), ParseView(entry.ApprovedView));
        using var key = baseKey.CreateSubKey(entry.KeyPath, writable: true)
            ?? throw new InvalidOperationException("无法打开键 " + entry.KeyPath);

        if (entry.OriginalExists)
        {
            key.SetValue(
                entry.ValueName,
                Convert.FromBase64String(entry.OriginalBase64 ?? string.Empty),
                RegistryValueKind.Binary);
        }
        else
        {
            key.DeleteValue(entry.ValueName, throwOnMissingValue: false);
        }
    }

    private static void TryRestoreRegistryEntryQuietly(StartupSnoozeRegistryEntry entry)
    {
        try
        {
            RestoreRegistryEntry(entry);
            entry.Applied = false;
        }
        catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException
                                              or IOException or InvalidOperationException
                                              or ArgumentException)
        {
            AppLog.Warn("回滚启动项失败：" + entry.ValueName, exception);
        }
    }

    // ===== 拒绝条件 =====

    private List<string> GetRefusalReasons()
    {
        var reasons = new List<string>();

        var runOnceTargets = new (RegistryHive Hive, RegistryView View, string Path)[]
        {
            (RegistryHive.CurrentUser, RegistryView.Registry64, RunKeyPath + "Once"),
            (RegistryHive.CurrentUser, RegistryView.Registry32,
                @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce"),
            (RegistryHive.LocalMachine, RegistryView.Registry64, RunKeyPath + "Once"),
            (RegistryHive.LocalMachine, RegistryView.Registry32,
                @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce"),
            (RegistryHive.LocalMachine, RegistryView.Registry64,
                @"Software\Microsoft\Windows\CurrentVersion\RunOnceEx")
        };

        var allowedRoots = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                @"Microsoft\EdgeWebView\Application"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                @"Microsoft\EdgeUpdate"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                @"Microsoft\EdgeWebView\Application")
        };

        foreach (var target in runOnceTargets)
        {
            using var baseKey = RegistryKey.OpenBaseKey(target.Hive, target.View);
            using var key = baseKey.OpenSubKey(target.Path, writable: false);
            if (key is null)
            {
                continue;
            }

            foreach (var name in key.GetValueNames())
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var commandLine = Convert.ToString(
                    key.GetValue(name, string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames),
                    CultureInfo.InvariantCulture);
                var executable = RunOnceClassifier.ParseExecutable(commandLine);
                if (RunOnceClassifier.IsAllowed(
                        name,
                        commandLine,
                        executable is not null && File.Exists(executable),
                        executable,
                        allowedRoots))
                {
                    AppLog.Info("放行微软 Edge 清理项：" + name + " -> " + commandLine);
                    continue;
                }

                reasons.Add("RunOnce 项 " + name);
            }
        }

        using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64))
        using (var policies = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run"))
        {
            if (policies is not null && policies.GetValueNames().Any(name => !string.IsNullOrWhiteSpace(name)))
            {
                reasons.Add("组策略启动项 Policies\\Explorer\\Run");
            }
        }

        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var winlogon = baseKey.OpenSubKey(
                @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon", writable: false);
            if (winlogon is null)
            {
                continue;
            }

            var userinit = Convert.ToString(winlogon.GetValue("Userinit"), CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(userinit)
                && !userinit.Trim().TrimEnd(',').EndsWith(
                    @"system32\userinit.exe",
                    StringComparison.OrdinalIgnoreCase))
            {
                reasons.Add("Winlogon Userinit 被改动");
            }

            var shell = Convert.ToString(winlogon.GetValue("Shell"), CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(shell)
                && !string.Equals(shell.Trim(), "explorer.exe", StringComparison.OrdinalIgnoreCase))
            {
                reasons.Add("Winlogon Shell 被改动");
            }
        }

        return reasons;
    }

    private void DropUncoveredEntries(StartupSnoozeJournal journal, List<string> warnings)
    {
        var exceptions = LoadExceptions();
        var kept = new List<StartupSnoozeRegistryEntry>();

        foreach (var entry in journal.Registry)
        {
            var key = string.Format(
                CultureInfo.InvariantCulture,
                "{0}|{1}|{2}",
                entry.Hive,
                entry.ApprovedView,
                entry.ValueName);

            if (exceptions.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                warnings.Add(entry.ValueName + "：已按例外清单保持启用");
                continue;
            }

            var problem = ProbeWritable(entry);
            if (problem is not null)
            {
                warnings.Add(entry.ValueName + "：系统拒绝写入（" + problem + "），仍会在分身里启动");
                AppLog.Warn("启动项写入被系统拒绝：" + key + " -> " + problem);
                continue;
            }

            kept.Add(entry);
        }

        journal.Registry = kept;
    }

    private void DropExceptionEntries(StartupSnoozeJournal journal, List<string> warnings)
    {
        var exceptions = LoadExceptions();
        if (!journal.Tasks.Any(task => exceptions.Contains(task.Relative, StringComparer.OrdinalIgnoreCase)))
        {
            return;
        }

        foreach (var task in journal.Tasks.Where(task =>
                     exceptions.Contains(task.Relative, StringComparer.OrdinalIgnoreCase)))
        {
            warnings.Add(task.Relative + "：已按例外清单保持启用");
        }

        journal.Tasks = journal.Tasks
            .Where(task => !exceptions.Contains(task.Relative, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    private static string? ProbeWritable(StartupSnoozeRegistryEntry entry)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(ParseHive(entry.Hive), ParseView(entry.ApprovedView));
            using var key = baseKey.CreateSubKey(entry.KeyPath, writable: true);
            if (key is null)
            {
                return "无法创建键";
            }

            if (entry.OriginalExists && entry.OriginalBase64 is { } base64)
            {
                // 原样写回：语义上什么都没改，但能问出「这个值让不让我们写」。
                key.SetValue(entry.ValueName, Convert.FromBase64String(base64), RegistryValueKind.Binary);
                return null;
            }

            // 没有原值时必须测「目标值名」本身，因为杀软可能只拦这个值。
            key.SetValue(entry.ValueName, StartupApproval.CreateDisabled(DateTime.UtcNow), RegistryValueKind.Binary);
            key.DeleteValue(entry.ValueName, throwOnMissingValue: false);
            return null;
        }
        catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException
                                              or IOException or InvalidOperationException
                                              or ArgumentException)
        {
            return exception.Message;
        }
    }

    private HashSet<string> LoadExceptions()
    {
        try
        {
            if (!File.Exists(ExceptionsPath))
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            var keys = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(ExceptionsPath, Encoding.UTF8));
            return new HashSet<string>(keys ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or JsonException
                                              or UnauthorizedAccessException)
        {
            AppLog.Warn("读取启动项例外清单失败，按空清单处理。", exception);
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    // ===== 计划任务 =====

    private static bool WaitForTaskEnabled(string relative, bool expected)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var xml = QueryTaskXml(relative);
            if (xml is not null && ScheduledTaskXml.IsEnabled(xml) == expected)
            {
                return true;
            }

            System.Threading.Thread.Sleep(300);
        }

        return false;
    }

    private static (int ExitCode, string Output) RunSchtasks(params string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo("schtasks.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return (-1, "无法启动 schtasks.exe");
            }

            var output = process.StandardOutput.ReadToEnd() + " " + process.StandardError.ReadToEnd();
            if (!process.WaitForExit(30_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // 已经退出。
                }

                return (-1, "schtasks 超时");
            }

            return (process.ExitCode, output.Trim());
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
                                              or InvalidOperationException
                                              or IOException)
        {
            return (-1, exception.Message);
        }
    }

    // ===== 日志 =====

    private void SaveJournal(StartupSnoozeJournal journal)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var json = JsonSerializer.Serialize(journal, JournalJsonOptions);
        var temporary = JournalPath + ".tmp";
        File.WriteAllText(temporary, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporary, JournalPath, overwrite: true);
    }

    private StartupSnoozeJournal LoadJournal()
    {
        var json = File.ReadAllText(JournalPath, Encoding.UTF8);
        return JsonSerializer.Deserialize<StartupSnoozeJournal>(json)
               ?? throw new InvalidOperationException("关停日志内容为空。");
    }

    // ===== 辅助 =====

    private static RegistryHive ParseHive(string hive) => hive switch
    {
        "LocalMachine" => RegistryHive.LocalMachine,
        _ => RegistryHive.CurrentUser
    };

    private static RegistryView ParseView(string view) => view switch
    {
        "Registry32" => RegistryView.Registry32,
        "Registry64" => RegistryView.Registry64,
        _ => RegistryView.Default
    };

    private static bool ExecutableExists(string? commandLine)
    {
        var executable = RunOnceClassifier.ParseExecutable(commandLine);
        if (string.IsNullOrWhiteSpace(executable))
        {
            return false;
        }

        try
        {
            return File.Exists(Environment.ExpandEnvironmentVariables(executable));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException
                                              or PathTooLongException)
        {
            return false;
        }
    }
}
