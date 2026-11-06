using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DesktopTwin.Localization;

/// <summary>
/// 本地化相关的命令行入口。
///
/// 目前只有一个：<c>--export-lng</c> —— 在当前目录生成一份简体中文的
/// <c>&lt;程序名&gt;.lng</c> 模板，然后直接退出，不打开主界面。
/// 这是给译者用的入口：拿到模板 → 改等号右边的文字 → 放回程序目录即可。
///
/// 本程序是 GUI 程序（没有控制台），直接从终端运行时看不到输出。
/// 这里临时附加到父进程的控制台，把结果打印出来；双击运行就什么都不显示，
/// 只是把文件写到当前目录。
/// </summary>
internal static class LocalizationCommandLine
{
    /// <summary>
    /// 导出语言模板的开关。
    /// </summary>
    public const string ExportSwitch = "--export-lng";

    /// <summary>
    /// 处理命令行参数。
    /// </summary>
    /// <param name="args">进程参数。</param>
    /// <returns>已经处理并应当直接退出时返回 true。</returns>
    public static bool TryHandle(string[] args)
    {
        if (!ContainsExportSwitch(args))
        {
            return false;
        }

        try
        {
            var (_, baseName) = Locale.GetProgramIdentity();
            var target = Path.Combine(Environment.CurrentDirectory, baseName + Locale.FileExtension);

            // 带 BOM：记事本等编辑器靠它识别 UTF-8，避免译者一保存就变成 ANSI 乱码。
            File.WriteAllText(target, Locale.BuildTemplate(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            var message = $"已生成界面语言模板：{target}"
                + Environment.NewLine
                + $"共 {Strings.Count} 条。把等号右边的文字翻译成目标语言，"
                + "文件名与程序保持一致（例如 DesktopTwin.exe → DesktopTwin.lng），"
                + "放在程序同一目录即可生效。";
            WriteFeedback(message);
            Services.AppLog.Info($"命令行导出语言模板：{target}（{Strings.Count} 条）");
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or System.Security.SecurityException)
        {
            WriteFeedback("生成语言模板失败：" + exception.Message);
            Services.AppLog.Warn("命令行导出语言模板失败。", exception);
        }

        return true;
    }

    private static bool ContainsExportSwitch(string[] args)
    {
        foreach (var arg in args)
        {
            if (string.Equals(arg, ExportSwitch, StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "-export-lng", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 尽量把提示写到父进程的控制台；没有控制台（双击启动）就静默跳过。
    /// </summary>
    private static void WriteFeedback(string text)
    {
        if (!NativeConsole.TryAttachParentConsole())
        {
            return;
        }

        try
        {
            using var handle = NativeConsole.OpenConsoleOutput();
            if (handle.IsInvalid)
            {
                return;
            }

            using var stream = new FileStream(handle, FileAccess.Write);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            writer.WriteLine(text);
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or ArgumentException)
        {
            // 输出失败不影响文件已经生成这件事。
        }
    }
}

/// <summary>
/// 附加父进程控制台所需的少量 Win32 调用。
/// </summary>
internal static class NativeConsole
{
    private const uint AttachParentProcess = 0xFFFFFFFF;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    /// <summary>
    /// 附加到父进程的控制台。已经是控制台程序或没有父控制台时返回 false。
    /// </summary>
    public static bool TryAttachParentConsole()
    {
        try
        {
            return AttachConsole(AttachParentProcess);
        }
        catch (Exception exception) when (exception is DllNotFoundException
                                              or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// 打开 <c>CONOUT$</c>，用于往刚附加上的控制台写文字。
    /// </summary>
    public static SafeFileHandle OpenConsoleOutput()
    {
        return CreateFileW(
            "CONOUT$",
            GenericWrite | GenericRead,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);
    }
}
