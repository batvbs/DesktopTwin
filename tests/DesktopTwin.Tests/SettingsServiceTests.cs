using System;
using System.IO;
using DesktopTwin.Services;

namespace DesktopTwin.Tests;

/// <summary>
/// 设置的持久化测试。覆盖「改完能读回来」与「文件损坏时不让程序起不来」两条主路径。
/// </summary>
public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _directory;

    public SettingsServiceTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "desktop-twin-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void 默认值符合预期()
    {
        using var service = new SettingsService(_directory);
        var settings = service.Current;

        Assert.Equal(1920, settings.Session.DesktopWidth);
        Assert.Equal(1080, settings.Session.DesktopHeight);

        // 显示比例默认 1:1（文字最清晰的一档）；自适应缩放需要用户主动选
        Assert.False(settings.Session.SmartSizing);
        Assert.True(settings.Session.KeepAspectRatio);
        Assert.True(settings.Launch.AutoLaunchOnConnect);

        // 提权默认关闭：减少与反作弊、账号安全机制的冲突
        Assert.False(settings.Launch.RunAsAdministrator);

        // 首次运行提示默认未确认
        Assert.False(settings.ChildSessionNoticeAccepted);

        // 自动化相关能力默认全关：它们会结束别的进程或保存凭据，必须由用户主动开启
        Assert.False(settings.Automation.UuViewSwitchEnabled);
        Assert.False(settings.Automation.AutoLoginEnabled);
        Assert.Equal("Ctrl+Alt+U", settings.Automation.UuViewSwitchHotkey);

        // 密码表默认是空的，且按键不区分大小写（多账户各存一份）
        Assert.Empty(settings.Automation.AutoLoginPasswords);
        settings.Automation.AutoLoginPasswords["SomeUser"] = "x";
        Assert.True(settings.Automation.AutoLoginPasswords.ContainsKey("someuser"));

        // 全屏顶栏默认不固定：只在鼠标到顶端时出现
        Assert.False(settings.Window.FullscreenBarPinned);
    }

    [Fact]
    public void 写入后能被新实例读回()
    {
        using (var writer = new SettingsService(_directory))
        {
            writer.Current.Session.DesktopWidth = 2560;
            writer.Current.Session.DesktopHeight = 1440;
            writer.Current.Session.AudioMuted = true;
            writer.Current.Launch.TargetPath = @"C:\Games\game.exe";
            writer.Current.Launch.RunAsAdministrator = true;
            writer.Current.ChildSessionNoticeAccepted = true;
            writer.Current.Window.FullscreenBarPinned = true;
            writer.Flush();
        }

        using var reader = new SettingsService(_directory);
        Assert.Equal(2560, reader.Current.Session.DesktopWidth);
        Assert.Equal(1440, reader.Current.Session.DesktopHeight);
        Assert.True(reader.Current.Session.AudioMuted);
        Assert.Equal(@"C:\Games\game.exe", reader.Current.Launch.TargetPath);
        Assert.True(reader.Current.Launch.RunAsAdministrator);
        Assert.True(reader.Current.ChildSessionNoticeAccepted);

        // 全屏顶栏的固定状态要能记住，否则每次进全屏都要重新点一次图钉。
        Assert.True(reader.Current.Window.FullscreenBarPinned);
    }

    [Fact]
    public void 设置文件损坏时回退到默认值()
    {
        File.WriteAllText(Path.Combine(_directory, "settings.json"), "{ 这不是合法 JSON");

        using var service = new SettingsService(_directory);

        Assert.Equal(1920, service.Current.Session.DesktopWidth);
    }

    [Fact]
    public void 最近使用列表去重后置顶由界面负责_此处只验证可序列化()
    {
        using (var writer = new SettingsService(_directory))
        {
            writer.Current.Launch.RecentTargets.Clear();
            writer.Current.Launch.RecentTargets.Add(@"C:\a.exe");
            writer.Current.Launch.RecentTargets.Add(@"C:\b.exe");
            writer.Flush();
        }

        using var reader = new SettingsService(_directory);
        Assert.Equal(2, reader.Current.Launch.RecentTargets.Count);
        Assert.Equal(@"C:\a.exe", reader.Current.Launch.RecentTargets[0]);
    }

    [Fact]
    public void 自动登录密码表按账户分别保存且不区分大小写()
    {
        using (var writer = new SettingsService(_directory))
        {
            writer.Current.Automation.AutoLoginPasswords["batvb"] = "enc-1";
            writer.Current.Automation.AutoLoginPasswords["OtherUser"] = "enc-2";
            writer.Flush();
        }

        using var reader = new SettingsService(_directory);
        var passwords = reader.Current.Automation.AutoLoginPasswords;

        // 两个账户各存一份，互不覆盖
        Assert.Equal(2, passwords.Count);

        // 从文件读回来后仍然是忽略大小写的字典（反序列化会重建字典，比较器要补回来）
        Assert.True(passwords.ContainsKey("BATVB"));
        Assert.True(passwords.ContainsKey("otheruser"));
        Assert.Equal("enc-2", passwords["OTHERUSER"]);
    }

    [Fact]
    public void 提权自动启动的授权与目标程序绑定()
    {
        var path = @"C:\Games\game.exe";
        var consent = ElevatedLaunchConsent.Issue(path, "--windowed");

        Assert.NotNull(consent);
        Assert.True(ElevatedLaunchConsent.IsValid(consent, path, "--windowed"));

        // 路径大小写不影响判定
        Assert.True(ElevatedLaunchConsent.IsValid(consent, @"C:\games\GAME.EXE", "--windowed"));

        // 换程序或换参数，授权立即失效——不能靠改 settings.json 让别的程序被提权执行
        Assert.False(ElevatedLaunchConsent.IsValid(consent, @"C:\Games\other.exe", "--windowed"));
        Assert.False(ElevatedLaunchConsent.IsValid(consent, path, "--fullscreen"));
        Assert.False(ElevatedLaunchConsent.IsValid(null, path, "--windowed"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 测试清理失败不影响结论
        }
    }
}
