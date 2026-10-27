using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Solar.App.Services;

public interface IDialogService
{
    Task ShowMessageAsync(string title, string message);
    Task<bool> ShowConfirmAsync(string title, string message, string primaryText = "确定", string closeText = "取消");

    /// <summary>
    /// 带一个勾选框的确认框。返回 (是否确认, 是否勾选)；
    /// 用户关掉对话框时返回 (false, false)。
    /// </summary>
    Task<(bool Confirmed, bool Checked)> ShowConfirmWithCheckBoxAsync(
        string title,
        string message,
        string checkBoxText,
        string primaryText = "确定",
        string closeText = "取消",
        bool defaultChecked = false);
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

    public async Task<(bool Confirmed, bool Checked)> ShowConfirmWithCheckBoxAsync(
        string title,
        string message,
        string checkBoxText,
        string primaryText = "确定",
        string closeText = "取消",
        bool defaultChecked = false)
    {
        var checkBox = new CheckBox
        {
            Content = checkBoxText,
            IsChecked = defaultChecked,
            Margin = new Thickness(0, 12, 0, 0),
        };

        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(checkBox);

        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            PrimaryButtonText = primaryText,
            CloseButtonText = closeText,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = App.MainWindow?.Content?.XamlRoot,
        };

        var result = await ShowAsync(dialog);
        return (result == ContentDialogResult.Primary, checkBox.IsChecked == true);
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
