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
        return GetToolPersistenceHits(out _);
    }

    /// <summary>
    /// 取持久化命中，并把这一趟的扫描完整性一并带出。
    ///
    /// 用返回值而不是再读共享标记：缓存失效会把标记清掉，调用方据此判断就会
    /// 把「没扫全」当成「扫全了」。
    /// </summary>
    internal static IReadOnlyList<ToolPersistenceHit> GetToolPersistenceHits(out bool scanIncomplete)
    {
        // 先记下代际，再开始扫描：扫描期间别处可能发现新的篡改（代际会变），
        // 那种情况下这轮的「干净」结论已经过期。
        int scanGeneration;
        lock (ObligationGate)
        {
            scanGeneration = _obligationGeneration;
        }

        var deep = ShouldDeepScanPersistence();
        var hits = new List<ToolPersistenceHit>(
            GetCachedTaskHits(deep, out var scannedNow, out var taskScanIncomplete));
        hits.AddRange(GetRunKeyHits(out var runIncomplete));

        // 每轮都按这一趟的结果赋值。只在 true 时置位的话，某个 Run 位置读失败过一次
        // 之后标记永远清不掉——目录后来恢复可读，界面还在说「扫描不完整」。
        lock (ScanGate)
        {
            _runScanIncomplete = runIncomplete;
        }

        ReleaseStickyDeepScanIfClean(
            hits,
            deep && scannedNow,
            taskScanIncomplete,
            runIncomplete,
            scanGeneration);

        scanIncomplete = taskScanIncomplete || runIncomplete;
        return hits;
    }

    /// <summary>
    /// 记录「这台机器被改过」：立刻置位深扫义务并持久化。
    ///
    /// 由「发现篡改」的地方直接调用（诊断出结论、开始修复），而不是等持久化扫描
    /// 去重新发现——那一步看到的可能是已被复核覆盖掉的「完好」状态。
    /// </summary>
    internal static void MarkTamperingObserved()
    {
        lock (ObligationGate)
        {
            // 每次都推进代际：在途的旧扫描不能拿它那轮「当时干净」的结论来清义务。
            _obligationGeneration++;

            if (_persistenceDeepScanSticky)
            {
                return;
            }

            _persistenceDeepScanSticky = true;
            DeepScanObligationChanged?.Invoke(true);
        }
    }

    /// <summary>
    /// 是否需要连 Microsoft 子树里的任务一起读内容。
    ///
    /// 判据是 termsrv.dll 被 sfc 判定为「与组件库不一致」：这类工具把补丁写回去的唯一
    /// 手段就是开机任务，文件真被改过时才值得为它多读一千多个系统任务（面板每次打开
    /// 都会跑一遍）。没被改过时深扫只是白等。
    ///
    /// 检测和修复共用这一个判断：修复前会重新取一次命中项，天然带上同样的深度。
    /// </summary>
    private static bool ShouldDeepScanPersistence()
    {
        var state = GetTermsrvState();

        // 文件不存在也算「被工具动过」：这类工具会先把 termsrv.dll 改名备份再写补丁，
        // 补丁文件缺失时正好是它最可能留下还原任务的时刻。只判「存在且被篡改」的话，
        // 首次检测就漏掉藏在系统子树里的那些任务，而清理又发生在修复之前。
        if (!state.Exists || (state.Verified && !state.Intact))
        {
            // 确认被篡改（或文件已被工具拿走）：从现在起深扫，直到深扫确认全清。
            lock (ObligationGate)
            {
                // 每次检测到都推进代际，哪怕标志已经是 true：这样在途的旧扫描
                // 就无法用它那轮「当时是干净的」结论来清除义务。
                _obligationGeneration++;

                if (!_persistenceDeepScanSticky)
                {
                    _persistenceDeepScanSticky = true;
                    DeepScanObligationChanged?.Invoke(true);
                }
            }
        }

        return _persistenceDeepScanSticky;
    }

    /// <summary>
    /// 文件已经还原、且深扫一次没再发现任何命中，才允许退回浅扫。
    ///
    /// 只要还留着命中项（例如修复时删不掉的那个任务），就继续深扫——否则提示会随着
    /// 文件被还原一起消失，而任务在下次开机时会把补丁再写回去。
    /// </summary>
    /// <param name="freshDeepScan">
    /// 这一轮是不是真的跑了一次深扫。缓存命中的空结果可能早于某次任务变动，
    /// 不能拿它当「干净」的证据——那会让标记提前释放，之后浅扫漏掉子树里的任务。
    /// </param>
    private static void ReleaseStickyDeepScanIfClean(
        IReadOnlyList<ToolPersistenceHit> hits,
        bool freshDeepScan,
        bool taskScanIncomplete,
        bool runScanIncomplete,
        int scanGeneration)
    {
        if (!_persistenceDeepScanSticky || hits.Count > 0 || !freshDeepScan)
        {
            return;
        }

        // 扫不完整时的零命中不等于干净：读不到的目录（或自启位置）里
        // 可能正好藏着那个任务。
        //
        // 用调用方传进来的本轮结果，而不是共享字段：缓存失效会把字段清掉，
        // 用它判断会让「这轮其实没扫全」的扫描通过检查。
        if (taskScanIncomplete || runScanIncomplete)
        {
            return;
        }

        var state = GetTermsrvState();
        if (!state.Exists || !state.Verified || !state.Intact)
        {
            return;
        }

        lock (ObligationGate)
        {
            // 这轮扫描开始之后，别处已经发现过新的篡改：我看到的「干净」不再是
            // 最新事实，清除会把那份新义务一起抹掉。
            if (scanGeneration != _obligationGeneration)
            {
                AppLog.Info("扫描结论已过期，本次不解除深扫义务。");
                return;
            }

            _persistenceDeepScanSticky = false;
            DeepScanObligationChanged?.Invoke(false);
        }
    }

    /// <summary>
    /// 清掉缓存。修复动作完成后调用，避免面板还显示旧结论。
    /// </summary>
    internal static void InvalidateScanCache()
    {
        lock (ScanGate)
        {
            _taskScanAt = DateTime.MinValue;
            _taskScanDeep = false;
            _taskScanIncomplete = false;
            _runScanIncomplete = false;
        }

        // 用户点「重新检测」或修复完成时，termsrv.dll 的 5 分钟缓存也要作废：
        // 否则用户在别处把文件修好了，这里还会显示旧警告和一个点了也修不动的按钮。
        InvalidateTermsrvCache();
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
        var hits = GetToolPersistenceHits(out var scanIncomplete);

        // 只要没扫全就单独报一条，不管有没有命中。
        // 有命中时如果只在上面那条里说「部分路径被拒」，一旦清掉这几个命中，
        // 没扫到的那部分就再也没有任何提示了。
        if (scanIncomplete)
        {
            issues.Add(new Issue(
                "rdp-tool-persistence-incomplete",
                Locale.Get("Check.Persistence.IncompleteTitle"),
                Locale.Get("Check.Persistence.IncompleteSummary"),
                Locale.Get("Check.Persistence.IncompleteDetail"),
                Severity.Notice,
                IsBlocking: false,
                CanAutoFix: false));
        }

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
            Locale.Format(
                IsTaskScanIncomplete()
                    ? "Check.Persistence.SummaryIncomplete"
                    : "Check.Persistence.Summary",
                tasks,
                runs),
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

        // 结论一出就记下深扫义务，不要等下一次持久化扫描去「发现」：
        // Collect() 先查持久化再查这一项，后台校验恰好在这两步之间出结果时，
        // 持久化那一步看到的是「未验证」，义务就不会被记下；而这一项照常报「已篡改」。
        // 反过来，用户随后在别处把文件修好、VerifyTermsrvNow 写回「完好」，
        // 之后每一轮都会是浅扫，藏在系统子树里的还原任务永远看不到。
        MarkTamperingObserved();

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

        // 三种状态要分开，不能都用「没有发现持久化项」含糊过去：
        // 读不到目录（没提权）、扫了但有目录被拒（不完整）、扫全了确实没有。
        var scanReadable = IsTaskScanReadable();
        var scanIncomplete = IsTaskScanIncomplete();
        var persistenceState = !scanReadable
            ? Locale.Get("Check.Persistence.Unreadable")
            : scanIncomplete
                ? Locale.Format("Check.Persistence.Incomplete", persistence.Count)
                : persistence.Count.ToString();

        AddOrInactive(
            all, active,
            "rdp-tool-persistence",
            "Check.Persistence.Title",
            scanReadable && !scanIncomplete
                ? "Check.Persistence.InactiveSummary"
                : "Check.Persistence.StateSummary",
            "Check.Persistence.InactiveDetail",
            persistenceState,
            persistenceState,
            blocking: false);

        // 扫描不完整时单独一条：命中数为 0 也可能只是「没扫到」，
        // 「全部检查项」里要能看见这件事，而不是当成「扫描完整」。
        AddOrInactive(
            all, active,
            "rdp-tool-persistence-incomplete",
            "Check.Persistence.IncompleteTitle",
            "Check.Persistence.IncompleteInactive",
            "Check.Persistence.IncompleteDetail",
            scanIncomplete ? Locale.Get("Check.Persistence.IncompleteValue") : Locale.Get("Check.None"),
            scanIncomplete ? Locale.Get("Check.Persistence.IncompleteValue") : Locale.Get("Check.None"),
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

    /// <summary>
    /// 这次扫描有没有目录被系统拒绝访问。
    ///
    /// 逐目录 try/catch 之后，某个子目录读不到只是跳过，命中列表看起来和「查过、确实没有」
    /// 一模一样。留一个标记，界面才能说清这一趟没扫全。
    /// </summary>
    private static bool _taskScanIncomplete;

    /// <summary>自启项（Run）扫描有没有位置读失败。和计划任务分开记，汇总时一起看。</summary>
    private static bool _runScanIncomplete;

    /// <summary>当前缓存是不是深扫的结果（含 Microsoft 子树的任务内容）。</summary>
    private static bool _taskScanDeep;

    /// <summary>
    /// 一旦确认 termsrv.dll 被篡改，就把「深扫」黏住，直到深扫一次全清干净为止。
    ///
    /// 只按「当前是否被篡改」判断会有两个坑：修复时完整性缓存刚好过期，会降级成浅扫，
    /// 漏掉系统子树里的任务；文件被还原之后条件不再成立，修复时没删掉的任务就再也
    /// 报不出来，用户以为已经清干净了。
    ///
    /// 注意：这个标记不随 <see cref="InvalidateScanCache"/> 复位——它记的是
    /// 「这台机器有过前科」，不是「此刻有没有病」。
    /// </summary>
    private static bool _persistenceDeepScanSticky;

    /// <summary>
    /// 深扫义务变化时通知外部（用于落盘）。跨重启保持，见设置项
    /// <c>PersistenceDeepScanPending</c>。
    /// </summary>
    internal static Action<bool>? DeepScanObligationChanged;

    /// <summary>
    /// 深扫义务的迁移锁。
    ///
    /// 修复扫描和「重新检测」可能并发：两个线程各自改标志、各自通知外部落盘，
    /// 通知顺序一旦反了，设置文件里留下 false 而内存是 true——重启后义务就丢了。
    /// 状态变更与通知必须在同一把锁里串行，顺序才对得上。
    /// </summary>
    private static readonly object ObligationGate = new();

    /// <summary>
    /// 深扫义务的代际号。每次「检测到篡改（或文件缺失）」都自增。
    ///
    /// 一次扫描从读到干净状态到执行清除之间，别处可能已经发现了新的篡改。
    /// 记下扫描开始时的代际，清除前比对：对不上就说明这轮结论已经过期，
    /// 不能拿它把新建立的义务抹掉——否则重启后会直接退回浅扫。
    /// </summary>
    private static int _obligationGeneration;

    /// <summary>
    /// 启动时恢复上一次没做完的深扫义务。
    /// </summary>
    internal static void RestoreDeepScanObligation(bool pending)
    {
        _persistenceDeepScanSticky = pending;
    }

    /// <summary>
    /// 上一次计划任务扫描有没有目录被拒绝访问。先取命中项，标记才是这一轮的。
    /// </summary>
    internal static bool IsTaskScanIncomplete()
    {
        lock (ScanGate)
        {
            return _taskScanIncomplete || _runScanIncomplete;
        }
    }

    /// <summary>
    /// 计划任务目录能不能读。非管理员时 <c>%SystemRoot%\System32\Tasks</c> 直接拒绝访问，
    /// 扫描会静默返回空——界面必须能把「没查」和「查了没有」分开。
    /// </summary>
    internal static bool IsTaskScanReadable()
    {
        try
        {
            var tasksRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "Tasks");

            using var entries = Directory.EnumerateFileSystemEntries(tasksRoot).GetEnumerator();
            _ = entries.MoveNext();
            return true;
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or SecurityException)
        {
            return false;
        }
    }

    /// <summary>任务扫描结果缓存 30 秒：界面每 2 秒刷一次，不能每轮都读几百个 XML。</summary>
    private static readonly TimeSpan TaskScanTtl = TimeSpan.FromSeconds(30);

    /// <param name="scannedNow">
    /// 这次是真扫了一遍（true），还是命中缓存（false）。
    /// 释放黏性标记时必须要求 true：缓存里的空结果可能早于某次任务变动。
    /// </param>
    private static IReadOnlyList<ToolPersistenceHit> GetCachedTaskHits(
        bool deep,
        out bool scannedNow,
        out bool incomplete)
    {
        lock (ScanGate)
        {
            // 浅扫的缓存不能拿去回答深扫：那正是「文件被改过但任务藏在系统子树里」的场景。
            if (DateTime.UtcNow - _taskScanAt < TaskScanTtl && (!deep || _taskScanDeep))
            {
                scannedNow = false;
                incomplete = _taskScanIncomplete;
                return _taskScanCache;
            }

            _taskScanCache = ScanScheduledTasks(deep, out var scanned);
            _taskScanIncomplete = scanned;
            _taskScanAt = DateTime.UtcNow;
            _taskScanDeep = deep;
            scannedNow = true;

            // 把这一趟的完整性一并交出去：共享字段可能被别处的缓存失效重置，
            // 调用方拿它判断「能不能解除深扫义务」就会被骗。
            incomplete = scanned;
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
    /// 内容默认只读 Microsoft 子树以外的文件：系统自带任务有一千多个，
    /// 全套读一遍会让每轮检测明显变慢。但 <paramref name="includeMicrosoftSubtree"/>
    /// 为 true 时会一并读——只有 termsrv.dll 被确认篡改时才这么做，那时才值得为
    /// 「藏在系统子树里的还原任务」付这个代价。
    ///
    /// 逐目录遍历而不是 <c>EnumerateFiles(AllDirectories)</c>：后者在第一个无权限的子目录上
    /// 就整体抛出，后面的任务全部漏掉，外面也只看到一句「扫描失败」。
    /// </summary>
    /// <param name="includeMicrosoftSubtree">是否连系统子树的任务内容一起读（深扫）。</param>
    /// <param name="incomplete">有目录被拒绝访问时置 true，调用方据此提示「扫描不完整」。</param>
    private static IReadOnlyList<ToolPersistenceHit> ScanScheduledTasks(
        bool includeMicrosoftSubtree,
        out bool incomplete)
    {
        var hits = new List<ToolPersistenceHit>();
        incomplete = false;

        var tasksRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "Tasks");

        if (!Directory.Exists(tasksRoot))
        {
            // Directory.Exists 在「拒绝访问」时同样返回 false，不能当成「没有这个目录」：
            // 当成没扫全，调用方才不会把「没查」报成「没有」。
            incomplete = true;
            return hits;
        }

        var pending = new Stack<string>();
        pending.Push(tasksRoot);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();

            string[] files;
            string[] subdirectories;
            try
            {
                files = Directory.GetFiles(directory);
                subdirectories = Directory.GetDirectories(directory);
            }
            catch (Exception exception) when (exception is IOException
                                                  or UnauthorizedAccessException
                                                  or SecurityException)
            {
                // 跳过这个目录，但要把「没扫全」记下来：否则结果和「扫过、确实没有」一样。
                incomplete = true;
                AppLog.Warn($"跳过无法读取的任务目录 {directory}。", exception);
                continue;
            }

            foreach (var subdirectory in subdirectories)
            {
                pending.Push(subdirectory);
            }

            foreach (var file in files)
            {
                var relative = Path.GetRelativePath(tasksRoot, file);
                var taskName = Path.GetFileName(file);
                var taskPath = @"\" + relative.Replace('/', '\\');

                if (ContainsKeyword(taskName))
                {
                    hits.Add(new ToolPersistenceHit(
                        "task",
                        taskPath,
                        Locale.Get("Check.Persistence.Reason.Name"),
                        TaskPath: taskPath));
                    continue;
                }

                // 内容检查只做非 Microsoft 子树，理由见方法注释。
                if (!includeMicrosoftSubtree
                    && relative.StartsWith(
                        "Microsoft" + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var content = TryReadTextFile(file);
                if (content is null)
                {
                    // 读不到任务定义同样算没扫全：这一条可能正好是目标，不能当作「查过、没有」。
                    incomplete = true;
                    continue;
                }

                if (!ContainsKeyword(content))
                {
                    continue;
                }

                hits.Add(new ToolPersistenceHit(
                    "task",
                    taskPath,
                    Locale.Get("Check.Persistence.Reason.Content"),
                    TaskPath: taskPath));
            }
        }

        return hits;
    }

    /// <summary>
    /// 扫描自启项（Run / RunOnce / 策略 Run，含其它已加载用户的 HKCU）。
    /// </summary>
    private static IReadOnlyList<ToolPersistenceHit> GetRunKeyHits(out bool incomplete)
    {
        var hits = new List<ToolPersistenceHit>();
        incomplete = false;

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
            // 枚举 HKU 需要权限，拿不到就跳过——但这等于没检查其它用户的自启项，
            // 必须记成「扫描不完整」，否则会报出「扫全了、没有残留」。
            incomplete = true;
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
                // 单个位置读不到不影响其它位置，但这一趟就不算扫全了。
                incomplete = true;
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
