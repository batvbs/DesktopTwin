using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using DesktopTwin.Services;

namespace DesktopTwin.Tests;

/// <summary>
/// 「开机自启动项」关停/恢复里那些不需要触碰真机就能验证的判定逻辑。
///
/// 这些判定出错的方式都很隐蔽：字节位判断反了会把用户本来就关着的启动项重新打开；
/// 计划任务按 State 判断会把运行中的任务当成没关掉；WOW64 视图搞错会把值写进影子键。
/// 所以每一条都要有对应的测试。
/// </summary>
public sealed class StartupSnoozeServiceTests
{
    [Fact]
    public void 关闭字节按低四位判定且保留原有的关闭时间()
    {
        var original = new byte[12];
        original[0] = 0x03;
        BitConverter.GetBytes(new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc).ToFileTimeUtc())
            .CopyTo(original, 4);

        Assert.True(StartupApproval.IsDisabled(original));

        // 0x02 / 0x01 / 0x06 都是「启用」，不能被误判成关闭。
        foreach (var flag in new byte[] { 0x00, 0x01, 0x02, 0x06 })
        {
            var enabled = (byte[])original.Clone();
            enabled[0] = flag;
            Assert.False(StartupApproval.IsDisabled(enabled));
        }
    }

    [Fact]
    public void 空值不会被判定为已关闭()
    {
        Assert.False(StartupApproval.IsDisabled(null));
        Assert.False(StartupApproval.IsDisabled(Array.Empty<byte>()));
    }

    [Fact]
    public void 生成的关闭字节是十二字节且带关闭时间()
    {
        var at = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var bytes = StartupApproval.CreateDisabled(at);

        Assert.Equal(12, bytes.Length);
        Assert.Equal(StartupApproval.DisabledFlag, bytes[0]);
        Assert.Equal(at.ToFileTimeUtc(), BitConverter.ToInt64(bytes, 4));
    }

    [Fact]
    public void 计划任务按_Settings_里的_Enabled_判断而不是_State()
    {
        // 真实的 schtasks /Query /XML 输出：声明是 UTF-16，但重定向后是 OEM 文本。
        // 用 XML 解析器读会因编码不符抛异常，所以实现必须是字符串扫描。
        const string runningButDisabled = """
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Author>test</Author></RegistrationInfo>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <Enabled>false</Enabled>
              </Settings>
              <State>Running</State>
            </Task>
            """;

        Assert.False(ScheduledTaskXml.IsEnabled(runningButDisabled));
    }

    [Fact]
    public void 计划任务缺少_Enabled_时按启用处理()
    {
        Assert.True(ScheduledTaskXml.IsEnabled(null));
        Assert.True(ScheduledTaskXml.IsEnabled(string.Empty));
        Assert.True(ScheduledTaskXml.IsEnabled("<Task><Settings></Settings></Task>"));
    }

    [Fact]
    public void 计划任务只认_Settings_这个元素而不是同前缀的别的元素()
    {
        // 元素名前缀相同不能算命中，否则会把别的块里的 Enabled 当成本任务的开关。
        const string xml = """
            <Task><SettingsData><Enabled>false</Enabled></SettingsData></Task>
            """;

        Assert.True(ScheduledTaskXml.IsEnabled(xml));
    }

    [Fact]
    public void 登录与注册触发器才算会话启动触发器()
    {
        Assert.True(ScheduledTaskXml.HasSessionStartTrigger("<Triggers><LogonTrigger /></Triggers>"));
        Assert.True(ScheduledTaskXml.HasSessionStartTrigger("<Triggers><SessionStateChangeTrigger /></Triggers>"));
        Assert.True(ScheduledTaskXml.HasSessionStartTrigger("<Triggers><RegistrationTrigger /></Triggers>"));

        Assert.False(ScheduledTaskXml.HasSessionStartTrigger("<Triggers><TimeTrigger /></Triggers>"));
        Assert.False(ScheduledTaskXml.HasSessionStartTrigger(null));
    }

    [Fact]
    public void 商店应用只有状态二才算确认启用()
    {
        Assert.True(UwpStartupState.IsEnabled(2));
        Assert.False(UwpStartupState.IsEnabled(0));
        Assert.False(UwpStartupState.IsEnabled(1));
    }

    [Fact]
    public void RunOnce_只放行微软_Edge_的清理项()
    {
        var roots = new[] { @"C:\Program Files (x86)\Microsoft\EdgeWebView\Application" };
        var exe = @"C:\Program Files (x86)\Microsoft\EdgeWebView\Application\120.0\installer\setup.exe";

        Assert.True(RunOnceClassifier.IsAllowed(
            "msedge_cleanup_{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
            "\"" + exe + "\" --uninstall",
            true,
            exe,
            roots));

        // 名字对但路径不在允许目录里 → 不放行。
        Assert.False(RunOnceClassifier.IsAllowed(
            "msedge_cleanup_evil",
            "\"C:\\Temp\\evil.exe\"",
            true,
            @"C:\Temp\evil.exe",
            roots));

        // 路径对但名字不是这个前缀 → 不放行。
        Assert.False(RunOnceClassifier.IsAllowed(
            "SomeOtherUpdater",
            "\"" + exe + "\"",
            true,
            exe,
            roots));

        // 文件不存在 → 不放行。
        Assert.False(RunOnceClassifier.IsAllowed(
            "msedge_cleanup_x",
            "\"" + exe + "\"",
            false,
            exe,
            roots));
    }

    [Fact]
    public void 从命令行里取出可执行文件路径()
    {
        Assert.Equal(@"C:\a b\app.exe", RunOnceClassifier.ParseExecutable("\"C:\\a b\\app.exe\" --flag"));
        Assert.Equal(@"C:\app.exe", RunOnceClassifier.ParseExecutable("C:\\app.exe --flag"));
        Assert.Equal(@"C:\app.exe", RunOnceClassifier.ParseExecutable("  C:\\app.exe  "));
        Assert.Null(RunOnceClassifier.ParseExecutable("   "));
        Assert.Null(RunOnceClassifier.ParseExecutable(null));
    }

    [Fact]
    public void 关停日志能原样往返序列化()
    {
        var journal = new StartupSnoozeJournal
        {
            CreatedAt = new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc),
            Machine = "TEST-PC",
            User = "tester",
            Completed = true
        };
        journal.Registry.Add(new StartupSnoozeRegistryEntry
        {
            Kind = "RunValue",
            Hive = "LocalMachine",
            RunView = "Registry32",
            ApprovedView = "Registry64",
            KeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32",
            ValueName = "delta_force_launcher",
            CommandLine = "\"C:\\Program Files\\delta\\launcher.exe\"",
            OriginalExists = true,
            OriginalBase64 = Convert.ToBase64String(new byte[] { 0x02, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 }),
            WrittenBase64 = Convert.ToBase64String(new byte[] { 0x03, 0, 0, 0, 9, 9, 9, 9, 9, 9, 9, 9 }),
            Applied = true
        });
        journal.Tasks.Add(new StartupSnoozeTaskEntry
        {
            Relative = @"\Clash Verge",
            TaskName = "Clash Verge",
            OriginalEnabled = true,
            ConfirmedDisabled = true
        });
        journal.Uwp.Add(new StartupSnoozeUwpEntry
        {
            KeyPath = @"Software\Classes\Local Settings\x",
            PackageFamilyName = "Test.App_abc",
            Task = "Startup",
            OriginalState = 2,
            WrittenState = 0
        });
        journal.Warnings.Add("无法关闭：sysdiag");

        var json = JsonSerializer.Serialize(journal, new JsonSerializerOptions { WriteIndented = true });
        var back = JsonSerializer.Deserialize<StartupSnoozeJournal>(json);

        Assert.NotNull(back);
        Assert.Equal(journal.Version, back!.Version);
        Assert.Equal(journal.CreatedAt, back.CreatedAt);
        Assert.True(back.Completed);
        Assert.Single(back.Registry);
        Assert.Equal("Registry32", back.Registry[0].RunView);
        Assert.Equal("Registry64", back.Registry[0].ApprovedView);
        Assert.Equal(journal.Registry[0].OriginalBase64, back.Registry[0].OriginalBase64);
        Assert.True(back.Registry[0].Applied);
        Assert.Single(back.Tasks);
        Assert.True(back.Tasks[0].ConfirmedDisabled);
        Assert.Single(back.Uwp);
        Assert.Equal(2, back.Uwp[0].OriginalState);
        Assert.Equal("无法关闭：sysdiag", Assert.Single(back.Warnings));
    }

    [Fact]
    public void 没有日志时不算已关停()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DesktopTwin-snooze-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            var service = new StartupSnoozeService(directory);
            Assert.False(service.IsSnoozed);
            Assert.False(service.HasPendingJournal);

            File.WriteAllText(service.JournalPath, "{}");
            Assert.True(service.IsSnoozed);
            Assert.True(service.HasPendingJournal);
        }
        finally
        {
            if (System.IO.Directory.Exists(directory))
            {
                System.IO.Directory.Delete(directory, recursive: true);
            }
        }
    }
}
