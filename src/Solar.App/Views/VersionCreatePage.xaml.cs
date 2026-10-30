using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Solar.App.ViewModels;
using Solar.Core.Versions;
using Solar.Core.Models;

namespace Solar.App.Views;

public sealed partial class VersionCreatePage : PageBase
{
    public VersionCreatePage() : this(App.GetService<VersionCreateViewModel>())
    {
    }

    public VersionCreatePage(VersionCreateViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        ViewModel.VersionCreated += OnVersionCreated;
        ViewModel.BackRequested += OnBackRequested;
    }

    public new VersionCreateViewModel ViewModel { get; }

    /// <summary>
    /// 页面被 NavigationCacheMode 缓存复用，每次导航都要把新参数交给视图模型；
    /// 参数可能是下载页传来的 <see cref="VersionSummary"/>，也可能为空（从版本页新建）。
    /// </summary>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel.Version = e.Parameter as VersionSummary;
        base.OnNavigatedTo(e);
    }

    /// <summary>用户在页面上动过隔离开关后，默认值就不再覆盖它。</summary>
    private void Isolation_Toggled(object sender, RoutedEventArgs e)
    {
        // 程序设置默认值也会触发 Toggled，那次不算用户操作
        if (sender is ToggleSwitch && !ViewModel.IsApplyingIsolation)
        {
            ViewModel.MarkIsolationTouched();
        }
    }

    /// <summary>
    /// 「清除」把选择置空；重新计算由视图模型监听 Selected 的变化完成，
    /// 这里只要清掉这一张卡即可。
    /// </summary>
    private void ClearCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ComponentCardViewModel card })
        {
            ViewModel.ClearCardCommand.Execute(card);
        }
    }

    private void OnVersionCreated(object? sender, GameVersion instance) =>
        Frame.Navigate(typeof(VersionsPage));

    private void OnBackRequested(object? sender, EventArgs e)
    {
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
        else
        {
            Frame.Navigate(typeof(DownloadPage), "minecraft");
        }
    }
}
