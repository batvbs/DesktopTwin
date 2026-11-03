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
    /// 确认 <c>Schedule.Service</c> 的 COM 注册指向系统目录里的 DLL。
    ///
    /// 这里只用 HKLM 注册表解析 CLSID，不使用 <see cref="Type.GetTypeFromProgID(string)"/> 的解析结果，
    /// 目的是发现「COM 劫持」——若某处把 Schedule.Service 的 CLSID 指向了用户可写目录，
    /// 本程序就会以管理员权限加载并执行那个 DLL。
    /// </summary>
    private static void EnsureTaskSchedulerIsTrusted()
    {
        const string progIdPath = @"SOFTWARE\Classes\Schedule.Service\CLSID";
        const string classesRoot = @"SOFTWARE\Classes\CLSID";

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
            // 读注册表失败时不做判定，交给后续 COM 调用。
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
