using System;
using System.IO;
using DesktopTwin.Services;

namespace DesktopTwin.Tests;

/// <summary>
/// 目标程序路径校验的测试。
///
/// 这一层是唯一不需要管理员权限、也不需要第二会话就能验证的安全边界，
/// 因此把它覆盖得细一些：放行了不该放行的路径，等于给提权启动开了后门。
/// </summary>
public sealed class ExecutablePathValidationTests
{
    [Fact]
    public void 空路径被拒绝()
    {
        Assert.Throws<ArgumentException>(() =>
            ChildSessionProcessLauncher.ValidateExecutablePath(string.Empty));
    }

    [Fact]
    public void 不存在的文件被拒绝()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".exe");
        Assert.Throws<FileNotFoundException>(() =>
            ChildSessionProcessLauncher.ValidateExecutablePath(path));
    }

    [Fact]
    public void 非exe被拒绝()
    {
        var path = Path.ChangeExtension(typeof(ExecutablePathValidationTests).Assembly.Location, ".dll");
        Assert.True(File.Exists(path), "测试自身程序集应当存在");
        Assert.Throws<ArgumentException>(() =>
            ChildSessionProcessLauncher.ValidateExecutablePath(path));
    }

    [Fact]
    public void UNC路径被拒绝()
    {
        Assert.Throws<ArgumentException>(() =>
            ChildSessionProcessLauncher.ValidateExecutablePath(@"\\server\share\program.exe"));
    }

    [Fact]
    public void 设备路径被拒绝()
    {
        Assert.Throws<ArgumentException>(() =>
            ChildSessionProcessLauncher.ValidateExecutablePath(@"\\?\C:\Windows\System32\cmd.exe"));
    }

    [Fact]
    public void 本地存在的exe被接受并返回绝对路径()
    {
        var path = Environment.ProcessPath;
        Assert.False(string.IsNullOrWhiteSpace(path), "无法取得测试宿主进程路径");

        var result = ChildSessionProcessLauncher.ValidateExecutablePath(path!);

        Assert.True(Path.IsPathFullyQualified(result));
        Assert.EndsWith(".exe", result, StringComparison.OrdinalIgnoreCase);
    }
}
