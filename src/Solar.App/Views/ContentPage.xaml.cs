using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Solar.App.ViewModels;
using Solar.Core.Content;

namespace Solar.App.Views;

public sealed partial class ContentPage : PageBase
{
    public ContentPage() : this(App.GetService<ContentViewModel>())
    {
    }

    public ContentPage(ContentViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public new ContentViewModel ViewModel { get; }

    /// <summary>
    /// 导入文件。具体认哪些后缀由 ContentManager 在列列表时决定，
    /// 选择器里放开为“所有文件”，用户选错类型时只是复制进去后不出现在列表里。
    /// </summary>
    private async void ImportFiles_Click(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is not { } window)
        {
            return;
        }

        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(window.AppWindow.Id)
            {
                CommitButtonText = "导入",
            };

            picker.FileTypeFilter.Add("*");

            var files = await picker.PickMultipleFilesAsync();
            var paths = files?
                .Select(f => f.Path)
                .Where(p => !string.IsNullOrEmpty(p))
                .ToArray() ?? [];

            if (paths.Length > 0)
            {
                await ViewModel.ImportAsync(paths);
            }
        }
        catch (Exception ex)
        {
            ViewModel.ErrorMessage = "无法打开文件选择器：" + ex.Message;
        }
    }

    /// <summary>导入解压后的资源包 / 光影包文件夹。</summary>
    private async void ImportFolder_Click(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is not { } window)
        {
            return;
        }

        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(window.AppWindow.Id)
            {
                CommitButtonText = "导入",
            };

            var folder = await picker.PickSingleFolderAsync();
            if (folder?.Path is { Length: > 0 } path)
            {
                await ViewModel.ImportAsync([path]);
            }
        }
        catch (Exception ex)
        {
            ViewModel.ErrorMessage = "无法打开文件夹选择器：" + ex.Message;
        }
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ContentPack pack })
        {
            ViewModel.RevealCommand.Execute(pack);
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ContentPack pack })
        {
            ViewModel.DeleteCommand.Execute(pack);
        }
    }
}
