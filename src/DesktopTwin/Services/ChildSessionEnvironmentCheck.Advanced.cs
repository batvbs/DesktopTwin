using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using DesktopTwin.Interop;
using DesktopTwin.Localization;
using Microsoft.Win32;

namespace DesktopTwin.Services;

/// <summary>
/// 环境预检的第二组结论：**RDP 破解工具留下的残渣**。
///
/// 第一组（<c>ChildSessionEnvironmentCheck.cs</c>）回答「Windows 本身能不能建立子会话」；
/// 这一组回答「有没有别的工具把这个入口改坏、或者会在背后改回去」。
///
/// 依据来自对 RDPTools 类工具包的实测分析：
/// - 它们的共同手法是把 TermService 的 <c>ServiceDll</c> 指向自己的 DLL，
///   但**整键覆盖注册表**的改版还会顺手冲掉服务自身的 <c>ImagePath</c>/<c>DependOnService</c>；
/// - 其中带自更新的版本会建一个开机任务，把配置写回去——只还原 DLL 而不清持久化，
///   用户看到的就是「修好了，重启又坏」；
/// - 少数改版直接给 <c>termsrv.dll</c> 打二进制补丁，此时 ServiceDll 指向原生也没用。
///
/// 与第一组一样：这里只做检测，改动在 <see cref="EnvironmentFixer"/>。
/// </summary>
public static partial class ChildSessionEnvironmentCheck
{
    /// <summary>
    /// TermService 服务键本身（不是 Parameters 子键）。
    /// </summary>
    internal const string TermServiceKeyPath = @"SYSTEM\CurrentControlSet\Services\TermService";

    /// <summary>
    /// RDP 监听器的安全参数所在位置。
    /// </summary>
    internal const string RdpTcpRegistryPath =
        @"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp";

    /// <summary>
    /// 破解工具的特征关键词（任务名、任务内容、自启项命令行）。
    ///
    /// 刻意把 <c>rdpwupdater</c>、<c>rdp_cnc</c> 这类换名版本也列进来：
    /// 只认「RDP Wrapper Autoupdate」一个名字，改个名就漏了。
    /// </summary>
    private static readonly string[] ToolKeywords =
    [
        "rdpwrap",
        "rdp wrapper",
        "rdp-wrapper",
        "rdp_wrapper",
        "rdpwrapper",
        "superrdp",
        "termwrap",
        "umwrap",
        "endpwrap",
        "rdpwinst",
        "rdpwupdater",
        "rdp_cnc",
        "rdp-cnc",
        "rdp-switch",
        "autoupdate.bat",
        "rdpwrap.ini"
    ];

    /// <summary>
    /// 直接改过 termsrv.dll 的工具会留下的备份文件。
    /// </summary>
    private static readonly string[] TermsrvBackupNames =
    [
        ".termsrv.dll",
        "termsrv.dll.bak",
        "termsrv.dll.orig",
        "termsrv.dll.rdpwrap",
        "termsrv.dll.uninstall_bak",
        "termsrv.dll.old"
    ];

    /// <summary>ServiceDll 值本身的状态。</summary>
    /// <param name="Exists">值是否存在。</param>
    /// <param name="Kind">值类型。</param>
    /// <param name="Value">原始字符串值（REG_SZ / REG_EXPAND_SZ 才有）。</param>
    internal readonly record struct ServiceDllEntry(bool Exists, RegistryValueKind Kind, string? Value);

    /// <summary>TermService 服务键里与系统默认不符的字段。</summary>
    internal sealed record ServiceKeyDeviation(string Field, string Current, string Expect);

    /// <summary>RDP-Tcp 里被降低的安全参数。</summary>
    internal sealed record RdpSecurityDowngrade(string Name, int Current, int Expect);

    /// <summary>一处 RDP 破解工具的持久化项。</summary>
    /// <param name="Kind">「task」或「run」。</param>
    /// <param name="Target">显示用：任务全路径，或「注册表位置 → 值名」。</param>
    /// <param name="Reason">命中的原因，给用户看。</param>
    /// <param name="TaskPath">计划任务的全路径（Kind=task）。</param>
    /// <param name="RegistryRoot">注册表根：HKLM / HKCU / HKU（Kind=run）。</param>
    /// <param name="RegistryPath">注册表子键路径（Kind=run；HKU 时含 SID）。</param>
    /// <param name="ValueName">值名（Kind=run）。</param>
    internal sealed record ToolPersistenceHit(
        string Kind,
        string Target,
        string Reason,
        string TaskPath = "",
        string RegistryRoot = "",
        string RegistryPath = "",
        string ValueName = "");

    // ---------------- 对外数据（修复器要用） ----------------

    /// <summary>读 ServiceDll 值（含类型）。</summary>
    internal static ServiceDllEntry GetServiceDllEntry()
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(TermServiceParametersRegistryPath);
            if (key is null)
            {
                return new ServiceDllEntry(false, RegistryValueKind.Unknown, null);
            }

            var names = key.GetValueNames();
            foreach (var name in names)
            {
                if (!string.Equals(name, "ServiceDll", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return new ServiceDllEntry(
                    true,
                    key.GetValueKind(name),
                    key.GetValue(name) as string);
            }

            return new ServiceDllEntry(false, RegistryValueKind.Unknown, null);
        }
        catch (Exception exception) when (exception is SecurityException
                                              or UnauthorizedAccessException
                                              or IOException)
        {
            return new ServiceDllEntry(false, RegistryValueKind.Unknown, null);
        }
    }

    /// <summary>
    /// 列出 TermService 服务键里与系统默认不符的字段。
    ///
    /// 只查三类真正会让服务起不来的：可执行映像、运行身份、依赖服务。
    /// <c>FailureActions</c> 一律不动：系统默认是存在且带重启动作的，
    /// 早期的一些还原脚本把它删掉，反而让服务崩溃后不再自愈。
    /// </summary>
    internal static IReadOnlyList<ServiceKeyDeviation> GetServiceKeyDeviations()
    {
        var deviations = new List<ServiceKeyDeviation>();

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var serviceKey = baseKey.OpenSubKey(TermServiceKeyPath);
            if (serviceKey is null)
            {
                // 服务键整个不见了，这是更严重的问题，交给「TermService 没运行」那条结论。
                return deviations;
            }

            var imagePath = serviceKey.GetValue("ImagePath") as string;
            if (!string.IsNullOrWhiteSpace(imagePath) && !LooksLikeSvchostNetworkService(imagePath))
            {
                deviations.Add(new ServiceKeyDeviation(
                    "ImagePath",
                    imagePath,
                    @"%SystemRoot%\System32\svchost.exe -k NetworkService"));
            }

            var objectName = serviceKey.GetValue("ObjectName") as string;
            if (!string.IsNullOrWhiteSpace(objectName)
                && objectName.IndexOf("NetworkService", StringComparison.OrdinalIgnoreCase) < 0)
            {
                deviations.Add(new ServiceKeyDeviation(
                    "ObjectName",
                    objectName,
                    @"NT AUTHORITY\NetworkService"));
            }

            if (serviceKey.GetValue("DependOnService") is string[] dependencies && dependencies.Length > 0)
            {
                if (!Contains(dependencies, "RPCSS"))
                {
                    deviations.Add(new ServiceKeyDeviation(
                        "DependOnService",
                        string.Join(",", dependencies),
                        "RPCSS"));
                }
                else if (HasServiceKey(baseKey, "StateRepository")
                         && !Contains(dependencies, "StateRepository"))
                {
                    // Win11 的 TermService 依赖 StateRepository；整键覆盖的 .reg 会把它冲掉。
                    deviations.Add(new ServiceKeyDeviation(
                        "DependOnService",
                        string.Join(",", dependencies),
                        "RPCSS,StateRepository"));
                }
            }
        }
        catch (Exception exception) when (exception is SecurityException
                                              or UnauthorizedAccessException
                                              or IOException)
        {
            AppLog.Warn("读取 TermService 服务键失败。", exception);
        }

        return deviations;
    }

    /// <summary>
    /// 列出被降低的 RDP 安全参数。**只报「低于系统默认」的项**，已经更严格的保持不动。
    /// </summary>
    internal static IReadOnlyList<RdpSecurityDowngrade> GetRdpSecurityDowngrades()
    {
        var result = new List<RdpSecurityDowngrade>();

        // UserAuthentication=1（NLA）、SecurityLayer=2（协商）、MinEncryptionLevel=2（客户端兼容）
        Add(RdpTcpRegistryPath, "UserAuthentication", 1);
        Add(RdpTcpRegistryPath, "SecurityLayer", 2);
        Add(RdpTcpRegistryPath, "MinEncryptionLevel", 2);
        return result;

        void Add(string path, string name, int expected)
        {
            var current = ReadInt(RegistryHive.LocalMachine, path, name);
            if (current is { } value && value < expected)
            {
                result.Add(new RdpSecurityDowngrade(name, value, expected));
            }
        }
    }

    /// <summary>
    /// 扫描 RDP 破解工具留下的持久化项。
    /// </summary>
    internal static IReadOnlyList<ToolPersistenceHit> GetToolPersistenceHits()
    {
        var hits = new List<ToolPersistenceHit>();
        hits.AddRange(GetCachedTaskHits());
        hits.AddRange(GetRunKeyHits());
        return hits;
    }

    /// <summary>
    /// 清掉缓存。修复动作完成后调用，避免面板还显示旧结论。
    /// </summary>
    internal static void InvalidateScanCache()
    {
        lock (ScanGate)
        {
            _taskScanAt = DateTime.MinValue;
        }
    }

    // ---------------- 五条新结论 ----------------

    /// <summary>
    /// ServiceDll 本身写坏了：值缺失，或者类型不是 REG_EXPAND_SZ。
    ///
    /// 类型错的影响容易被忽略：写成 REG_SZ 时 <c>%SystemRoot%</c> 不会被展开，
    /// svchost 拿到的就是字面量路径，模块加载失败 —— 表现为「TermService 起不来」，
    /// 而只去 start 服务的修复永远修不好。
    /// </summary>
    private static void CollectServiceDllTypeIssue(List<Issue> issues)
    {
        var entry = GetServiceDllEntry();

        // 路径指向第三方 DLL 的情况交给 rdp-wrapper：那边修完同样是 REG_EXPAND_SZ。
        if (entry.Exists && IsServiceDllHijacked(entry.Value))
        {
            return;
        }

        if (entry.Exists && entry.Kind == RegistryValueKind.ExpandString)
        {
            return;
        }

        issues.Add(new Issue(
            "termservice-dll-type",
            Locale.Get("Check.DllType.Title"),
            entry.Exists
                ? Locale.Format("Check.DllType.Summary.Kind", entry.Kind.ToString())
                : Locale.Get("Check.DllType.Summary.Missing"),
            entry.Exists
                ? Locale.Format("Check.DllType.Detail.Kind", entry.Value ?? string.Empty, entry.Kind.ToString())
                : Locale.Get("Check.DllType.Detail.Missing"),
            Severity.Warning,
            IsBlocking: true,
            CanAutoFix: true));
    }

    /// <summary>
    /// TermService 服务键被整键覆盖：可执行映像、运行身份或依赖服务不对。
    /// </summary>
    private static void CollectServiceKeyIssue(List<Issue> issues)
    {
        var deviations = GetServiceKeyDeviations();
        if (deviations.Count == 0)
        {
            return;
        }

        var builder = new StringBuilder();
        foreach (var deviation in deviations)
        {
            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(Locale.Format(
                "Check.ServiceKey.Detail.Item",
                deviation.Field,
                deviation.Current,
                deviation.Expect));
        }

        issues.Add(new Issue(
            "termservice-key-broken",
            Locale.Get("Check.ServiceKey.Title"),
            Locale.Format(
                "Check.ServiceKey.Summary",
                deviations.Count,
                string.Join(Locale.Get("Common.ListSeparator"), deviations.Select(d => d.Field).Distinct())),
            Locale.Format("Check.ServiceKey.Detail", builder.ToString()),
            Severity.Warning,
            IsBlocking: true,
            CanAutoFix: true));
    }

    /// <summary>
    /// 发现 RDP 破解工具的持久化项（开机任务 / 自启项）。
    /// </summary>
    private static void CollectToolPersistenceIssue(List<Issue> issues)
    {
        var hits = GetToolPersistenceHits();
        if (hits.Count == 0)
        {
            return;
        }

        var tasks = 0;
        var runs = 0;
        var builder = new StringBuilder();
        foreach (var hit in hits)
        {
            if (hit.Kind == "task")
            {
                tasks++;
            }
            else
            {
                runs++;
            }

            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append("· ").Append(hit.Target).Append(" — ").Append(hit.Reason);
        }

        issues.Add(new Issue(
            "rdp-tool-persistence",
            Locale.Get("Check.Persistence.Title"),
            Locale.Format("Check.Persistence.Summary", tasks, runs),
            Locale.Format("Check.Persistence.Detail", builder.ToString()),
            Severity.Notice,
            IsBlocking: false,
            CanAutoFix: true));
    }

    /// <summary>
    /// RDP-Tcp 的安全参数被降低。
    /// </summary>
    private static void CollectRdpSecurityIssue(List<Issue> issues)
    {
        var downgrades = GetRdpSecurityDowngrades();
        if (downgrades.Count == 0)
        {
            return;
        }

        var builder = new StringBuilder();
        foreach (var downgrade in downgrades)
        {
            if (builder.Length > 0)
            {
                builder.Append('、');
            }

            builder.Append(downgrade.Name).Append('=').Append(downgrade.Current);
        }

        issues.Add(new Issue(
            "rdp-security-downgraded",
            Locale.Get("Check.RdpSecurity.Title"),
            Locale.Format("Check.RdpSecurity.Summary", builder.ToString()),
            Locale.Format(
                "Check.RdpSecurity.Detail",
                string.Join("\n", downgrades.Select(
                    d => Locale.Format("Check.RdpSecurity.Detail.Item", d.Name, d.Current, d.Expect)))),
            Severity.Notice,
            IsBlocking: false,
            CanAutoFix: true));
    }

    /// <summary>
    /// termsrv.dll 被直接打过补丁（签名校验失败）或干脆不存在。
    /// </summary>
    private static void CollectTermsrvIssue(List<Issue> issues)
    {
        var state = GetTermsrvState();

        // 未验证（没提权、sfc 跑不起来）时不下结论：宁可漏报，不可误报。
        if (state.Exists && (!state.Verified || state.Intact))
        {
            return;
        }

        var backups = state.Backups.Count == 0
            ? Locale.Get("Check.Termsrv.NoBackup")
            : Locale.Format("Check.Termsrv.Backups", string.Join("、", state.Backups));

        issues.Add(new Issue(
            "termsrv-dll-tampered",
            Locale.Get("Check.Termsrv.Title"),
            state.Exists
                ? Locale.Get("Check.Termsrv.Summary.Dirty")
                : Locale.Get("Check.Termsrv.Summary.Missing"),
            Locale.Format(
                state.Exists ? "Check.Termsrv.Detail.Dirty" : "Check.Termsrv.Detail.Missing",
                backups),
            Severity.Warning,

            // 不阻塞：签名异常也可能是「精简版系统」自己改过文件，未必真的连不上，
            // 让用户先试一次比直接拦下来更合适。
            IsBlocking: false,
            CanAutoFix: true));
    }

    // ---------------- CollectAll 的补充条目 ----------------

    /// <summary>
    /// 生成一条「已触发」或「未触发」的记录。
    ///
    /// 未触发时 <c>blocking</c> 一律给 false：<c>IsBlocking</c> 描述的是「这一项真的触发时有多严重」，
    /// 没触发就没有意义，界面也不会拿未触发项去做阻塞判定。
    /// </summary>
    private static void AddOrInactive(
        List<Issue> all,
        Dictionary<string, Issue> active,
        string id,
        string titleKey,
        string summaryKey,
        string inactiveDetailKey,
        object? summaryValue,
        object? detailValue,
        bool blocking)
    {
        if (active.TryGetValue(id, out var found))
        {
            all.Add(found);
            return;
        }

        all.Add(new Issue(
            id,
            Locale.Get(titleKey),
            Locale.Format("Check.Inactive.Summary", Locale.Format(summaryKey, summaryValue)),
            Locale.Format(inactiveDetailKey, detailValue),
            Severity.Inactive,
            IsBlocking: false,

            // 未触发就没有可修的东西，一律不给「修复」按钮。
            CanAutoFix: false));
    }

    /// <summary>
    /// 把五条新结论补进「全部检查项」视图。
    /// </summary>
    /// <param name="all">结果列表。</param>
    /// <param name="active">当前已触发的问题（按 id）。</param>
    private static void AppendAdvancedSpecs(List<Issue> all, Dictionary<string, Issue> active)
    {
        var dllEntry = GetServiceDllEntry();
        AddOrInactive(
            all, active,
            "termservice-dll-type",
            "Check.DllType.Title",
            "Check.DllType.InactiveSummary",
            "Check.DllType.InactiveDetail",
            dllEntry.Exists ? dllEntry.Kind.ToString() : Locale.Get("Check.None"),
            dllEntry.Exists ? dllEntry.Kind.ToString() : Locale.Get("Check.None"),
            blocking: true);

        var deviations = GetServiceKeyDeviations();
        AddOrInactive(
            all, active,
            "termservice-key-broken",
            "Check.ServiceKey.Title",
            "Check.ServiceKey.InactiveSummary",
            "Check.ServiceKey.InactiveDetail",
            deviations.Count == 0 ? Locale.Get("Check.None") : deviations.Count.ToString(),
            deviations.Count == 0 ? Locale.Get("Check.None") : deviations.Count.ToString(),
            blocking: true);

        var persistence = GetToolPersistenceHits();
        AddOrInactive(
            all, active,
            "rdp-tool-persistence",
            "Check.Persistence.Title",
            "Check.Persistence.InactiveSummary",
            "Check.Persistence.InactiveDetail",
            persistence.Count,
            persistence.Count,
            blocking: false);

        var downgrades = GetRdpSecurityDowngrades();
        AddOrInactive(
            all, active,
            "rdp-security-downgraded",
            "Check.RdpSecurity.Title",
            "Check.RdpSecurity.InactiveSummary",
            "Check.RdpSecurity.InactiveDetail",
            downgrades.Count == 0 ? Locale.Get("Check.None") : downgrades.Count.ToString(),
            downgrades.Count == 0 ? Locale.Get("Check.None") : downgrades.Count.ToString(),
            blocking: false);

        var termsrv = GetTermsrvState();
        AddOrInactive(
            all, active,
            "termsrv-dll-tampered",
            "Check.Termsrv.Title",
            "Check.Termsrv.InactiveSummary",
            "Check.Termsrv.InactiveDetail",
            DescribeTermsrvState(termsrv),
            DescribeTermsrvState(termsrv),
            blocking: false);
    }


    private static string DescribeTermsrvState(TermsrvFileState state)
    {
        if (!state.Exists)
        {
            return Locale.Get("Check.Termsrv.State.Missing");
        }

        if (!state.Verified)
        {
            return Locale.Get("Check.Termsrv.State.Unknown");
        }

        return state.Intact
            ? Locale.Get("Check.Termsrv.State.Intact")
            : Locale.Get("Check.Termsrv.State.Dirty");
    }

    // ---------------- 扫描实现 ----------------

    private static readonly object ScanGate = new();
    private static DateTime _taskScanAt = DateTime.MinValue;
    private static IReadOnlyList<ToolPersistenceHit> _taskScanCache = Array.Empty<ToolPersistenceHit>();

    /// <summary>任务扫描结果缓存 30 秒：界面每 2 秒刷一次，不能每轮都读几百个 XML。</summary>
    private static readonly TimeSpan TaskScanTtl = TimeSpan.FromSeconds(30);

    private static IReadOnlyList<ToolPersistenceHit> GetCachedTaskHits()
    {
        lock (ScanGate)
        {
            if (DateTime.UtcNow - _taskScanAt < TaskScanTtl)
            {
                return _taskScanCache;
            }

            _taskScanCache = ScanScheduledTasks();
            _taskScanAt = DateTime.UtcNow;
            return _taskScanCache;
        }
    }

    /// <summary>
    /// 扫描计划任务。
    ///
    /// 任务定义就是 <c>%SystemRoot%\System32\Tasks</c> 下的 XML 文件，直接读文件比解析
    /// <c>schtasks</c> 的本地化输出可靠。命中规则两条：
    /// 1. 任务名含关键词；
    /// 2. 任务 XML 内容含关键词（覆盖「改名但还指向 autoupdate.bat」的情况）。
    ///
    /// 内容只读 Microsoft 子树以外的文件：系统自带任务有一千多个，
    /// 全套读一遍会让每轮检测明显变慢，而破解工具不会把任务藏在 Microsoft 子树里。
    /// </summary>
    private static IReadOnlyList<ToolPersistenceHit> ScanScheduledTasks()
    {
        var hits = new List<ToolPersistenceHit>();

        try
        {
            var tasksRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "Tasks");

            if (!Directory.Exists(tasksRoot))
            {
                return hits;
            }

            foreach (var file in Directory.EnumerateFiles(tasksRoot, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(tasksRoot, file);
                var taskName = Path.GetFileName(file);

                if (ContainsKeyword(taskName))
                {
                    var taskPath = @"\" + relative.Replace('/', '\\');
                    hits.Add(new ToolPersistenceHit(
                        "task",
                        taskPath,
                        Locale.Get("Check.Persistence.Reason.Name"),
                        TaskPath: taskPath));
                    continue;
                }

                // 内容检查只做非 Microsoft 子树，理由见方法注释。
                if (relative.StartsWith("Microsoft" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var content = TryReadTextFile(file);
                if (content is null || !ContainsKeyword(content))
                {
                    continue;
                }

                var contentTaskPath = @"\" + relative.Replace('/', '\\');
                hits.Add(new ToolPersistenceHit(
                    "task",
                    contentTaskPath,
                    Locale.Get("Check.Persistence.Reason.Content"),
                    TaskPath: contentTaskPath));
            }
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or SecurityException)
        {
            AppLog.Warn("扫描计划任务失败。", exception);
        }

        return hits;
    }

    /// <summary>
    /// 扫描自启项（Run / RunOnce / 策略 Run，含其它已加载用户的 HKCU）。
    /// </summary>
    private static IReadOnlyList<ToolPersistenceHit> GetRunKeyHits()
    {
        var hits = new List<ToolPersistenceHit>();

        var locations = new List<(RegistryKey Root, string Path, string Label)>
        {
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "HKLM"),
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", "HKLM"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "HKLM"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce", "HKLM"),
            (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "HKCU"),
            (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", "HKCU"),
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run", "HKLM"),
            (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run", "HKCU")
        };

        // 其它用户：值写在 HKU\<SID>\...\Run 里时，当前用户的 HKCU 看不到。
        try
        {
            foreach (var sid in Registry.Users.GetSubKeyNames())
            {
                if (!sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var suffix in new[] { @"\Run", @"\RunOnce" })
                {
                    locations.Add((
                        Registry.Users,
                        sid + @"\SOFTWARE\Microsoft\Windows\CurrentVersion" + suffix,
                        "HKU\\" + sid));
                }
            }
        }
        catch (Exception exception) when (exception is SecurityException
                                              or UnauthorizedAccessException
                                              or IOException)
        {
            // 枚举 HKU 需要权限，拿不到就跳过，不影响主流程。
            AppLog.Warn("枚举其它用户的自启项失败。", exception);
        }

        foreach (var (root, path, label) in locations)
        {
            try
            {
                using var key = root.OpenSubKey(path);
                if (key is null)
                {
                    continue;
                }

                foreach (var name in key.GetValueNames())
                {
                    if (key.GetValue(name) is not string data || string.IsNullOrWhiteSpace(data))
                    {
                        continue;
                    }

                    if (!ContainsKeyword(name) && !ContainsKeyword(data))
                    {
                        continue;
                    }

                    hits.Add(new ToolPersistenceHit(
                        "run",
                        $"{label}\\{path} → {name}",
                        data,
                        RegistryRoot: root == Registry.CurrentUser ? "HKCU"
                            : root == Registry.Users ? "HKU"
                            : "HKLM",
                        RegistryPath: path,
                        ValueName: name));
                }
            }
            catch (Exception exception) when (exception is SecurityException
                                                  or UnauthorizedAccessException
                                                  or IOException)
            {
                // 单个位置读不到不影响其它位置。
                AppLog.Warn($"读取自启项 {label}\\{path} 失败。", exception);
            }
        }

        return hits;
    }

    private static string? TryReadTextFile(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0)
            {
                return string.Empty;
            }

            // 任务 XML 通常是 UTF-16 LE（带 BOM）
            if (bytes.Length > 1 && (bytes[0] == 0xFF && bytes[1] == 0xFE
                                     || bytes[0] == 0x3C && bytes[1] == 0x00))
            {
                return Encoding.Unicode.GetString(bytes);
            }

            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or SecurityException)
        {
            return null;
        }
    }

    private static bool ContainsKeyword(string text)
    {
        foreach (var keyword in ToolKeywords)
        {
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksLikeSvchostNetworkService(string imagePath)
    {
        var expanded = Environment.ExpandEnvironmentVariables(imagePath);
        if (expanded.IndexOf("svchost.exe", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        return expanded.IndexOf("-k", StringComparison.OrdinalIgnoreCase) >= 0
               && expanded.IndexOf("NetworkService", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool Contains(string[] values, string expected)
    {
        foreach (var value in values)
        {
            if (string.Equals(value, expected, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasServiceKey(RegistryKey baseKey, string serviceName)
    {
        try
        {
            using var key = baseKey.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + serviceName);
            return key is not null;
        }
        catch (Exception exception) when (exception is SecurityException
                                              or UnauthorizedAccessException
                                              or IOException)
        {
            return false;
        }
    }
}
