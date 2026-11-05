using DesktopTwin.Services;

namespace DesktopTwin.Tests;

/// <summary>
/// 全局热键的组合键解析与显示。
///
/// 这两件事都是纯函数，但一旦错了后果不小：解析错了会注册到一个完全不同的键上，
/// 或者把「没有修饰键」的裸按键注册成全局热键，直接吃掉整个系统的输入。
/// </summary>
public sealed class GlobalHotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Alt+U", 0x2u | 0x1u, 0x55u)]
    [InlineData("ctrl+alt+u", 0x2u | 0x1u, 0x55u)]
    [InlineData("Ctrl+Shift+F12", 0x2u | 0x4u, 0x7Bu)]
    [InlineData("Alt+Space", 0x1u, 0x20u)]
    [InlineData("Win+`", 0x8u, 0xC0u)]
    public void 解析组合键(string combination, uint expectedModifiers, uint expectedKey)
    {
        Assert.True(GlobalHotkeyService.TryParse(combination, out var modifiers, out var virtualKey));
        Assert.Equal(expectedModifiers, modifiers);
        Assert.Equal(expectedKey, virtualKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl+Alt")]
    [InlineData("Ctrl+Alt+NoSuchKey")]
    public void 无主键或无法识别时返回失败(string combination)
    {
        Assert.False(GlobalHotkeyService.TryParse(combination, out _, out _));
    }

    [Fact]
    public void 只有修饰键时不产生主键()
    {
        // 必须返回 false：否则「Ctrl」本身会被当成主键注册，全局吞掉 Ctrl。
        Assert.False(GlobalHotkeyService.TryParse("Ctrl", out _, out _));
    }

    [Theory]
    [InlineData("ctrl+alt+u", "Ctrl + Alt + U")]
    [InlineData("Win+Shift+F5", "Win + Shift + F5")]
    [InlineData("alt+space", "Alt + Space")]
    public void 显示文本规范化(string combination, string expected)
    {
        Assert.Equal(expected, GlobalHotkeyService.Format(combination));
    }

    [Fact]
    public void 空组合键显示为空串()
    {
        Assert.Equal(string.Empty, GlobalHotkeyService.Format(null));
        Assert.Equal(string.Empty, GlobalHotkeyService.Format("  "));
    }
}
