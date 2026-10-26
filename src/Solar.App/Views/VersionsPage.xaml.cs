using Solar.App.ViewModels;

namespace Solar.App.Views;

public sealed partial class VersionsPage : PageBase
{
    public VersionsPage() : this(App.GetService<VersionsViewModel>())
    {
    }

    public VersionsPage(VersionsViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public new VersionsViewModel ViewModel { get; }
}
