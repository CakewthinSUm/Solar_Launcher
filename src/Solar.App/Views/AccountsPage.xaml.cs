using Microsoft.UI.Xaml;
using Solar.App.ViewModels;
using Solar.Core.Accounts;

namespace Solar.App.Views;

public sealed partial class AccountsPage : PageBase
{
    public AccountsPage() : this(App.GetService<AccountsViewModel>())
    {
    }

    public AccountsPage(AccountsViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public new AccountsViewModel ViewModel { get; }

    private void SetActive_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GameAccount account })
        {
            ViewModel.SetActiveCommand.Execute(account);
        }
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GameAccount account })
        {
            ViewModel.RemoveCommand.Execute(account);
        }
    }
}
