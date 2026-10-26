using Solar.App.ViewModels;

namespace Solar.App.Views;

public sealed partial class SettingsPage : PageBase
{
    public SettingsPage() : this(App.GetService<SettingsViewModel>())
    {
    }

    public SettingsPage(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public new SettingsViewModel ViewModel { get; }
}
