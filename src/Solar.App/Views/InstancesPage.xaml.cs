using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Solar.App.ViewModels;
using Solar.Core.Instances;

namespace Solar.App.Views;

public sealed partial class InstancesPage : PageBase
{
    public InstancesPage() : this(App.GetService<InstancesViewModel>())
    {
    }

    public InstancesPage(InstancesViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public new InstancesViewModel ViewModel { get; }

    private void SetupInstance_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GameInstance instance })
        {
            Frame.Navigate(typeof(InstanceSetupPage), instance.Id);
        }
    }

    private void DeleteInstance_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GameInstance instance })
        {
            ViewModel.DeleteCommand.Execute(instance);
        }
    }
}
