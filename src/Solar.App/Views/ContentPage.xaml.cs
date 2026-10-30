using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
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

        // 这一页要在 Mods / 资源包 / 光影 之间切换。缓存复用旧页面时，
        // 分区栏不会跟着新参数重建，容易停在上一段分区上；每次重建最省心。
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Disabled;
    }

    public new ContentViewModel ViewModel { get; }

    /// <summary>分区栏跟着导航参数走：从版本页或栏上切过来时，把选中项拨到当前分区。</summary>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        SectionBar.SelectedKey = e.Parameter as string ?? ViewModel.Section.Key;
    }

    /// <summary>
    /// 分区栏切到版本：回到版本列表；其余分区仍是这一页，换个参数即可。
    /// 分区切换不播页面动画——它更像页内 tab，用入场动画整页飞进来会显得很跳。
    /// </summary>
    private void SectionBar_SectionSelected(object? sender, string key)
    {
        if (key == "Versions")
        {
            Frame.Navigate(typeof(VersionsPage), null, new SuppressNavigationTransitionInfo());
            return;
        }

        Frame.Navigate(typeof(ContentPage), key, new SuppressNavigationTransitionInfo());
    }

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
