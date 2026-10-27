using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Solar.App.ViewModels;

namespace Solar.App.Views;

public sealed partial class ModDownloadPage : PageBase
{
    public ModDownloadPage() : this(App.GetService<ModDownloadViewModel>())
    {
    }

    public ModDownloadPage(ModDownloadViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public new ModDownloadViewModel ViewModel { get; }

    /// <summary>点一行进入详情页，带上列表里的项目信息与当前实例。</summary>
    private void Mod_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ModCardViewModel card)
        {
            Frame.Navigate(typeof(ModProjectPage), new ModProjectRequest(card.Project, ViewModel.SelectedInstance));
        }
    }

    /// <summary>图标加载失败（WebP 解不了、网络不通）时切回首字母占位。</summary>
    private void ModIcon_ImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ModCardViewModel card })
        {
            card.MarkIconFailed();
        }
    }
}
