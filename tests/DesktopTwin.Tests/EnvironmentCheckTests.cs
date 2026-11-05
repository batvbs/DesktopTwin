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
}
