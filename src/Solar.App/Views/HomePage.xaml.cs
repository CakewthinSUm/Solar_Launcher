using Microsoft.UI.Xaml.Navigation;
using Solar.App.ViewModels;

namespace Solar.App.Views;

public sealed partial class HomePage : PageBase
{
    public HomePage() : this(App.GetService<HomeViewModel>())
    {
    }

    public HomePage(HomeViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public new HomeViewModel ViewModel { get; }
}
