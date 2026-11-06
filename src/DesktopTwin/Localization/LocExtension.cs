using System;
using System.Windows.Markup;

namespace DesktopTwin.Localization;

/// <summary>
/// XAML 里取本地化文字的标记扩展。
///
/// 用法：<c>Content="{loc:Loc Main.Start}"</c>。
/// 值在界面创建时取一次——本程序没有运行时切换语言的入口，
/// 换语言就是换那个 <c>.lng</c> 文件然后重启。
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    /// <summary>
    /// 创建空的标记扩展（XAML 里以属性形式写键）。
    /// </summary>
    public LocExtension()
    {
    }

    /// <summary>
    /// 创建标记扩展并指定键。
    /// </summary>
    /// <param name="key">键名，例如 <c>Main.Start</c>。</param>
    public LocExtension(string key)
    {
        Key = key;
    }

    /// <summary>
    /// 键名。
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <inheritdoc />
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        return Locale.Get(Key);
    }
}
