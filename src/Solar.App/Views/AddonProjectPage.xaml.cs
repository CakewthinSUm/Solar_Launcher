using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Solar.App.Services;
using Solar.App.ViewModels;
using Solar.Core.Mods;

namespace Solar.App.Views;

public sealed partial class AddonProjectPage : PageBase
{
    public AddonProjectPage() : this(App.GetService<AddonProjectViewModel>())
    {
    }

    public AddonProjectPage(AddonProjectViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        // 页面被 NavigationCacheMode 缓存复用，订阅放在构造函数里而不是 OnNavigatedTo，
        // 否则每次进入都会多挂一个处理器
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        RenderBody();
    }

    public new AddonProjectViewModel ViewModel { get; }

    /// <summary>正文是解析后的块数据，x:Bind 铺不开，交给渲染器；详情拉回来后再重绘一次。</summary>
    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AddonProjectViewModel.BodyBlocks))
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
        // 整合包走另一条流程：它不装进已有版本，而是创建一个新版本
        if (ViewModel.GetModpackRequest() is { } modpack)
        {
            await InstallModpackAsync(modpack.Version);
            return;
        }

        if (ViewModel.GetInstallRequest() is not { } request)
        {
            return;
        }

        var dialog = new AddonInstallDialog(request.Project, request.Version, request.GameVersion);

        // 对话框在安装完成后自己 Hide()，返回值不一定是 Primary，以 Result 为准
        await dialog.ShowAsync();

        if (dialog.Result is { } result)
        {
            ViewModel.StatusMessage = result.Installed == 0
                ? "没有安装任何文件。"
                : $"已安装 {result.Installed} 个文件" + (result.Skipped > 0 ? $"，跳过 {result.Skipped} 个已存在的文件" : "");
        }
    }

    private async Task InstallModpackAsync(ModVersion version)
    {
        var dialog = new ModpackInstallDialog(version);
        await dialog.ShowAsync();

        if (dialog.Result is { } result)
        {
            ViewModel.StatusMessage =
                $"整合包已安装为版本「{result.Version.Name}」，可以直接启动。"
                + (result.FailedFiles.Count > 0 ? $"（{result.FailedFiles.Count} 个文件下载失败）" : "");
        }
    }

    private void Icon_ImageFailed(object sender, ExceptionRoutedEventArgs e) => ViewModel.HasIcon = false;
}
