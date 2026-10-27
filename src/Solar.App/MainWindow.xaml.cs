using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
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

    /// <summary>
    /// 最小窗口尺寸（逻辑像素）。取 800×600：再窄侧栏会退到只剩图标，
    /// 页面里的表单和卡片（实例页的四列表单、设置页的说明文字）就挤不下了。
    /// </summary>
    private const int MinimumLogicalWidth = 800;
    private const int MinimumLogicalHeight = 600;

    /// <summary>最近一次导航携带的参数，用于判断同一个页面的不同分区是否需要重新导航。</summary>
    private object? _currentParameter;

    /// <summary>上次停留的下载子分区；点父项「下载」时回到这里，而不是每次都跳到 Minecraft。</summary>
    private NavigationViewItem _lastDownloadItem;

    /// <summary>上次停留的管理子分区；点父项「管理」时回到这里。</summary>
    private NavigationViewItem _lastManageItem;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        ResizeToLogicalSize(1180, 760);
        ApplyMinimumSize();

        App.RegisterMainWindow(this);

        // 还没进过下载页时，点父项默认落在 Minecraft 分区
        _lastDownloadItem = MinecraftNavItem;

        // 同理，管理默认落在「实例」
        _lastManageItem = ManageInstancesNavItem;

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

    /// <summary>
    /// 最小窗口尺寸。以逻辑像素给出，再按当前 DPI 换算成物理像素——
    /// <c>PreferredMinimumWidth</c> 收的是物理像素，而 125%/150% 缩放下
    /// 直接填逻辑值会让窗口缩得比预期小。
    /// </summary>
    private void ApplyMinimumSize()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter)
        {
            return;
        }

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        if (scale <= 0)
        {
            scale = 1;
        }

        presenter.PreferredMinimumWidth = (int)(MinimumLogicalWidth * scale);
        presenter.PreferredMinimumHeight = (int)(MinimumLogicalHeight * scale);
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
    /// 父项「下载」与「管理」都是 <c>SelectsOnInvoked=False</c>：点击它们只展开/收起，自己不参与选中。
    /// 页面切到上次停留的子分区，选中项则等父项展开后再补上。
    /// </summary>
    private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is not NavigationViewItem { Tag: string parentTag })
        {
            return;
        }

        switch (parentTag)
        {
            case "download":
                NavigateToDownloadSection(_lastDownloadItem);
                ExpandAndSelect(DownloadNavItem, _lastDownloadItem);
                break;

            case "manage":
                NavigateToManageSection(_lastManageItem);
                ExpandAndSelect(ManageNavItem, _lastManageItem);
                break;
        }
    }

    /// <summary>
    /// 展开与补选都必须推迟到 NavigationView 处理完本次点击之后：
    /// 同步改这两个状态会和它自己的展开/选中流程打架（子项重复渲染或直接崩溃），
    /// 而父项折叠时子项容器不存在，过早设 SelectedItem 又会被丢掉。
    /// </summary>
    private void ExpandAndSelect(NavigationViewItem parent, NavigationViewItem child)
    {
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            parent.IsExpanded = true;
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => NavView.SelectedItem = child);
        });
    }

    /// <summary>子项 Tag 去掉前缀后的分区键，如 <c>download-shaders</c> → <c>shaders</c>。</summary>
    private static string? SectionKey(NavigationViewItem item, string prefix) =>
        item.Tag as string is { } tag && tag.StartsWith(prefix, StringComparison.Ordinal)
            ? tag[prefix.Length..]
            : null;

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
                case "accounts":
                    NavigateTo(typeof(AccountsPage), null);
                    break;

                // 子项的 Tag 形如 download-shaders，后缀就是下载页的分区键。
                // 父项「下载」是 SelectsOnInvoked=False，永远不会成为 SelectedItem。
                case not null when tag.StartsWith("download-", StringComparison.Ordinal):
                    // 记住这次停留的分区，下次点父项「下载」直接回到这里。
                    // 必须用 args.SelectedItem：事件触发时 sender.SelectedItem 还是旧值。
                    _lastDownloadItem = selected;

                    NavigateToDownloadSection(selected);
                    break;

                // 「管理」下的四个子项：实例页 + 三类内容页
                case not null when tag.StartsWith("manage-", StringComparison.Ordinal):
                    _lastManageItem = selected;
                    NavigateToManageSection(selected);
                    break;

                default:
                    NavigateTo(typeof(HomePage), null);
                    break;
            }
        }
    }

    /// <summary>
    /// 「下载」的子项：「Mod」是独立页面（有自己的搜索与详情），其余五项仍是下载页的不同分区。
    /// 不把 Mod 塞进 DownloadPage 是因为它需要一整套自己的视图模型与二级页面，
    /// 混在一个页面里会变成两套 DataContext。
    /// </summary>
    private void NavigateToDownloadSection(NavigationViewItem item)
    {
        var key = SectionKey(item, "download-");
        if (key == "mod")
        {
            NavigateTo(typeof(ModDownloadPage), null);
            return;
        }

        NavigateTo(typeof(DownloadPage), key ?? "minecraft");
    }

    /// <summary>
    /// 「管理」的子项：「实例」是独立的实例页，其余三项是同一个内容页的不同分区。
    /// </summary>
    private void NavigateToManageSection(NavigationViewItem item)
    {
        var key = SectionKey(item, "manage-");
        if (key is null or "instances")
        {
            NavigateTo(typeof(InstancesPage), null);
            return;
        }

        NavigateTo(typeof(ContentPage), key);
    }

    /// <summary>
    /// 下载页与内容页的分区都是同一个页面、只是参数不同，
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
