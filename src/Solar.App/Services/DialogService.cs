using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Solar.App.Services;

public interface IDialogService
{
    Task ShowMessageAsync(string title, string message);
    Task<bool> ShowConfirmAsync(string title, string message, string primaryText = "确定", string closeText = "取消");
}

public sealed class DialogService : IDialogService
{
    public async Task ShowMessageAsync(string title, string message)
    {
        var dialog = CreateDialog(title, message, "确定");
        await ShowAsync(dialog);
    }

    public async Task<bool> ShowConfirmAsync(string title, string message, string primaryText = "确定", string closeText = "取消")
    {
        var dialog = CreateDialog(title, message, primaryText, closeText);
        return await ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    private static ContentDialog CreateDialog(string title, string message, string primaryText, string? closeText = null) => new()
    {
        Title = title,
        Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
        PrimaryButtonText = primaryText,
        CloseButtonText = closeText,
        DefaultButton = ContentDialogButton.Primary,
        XamlRoot = App.MainWindow?.Content?.XamlRoot,
    };

    private static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        try
        {
            return await dialog.ShowAsync();
        }
        catch (InvalidOperationException)
        {
            // 已有对话框打开时 WinUI 会抛异常，忽略即可。
            return ContentDialogResult.None;
        }
    }
}
