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

    /// <summary>点版本进入创建实例页，带上完整的版本摘要供那边显示类型与发布时间。</summary>
    private void Version_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is VersionSummary version)
        {
            Frame.Navigate(typeof(InstanceCreatePage), version);
        }
    }
}
