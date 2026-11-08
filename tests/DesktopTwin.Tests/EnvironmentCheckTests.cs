using System;
using System.IO;
using DesktopTwin.Services;

namespace DesktopTwin.Tests;

/// <summary>
/// 环境检查里那些「纯判断」的测试。
///
/// 重点是 <see cref="ChildSessionEnvironmentCheck.IsServiceDllHijacked"/>：
/// 旧实现只匹配 rdpwrap.dll，导致 superrdp.dll、RDPWrappers.dll 这些同样会劫持
/// TermService 的补丁完全漏检——用户装的是「另一个版本」的补丁工具时，
/// 程序会告诉他「环境正常」，然后连接一直失败。
/// </summary>
public sealed class EnvironmentCheckTests
{
    private static string NativePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "termsrv.dll");

    [Fact]
    public void 原生路径不算被劫持()
    {
        Assert.False(ChildSessionEnvironmentCheck.IsServiceDllHijacked(NativePath));
    }

    [Fact]
    public void 原生路径大小写不同不算被劫持()
    {
        var variant = NativePath.Replace("termsrv.dll", "Termsrv.DLL");
        Assert.False(ChildSessionEnvironmentCheck.IsServiceDllHijacked(variant));
    }

    [Fact]
    public void 环境变量形式不算被劫持()
    {
        Assert.False(ChildSessionEnvironmentCheck.IsServiceDllHijacked(@"%SystemRoot%\System32\termsrv.dll"));
    }

    [Theory]
    [InlineData(@"C:\Program Files\RDP Wrapper\rdpwrap.dll")]
    [InlineData(@"C:\Windows\System32\rdpwrap.dll")]
    [InlineData(@"C:\Windows\System32\superrdp.dll")]
    [InlineData(@"C:\Windows\System32\RDPWrappers.dll")]
    [InlineData(@"D:\tools\whatever.dll")]
    public void 任何非原生DLL都算被劫持(string serviceDll)
    {
        Assert.True(ChildSessionEnvironmentCheck.IsServiceDllHijacked(serviceDll));
    }

    [Fact]
    public void 空值表示读不到而不是被劫持()
    {
        // 读不到 ServiceDll 时不该误报「被劫持」，那会把用户引到完全错误的方向。
        Assert.False(ChildSessionEnvironmentCheck.IsServiceDllHijacked(null));
        Assert.False(ChildSessionEnvironmentCheck.IsServiceDllHijacked(string.Empty));
        Assert.False(ChildSessionEnvironmentCheck.IsServiceDllHijacked("   "));
    }

    [Fact]
    public void 原生DLL路径来自系统目录()
    {
        var path = ChildSessionEnvironmentCheck.GetNativeServiceDllPath();
        Assert.True(Path.IsPathFullyQualified(path));
        Assert.Equal("termsrv.dll", Path.GetFileName(path), ignoreCase: true);
        Assert.StartsWith(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            path,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 收集结论时标识唯一且可枚举()
    {
        var issues = ChildSessionEnvironmentCheck.Collect();
        var seen = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

        foreach (var issue in issues)
        {
            Assert.False(string.IsNullOrWhiteSpace(issue.Id));
            Assert.True(seen.Add(issue.Id), $"问题标识重复：{issue.Id}");
            Assert.False(string.IsNullOrWhiteSpace(issue.Title));
            Assert.False(string.IsNullOrWhiteSpace(issue.Detail));
        }
    }

    [Fact]
    public void 家庭版属于提示而不是阻塞()
    {
        // 家庭版只是「不受支持」，实测多数仍可用，不能把它做成阻塞项。
        var issues = ChildSessionEnvironmentCheck.Collect();
        foreach (var issue in issues)
        {
            if (issue.Id == "home-edition")
            {
                Assert.Equal(ChildSessionEnvironmentCheck.Severity.Notice, issue.IssueSeverity);
                Assert.False(issue.IsBlocking);
                Assert.False(issue.CanAutoFix);
            }
        }
    }

    [Fact]
    public void 全部检查项包含每一个已知结论()
    {
        var active = ChildSessionEnvironmentCheck.Collect();
        var all = ChildSessionEnvironmentCheck.CollectAll();

        // 当前触发的条目必须原样出现在全量列表里（同一 id，且不是 Inactive）
        foreach (var issue in active)
        {
            var match = Assert.Single(all, x => x.Id == issue.Id);
            Assert.Equal(issue.IssueSeverity, match.IssueSeverity);
            Assert.Equal(issue.Title, match.Title);
        }

        // 全量列表不能有重复 id
        var ids = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        foreach (var issue in all)
        {
            Assert.True(ids.Add(issue.Id), $"全量检查项里出现重复 id：{issue.Id}");
        }

        // 已知检查项固定 13 个，增减时要同步改这里。
        //
        // 其中 1 项（credential-rejected）是运行时结论：它不属于环境状态，只有连接失败后的现场
        // 才会被 Collect(true) 触发；全量视图里它永远以「未触发」出现，用于核对文案。
        Assert.Equal(13, all.Count);
        Assert.Contains(all, x => x.Id == "credential-rejected");
    }

    [Fact]
    public void 未触发的检查项不会给出修复按钮()
    {
        var all = ChildSessionEnvironmentCheck.CollectAll();
        foreach (var issue in all)
        {
            if (issue.IssueSeverity != ChildSessionEnvironmentCheck.Severity.Inactive)
            {
                continue;
            }

            // 没触发就没有可修的东西；给了按钮会被用户当成「这里有毛病」
            Assert.False(issue.CanAutoFix);
            Assert.Contains("未触发", issue.Summary);
            Assert.NotEmpty(issue.Detail);
        }
    }

    [Fact]
    public void 未触发项不参与阻塞判定()
    {
        // 未触发项一律不是阻塞项：IsBlocking 描述的是「触发时有多严重」，
        // 没触发就不该让 HasBlockingIssue 变真，否则一切正常时也会拦下启动。
        var all = ChildSessionEnvironmentCheck.CollectAll();
        var inactive = new System.Collections.Generic.List<ChildSessionEnvironmentCheck.Issue>();
        foreach (var issue in all)
        {
            if (issue.IssueSeverity != ChildSessionEnvironmentCheck.Severity.Inactive)
            {
                continue;
            }

            Assert.False(issue.IsBlocking);
            inactive.Add(issue);
        }

        // 未触发项构成的列表（完整列表里去掉已触发项）不应被判为有阻塞
        Assert.False(ChildSessionEnvironmentCheck.HasBlockingIssue(inactive));
    }
}
