using Solar.App.ViewModels;

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
}
