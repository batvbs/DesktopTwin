using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using System.Text;
using DesktopTwin.Interop;
using Microsoft.Win32;
using DesktopTwin.Localization;

namespace DesktopTwin.Services;

/// <summary>
/// 桌面分身的 Windows 环境预检。
///
/// 桌面分身完全依赖 Windows 自身的 RDP / Child Session 能力（WTSEnableChildSessions +
/// RDP ActiveX 的 ConnectToChildSession），本程序只是唤起这个功能。
/// 当系统缺少前置条件时，RDP ActiveX 只会回报「发生内部错误」以及一个无法定位的断开原因，
/// 用户和日志都无法判断真实原因，因此在这里提前收集可读的环境结论。
///
/// 本类只做检测；自动修复在 <see cref="EnvironmentFixer"/> 里，两者分开是为了让
/// 「看」和「改」各自可以被独立测试与审查。
/// </summary>
public static class ChildSessionEnvironmentCheck
{
    /// <summary>
    /// 「仅允许 Windows Hello 登录」设置所在路径。
    /// </summary>
    internal const string PasswordLessRegistryPath =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\PasswordLess\Device";

    /// <summary>
    /// 远程桌面主机的本机设置所在路径。
    /// </summary>
    internal const string TerminalServerRegistryPath =
        @"SYSTEM\CurrentControlSet\Control\Terminal Server";

    /// <summary>
    /// 远程桌面开关的组策略路径。该键存在时会覆盖本机设置。
    /// </summary>
    internal const string TerminalServerPolicyRegistryPath =
        @"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services";

    /// <summary>
    /// TermService 的配置路径，ServiceDll 指向的 DLL 决定它是否被补丁劫持。
    /// </summary>
    internal const string TermServiceParametersRegistryPath =
        @"SYSTEM\CurrentControlSet\Services\TermService\Parameters";

    /// <summary>
    /// 系统版本信息所在路径。
    /// </summary>
    internal const string CurrentVersionRegistryPath =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    /// <summary>
    /// DevicePasswordLessBuildVersion 为 2 时，系统仅允许 Windows Hello 登录。
    /// 注意：该注册表值未见于微软官方文档，属于社区逆向得出的结论，
    /// 因此只把 2 判定为「已开启」，其余取值一律视为未开启。
    /// </summary>
    internal const int PasswordLessHelloOnlyValue = 2;

    /// <summary>
    /// 家庭版（Core 系列）不带远程桌面主机，无法建立分身会话。
    /// </summary>
    private static readonly string[] HomeEditionIds =
    [
        "Core",
        "CoreSingleLanguage",
        "CoreCountrySpecific",
        "CoreN",
        "Starter",
        "Home"
    ];

    /// <summary>
    /// 问题的严重程度，决定它在诊断面板里落在哪一栏。
    /// </summary>
    public enum Severity
    {
        /// <summary>会导致启动失败，需要处理。</summary>
        Warning,

        /// <summary>不影响启动，但值得知道（例如家庭版、功能受限）。</summary>
        Notice,

        /// <summary>
        /// 当前环境里没有触发这一项。只有「全部检查项」视图才会产出，
        /// 用于人工核对每条结论的文案（尤其是翻译）。
        /// </summary>
        Inactive
    }

    /// <summary>
    /// 一条环境检查结论。
    /// </summary>
    /// <param name="Id">稳定标识，用于修复与「已解决」跟踪。</param>
    /// <param name="Title">标题。</param>
    /// <param name="Summary">一行简述，折叠状态下显示。</param>
    /// <param name="Detail">展开后的完整说明，含手动修复步骤。</param>
    /// <param name="IssueSeverity">严重程度。</param>
    /// <param name="IsBlocking">是否属于「必然失败」的阻塞项。</param>
    /// <param name="CanAutoFix">是否提供一键修复。</param>
    public sealed record Issue(
        string Id,
        string Title,
        string Summary,
        string Detail,
        Severity IssueSeverity,
        bool IsBlocking,
        bool CanAutoFix);

    /// <summary>
    /// 收集当前系统的环境结论。读取失败（键或值不存在、无权限）时跳过该项，不抛异常。
    /// </summary>
    public static IReadOnlyList<Issue> Collect()
    {
        var issues = new List<Issue>();

        CollectEditionIssue(issues);
        CollectChildSessionApiIssue(issues);
        CollectTermServiceIssue(issues);
        CollectRdpHostIssue(issues);
        CollectPasswordLessIssue(issues);
        CollectRdpWrapperIssue(issues);

        return issues;
    }

    /// <summary>
    /// 是否存在「必然失败」的环境问题。
    /// </summary>
    /// <summary>
    /// 列出**全部**已知检查项，包括当前没有触发的。
    ///
    /// 用途是核对文案：正常情况下只有出问题时才会有结论，改一句提示语、翻一份语言文件
    /// 都看不到效果。这个视图把每一项的标题、简述、完整说明都摆出来，
    /// 未触发的额外给出「当前值」，方便判断是文本问题还是环境真的变了。
    ///
    /// 不参与 <see cref="HasBlockingIssue"/> 之类的判定——未触发项一律
    /// <see cref="Severity.Inactive"/>，界面上分开放。
    /// </summary>
    public static IReadOnlyList<Issue> CollectAll()
    {
        var active = new Dictionary<string, Issue>(StringComparer.Ordinal);
        foreach (var issue in Collect())
        {
            active[issue.Id] = issue;
        }

        var all = new List<Issue>(8);

        void Add(
            string id,
            string titleKey,
            string summaryKey,
            string inactiveDetailKey,
            object? summaryValue,
            object? detailValue,
            bool blocking,
            bool CanAutoFix)
        {
            if (active.TryGetValue(id, out var found))
            {
                all.Add(found);
                return;
            }

            all.Add(new Issue(
                id,
                Locale.Get(titleKey),
                Locale.Format(
                    "Check.Inactive.Summary",
                    Locale.Format(summaryKey, summaryValue)),
                Locale.Format(inactiveDetailKey, detailValue),
                Severity.Inactive,
                blocking,
                CanAutoFix: false));
        }

        Add(
            "home-edition",
            "Check.Home.Title",
            "Check.Home.Summary",
            "Check.Home.InactiveDetail",
            ReadString(RegistryHive.LocalMachine, CurrentVersionRegistryPath, "EditionID")
                ?? Locale.Get("Check.Unknown"),
            ReadString(RegistryHive.LocalMachine, CurrentVersionRegistryPath, "EditionID")
                ?? Locale.Get("Check.Unknown"),
            blocking: false,
            CanAutoFix: false);

        Add(
            "child-session-api-missing",
            "Check.OldSystem.Title",
            "Check.OldSystem.Summary",
            "Check.OldSystem.InactiveDetail",
            Environment.OSVersion.VersionString,
            Environment.OSVersion.VersionString,
            blocking: true,
            CanAutoFix: false);

        var (serviceState, serviceStartType) = QueryTermService();

        Add(
            "termservice-disabled",
            "Check.TermDisabled.Title",
            "Check.TermDisabled.Summary",
            "Check.TermDisabled.InactiveDetail",
            serviceStartType,
            serviceStartType,
            blocking: true,
            CanAutoFix: true);

        Add(
            "termservice-stopped",
            "Check.TermStopped.Title",
            "Check.TermStopped.Summary",
            "Check.TermStopped.InactiveDetail",
            serviceState,
            serviceState,
            blocking: true,
            CanAutoFix: true);

        Add(
            "rdp-disabled",
            "Check.RdpOff.Title",
            "Check.RdpOff.Summary",
            "Check.RdpOff.InactiveDetail",
            ReadInt(
                RegistryHive.LocalMachine,
                TerminalServerPolicyRegistryPath,
                "fDenyTSConnections")?.ToString()
            ?? ReadInt(
                RegistryHive.LocalMachine,
                TerminalServerRegistryPath,
                "fDenyTSConnections")?.ToString()
            ?? Locale.Get("Check.NotSet"),
            ReadInt(
                RegistryHive.LocalMachine,
                TerminalServerPolicyRegistryPath,
                "fDenyTSConnections")?.ToString()
            ?? ReadInt(
                RegistryHive.LocalMachine,
                TerminalServerRegistryPath,
                "fDenyTSConnections")?.ToString()
            ?? Locale.Get("Check.NotSet"),
            blocking: true,
            CanAutoFix: true);

        Add(
            "hello-only",
            "Check.HelloOnly.Title",
            "Check.HelloOnly.Summary",
            "Check.HelloOnly.InactiveDetail",
            ReadInt(
                RegistryHive.LocalMachine,
                PasswordLessRegistryPath,
                "DevicePasswordLessBuildVersion")?.ToString()
            ?? Locale.Get("Check.NotSet"),
            ReadInt(
                RegistryHive.LocalMachine,
                PasswordLessRegistryPath,
                "DevicePasswordLessBuildVersion")?.ToString()
            ?? Locale.Get("Check.NotSet"),
            blocking: true,
            CanAutoFix: true);

        Add(
            "rdp-wrapper",
            "Check.Wrapper.Title",
            "Check.Wrapper.Summary",
            "Check.Wrapper.InactiveDetail",
            Path.GetFileName(ReadServiceDll() ?? string.Empty),
            ReadServiceDll() ?? Locale.Get("Check.ReadFailed"),
            blocking: true,
            CanAutoFix: true);

        return all;
    }

    public static bool HasBlockingIssue(IReadOnlyList<Issue> issues)
    {
        foreach (var issue in issues)
        {
            if (issue.IsBlocking)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 把结论格式化成可直接展示给用户的文本。
    /// </summary>
    public static string BuildSummary(IReadOnlyList<Issue> issues)
    {
        if (issues.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var issue in issues)
        {
            if (builder.Length > 0)
            {
                builder.AppendLine().AppendLine();
            }

            builder.AppendLine(Locale.Format("Check.SummaryEntry", issue.Title));
            builder.Append(issue.Detail);
        }

        return builder.ToString();
    }

    /// <summary>
    /// 生成用于问题排查的环境快照。用户反馈问题时贴这段文本即可，不必逐项询问。
    /// </summary>
    public static string BuildDiagnosticSnapshot()
    {
        var builder = new StringBuilder();
        builder.AppendLine(Locale.Format("Check.Snapshot.OsVersion", Environment.OSVersion.VersionString, Environment.Is64BitProcess));
        builder.AppendLine(Locale.Format("Check.Snapshot.WindowsVersion", ReadString(RegistryHive.LocalMachine, CurrentVersionRegistryPath, "DisplayVersion") ?? Locale.Get("Check.Unknown")));
        builder.AppendLine(Locale.Format("Check.Snapshot.Build", ReadString(RegistryHive.LocalMachine, CurrentVersionRegistryPath, "CurrentBuild") ?? Locale.Get("Check.Unknown")));
        builder.AppendLine(Locale.Format("Check.Snapshot.Edition", ReadString(RegistryHive.LocalMachine, CurrentVersionRegistryPath, "EditionID") ?? Locale.Get("Check.Unknown")));
        builder.AppendLine(Locale.Format("Check.Snapshot.Product", ReadString(RegistryHive.LocalMachine, CurrentVersionRegistryPath, "ProductName") ?? Locale.Get("Check.Unknown")));
        builder.AppendLine(Locale.Format("Check.Snapshot.RdpPort", ChildSessionNativeMethods.GetConfiguredRdpPort()));
        builder.AppendLine(Locale.Format("Check.Snapshot.DenyTsConnections", ReadInt(RegistryHive.LocalMachine, TerminalServerRegistryPath, "fDenyTSConnections")?.ToString() ?? Locale.Get("Check.NotSet")));
        builder.AppendLine(Locale.Format("Check.Snapshot.TermService", DescribeTermService()));
        builder.AppendLine(Locale.Format("Check.Snapshot.ServiceDll", ReadServiceDll() ?? Locale.Get("Check.ReadFailed")));

        try
        {
            builder.AppendLine(Locale.Format("Check.Snapshot.ChildSessions", ChildSessionNativeMethods.IsChildSessionsEnabled()));
            builder.AppendLine(Locale.Format("Check.Snapshot.CurrentSession", ChildSessionNativeMethods.TryGetChildSessionId()?.ToString() ?? Locale.Get("Check.None")));
        }
        catch (Exception exception)
        {
            builder.AppendLine(Locale.Format("Check.Snapshot.ChildSessionFailed", exception.Message));
        }

        var issues = Collect();
        builder.AppendLine();
        builder.AppendLine(Locale.Get("Check.Snapshot.Header"));
        builder.AppendLine(issues.Count == 0 ? Locale.Get("Check.NoIssues") : BuildSummary(issues));
        return builder.ToString();
    }

    /// <summary>
    /// 读 TermService 的 ServiceDll。读不到返回 null。
    /// </summary>
    internal static string? ReadServiceDll() =>
        ReadString(RegistryHive.LocalMachine, TermServiceParametersRegistryPath, "ServiceDll");

    /// <summary>
    /// 原生 termsrv.dll 的完整路径。
    /// </summary>
    internal static string GetNativeServiceDllPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "termsrv.dll");

    /// <summary>
    /// 判断 ServiceDll 是否被第三方 DLL 劫持。
    ///
    /// **不能只匹配 rdpwrap.dll**：实际补丁还有 superrdp.dll、RDPWrappers.dll 等，
    /// 不同工具、不同版本用的名字都不一样。正确的判定是「不等于原生 termsrv.dll 就算异常」。
    /// </summary>
    /// <param name="serviceDll">读到的 ServiceDll 值。</param>
    internal static bool IsServiceDllHijacked(string? serviceDll)
    {
        if (string.IsNullOrWhiteSpace(serviceDll))
        {
            return false;
        }

        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(serviceDll);
            return !string.Equals(
                Path.GetFullPath(expanded),
                Path.GetFullPath(GetNativeServiceDllPath()),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException
                                              or NotSupportedException
                                              or PathTooLongException)
        {
            // 路径畸形时按可疑处理，不静默放行。
            return true;
        }
    }

    /// <summary>
    /// 检查 Windows 版本。
    ///
    /// 微软官方文档说明家庭版不支持远程桌面主机，但实测大部分家庭版仍可建立 Child Session，
    /// 因此这里只作为**提示**而非阻塞项，避免把本来能用的用户挡在门外。
    /// </summary>
    private static void CollectEditionIssue(List<Issue> issues)
    {
        var editionId = ReadString(
            RegistryHive.LocalMachine,
            CurrentVersionRegistryPath,
            "EditionID");
        if (string.IsNullOrWhiteSpace(editionId))
        {
            return;
        }

        foreach (var homeEditionId in HomeEditionIds)
        {
            if (!string.Equals(editionId, homeEditionId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            issues.Add(new Issue(
                "home-edition",
                Locale.Get("Check.Home.Title"),
                Locale.Get("Check.Home.Summary"),
                Locale.Format("Check.Home.Detail", editionId),
                Severity.Notice,
                IsBlocking: false,
                CanAutoFix: false));
            return;
        }
    }

    /// <summary>
    /// 检查 Child Session API 是否存在。Windows 10 1803 之前没有这个导出。
    /// </summary>
    private static void CollectChildSessionApiIssue(List<Issue> issues)
    {
        try
        {
            _ = ChildSessionNativeMethods.IsChildSessionsEnabled();
        }
        catch (EntryPointNotFoundException)
        {
            issues.Add(new Issue(
                "child-session-api-missing",
                Locale.Get("Check.OldSystem.Title"),
                Locale.Get("Check.OldSystem.Summary"),
                Locale.Get("Check.OldSystem.Detail"),
                Severity.Warning,
                IsBlocking: true,
                CanAutoFix: false));
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
                                              or InvalidOperationException)
        {
            // 其他失败交给后续连接流程报错，不在这里拦死。
            AppLog.Warn("检查 Child Session API 时出现异常。", exception);
        }
    }

    /// <summary>
    /// 检查 TermService 是否在运行、启动类型是否被禁用。
    /// </summary>
    private static void CollectTermServiceIssue(List<Issue> issues)
    {
        var (state, startType) = QueryTermService();

        if (startType is "Disabled" or "已禁用")
        {
            issues.Add(new Issue(
                "termservice-disabled",
                Locale.Get("Check.TermDisabled.Title"),
                Locale.Get("Check.TermDisabled.Summary"),
                Locale.Get("Check.TermDisabled.Detail"),
                Severity.Warning,
                IsBlocking: true,
                CanAutoFix: true));
            return;
        }

        if (state is "Stopped" or "已停止")
        {
            issues.Add(new Issue(
                "termservice-stopped",
                Locale.Get("Check.TermStopped.Title"),
                Locale.Get("Check.TermStopped.Summary"),
                Locale.Get("Check.TermStopped.Detail"),
                Severity.Warning,
                IsBlocking: true,
                CanAutoFix: true));
        }
    }

    /// <summary>
    /// 检查远程桌面主机是否被关闭。组策略中的值会覆盖本机设置，因此优先读取组策略。
    /// </summary>
    private static void CollectRdpHostIssue(List<Issue> issues)
    {
        var policyValue = ReadInt(
            RegistryHive.LocalMachine,
            TerminalServerPolicyRegistryPath,
            "fDenyTSConnections");
        var localValue = policyValue is null
            ? ReadInt(RegistryHive.LocalMachine, TerminalServerRegistryPath, "fDenyTSConnections")
            : null;
        var denyTsConnections = policyValue ?? localValue;
        if (denyTsConnections != 1)
        {
            return;
        }

        var byPolicy = policyValue is not null;

        issues.Add(new Issue(
            "rdp-disabled",
            Locale.Get("Check.RdpOff.Title"),
            Locale.Get("Check.RdpOff.Summary"),
            byPolicy
                ? Locale.Get("Check.RdpOff.Detail.Policy")
                : Locale.Get("Check.RdpOff.Detail.Local"),
            Severity.Warning,
            IsBlocking: true,
            CanAutoFix: !byPolicy));
    }

    /// <summary>
    /// 检查是否开启了「仅允许 Windows Hello 登录」，该限制会让密码验证必然失败。
    /// </summary>
    private static void CollectPasswordLessIssue(List<Issue> issues)
    {
        var passwordLessVersion = ReadInt(
            RegistryHive.LocalMachine,
            PasswordLessRegistryPath,
            "DevicePasswordLessBuildVersion");
        if (passwordLessVersion != PasswordLessHelloOnlyValue)
        {
            return;
        }

        issues.Add(new Issue(
            "hello-only",
            Locale.Get("Check.HelloOnly.Title"),
            Locale.Get("Check.HelloOnly.Summary"),
            Locale.Get("Check.HelloOnly.Detail"),
            Severity.Warning,
            IsBlocking: true,
            CanAutoFix: true));
    }

    /// <summary>
    /// 检查 TermService 是否被第三方补丁劫持。
    /// </summary>
    private static void CollectRdpWrapperIssue(List<Issue> issues)
    {
        var serviceDll = ReadServiceDll();
        if (!IsServiceDllHijacked(serviceDll))
        {
            return;
        }

        issues.Add(new Issue(
            "rdp-wrapper",
            Locale.Get("Check.Wrapper.Title"),
            Locale.Format("Check.Wrapper.Summary", Path.GetFileName(serviceDll ?? string.Empty)),
            Locale.Format("Check.Wrapper.Detail", serviceDll),
            Severity.Warning,
            IsBlocking: true,
            CanAutoFix: true));
    }

    /// <summary>
    /// 查询 TermService 的运行状态与启动类型。查询失败返回 ("未知", "未知")。
    /// </summary>
    internal static (string State, string StartType) QueryTermService()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\TermService");
            var start = key?.GetValue("Start") as int?;
            var startType = start switch
            {
                2 => "Automatic",
                3 => "Manual",
                4 => "Disabled",
                _ => "Unknown"
            };

            var state = ServiceNativeMethods.TryQueryState("TermService", out var currentState)
                ? currentState switch
                {
                    1 => "Stopped",
                    2 => "StartPending",
                    3 => "StopPending",
                    4 => "Running",
                    _ => "Unknown"
                }
                : "Unknown";

            return (state, startType);
        }
        catch (Exception exception) when (exception is SecurityException
                                              or UnauthorizedAccessException
                                              or IOException)
        {
            return ("Unknown", "Unknown");
        }
    }

    private static string DescribeTermService()
    {
        var (state, startType) = QueryTermService();
        return $"{state} / {startType}";
    }

    /// <summary>
    /// 读取注册表 DWORD 值。键或值不存在、类型不符、无权限时返回 null。
    /// </summary>
    internal static int? ReadInt(RegistryHive hive, string path, string name)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(path);
            return key?.GetValue(name) as int?;
        }
        catch (Exception exception) when (exception is SecurityException
                                              or UnauthorizedAccessException
                                              or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// 读取注册表字符串值。键或值不存在、无权限时返回 null。
    /// </summary>
    internal static string? ReadString(RegistryHive hive, string path, string name)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(path);
            return key?.GetValue(name) as string;
        }
        catch (Exception exception) when (exception is SecurityException
                                              or UnauthorizedAccessException
                                              or IOException)
        {
            return null;
        }
    }
}
