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
///
/// <para>
/// 控制台输出一律用**固定英文**，不走本地化。原因很实际：程序往 CONOUT$ 写的是
/// UTF-8 字节，而 cmd 的活动代码页通常是 936，中文会被按 GBK 解读成
/// 「宸茬敓鎴愮晫闈」这种乱码。译文也不该出现在这里——看这段输出的人是译者，
/// 他需要的是能看懂的操作说明，而不是另一种自己看不懂的语言。
/// </para>
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

            // 目标已存在就先另存一份再覆盖。
            //
            // 这个命令最常见的用法是「看看有哪些键」，很容易在已经放了译文（甚至自己刚译完）的
            // 目录里执行一次，把成果直接盖成中文原文模板。改名比拒绝更好：既保住了文件，
            // 也不妨碍用户当场拿到模板。
            var backup = CreateBackupIfExists(target);

            // 带 BOM：记事本等编辑器靠它识别 UTF-8，避免译者一保存就变成 ANSI 乱码。
            File.WriteAllText(target, Locale.BuildTemplate(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            var feedback = "Language template written: " + target
                + Environment.NewLine
                + Strings.Count + " entries. Translate the text on the right of each '=',"
                + Environment.NewLine
                + "save as UTF-8, keep the same file name next to the program"
                + Environment.NewLine
                + "(DesktopTwin.exe -> DesktopTwin.lng), then relaunch the app.";

            if (backup is not null)
            {
                feedback += Environment.NewLine
                    + Environment.NewLine
                    + "NOTE: this file already existed and was overwritten. The previous"
                    + Environment.NewLine
                    + "version was kept as: " + backup;
            }

            WriteFeedback(feedback);
            Services.AppLog.Info($"命令行导出语言模板：{target}（{Strings.Count} 条）"
                + (backup is null ? string.Empty : $"；原文件已备份为 {backup}"));
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or System.Security.SecurityException)
        {
            WriteFeedback("Failed to write the language template: " + exception.Message);
            Services.AppLog.Warn("命令行导出语言模板失败。", exception);
        }

        return true;
    }

    /// <summary>
    /// 目标文件已存在时，把它改名成一份带时间戳的备份。
    ///
    /// 拷贝而不是直接改名也可以，但改名在覆盖失败时更安全（原文件仍然完整）。
    /// 备份本身失败不影响主流程——导出模板仍然继续，最多是原来的文件被覆盖。
    /// </summary>
    /// <param name="path">将要写入的目标路径。</param>
    /// <returns>备份后的路径；没有备份时返回 null。</returns>
    private static string? CreateBackupIfExists(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var backup = Path.Combine(
                Path.GetDirectoryName(path) ?? Environment.CurrentDirectory,
                Path.GetFileNameWithoutExtension(path) + ".bak-" + stamp + Locale.FileExtension);

            File.Move(path, backup);
            return backup;
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException)
        {
            Services.AppLog.Warn("导出语言模板前备份原文件失败，将继续覆盖。", exception);
            return null;
        }
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
    ///
    /// 走 WriteConsoleW 而不是往 CONOUT$ 写字节：文本在 .NET 里本来就是 UTF-16，
    /// WriteConsoleW 接收的正是 UTF-16，由控制台自己按当前字体渲染，
    /// 完全绕开代码页转换。
    ///
    /// 这一点是被实际乱码逼出来的：「已生成界面语言模板」曾经显示成
    /// 「宸茬敓鎴愮晫闈」——因为按 UTF-8 写出的字节被 cmd 按代码页 936 解读了。
    /// 试过「按活动代码页编码」，但 .NET 10 默认不带 936 等代码页
    /// （Encoding.GetEncoding(936) 抛 NotSupportedException），
    /// 换 Encoding.Default、注册 CodePagesEncodingProvider 都同样不可靠。
    /// 走 WriteConsoleW 之后，中文路径（C:\用户\...）也能正确显示。
    /// </summary>
    private static void WriteFeedback(string text)
    {
        try
        {
            // 必须先判断重定向，再决定要不要附加控制台：AttachConsole 会把标准句柄
            // 换成控制台的句柄，之后 IsOutputRedirected 一律为 false，
            // `prog > file` 的输出就会落到屏幕上、文件里空空如也。
            //
            // 重定向时手写 UTF-8 字节而不是走 Console.Out：后者的编码跟着控制台代码页走，
            // 而 .NET 10 默认不带 936 这类代码页，拿不到就只能替换成 '?'。
            // 重定向的目标通常是文件或工具，UTF-8 是最普遍能识别的编码。
            if (Console.IsOutputRedirected)
            {
                using var stdout = Console.OpenStandardOutput();
                var payload = Encoding.UTF8.GetBytes(text + Environment.NewLine);
                stdout.Write(payload, 0, payload.Length);
                stdout.Flush();
                return;
            }

            if (!NativeConsole.TryAttachParentConsole())
            {
                return;
            }

            using var handle = NativeConsole.OpenConsoleOutput();
            if (handle.IsInvalid)
            {
                return;
            }

            NativeConsole.WriteConsole(handle, text + Environment.NewLine);
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or ArgumentException
                                              or NotSupportedException)
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
    private static extern bool WriteConsoleW(
        SafeFileHandle consoleOutput,
        string text,
        uint textLength,
        out uint written,
        IntPtr reserved);

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
    /// <summary>
    /// 往控制台写一段文本。文本是 UTF-16，由控制台按当前字体渲染，不经过代码页。
    /// </summary>
    /// <param name="consoleOutput">控制台输出句柄。</param>
    /// <param name="text">要写的文本。</param>
    public static void WriteConsole(SafeFileHandle consoleOutput, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        WriteConsoleW(
            consoleOutput,
            text,
            (uint)text.Length,
            out _,
            IntPtr.Zero);
    }

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
