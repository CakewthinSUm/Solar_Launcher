using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Solar.App.Views;

namespace Solar.App;

public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>最近一次导航携带的参数，用于判断同一个页面的不同分区是否需要重新导航。</summary>
    private object? _currentParameter;

    /// <summary>上次停留的下载子分区；点父项「下载」时回到这里，而不是每次都跳到 Minecraft。</summary>
    private NavigationViewItem _lastDownloadItem;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        ResizeToLogicalSize(1180, 760);

        App.RegisterMainWindow(this);

        // 还没进过下载页时，点父项默认落在 Minecraft 分区
        _lastDownloadItem = MinecraftNavItem;

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

    /// <summary>标题栏的返回键：没有可回退的页面时禁用，避免点了没反应。</summary>
    private void AppTitleBar_BackRequested(TitleBar sender, object args)
    {
        if (ContentFrame.CanGoBack)
        {
            ContentFrame.GoBack();
        }
    }

    private void ContentFrame_Navigated(object sender, NavigationEventArgs e) =>
        AppTitleBar.IsBackButtonEnabled = ContentFrame.CanGoBack;

    /// <summary>
    /// 父项「下载」是 <c>SelectsOnInvoked=False</c>：点击它只展开/收起，自己不参与选中。
    /// 页面切到上次停留的子分区，选中项则等父项展开后再补上。
    /// </summary>
    private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is NavigationViewItem { Tag: "download" })
        {
            NavigateTo(typeof(DownloadPage), _lastDownloadItem.Tag as string is { } tag
                ? tag["download-".Length..]
                : "minecraft");

            // 展开与补选都必须推迟到 NavigationView 处理完本次点击之后：
            // 同步改这两个状态会和它自己的展开/选中流程打架（子项重复渲染或直接崩溃），
            // 而父项折叠时子项容器不存在，过早设 SelectedItem 又会被丢掉。
            var item = _lastDownloadItem;
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                DownloadNavItem.IsExpanded = true;
                DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => NavView.SelectedItem = item);
            });
        }
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            NavigateTo(typeof(SettingsPage), null);
        }
        else if (args.SelectedItem is NavigationViewItem { Tag: string tag } selected)
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
                    // 记住这次停留的分区，下次点父项「下载」直接回到这里。
                    // 必须用 args.SelectedItem：事件触发时 sender.SelectedItem 还是旧值。
                    _lastDownloadItem = selected;

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
