using System;
using DesktopTwin.Services;
using Microsoft.Win32;

namespace DesktopTwin.Tests;

/// <summary>
/// 任务计划程序 COM 守卫。
///
/// 这里的断言片段全部取自生产常量（<see cref="ChildSessionProcessLauncher.TaskSchedulerClsid"/>、
/// <see cref="ChildSessionProcessLauncher.UserClassesPrefix"/>），不自己拼一份。
/// 否则测试就成了自指涉：生产把路径写错，测试用同一份错路径去查，照样绿。
/// </summary>
public sealed class SchedulerComGuardTests
{
    /// <summary>
    /// 守住 CLSID 常量本身：它必须等于系统里真实的注册值。
    ///
    /// 其余测试都用这个常量构造，常量错了它们会「一致地错」——测试全绿，守卫却盯着
    /// 一个不存在的类。只有真的去读一次 HKLM，才能证明它指向的是任务计划程序。
    /// </summary>
    [Fact]
    public void TaskSchedulerClsid_MatchesSystemRegistration()
    {
        using var localMachine = RegistryKey.OpenBaseKey(
            RegistryHive.LocalMachine,
            RegistryView.Registry64);

        var key = localMachine.OpenSubKey(
            ChildSessionProcessLauncher.UserClassesPrefix + @"\Schedule.Service\CLSID");
        if (key is null)
        {
            Assert.Fail(
                @"读不到 HKLM\" + ChildSessionProcessLauncher.UserClassesPrefix
                + @"\Schedule.Service\CLSID，无法验证 CLSID 常量。");
            return;
        }

        using (key)
        {
            var actual = key.GetValue(null) as string;
            Assert.False(
                string.IsNullOrWhiteSpace(actual),
                @"HKLM\" + ChildSessionProcessLauncher.UserClassesPrefix
                + @"\Schedule.Service\CLSID 的默认值（CLSID）为空。");

            Assert.Equal(
                ChildSessionProcessLauncher.TaskSchedulerClsid,
                actual,
                ignoreCase: true);
        }
    }

    /// <summary>
    /// 守住根路径常量：它必须指向当前用户配置单元里真实存在的位置。
    ///
    /// 生产代码拿 <see cref="ChildSessionProcessLauncher.UserClassesPrefix"/> 拼 HKCU 路径。
    /// 这个前缀一旦写错（比如少一个 s），守卫查到的是一个永远不存在的键，
    /// 于是「没有覆盖」恒成立——守卫彻底失效，而且不会有任何报错。
    /// </summary>
    [Fact]
    public void UserClassesPrefix_PointsAtExistingUserHiveLocation()
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            ChildSessionProcessLauncher.UserClassesPrefix + @"\CLSID");

        Assert.NotNull(key);
    }

    [Fact]
    public void ThrowIfOverridePresent_NoOverride_DoesNotThrow()
    {
        ChildSessionProcessLauncher.ThrowIfOverridePresent(
            clsidOverride: false,
            progIdOverride: false);
    }

    [Fact]
    public void ThrowIfOverridePresent_ClsidOverride_ThrowsAndNamesClsidKey()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ChildSessionProcessLauncher.ThrowIfOverridePresent(
                clsidOverride: true,
                progIdOverride: false));

        // 断言消息而不只是类型：如果有人把两个分支合并成
        // if (clsid || progId) throw("...CLSID...")，这条仍会抛、仍然绿，
        // 但用户照着提示会去查错键。只有断言内容能挡住这种退化。
        Assert.Contains(ChildSessionProcessLauncher.TaskSchedulerClsid, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CLSID", exception.Message, StringComparison.Ordinal);

        // 断言的是「没有点出另一个键路径」，而不是「没有出现 Schedule.Service 这几个字」。
        // 后者会惩罚「顺手把 ProgID 名字也标进提示」这种改进——那反而对用户更好。
        Assert.DoesNotContain(
            ChildSessionProcessLauncher.UserClassesPrefix + @"\Schedule.Service",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ThrowIfOverridePresent_ProgIdOverride_ThrowsAndNamesProgIdKey()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ChildSessionProcessLauncher.ThrowIfOverridePresent(
                clsidOverride: false,
                progIdOverride: true));

        Assert.Contains("Schedule.Service", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            ChildSessionProcessLauncher.TaskSchedulerClsid,
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ThrowIfOverridePresent_BothOverrides_ReportsClsidKeyFirst()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ChildSessionProcessLauncher.ThrowIfOverridePresent(
                clsidOverride: true,
                progIdOverride: true));

        // 两种都被覆盖时只报 CLSID：它是 CoCreateInstance 直接解析的那一项，危害更直接。
        // 这里把「先报哪个」固定下来，避免以后调整顺序时无人察觉。
        Assert.Contains(ChildSessionProcessLauncher.TaskSchedulerClsid, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            ChildSessionProcessLauncher.UserClassesPrefix + @"\Schedule.Service",
            exception.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// 环境探针：直接调用生产入口，确认它在真实机器上不会误报。
    ///
    /// **它证明不了「我们检查的正是那个真实劫持入口」。** CLSID 或路径常量写错时，
    /// 这里同样找不到覆盖、同样不抛、同样绿——那是
    /// <see cref="TaskSchedulerClsid_MatchesSystemRegistration"/> 和
    /// <see cref="UserClassesPrefix_PointsAtExistingUserHiveLocation"/> 的职责。
    /// 所以它是探针，不是安全测试，别把「这条绿了」当成「劫持已被防住」。
    ///
    /// 它能证明的只有两件事：
    /// 1. 生产入口不是无条件抛（真有人把方法写成恒抛，只有跑一次才发现）；
    /// 2. 记录当前机器的 HKCU 状态——这台机器红了，就是它真的被覆盖了，属于该红。
    /// </summary>
    [Fact]
    public void EnvironmentProbe_CurrentUserHasNoSchedulerComOverride()
    {
        try
        {
            ChildSessionProcessLauncher.EnsureNoUserClassOverride();
        }
        catch (InvalidOperationException exception)
        {
            Assert.Fail(
                "当前用户的 HKCU 下存在任务计划程序的 COM 注册覆盖，或该键无法读取。"
                + Environment.NewLine
                + "请检查以下两个键是否存在："
                + Environment.NewLine
                + @"  HKCU\" + ChildSessionProcessLauncher.UserClassesPrefix
                + @"\CLSID\" + ChildSessionProcessLauncher.TaskSchedulerClsid
                + Environment.NewLine
                + @"  HKCU\" + ChildSessionProcessLauncher.UserClassesPrefix + @"\Schedule.Service"
                + Environment.NewLine
                + "原始信息：" + exception.Message);
        }
    }
}
