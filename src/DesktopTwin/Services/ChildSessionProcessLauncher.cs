using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading.Tasks;
using DesktopTwin.Interop;

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
    /// 校验目标程序路径：必须存在且是 .exe。
    /// </summary>
    public static string ValidateExecutablePath(string executablePath)
    {
        var fullPath = Path.GetFullPath(executablePath);
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

        var schedulerType = Type.GetTypeFromProgID("Schedule.Service")
            ?? throw new InvalidOperationException("当前 Windows 未提供任务计划程序 COM 服务。");

        var taskName = $"DesktopTwin-SessionLaunch-{Guid.NewGuid():N}";
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
                catch (COMException)
                {
                    // 临时任务启动成功后，清理失败不应中断已经启动的目标程序。
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
