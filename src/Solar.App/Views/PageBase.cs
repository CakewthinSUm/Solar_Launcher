using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Solar.App.ViewModels;

namespace Solar.App.Views;

/// <summary>
/// 所有页面的基类：把 ViewModel 作为 DataContext，并在导航到页面时触发加载。
/// </summary>
public abstract class PageBase : Page
{
    protected PageBase()
    {
        // 页面常驻，避免来回切换时重复创建控件树
        NavigationCacheMode = NavigationCacheMode.Required;
    }

    protected ViewModelBase? ViewModel => DataContext as ViewModelBase;

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (ViewModel is { } viewModel)
        {
            try
            {
                await viewModel.OnNavigatedToAsync(e.Parameter);
            }
            catch (Exception ex)
            {
                viewModel.ErrorMessage = ex.Message;
            }
        }
    }
}
