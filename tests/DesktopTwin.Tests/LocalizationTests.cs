using System;
using System.Collections.Generic;
using DesktopTwin.Localization;

namespace DesktopTwin.Tests;

/// <summary>
/// 界面语言文件的解析与回退。
///
/// 这些测试保护的是「文件写坏了界面不能崩、缺条目要能回退」这条底线——
/// 语言文件是用户自己编辑的，出错是常态。
/// </summary>
public sealed class LocalizationTests
{
    [Fact]
    public void 基本格式能解析且忽略注释与空行()
    {
        var count = Locale.Load(
            """
            # 注释
            ; 另一种注释

            Main.Title=Desktop Twin
            Main.Start=Start
            """);

        Assert.Equal(2, count);
        Assert.Equal("Desktop Twin", Locale.Get("Main.Title"));
        Assert.Equal("Start", Locale.Get("Main.Start"));
    }

    [Fact]
    public void 键不区分大小写且值保留等号后面的内容()
    {
        Locale.Load("Menu.UuSwitch.ToolTip=Use Ctrl+Alt+U = switch view");

        Assert.Equal("Use Ctrl+Alt+U = switch view", Locale.Get("menu.uuswitch.tooltip"));
    }

    [Fact]
    public void 支持换行与反斜杠转义()
    {
        Locale.Load("Startup.Hint.Close=line1\\nline2\\npath C:\\\\Games");

        Assert.Equal("line1\nline2\npath C:\\Games", Locale.Get("Startup.Hint.Close"));
    }

    [Fact]
    public void 缺失的键回退到内置中文()
    {
        Locale.Load("Main.Title=Desktop Twin");

        // 文件里没写这一条 → 用内置中文
        Assert.Equal("立即在分身中启动", Locale.Get("Menu.LaunchTarget"));
    }

    [Fact]
    public void 未知的键返回键名而不是空串()
    {
        Locale.Load("Main.Title=Desktop Twin");

        Assert.Equal("Not.A.Key", Locale.Get("Not.A.Key"));
    }

    [Fact]
    public void 值为空的条目按未翻译处理()
    {
        Locale.Load("Main.Title=");

        Assert.Equal("桌面分身", Locale.Get("Main.Title"));
    }

    [Fact]
    public void 导出的模板能原样解析回来()
    {
        // 模板是给译者的起点：它自己必须能被完整读回去，否则译者一保存界面就少东西。
        var template = Locale.BuildTemplate();
        var count = Locale.Load(template);

        Assert.Equal(Strings.Count, count);

        foreach (var entry in Strings.All)
        {
            Assert.Equal(entry.Value, Locale.Get(entry.Key));
        }
    }

    [Fact]
    public void 模板里每条内置文字都有对应的键()
    {
        var template = Locale.BuildTemplate();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in template.Split('\n'))
        {
            var text = line.Trim();
            if (text.Length == 0 || text[0] == '#')
            {
                continue;
            }

            var index = text.IndexOf('=');
            Assert.True(index > 0, $"模板里有无法解析的行：{text}");
            Assert.True(seen.Add(text[..index]), $"模板里有重复的键：{text[..index]}");
        }

        Assert.Equal(Strings.Count, seen.Count);
    }

    [Fact]
    public void 值的空白一律保留()
    {
        // 行首空白：Notify.RdpFailed.Suffix 以空格开头，拼在错误信息后面用；
        // 全角空格：界面用它做缩进（「　　1. 点下面的…」），string.Trim 会一并吃掉；
        // 行尾空格：英文的列表分隔符是 ", "，裁掉就变成 ","。
        Locale.Load(
            """
            Test.Suffix= tail
            Test.FullWidthIndent=　　缩进过的文字
            Test.TrailingSpace=, 
            """);

        Assert.Equal(" tail", Locale.Get("Test.Suffix"));
        Assert.Equal("　　缩进过的文字", Locale.Get("Test.FullWidthIndent"));
        Assert.Equal(", ", Locale.Get("Test.TrailingSpace"));
    }

}
