using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Navigation;
using Solar.App.ViewModels;

namespace Solar.App.Views;

public sealed partial class InstanceSetupPage : PageBase
{
    public InstanceSetupPage() : this(App.GetService<InstanceSetupViewModel>())
    {
    }

    public InstanceSetupPage(InstanceSetupViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public new InstanceSetupViewModel ViewModel { get; }

    /// <summary>页面被 NavigationCacheMode 缓存复用，每次导航都要告诉 VM 现在是哪个实例。</summary>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel.InstanceId = e.Parameter as string;
        base.OnNavigatedTo(e);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
    }
}
