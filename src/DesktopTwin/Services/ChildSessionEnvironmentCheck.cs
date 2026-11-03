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
/// 所有检查都是只读的，不会修改任何系统设置。
/// </summary>
public static class ChildSessionEnvironmentCheck
{
    /// <summary>
    /// 「仅允许 Windows Hello 登录」设置所在路径。
    /// </summary>
    private const string PasswordLessRegistryPath =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\PasswordLess\Device";

    /// <summary>
    /// 远程桌面主机的本机设置所在路径。
    /// </summary>
    private const string TerminalServerRegistryPath =
        @"SYSTEM\CurrentControlSet\Control\Terminal Server";

    /// <summary>
    /// 远程桌面开关的组策略路径。该键存在时会覆盖本机设置。
    /// </summary>
    private const string TerminalServerPolicyRegistryPath =
        @"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services";

    /// <summary>
    /// 系统版本信息所在路径。
    /// </summary>
    private const string CurrentVersionRegistryPath =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    /// <summary>
    /// DevicePasswordLessBuildVersion 为 2 时，系统仅允许 Windows Hello 登录。
    /// 注意：该注册表值未见于微软官方文档，属于社区逆向得出的结论，
    /// 因此只把 2 判定为「已开启」，其余取值一律视为未开启。
    /// </summary>
    private const int PasswordLessHelloOnlyValue = 2;

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
    /// 一条环境检查结论。<see cref="IsBlocking"/> 为 true 表示当前系统不可能成功。
    /// </summary>
    /// <param name="Title">结论标题。</param>
    /// <param name="Message">结论详情与修复建议。</param>
    /// <param name="IsBlocking">是否属于必然失败的阻塞项。</param>
    public sealed record Issue(string Title, string Message, bool IsBlocking = false);

    /// <summary>
    /// 收集当前系统的环境结论。读取失败（键或值不存在、无权限）时跳过该项，不抛异常。
    /// </summary>
    public static IReadOnlyList<Issue> Collect()
    {
        var issues = new List<Issue>();
        CollectEditionIssue(issues);
        CollectRdpHostIssue(issues);
        CollectPasswordLessIssue(issues);
        CollectRdpWrapperIssue(issues);
        return issues;
    }

    /// <summary>
    /// 是否存在「必然失败」的环境问题。界面据此直接拒绝启动，而不是让用户白等一次连接超时。
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
            builder.Append(issue.Message);
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
        builder.AppendLine($"RDP Wrapper：{(ChildSessionNativeMethods.IsRdpWrapperEnabled() ? "已启用" : "未启用")}");

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
    /// 检查 Windows 版本。
    ///
    /// 微软官方文档说明家庭版不支持远程桌面主机，但实测大部分家庭版仍可建立 Child Session，
    /// 因此这里只作为**强提示**而非阻塞项，避免把本来能用的用户挡在门外。
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
                "当前系统为家庭版",
                $"检测到系统版本为 {editionId}（家庭版）。微软官方文档说明家庭版不包含远程桌面主机（RDP 服务端），"
                + "桌面分身在该版本上属于「不受支持」状态。" + Environment.NewLine
                + "实测大部分家庭版系统仍可正常建立分身会话，因此这里不做阻止。"
                + "如果启动后一直连不上，请优先怀疑版本限制，并考虑升级到专业版 / 企业版 / 教育版。"));
            return;
        }
    }

    /// <summary>
    /// 检查远程桌面主机是否被关闭。组策略中的值会覆盖本机设置，因此优先读取组策略。
    /// </summary>
    private static void CollectRdpHostIssue(List<Issue> issues)
    {
        // 组策略中的值会覆盖本机设置，因此优先读取组策略。
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

        // 组策略来源时，改本机注册表没有意义，必须给出不同的修复路径。
        var suggestion = policyValue is not null
            ? "该值由组策略下发，修改本机注册表会被组策略覆盖。"
              + "请在 gpedit.msc 的「计算机配置 - 管理模板 - Windows 组件 - 远程桌面服务 - "
              + "远程桌面会话主机 - 连接」中启用「允许用户通过使用远程桌面服务进行远程连接」，"
              + "或联系域管理员。"
            : "可以尝试在管理员权限下执行：" + Environment.NewLine
              + "Set-ItemProperty -Path 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Terminal Server' "
              + "-Name fDenyTSConnections -Value 0 -Type DWord" + Environment.NewLine
              + "Restart-Service TermService -Force";

        issues.Add(new Issue(
            "远程桌面主机已关闭",
            $"注册表 fDenyTSConnections = 1（{(policyValue is not null ? "组策略" : "本机设置")}），"
            + "本机 RDP 监听器不会启动，桌面分身可能无法建立会话。" + Environment.NewLine
            + suggestion));
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
            "仅允许 Windows Hello 登录",
            "系统已开启「为了提高安全性，仅允许对此设备上的 Microsoft 帐户使用 Windows Hello 登录」，"
            + "此时 Microsoft 帐户在本机没有可用的密码凭据，桌面分身的登录会一直提示凭据无效，"
            + "无论输入什么密码都会失败。" + Environment.NewLine
            + "可在「设置 - 帐户 - 登录选项」中关闭该选项，然后注销并重新登录（部分情况需要重启系统）。"));
    }

    /// <summary>
    /// 检查是否安装了 RDP Wrapper。它会劫持 TermService，与桌面分身功能不兼容。
    /// </summary>
    private static void CollectRdpWrapperIssue(List<Issue> issues)
    {
        if (!ChildSessionNativeMethods.IsRdpWrapperEnabled())
        {
            return;
        }

        issues.Add(new Issue(
            "检测到 RDP Wrapper",
            "系统 TermService 已被 RDP Wrapper 替换（rdpwrap.dll）。RDP Wrapper 会改写 RDP 会话行为，"
            + "与桌面分身使用的 Child Session 不兼容，通常表现为连接一直失败或画面异常。" + Environment.NewLine
            + "如需使用桌面分身，请先卸载 RDP Wrapper 并恢复系统原始 TermService 配置。"));
    }

    /// <summary>
    /// 读取注册表 DWORD 值。键或值不存在、类型不符、无权限时返回 null。
    /// </summary>
    private static int? ReadInt(RegistryHive hive, string path, string name)
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
    private static string? ReadString(RegistryHive hive, string path, string name)
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
