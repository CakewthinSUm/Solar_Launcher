using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Solar.App.Views;

namespace Solar.App;

public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>最近一次导航携带的参数，用于判断同一个页面的不同分区是否需要重新导航。</summary>
    private object? _currentParameter;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        ResizeToLogicalSize(1180, 760);

        App.RegisterMainWindow(this);

        // 首页与导航项同一次导航，避免启动时出现两次页面切换动画
        NavigateTo(typeof(HomePage), null);
    }

    /// <summary>AppWindow 使用物理像素，按当前 DPI 换算成逻辑尺寸，保证各缩放下观感一致。</summary>
    private void ResizeToLogicalSize(int width, int height)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        if (scale <= 0)
        {
            scale = 1;
        }

        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(width * scale), (int)(height * scale)));
    }

    private void AppTitleBar_PaneToggleRequested(TitleBar sender, object args) =>
        NavView.IsPaneOpen = !NavView.IsPaneOpen;

    /// <summary>
    /// 父项「下载」是 <c>SelectsOnInvoked=False</c>：点击它只展开子项、自己不参与选中。
    /// 展开交给 NavigationView 自己做（这一步是可靠的），这里把选中项交给 Minecraft 子项，
    /// 否则会出现「子项全部展开、侧边栏却没有任何高亮」的状态。
    /// </summary>
    private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is NavigationViewItem { Tag: "download" })
        {
            NavView.SelectedItem = MinecraftNavItem;
        }
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            NavigateTo(typeof(SettingsPage), null);
        }
        else if (args.SelectedItem is NavigationViewItem { Tag: string tag })
        {
            switch (tag)
            {
                case "instances":
                    NavigateTo(typeof(InstancesPage), null);
                    break;
                case "accounts":
                    NavigateTo(typeof(AccountsPage), null);
                    break;

                // 子项的 Tag 形如 download-shaders，后缀就是下载页的分区键。
                // 父项「下载」是 SelectsOnInvoked=False，永远不会成为 SelectedItem。
                case not null when tag.StartsWith("download-", StringComparison.Ordinal):
                    NavigateTo(typeof(DownloadPage), tag["download-".Length..]);
                    break;
                default:
                    NavigateTo(typeof(HomePage), null);
                    break;
            }
        }
    }

    /// <summary>
    /// 下载页的六个分区是同一个页面，只是参数不同，
    /// 所以除了页面类型，参数变化时也要重新导航，否则切换子项不会刷新内容。
    /// </summary>
    private void NavigateTo(Type pageType, object? parameter)
    {
        if (ContentFrame.CurrentSourcePageType == pageType && Equals(_currentParameter, parameter))
        {
            return;
        }

        _currentParameter = parameter;
        ContentFrame.Navigate(pageType, parameter, new EntranceNavigationTransitionInfo());
    }
}
