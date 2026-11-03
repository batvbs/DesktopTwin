using System;
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
                if (!taskName.StartsWith(TaskNamePrefix, StringComparison.Ordinal))
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
        try
        {
            dynamic tasks = rootFolder.GetTasks(1);
            var count = (int)tasks.Count;
            var names = new string[count];
            for (var index = 1; index <= count; index++)
            {
                dynamic task = tasks.Item(index);
                names[index - 1] = (string)task.Name;
            }

            return names;
        }
        catch (Exception exception) when (exception is COMException
                                              or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException
                                              or InvalidCastException)
        {
            return [];
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
    /// </summary>
    private static void EnsureNoUserClassOverride()
    {
        const string taskSchedulerClsid = "{0f87369f-a4e5-4cfc-bd3e-73e6154572dd}";

        try
        {
            using var userClasses = Registry.CurrentUser.OpenSubKey(
                $@"Software\Classes\CLSID\{taskSchedulerClsid}");
            if (userClasses is not null)
            {
                throw new InvalidOperationException(
                    @"任务计划程序的 COM 类存在用户级注册覆盖（HKCU\Software\Classes\CLSID\"
                    + taskSchedulerClsid
                    + "），可能已被劫持。已中止在分身中启动程序。");
            }

            using var userProgId = Registry.CurrentUser.OpenSubKey(
                @"Software\Classes\Schedule.Service");
            if (userProgId is not null)
            {
                throw new InvalidOperationException(
                    @"任务计划程序的 ProgID 存在用户级注册覆盖（HKCU\Software\Classes\Schedule.Service），"
                    + "可能已被劫持。已中止在分身中启动程序。");
            }
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
    }

    /// <summary>
    /// 校验 HKLM 下 <c>Schedule.Service</c> 的 ProgID/CLSID 指向系统目录里的模块。
    ///
    /// 这一层挡不住普通用户（改 HKLM 已需管理员），只用于发现被管理员级工具改写过的注册。
    /// 注册表读不到时放行，但留日志，便于排查「功能在策略收紧的机器上不可用」这类问题。
    /// </summary>
    private static void EnsureMachineRegistrationIsSystemOwned()
    {
        const string progIdPath = @"SOFTWARE\Classes\Schedule.Service\CLSID";
        const string classesRoot = @"SOFTWARE\Classes\CLSID";
        const string taskSchedulerClsid = "{0f87369f-a4e5-4cfc-bd3e-73e6154572dd}";

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

            if (!string.Equals(clsid, taskSchedulerClsid, StringComparison.OrdinalIgnoreCase))
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
            var isUnderSystem = Path.GetFullPath(expanded)
                .StartsWith(
                    Path.GetFullPath(systemDirectory) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);

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

    private static void LaunchWithTemporaryTask(
        uint childSessionId,
        string executablePath,
        string arguments,
        string workingDirectory,
        bool runAsAdministrator)
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

        var taskName = TaskNamePrefix + Guid.NewGuid().ToString("N");
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
            if (taskRegistered && rootFolderObject is not null)
            {
                try
                {
                    dynamic rootFolder = rootFolderObject;
                    rootFolder.DeleteTask(taskName, 0);
                }
                catch (COMException exception)
                {
                    // 临时任务启动成功后，清理失败不应中断已经启动的目标程序，
                    // 但必须留痕：残留任务会在下次启动时由 SweepOrphanTasks 清理。
                    AppLog.Warn($"删除临时计划任务 {taskName} 失败。", exception);
                }
            }

            ReleaseComObject(runningTaskObject);
            ReleaseComObject(registeredTaskObject);
            ReleaseComObject(actionObject);
            ReleaseComObject(taskDefinitionObject);
            ReleaseComObject(rootFolderObject);
            ReleaseComObject(schedulerObject);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }
}
