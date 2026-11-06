using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DesktopTwin.Services;

namespace DesktopTwin.Localization;

/// <summary>
/// 界面文字的本地化。
///
/// 机制刻意做得很小：程序**只认识一种外部文件**——与可执行文件同目录、同名的
/// 纯文本 <c>.lng</c>（例如 <c>DesktopTwin.exe</c> 对应 <c>DesktopTwin.lng</c>）。
/// 文件存在就按里面的键值覆盖内置的简体中文；不存在就全用内置中文。
/// 程序本身不带任何其它语言，也没有语言切换入口——想换语言就放一个文件。
///
/// 文件格式（UTF-8，带不带 BOM 都行）：
/// <code>
/// # 以 # 或 ; 开头的行是注释，空行忽略
/// Main.Title=桌面分身
/// Menu.LaunchTarget=立即在分身中启动
/// </code>
/// 值里可以用 <c>\n</c>、<c>\t</c>、<c>\\</c> 转义；键不区分大小写；
/// 同一个键写多次以最后一次为准；某个键没翻译就回退到内置中文。
/// </summary>
internal static class Locale
{
    /// <summary>
    /// 外部语言文件的扩展名。
    /// </summary>
    public const string FileExtension = ".lng";

    private static readonly Dictionary<string, string> Entries =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 是否成功加载了外部语言文件。
    /// </summary>
    public static bool IsLoaded { get; private set; }

    /// <summary>
    /// 已加载的文件路径；没加载时为 null。
    /// </summary>
    public static string? LoadedPath { get; private set; }

    /// <summary>
    /// 已加载的条目数。
    /// </summary>
    public static int LoadedCount => Entries.Count;

    /// <summary>
    /// 取一段界面文字。
    ///
    /// 找不到键时先回退到内置中文，再不行就返回键本身——界面上出现一个英文键名
    /// 比空白更容易定位是哪个键漏了。
    /// </summary>
    /// <param name="key">键，例如 <c>Main.Title</c>。</param>
    public static string Get(string key)
    {
        if (Entries.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value))
        {
            return value;
        }

        return Strings.TryGet(key, out var fallback) ? fallback : key;
    }

    /// <summary>
    /// 取一段带占位符的界面文字，并按 <see cref="string.Format(string, object[])"/> 填充。
    /// </summary>
    /// <param name="key">键。</param>
    /// <param name="args">占位符参数。</param>
    public static string Format(string key, params object?[] args)
    {
        var template = Get(key);
        try
        {
            return string.Format(template, args);
        }
        catch (FormatException)
        {
            // 译者把占位符写坏了不能让界面崩，原样返回并留日志。
            AppLog.Warn($"本地化字符串的占位符不合法：{key}");
            return template;
        }
    }

    /// <summary>
    /// 程序启动时调用：找同目录同名的 .lng 并加载。
    /// </summary>
    public static void Initialize()
    {
        try
        {
            var path = ResolvePath();
            if (path is null)
            {
                AppLog.Info("没有找到外部语言文件，界面使用内置简体中文。");
                return;
            }

            var text = File.ReadAllText(path, Encoding.UTF8);
            var count = Load(text);
            IsLoaded = true;
            LoadedPath = path;
            MarkForeignLanguageIfNeeded();
            AppLog.Info($"已加载界面语言文件 {path}（{count} 条，共 {Strings.Count} 条内置文字）。");
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or System.Security.SecurityException)
        {
            AppLog.Warn("读取界面语言文件失败，继续使用内置简体中文。", exception);
        }
    }

    /// <summary>
    /// 语言文件应该放的位置：与可执行文件同目录、同文件名，扩展名换成 <c>.lng</c>。
    /// </summary>
    public static string? ResolvePath()
    {
        var (directory, baseName) = GetProgramIdentity();
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(baseName))
        {
            return null;
        }

        var candidate = Path.Combine(directory, baseName + FileExtension);
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>
    /// 程序的「身份」：所在目录与文件名（不含扩展名）。
    ///
    /// 优先用进程自己的路径——这样用户把 exe 改名成 <c>我的分身.exe</c> 时，
    /// 语言文件跟着改名就能用。用 <c>dotnet DesktopTwin.dll</c> 这种宿主方式启动时，
    /// 进程路径是 dotnet.exe，要退回程序集名，否则会去找 dotnet.lng。
    /// </summary>
    public static (string Directory, string BaseName) GetProgramIdentity()
    {
        var executable = Environment.ProcessPath;
        var baseName = executable is null ? null : Path.GetFileNameWithoutExtension(executable);

        if (!string.IsNullOrEmpty(baseName)
            && !string.Equals(baseName, "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return (Path.GetDirectoryName(executable!) ?? string.Empty, baseName);
        }

        var assemblyName = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name;
        return (AppContext.BaseDirectory, assemblyName ?? "DesktopTwin");
    }

    /// <summary>
    /// 解析并合并一段语言文件内容。
    /// </summary>
    /// <param name="content">文件内容。</param>
    /// <returns>成功读到的条目数。</returns>
    public static int Load(string content)
    {
        Entries.Clear();
        var count = 0;

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#' || trimmed[0] == ';')
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line.Substring(0, separator).Trim();
            if (key.Length == 0)
            {
                continue;
            }

            // 什么都不裁——空白属于内容本身：
            //
            // - 值末尾：英文的列表分隔符是「, 」，裁掉就成了「,」；
            // - 值开头：有的文案紧跟在别的文字后面，靠开头的空格分开
            //   （Notify.RdpFailed.Suffix 就是「 已打开诊断面板…」）；
            //   界面上用全角空格（U+3000）做的缩进同理，而且 string.Trim 会
            //   把它按 Unicode 空白一并吃掉。
            //
            // 行尾的 '\r' 已在上面的 TrimEnd('\r') 去掉，这里不裁任何东西。
            var value = Unescape(line.Substring(separator + 1));
            if (value.Length == 0)
            {
                // 空值当作「没翻译」：直接删掉，回退到内置中文。
                Entries.Remove(key);
                continue;
            }

            Entries[key] = value;
            count++;
        }

        return count;
    }

    /// <summary>
    /// 生成一份可直接交给译者编辑的模板（简体中文原文）。
    /// </summary>
    public static string BuildTemplate()
    {
        var builder = new StringBuilder();
        builder.AppendLine("# 桌面分身 · 界面语言文件（简体中文原文）");
        builder.AppendLine("#");
        builder.AppendLine("# 用法：");
        builder.AppendLine("#   1. 把本文件与程序放在同一个目录，文件名改成与程序相同（如 DesktopTwin.lng）；");
        builder.AppendLine("#   2. 把等号右边的文字翻译成目标语言，保存为 UTF-8；");
        builder.AppendLine("#   3. 重新启动程序即可生效。程序不带语言切换入口，靠这个文件决定界面语言。");
        builder.AppendLine("#");
        builder.AppendLine("# 说明：");
        builder.AppendLine("#   - 以 # 或 ; 开头的行是注释，空行会被忽略；");
        builder.AppendLine("#   - 键名不要改，只改等号右边的内容；");
        builder.AppendLine("#   - 值里可以用 \\n 表示换行、\\t 表示制表符；");
        builder.AppendLine("#   - {0}、{1} 是占位符，翻译时保留它们的位置（顺序可以调整）；");
        builder.AppendLine("#   - 某个键不写或留空，程序会回退到内置简体中文。");
        builder.AppendLine();

        foreach (var entry in Strings.All)
        {
            builder.Append(entry.Key).Append('=').AppendLine(Escape(entry.Value));
        }

        return builder.ToString();
    }


    /// <summary>
    /// 把一段文字写成单行形式（反转义规则）。
    /// </summary>
    public static string Escape(string value)
    {
        return value
            .Replace("\\", "\\\\")
            .Replace("\r\n", "\\n")
            .Replace("\n", "\\n")
            .Replace("\r", "\\n")
            .Replace("\t", "\\t");
    }

    /// <summary>
    /// 把单行形式还原成多行文字。
    /// </summary>
    public static string Unescape(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (current != '\\' || index == value.Length - 1)
            {
                builder.Append(current);
                continue;
            }

            index++;
            switch (value[index])
            {
                case 'n':
                    builder.Append('\n');
                    break;
                case 'r':
                    builder.Append('\r');
                    break;
                case 't':
                    builder.Append('\t');
                    break;
                case '\\':
                    builder.Append('\\');
                    break;
                default:
                    // 不认识的转义原样保留，避免把路径里的反斜杠吃掉。
                    builder.Append('\\').Append(value[index]);
                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// 语言文件里的内容如果和内置中文完全一样，说明这是「中文原文」而不是别的语言，
    /// 不需要看系统的语言设置也知道界面是中文——这里只是留个日志方便排查。
    /// </summary>
    private static void MarkForeignLanguageIfNeeded()
    {
        if (LoadedCount >= Strings.Count)
        {
            AppLog.Info("界面语言文件覆盖了全部内置文字。");
        }
        else
        {
            AppLog.Info($"界面语言文件缺少 {Strings.Count - LoadedCount} 条，缺失的键回退到内置中文。");
        }
    }
}
