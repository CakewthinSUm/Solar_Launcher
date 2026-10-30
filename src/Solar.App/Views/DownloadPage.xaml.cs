using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Solar.App.ViewModels;
using Solar.Core.Models;

namespace Solar.App.Views;

public sealed partial class DownloadPage : PageBase
{
    public DownloadPage() : this(App.GetService<DownloadViewModel>())
    {
    }

    public DownloadPage(DownloadViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public new DownloadViewModel ViewModel { get; }

    private void CloseFilter_Click(object sender, RoutedEventArgs e) => FilterButton.Flyout?.Hide();

    /// <summary>点版本进入创建版本页，带上完整的版本摘要供那边显示类型与发布时间。</summary>
    private void Version_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is VersionSummary version)
        {
            Frame.Navigate(typeof(VersionCreatePage), version);
        }
    }
}
