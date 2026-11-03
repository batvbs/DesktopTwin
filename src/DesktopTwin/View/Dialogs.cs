using System.Threading.Tasks;
using System.Windows;
using Wpf.Ui.Controls;

namespace DesktopTwin.View;

/// <summary>
/// 统一的对话框入口。全部走 WPF-UI 的主题化弹窗，避免在深色界面里弹出一个白色系统对话框。
/// </summary>
internal static class Dialogs
{
    /// <summary>
    /// 询问用户是否继续。返回 true 表示用户选择了「继续」。
    /// </summary>
    internal static async Task<bool> ConfirmAsync(string message, string title)
    {
        var messageBox = new Wpf.Ui.Controls.MessageBox
        {
            Title = title,
            Content = message,
            PrimaryButtonText = "继续",
            CloseButtonText = "取消"
        };

        var result = await messageBox.ShowDialogAsync();
        return result == Wpf.Ui.Controls.MessageBoxResult.Primary;
    }

    /// <summary>
    /// 展示一条错误信息。
    /// </summary>
    internal static async Task ErrorAsync(string message, string title)
    {
        var messageBox = new Wpf.Ui.Controls.MessageBox
        {
            Title = title,
            Content = message,
            CloseButtonText = "知道了"
        };

        _ = await messageBox.ShowDialogAsync();
    }

    /// <summary>
    /// 在非异步上下文中展示错误信息（例如界面命令的兜底分支）。
    /// </summary>
    internal static void ShowError(string message, string title)
    {
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(new System.Action(() => ShowError(message, title)));
            return;
        }

        _ = ErrorAsync(message, title);
    }
}
