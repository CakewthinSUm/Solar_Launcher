using Microsoft.UI.Xaml.Navigation;
using Solar.App.ViewModels;

namespace Solar.App.Views;

public sealed partial class VersionSetupPage : PageBase
{
    public VersionSetupPage() : this(App.GetService<VersionSetupViewModel>())
    {
    }

    public VersionSetupPage(VersionSetupViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public new VersionSetupViewModel ViewModel { get; }

    /// <summary>页面被 NavigationCacheMode 缓存复用，每次导航都要告诉 VM 现在是哪个版本。</summary>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel.VersionId = e.Parameter as string;
        base.OnNavigatedTo(e);
    }
}
