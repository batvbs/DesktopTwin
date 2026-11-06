using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading.Tasks;
using DesktopTwin.Interop;
using Microsoft.Win32;

namespace DesktopTwin.Services;

/// <summary>
/// 把目标程序启动到指定的分身会话中。
///
/// 为什么不能直接 <see cref="System.Diagnostics.Process.Start(string)"/>：
/// 进程只能在自己所属的 Windows 会话里创建窗口，跨会话启动必须借道「任务计划程序」——
/// 以交互令牌注册一个一次性任务，再用 RunEx 指定目标会话号运行，最后立刻删除任务。
/// </summary>
public static class ChildSessionProcessLauncher
{
    /// <summary>
    /// 临时计划任务的前缀。启动后会被删除，异常退出时可能在系统里残留。
    /// </summary>
    private const string TaskNamePrefix = "DesktopTwin-SessionLaunch-";

    /// <summary>
    /// 清扫时要认的任务名前缀。
    ///
    /// 除了本类创建的 <see cref="TaskNamePrefix"/>，分身端代理用的
    /// <c>DesktopTwin-SessionAgent-</c> 也是同一种一次性任务——
    /// 不清扫的话，程序异常退出会让它们一直躺在任务计划程序里。
    /// </summary>
    private const string CommonTaskNamePrefix = "DesktopTwin-";

    /// <summary>
    /// 临时任务的延迟清理时间：要留够「任务真正拉起进程」的余量。
    /// </summary>
    private static readonly TimeSpan TaskCleanupDelay = TimeSpan.FromSeconds(45);

    /// <summary>
    /// 用户级 COM 注册所在的根路径。
    ///
    /// 与 <see cref="TaskSchedulerClsid"/> 同理，只写一次。守卫要拼它去查 HKCU，
    /// 测试要拼它去验证「这个位置真的存在」——如果测试用自己的字面量拼路径，
    /// 生产把 <c>Classes</c> 写成 <c>Class</c> 时测试照样绿，守卫永远不触发，
    /// 那种测试比没有测试更糟：它给出虚假的安全感。
    /// </summary>
    internal const string UserClassesPrefix = @"Software\Classes";

    /// <summary>
    /// <c>Schedule.Service</c> 的 COM 类标识。
    ///
    /// 只在这里写一次：守卫要拿它去查 HKCU 覆盖，报错要拿它告诉用户查哪个键，
    /// HKLM 校验要拿它比对 ProgID 指向。三处各写一份字面量的话，
    /// 改漏一处就会出现「检查的是 A、报给用户的是 B」，而两边都不报错。
    /// </summary>
    internal const string TaskSchedulerClsid = "{0f87369f-a4e5-4cfc-bd3e-73e6154572dd}";

    private const int TaskActionExecute = 0;
    private const int TaskCreate = 2;
    private const int TaskLogonInteractiveToken = 3;

    /// <summary>
    /// 以最高权限运行（管理员）。
    /// </summary>
    private const int TaskRunLevelHighest = 1;

    /// <summary>
    /// 以当前用户的普通（未提权）权限运行。
    /// </summary>
    private const int TaskRunLevelLimited = 0;

    /// <summary>
    /// 使用指定的会话号运行任务。
    /// </summary>
    private const int TaskRunUseSessionId = 0x4;

    /// <summary>
    /// 在指定会话中启动目标程序。
    /// </summary>
    /// <param name="childSessionId">目标分身会话号。</param>
    /// <param name="executablePath">目标程序完整路径。</param>
    /// <param name="arguments">命令行参数。</param>
    /// <param name="runAsAdministrator">是否以管理员权限运行。</param>
    public static Task LaunchAsync(
        uint childSessionId,
        string executablePath,
        string arguments,
        bool runAsAdministrator)
    {
        var fullPath = ValidateExecutablePath(executablePath);
        var workingDirectory = Path.GetDirectoryName(fullPath) ?? AppContext.BaseDirectory;
        return Task.Run(() => LaunchWithTemporaryTask(
            childSessionId,
            fullPath,
            arguments ?? string.Empty,
            workingDirectory,
            runAsAdministrator));
    }

    /// <summary>
    /// 校验目标程序路径：必须是本地驱动器上的绝对路径，存在且为 .exe。
    ///
    /// 这里刻意拒绝 UNC 路径与设备路径（<c>\\?\</c>、<c>\\.\</c>）：
    /// 本程序以管理员权限运行，允许它拉起网络共享上的可执行文件，
    /// 等于把提权能力交给共享目录的所有者。
    /// </summary>
    public static string ValidateExecutablePath(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("目标程序路径不能为空。", nameof(executablePath));
        }

        var fullPath = Path.GetFullPath(executablePath);

        if (!Path.IsPathFullyQualified(fullPath)
            || fullPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "只允许启动本地驱动器上的程序，不接受网络路径或设备路径。",
                nameof(executablePath));
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("要启动的程序不存在。", fullPath);
        }

        if (!string.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("当前只允许启动 .exe 程序。", nameof(executablePath));
        }

        return fullPath;
    }

    /// <summary>
    /// 清扫上一次异常退出遗留的临时计划任务。
    ///
    /// 正常流程会在启动后立刻删除任务；如果进程在注册与删除之间被结束，任务就会留在系统里。
    /// 这些残留任务带的是本程序的身份与目标程序路径，必须在下次启动时清掉。
    /// </summary>
    public static void SweepOrphanTasks()
    {
        object? schedulerObject = null;
        object? rootFolderObject = null;
        try
        {
            // 清扫同样会 CoCreateInstance("Schedule.Service")，而且是在启动时立刻执行，
            // 不校验就等于给了一条不经过 LaunchWithTemporaryTask 的劫持通道。
            // 这里只查 HKCU（fail-closed）；HKLM 那层是兜底，读不到也没必要阻止清扫。
            EnsureNoUserClassOverride();

            var schedulerType = Type.GetTypeFromProgID("Schedule.Service");
            if (schedulerType is null)
            {
                return;
            }

            schedulerObject = Activator.CreateInstance(schedulerType);
            if (schedulerObject is null)
            {
                return;
            }

            dynamic scheduler = schedulerObject;
            scheduler.Connect();
            rootFolderObject = scheduler.GetFolder("\\");
            dynamic rootFolder = rootFolderObject;

            foreach (var taskName in EnumerateTaskNames(rootFolder))
            {
                if (!taskName.StartsWith(CommonTaskNamePrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    rootFolder.DeleteTask(taskName, 0);
                    AppLog.Info($"已清理残留的临时计划任务 {taskName}");
                }
                catch (COMException exception)
                {
                    AppLog.Warn($"清理残留计划任务 {taskName} 失败。", exception);
                }
            }
        }
        catch (InvalidOperationException exception)
        {
            // 守卫判定不可信（或注册表读不到）。清扫只是清理垃圾，没必要因此让程序起不来，
            // 但要留下记录：真正启动程序时同一个守卫会再次拦下。
            AppLog.Warn("跳过残留计划任务清扫：COM 注册未通过校验。", exception);
        }
        catch (Exception exception) when (exception is COMException
                                              or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException
                                              or InvalidCastException)
        {
            AppLog.Warn("清扫残留计划任务失败，忽略。", exception);
        }
        finally
        {
            ReleaseComObject(rootFolderObject);
            ReleaseComObject(schedulerObject);
        }
    }

    private static string[] EnumerateTaskNames(dynamic rootFolder)
    {
        object? taskCollection = null;
        var names = new List<string>();

        try
        {
            taskCollection = rootFolder.GetTasks(1);
            dynamic tasks = taskCollection;
            var count = (int)tasks.Count;

            for (var index = 1; index <= count; index++)
            {
                object? taskObject = null;
                try
                {
                    taskObject = tasks.Item(index);
                    dynamic task = taskObject;
                    names.Add((string)task.Name);
                }
                finally
                {
                    // 每个 task 都是独立的 RCW，不清掉会一直堆到 GC 才回收。
                    ReleaseComObject(taskObject);
                }
            }

            return [.. names];
        }
        catch (Exception exception) when (exception is COMException
                                              or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException
                                              or InvalidCastException)
        {
            // 枚举失败时返回已经拿到的部分，调用方只关心名字前缀匹配。
            return [.. names];
        }
        finally
        {
            ReleaseComObject(taskCollection);
        }
    }

    /// <summary>
    /// 在创建 <c>Schedule.Service</c> 的 COM 对象之前校验注册表，避免被本地提权。
    ///
    /// 本程序以管理员权限运行，一旦 <c>CoCreateInstance</c> 被引到攻击者的 DLL，
    /// 那个 DLL 就会在管理员上下文里加载。检查分两层，两层的失败策略刻意不同：
    /// 1. HKCU 覆盖检查 fail-closed。CoCreateInstance 解析 CLSID 时 HKCU 优先于 HKLM，
    ///    而 HKCU 是普通权限进程就能写的——这是唯一现实的劫持入口，读不到也必须拒绝。
    /// 2. HKLM 注册检查 fail-open。改 HKLM 已经需要管理员权限，这一层只做兜底；
    ///    读不到时放行并记日志，避免策略收紧的机器上功能整体不可用。
    /// </summary>
    private static void EnsureTaskSchedulerIsTrusted()
    {
        EnsureNoUserClassOverride();
        EnsureMachineRegistrationIsSystemOwned();
    }

    /// <summary>
    /// 拒绝 HKCU 下对 <c>Schedule.Service</c> 的 COM 注册覆盖。
    ///
    /// 读不到注册表时同样拒绝：既然无法确认是否被覆盖，就不该以管理员权限创建该 COM 对象。
    /// 攻击者也可以用拒绝读取权限的方式让本检查抛异常，fail-open 会正好被这一手绕过。
    ///
    /// 可见性为 internal 而非 private，是为了让测试能直接调这个真实入口做烟囱验证。
    /// 判定本身在 <see cref="ThrowIfOverridePresent"/> 里，那个方法是纯函数、不碰注册表。
    /// </summary>
    internal static void EnsureNoUserClassOverride()
    {
        bool clsidOverride;
        bool progIdOverride;

        try
        {
            using var userClasses = Registry.CurrentUser.OpenSubKey(
                $@"{UserClassesPrefix}\CLSID\{TaskSchedulerClsid}");
            clsidOverride = userClasses is not null;

            using var userProgId = Registry.CurrentUser.OpenSubKey(
                $@"{UserClassesPrefix}\Schedule.Service");
            progIdOverride = userProgId is not null;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException
                                              or UnauthorizedAccessException
                                              or IOException)
        {
            AppLog.Warn("无法读取 HKCU 的 COM 注册，按「可能已被覆盖」处理。", exception);
            throw new InvalidOperationException(
                "无法读取当前用户的 COM 注册信息，无法确认任务计划程序未被劫持，已中止在分身中启动程序。",
                exception);
        }

        ThrowIfOverridePresent(clsidOverride, progIdOverride);
    }

    /// <summary>
    /// 根据 HKCU 覆盖情况抛出结论。
    ///
    /// 纯函数：只依赖两个布尔值，不读注册表也不写任何东西，所以可以直接单测四种组合。
    /// 判定逻辑放在这里而不是散在上面的读取代码里，是为了让「读到了什么」和「该不该拒绝」
    /// 两件事分开——前者依赖环境，后者必须可复现。
    /// </summary>
    internal static void ThrowIfOverridePresent(bool clsidOverride, bool progIdOverride)
    {
        if (clsidOverride)
        {
            throw new InvalidOperationException(
                $@"任务计划程序的 COM 类存在用户级注册覆盖（HKCU\{UserClassesPrefix}\CLSID\{TaskSchedulerClsid}），"
                + "可能已被劫持。已中止在分身中启动程序。");
        }

        if (progIdOverride)
        {
            throw new InvalidOperationException(
                $@"任务计划程序的 ProgID 存在用户级注册覆盖（HKCU\{UserClassesPrefix}\Schedule.Service），"
                + "可能已被劫持。已中止在分身中启动程序。");
        }
    }

    /// <summary>
    /// 校验 HKLM 下 <c>Schedule.Service</c> 的 ProgID/CLSID 指向系统目录里的模块。
    ///
    /// 这一层挡不住普通用户（改 HKLM 已需管理员），只用于发现被管理员级工具改写过的注册。
    /// 注册表读不到时放行，但留日志，便于排查「功能在策略收紧的机器上不可用」这类问题。
    /// </summary>
    private static void EnsureMachineRegistrationIsSystemOwned()
    {
        const string progIdPath = $@"{UserClassesPrefix}\Schedule.Service\CLSID";
        const string classesRoot = $@"{UserClassesPrefix}\CLSID";

        try
        {
            using var localMachine = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine,
                RegistryView.Registry64);

            using var progIdKey = localMachine.OpenSubKey(progIdPath);
            var clsid = progIdKey?.GetValue(null) as string;
            if (string.IsNullOrWhiteSpace(clsid))
            {
                // 读不到就交给后续 COM 调用报错，不在这里拦死。
                return;
            }

            if (!string.Equals(clsid, TaskSchedulerClsid, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "任务计划程序的 ProgID 指向了非预期的 COM 类（"
                    + clsid
                    + "），可能已被劫持。已中止在分身中启动程序。");
            }

            using var clsidKey = localMachine.OpenSubKey($@"{classesRoot}\{clsid}\InprocServer32");
            var serverPath = clsidKey?.GetValue(null) as string;
            if (string.IsNullOrWhiteSpace(serverPath))
            {
                return;
            }

            var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var expanded = Environment.ExpandEnvironmentVariables(serverPath);

            bool isUnderSystem;
            try
            {
                isUnderSystem = Path.GetFullPath(expanded)
                    .StartsWith(
                        Path.GetFullPath(systemDirectory) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception exception) when (exception is ArgumentException
                                                  or NotSupportedException
                                                  or PathTooLongException)
            {
                // 注册表里的路径本身畸形（含非法字符、过长等）。这不是「读不到」，是「读到了垃圾」，
                // 不能按 fail-open 放行；同时也不能让 .NET 的英文原始消息直接弹给用户。
                AppLog.Warn($"任务计划程序的 COM 注册路径无法解析：{expanded}", exception);
                throw new InvalidOperationException(
                    "任务计划程序的 COM 注册指向了无法解析的路径（" + expanded + "），已中止在分身中启动程序。",
                    exception);
            }

            if (!isUnderSystem)
            {
                throw new InvalidOperationException(
                    "任务计划程序的 COM 注册指向了非系统目录（"
                    + expanded
                    + "），可能已被劫持。已中止在分身中启动程序。");
            }
        }
        catch (Exception exception) when (exception is System.Security.SecurityException
                                              or UnauthorizedAccessException
                                              or IOException)
        {
            AppLog.Warn("无法读取 HKLM 的任务计划程序注册信息，跳过该层校验。", exception);
        }
    }

    /// <summary>
    /// 在指定会话里运行一条命令。
    ///
    /// 与 <see cref="LaunchProgram"/> 的区别：不校验可执行文件路径——
    /// 调用方传的是系统自带的 <c>powershell.exe</c>，不是用户选的目标程序。
    /// 用途是拉起分身端的脚本代理。
    /// </summary>
    /// <returns>是否已成功下发启动任务。</returns>
    internal static bool RunInSession(
        uint sessionId,
        string executablePath,
        string arguments,
        string taskNamePrefix)
    {
        try
        {
            LaunchWithTemporaryTask(
                sessionId,
                executablePath,
                arguments,
                Path.GetDirectoryName(executablePath) ?? string.Empty,
                runAsAdministrator: false,
                taskNamePrefix: taskNamePrefix);
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                              or COMException
                                              or UnauthorizedAccessException
                                              or System.Security.SecurityException
                                              or IOException)
        {
            AppLog.Warn("在分身会话里启动命令失败。", exception);
            return false;
        }
    }

    private static void LaunchWithTemporaryTask(
        uint childSessionId,
        string executablePath,
        string arguments,
        string workingDirectory,
        bool runAsAdministrator,
        string? taskNamePrefix = null)
    {
        // 会话可能在用户点击后被重建，启动前必须确认目标会话号仍然有效，
        // 否则会把程序启动到一个已经注销的会话里（表现为「点了没反应」）。
        var actualChildSessionId = ChildSessionNativeMethods.TryGetChildSessionId();
        if (actualChildSessionId != childSessionId)
        {
            throw new InvalidOperationException(
                $"目标分身会话已发生变化。请求会话为 {childSessionId}，当前会话为 "
                + (actualChildSessionId?.ToString(CultureInfo.InvariantCulture) ?? "无")
                + "。");
        }

        EnsureTaskSchedulerIsTrusted();

        var schedulerType = Type.GetTypeFromProgID("Schedule.Service")
            ?? throw new InvalidOperationException("当前 Windows 未提供任务计划程序 COM 服务。");

        var taskName = (taskNamePrefix ?? TaskNamePrefix) + Guid.NewGuid().ToString("N");
        string accountName;
        using (var currentIdentity = WindowsIdentity.GetCurrent())
        {
            accountName = currentIdentity.Name;
        }

        object? schedulerObject = null;
        object? rootFolderObject = null;
        object? taskDefinitionObject = null;
        object? actionObject = null;
        object? registeredTaskObject = null;
        object? runningTaskObject = null;
        var taskRegistered = false;

        try
        {
            schedulerObject = Activator.CreateInstance(schedulerType)
                ?? throw new InvalidOperationException("无法创建任务计划程序 COM 对象。");
            dynamic scheduler = schedulerObject;
            scheduler.Connect();

            rootFolderObject = scheduler.GetFolder("\\");
            dynamic rootFolder = rootFolderObject;
            taskDefinitionObject = scheduler.NewTask(0);
            dynamic taskDefinition = taskDefinitionObject;

            taskDefinition.RegistrationInfo.Author = "DesktopTwin";
            taskDefinition.RegistrationInfo.Description =
                $"临时启动 {Path.GetFileName(executablePath)} 到分身会话 {childSessionId}";

            taskDefinition.Settings.Enabled = true;
            taskDefinition.Settings.Hidden = true;
            taskDefinition.Settings.AllowDemandStart = true;
            taskDefinition.Settings.DisallowStartIfOnBatteries = false;
            taskDefinition.Settings.StopIfGoingOnBatteries = false;
            taskDefinition.Settings.ExecutionTimeLimit = "PT0S";

            taskDefinition.Principal.UserId = accountName;
            taskDefinition.Principal.LogonType = TaskLogonInteractiveToken;
            taskDefinition.Principal.RunLevel = runAsAdministrator
                ? TaskRunLevelHighest
                : TaskRunLevelLimited;

            actionObject = taskDefinition.Actions.Create(TaskActionExecute);
            dynamic action = actionObject;
            action.Path = executablePath;
            action.Arguments = arguments;
            action.WorkingDirectory = workingDirectory;

            registeredTaskObject = rootFolder.RegisterTaskDefinition(
                taskName,
                taskDefinition,
                TaskCreate,
                accountName,
                null,
                TaskLogonInteractiveToken,
                null);
            taskRegistered = true;

            dynamic registeredTask = registeredTaskObject;
            runningTaskObject = registeredTask.RunEx(
                null,
                TaskRunUseSessionId,
                checked((int)childSessionId),
                null);

            if (runningTaskObject is null)
            {
                throw new InvalidOperationException("任务计划程序没有返回运行实例。");
            }

            AppLog.Info(
                $"已在会话 {childSessionId} 中启动 {executablePath}"
                + (runAsAdministrator ? "（管理员权限）" : "（普通权限）"));
        }
        finally
        {
            if (taskRegistered)
            {
                // 不能在这里立刻删任务。
                //
                // RunEx 只是「登记运行」，进程真正起来有几秒延迟；任务计划程序在删除
                // 任务定义时会连带终止它的运行实例——删得太快，刚拉起的进程会被掐掉。
                // 实测表现：「已在会话 X 中启动」的日志有，但会话里根本没有进程，
                // 三次拉起只有一次碰巧活下来。所以改成延迟清理，异常退出时留给下次启动清扫。
                ScheduleTaskCleanup(taskName);
            }

            ReleaseComObject(runningTaskObject);
            ReleaseComObject(registeredTaskObject);
            ReleaseComObject(actionObject);
            ReleaseComObject(taskDefinitionObject);
            ReleaseComObject(rootFolderObject);
            ReleaseComObject(schedulerObject);
        }
    }

    /// <summary>
    /// 在后台延迟删除临时计划任务。
    ///
    /// 延迟期间目标进程已经稳定运行，此时删除任务不会影响它。
    /// </summary>
    /// <param name="taskName">要删除的任务名。</param>
    private static void ScheduleTaskCleanup(string taskName)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TaskCleanupDelay).ConfigureAwait(false);

                var schedulerType = Type.GetTypeFromProgID("Schedule.Service");
                if (schedulerType is null)
                {
                    return;
                }

                object? schedulerObject = null;
                object? rootFolderObject = null;
                try
                {
                    schedulerObject = Activator.CreateInstance(schedulerType);
                    if (schedulerObject is null)
                    {
                        return;
                    }

                    dynamic scheduler = schedulerObject;
                    scheduler.Connect();
                    rootFolderObject = scheduler.GetFolder("\\");
                    dynamic rootFolder = rootFolderObject;
                    rootFolder.DeleteTask(taskName, 0);
                }
                finally
                {
                    ReleaseComObject(rootFolderObject);
                    ReleaseComObject(schedulerObject);
                }
            }
            catch (Exception exception) when (exception is COMException
                                                  or InvalidOperationException
                                                  or UnauthorizedAccessException
                                                  or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException
                                                  or InvalidCastException)
            {
                // 删不掉不算错：下次启动 SweepOrphanTasks 会清掉。
                AppLog.Warn($"延迟删除临时计划任务 {taskName} 失败，留给下次启动清扫。", exception);
            }
        });
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }
}
