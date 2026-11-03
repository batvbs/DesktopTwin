using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace DesktopTwin.Services;

/// <summary>
/// 极简文件日志。桌面分身出问题时，用户几乎只能依赖日志定位原因，
/// 因此这里保证：线程安全、单文件按天滚动、写失败不影响主流程。
/// </summary>
public static class AppLog
{
    private const int RetainedDays = 14;
    private static readonly object SyncRoot = new();
    private static string? _logDirectory;

    /// <summary>
    /// 日志目录，默认 <c>%AppData%\DesktopTwin\logs</c>。
    /// </summary>
    public static string LogDirectory => _logDirectory ??= EnsureDirectory();

    /// <summary>
    /// 写入一条信息级日志。
    /// </summary>
    public static void Info(string message)
    {
        Write("INFO ", message, exception: null);
    }

    /// <summary>
    /// 写入一条警告级日志。
    /// </summary>
    public static void Warn(string message, Exception? exception = null)
    {
        Write("WARN ", message, exception);
    }

    /// <summary>
    /// 写入一条错误级日志。
    /// </summary>
    public static void Error(string message, Exception? exception = null)
    {
        Write("ERROR", message, exception);
    }

    private static void Write(string level, string message, Exception? exception)
    {
        var line = new StringBuilder()
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append(" [").Append(level).Append("] ")
            .Append(message);

        if (exception is not null)
        {
            line.AppendLine().Append(exception);
        }

        var text = line.ToString();
        System.Diagnostics.Debug.WriteLine(text);

        try
        {
            lock (SyncRoot)
            {
                var directory = LogDirectory;
                var file = Path.Combine(
                    directory,
                    $"desktop-twin-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(file, text + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception writeException) when (writeException is IOException
                                                   or UnauthorizedAccessException)
        {
            // 日志写不进去不应影响功能，直接放弃这一条。
        }
    }

    private static string EnsureDirectory()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DesktopTwin",
            "logs");

        try
        {
            Directory.CreateDirectory(directory);
            CleanupOldFiles(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Path.GetTempPath();
        }

        return directory;
    }

    private static void CleanupOldFiles(string directory)
    {
        var threshold = DateTime.Now.AddDays(-RetainedDays);
        foreach (var file in Directory.EnumerateFiles(directory, "desktop-twin-*.log"))
        {
            try
            {
                if (File.GetLastWriteTime(file) < threshold)
                {
                    File.Delete(file);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // 清理失败可以忽略。
            }
        }
    }
}
