using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Solar.App.Services;
using Solar.App.ViewModels;

namespace Solar.App.Views;

public sealed partial class ModProjectPage : PageBase
{
    public ModProjectPage() : this(App.GetService<ModProjectViewModel>())
    {
    }

    public ModProjectPage(ModProjectViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        // 页面被 NavigationCacheMode 缓存复用，订阅放在构造函数里而不是 OnNavigatedTo，
        // 否则每次进入都会多挂一个处理器
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        RenderBody();
    }

    public new ModProjectViewModel ViewModel { get; }

    /// <summary>正文是解析后的块数据，x:Bind 铺不开，交给渲染器；详情拉回来后再重绘一次。</summary>
    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ModProjectViewModel.BodyBlocks))
        {
            RenderBody();
        }
    }

    private void RenderBody()
    {
        // 主题资源只在元素进入可视化树之后才能解析，构造函数里第一次调用时取不到，
        // 那时正文也还是空的；真正的正文在详情拉回来后由 PropertyChanged 触发重绘。
        var secondary = Application.Current.Resources.TryGetValue("TextFillColorSecondaryBrush", out var value)
            ? value as Microsoft.UI.Xaml.Media.Brush
            : null;

        MarkdownBlockRenderer.Render(BodyText, ViewModel.BodyBlocks, secondary);
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.GetInstallRequest() is not { } request)
        {
            return;
        }

        var dialog = new ModInstallDialog(request.Project, request.Version, request.Instance);

        // 对话框在安装完成后自己 Hide()，返回值不一定是 Primary，以 Result 为准
        await dialog.ShowAsync();

        if (dialog.Result is { } result)
        {
            ViewModel.StatusMessage = result.Installed == 0
                ? "没有安装任何文件。"
                : $"已安装 {result.Installed} 个文件" + (result.Skipped > 0 ? $"，跳过 {result.Skipped} 个已存在的文件" : "");
        }
    }

    private void Icon_ImageFailed(object sender, ExceptionRoutedEventArgs e) => ViewModel.HasIcon = false;
}
