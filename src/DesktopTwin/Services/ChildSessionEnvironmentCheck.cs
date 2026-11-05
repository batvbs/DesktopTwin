using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using System.Text;
using DesktopTwin.Interop;
using Microsoft.Win32;

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
        Notice
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

            builder.Append('【').Append(issue.Title).Append('】').AppendLine();
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
        builder.AppendLine($"系统版本：{Environment.OSVersion.VersionString}（64 位进程：{Environment.Is64BitProcess}）");
        builder.AppendLine($"Windows 版本：{ReadString(RegistryHive.LocalMachine, CurrentVersionRegistryPath, "DisplayVersion") ?? "未知"}");
        builder.AppendLine($"系统内部版本：{ReadString(RegistryHive.LocalMachine, CurrentVersionRegistryPath, "CurrentBuild") ?? "未知"}");
        builder.AppendLine($"版本标识：{ReadString(RegistryHive.LocalMachine, CurrentVersionRegistryPath, "EditionID") ?? "未知"}");
        builder.AppendLine($"产品名称：{ReadString(RegistryHive.LocalMachine, CurrentVersionRegistryPath, "ProductName") ?? "未知"}");
        builder.AppendLine($"RDP 端口：{ChildSessionNativeMethods.GetConfiguredRdpPort()}");
        builder.AppendLine($"fDenyTSConnections：{ReadInt(RegistryHive.LocalMachine, TerminalServerRegistryPath, "fDenyTSConnections")?.ToString() ?? "未设置"}");
        builder.AppendLine($"TermService 状态：{DescribeTermService()}");
        builder.AppendLine($"ServiceDll：{ReadServiceDll() ?? "读取失败"}");

        try
        {
            builder.AppendLine($"Child Sessions 已启用：{ChildSessionNativeMethods.IsChildSessionsEnabled()}");
            builder.AppendLine($"当前分身会话：{ChildSessionNativeMethods.TryGetChildSessionId()?.ToString() ?? "无"}");
        }
        catch (Exception exception)
        {
            builder.AppendLine($"读取 Child Session 状态失败：{exception.Message}");
        }

        var issues = Collect();
        builder.AppendLine();
        builder.AppendLine("环境预检结论：");
        builder.AppendLine(issues.Count == 0 ? "未发现问题。" : BuildSummary(issues));
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
                "当前系统为家庭版",
                "家庭版不带远程桌面主机，桌面分身属于「不受支持」状态",
                "检测到系统版本为 " + editionId + "（家庭版）。微软官方文档说明家庭版不包含远程桌面主机，"
                + "桌面分身在该版本上属于「不受支持」状态。" + Environment.NewLine
                + "实测大部分家庭版系统仍可正常建立分身会话，因此这里不做阻止——"
                + "如果启动后一直连不上，再考虑升级。" + Environment.NewLine + Environment.NewLine
                + "升级到专业版的方法（仅切换版本，**不会激活系统**，激活需要你自己另行处理）：" + Environment.NewLine
                + "1. 断开网络；" + Environment.NewLine
                + "2. 打开「设置」→ 搜索「激活」→ 点「更改产品密钥」；" + Environment.NewLine
                + "3. 输入通用密钥 VK7JG-NPHTM-C97JM-9MPGT-3V66T；" + Environment.NewLine
                + "4. 按提示确认并重启，重启后即为专业版。" + Environment.NewLine + Environment.NewLine
                + "也可以输入你自己从其他途径获得的产品密钥，效果相同。",
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
                "系统版本过旧",
                "当前 Windows 没有 Child Session API，无法创建分身",
                "本程序依赖 wtsapi32.dll 的 WTSEnableChildSessions，该接口从 Windows 10 1803 开始提供。"
                + "当前系统上找不到这个导出，桌面分身无法使用。" + Environment.NewLine
                + "请升级到 Windows 10 1803 或更高版本。",
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
                "TermService 已被禁用",
                "远程桌面服务被设为「禁用」，分身无法建立",
                "远程桌面服务（TermService）的启动类型是「禁用」，系统不会自动拉起它，"
                + "桌面分身因此无法建立会话。" + Environment.NewLine
                + "修复会把启动类型改回「手动」并启动服务。这不会让服务开机自启，"
                + "只是允许在需要时被拉起——与 Windows 默认状态一致。",
                Severity.Warning,
                IsBlocking: true,
                CanAutoFix: true));
            return;
        }

        if (state is "Stopped" or "已停止")
        {
            issues.Add(new Issue(
                "termservice-stopped",
                "TermService 没有运行",
                "远程桌面服务处于停止状态",
                "远程桌面服务（TermService）当前没有运行，分身无法建立会话。" + Environment.NewLine
                + "修复会直接启动该服务。",
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
            "远程桌面主机已关闭",
            "fDenyTSConnections = 1，本机不监听 RDP 连接",
            $"注册表 fDenyTSConnections = 1（{(byPolicy ? "组策略" : "本机设置")}），"
            + "本机 RDP 监听器不会启动，桌面分身无法建立会话。" + Environment.NewLine + Environment.NewLine
            + (byPolicy
                ? "该值由组策略下发，改本机注册表会被组策略覆盖，程序无法自动修复。" + Environment.NewLine
                  + "请在 gpedit.msc 的「计算机配置 → 管理模板 → Windows 组件 → 远程桌面服务 → "
                  + "远程桌面会话主机 → 连接」中启用「允许用户通过使用远程桌面服务进行远程连接」，"
                  + "或联系域管理员。"
                : "可以点右侧的「修复」自动改回 0 并重启远程桌面服务。" + Environment.NewLine
                  + "注意：这一项等于「允许别人从网络远程连接这台电脑」，是安全设置的放宽；"
                  + "重启服务还会断开当前所有远程桌面会话（包括正在运行的分身）。"),
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
            "仅允许 Windows Hello 登录",
            "微软账户在本机没有可用的密码凭据",
            "系统已开启「为了提高安全性，仅允许对此设备上的 Microsoft 帐户使用 Windows Hello 登录」，"
            + "此时 Microsoft 帐户在本机没有可用的密码凭据，桌面分身的登录会一直提示凭据无效，"
            + "无论输入什么密码都会失败。" + Environment.NewLine + Environment.NewLine
            + "可以点右侧的「修复」把该开关关掉，但**改完必须注销或重启才生效**——"
            + "凭据提供程序在登录时就已确定，不重新登录不会刷新。" + Environment.NewLine
            + "也可以手动在「设置 → 帐户 → 登录选项」中关闭同名选项。",
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
            "TermService 被第三方补丁接管",
            $"ServiceDll 指向 {Path.GetFileName(serviceDll ?? string.Empty)}，与分身互斥",
            "系统 TermService 的 ServiceDll 指向了非原生的 DLL（当前：" + serviceDll + "）。"
            + "RDP Wrapper、SuperRDP 这类工具就是靠替换这个值来解锁多用户并发远程桌面，"
            + "而桌面分身走的是原生 termsrv.dll 的 Child Session——同一个值只能指向一个 DLL，两者互斥。"
            + Environment.NewLine + Environment.NewLine
            + "修复会做三件事：备份当前值 → 改回原生 termsrv.dll → 停用计划任务「RDP Wrapper Autoupdate」"
            + "（不停用的话它会在开机时把补丁写回去）→ 重启远程桌面服务。" + Environment.NewLine
            + "代价是：还原后多用户并发远程桌面的能力就没有了，需要重新安装补丁工具才能恢复。",
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
