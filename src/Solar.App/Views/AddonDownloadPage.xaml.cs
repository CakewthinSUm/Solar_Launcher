using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Solar.App.ViewModels;

namespace Solar.App.Views;

public sealed partial class AddonDownloadPage : PageBase
{
    public AddonDownloadPage() : this(App.GetService<AddonDownloadViewModel>())
    {
    }

    public AddonDownloadPage(AddonDownloadViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public new AddonDownloadViewModel ViewModel { get; }

    /// <summary>点一行进入详情页，带上列表里的项目信息与当前版本。</summary>
    private void Addon_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AddonCardViewModel card)
        {
            Frame.Navigate(typeof(AddonProjectPage), new AddonProjectRequest(card.Project, ViewModel.SelectedVersion));
        }
    }

    /// <summary>图标加载失败（WebP 解不了、网络不通）时切回首字母占位。</summary>
    private void AddonIcon_ImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AddonCardViewModel card })
        {
            card.MarkIconFailed();
        }
    }
}
