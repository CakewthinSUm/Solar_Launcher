using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;

namespace Solar.App.Services;

public interface INavigationService
{
    /// <summary>导航目标页面的类型（由导航视图的 Tag 提供）。</summary>
    Type? CurrentPageType { get; }

    event EventHandler<Type>? Navigated;

    bool Navigate(Type pageType, object? parameter = null, bool clearStack = false);
    bool Navigate<T>(object? parameter = null, bool clearStack = false) where T : Page;
    bool GoBack();
}

public sealed class NavigationService(IServiceProvider services) : INavigationService
{
    private Frame? _frame;

    public Type? CurrentPageType => _frame?.CurrentSourcePageType;

    public event EventHandler<Type>? Navigated;

    internal void Attach(Frame frame)
    {
        _frame = frame;
        _frame.Navigated += (_, e) =>
        {
            if (e.SourcePageType is { } type)
            {
                Navigated?.Invoke(this, type);
            }
        };
    }

    public bool Navigate(Type pageType, object? parameter = null, bool clearStack = false)
    {
        if (_frame is null)
        {
            return false;
        }

        if (_frame.CurrentSourcePageType == pageType)
        {
            return true;
        }

        var transition = new EntranceNavigationTransitionInfo();
        var navigated = _frame.Navigate(pageType, parameter, transition);
        if (navigated && clearStack)
        {
            _frame.BackStack.Clear();
        }

        return navigated;
    }

    public bool Navigate<T>(object? parameter = null, bool clearStack = false) where T : Page =>
        Navigate(typeof(T), parameter, clearStack);

    public bool GoBack()
    {
        if (_frame?.CanGoBack != true)
        {
            return false;
        }

        _frame.GoBack();
        return true;
    }

    /// <summary>页面版本交给 DI 创建，从而支持构造函数注入。</summary>
    internal Page? ResolvePage(Type pageType) => services.GetService(pageType) as Page;
}
