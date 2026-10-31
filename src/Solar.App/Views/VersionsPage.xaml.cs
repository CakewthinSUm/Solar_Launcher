using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Solar.App.ViewModels;
using Solar.Core.Versions;

namespace Solar.App.Views;

public sealed partial class VersionsPage : PageBase
{
    public VersionsPage() : this(App.GetService<VersionsViewModel>())
    {
    }

    public VersionsPage(VersionsViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        // 与内容页一样不缓存：分区栏的选中项在重建时才能可靠地回到「版本」
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Disabled;

        // 「管理」点开默认落在版本分区
        SectionBar.SelectedKey = "Versions";
    }

    public new VersionsViewModel ViewModel { get; }

    /// <summary>
    /// 分区栏切到 Mods / 资源包 / 光影：仍在「管理」里，换成内容页即可。
    /// 分区切换不播页面动画——它更像页内 tab，用入场动画整页飞进来会显得很跳。
    /// </summary>
    private void SectionBar_SectionSelected(object? sender, string key)
    {
        if (key == "Versions")
        {
            return;
        }

        Frame.Navigate(typeof(ContentPage), key, new SuppressNavigationTransitionInfo());
    }

    /// <summary>新建版本走创建页：那里能选版本、加载器与附加组件，并一次装完。</summary>
    private void NewVersion_Click(object sender, RoutedEventArgs e) =>
        Frame.Navigate(typeof(VersionCreatePage));

    private void SetupVersion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GameVersion version })
        {
            Frame.Navigate(typeof(VersionSetupPage), version.Id);
        }
    }

    private void DeleteVersion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GameVersion version })
        {
            ViewModel.DeleteCommand.Execute(version);
        }
    }

    /// <summary>列表里的心形按钮：切换收藏，条目会自己移动到收藏夹或移回原组。</summary>
    private void Favorite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GameVersion version })
        {
            ViewModel.ToggleFavoriteCommand.Execute(version);
        }
    }

    private void VersionRow_PointerEntered(object sender, PointerRoutedEventArgs e) =>
        SetRowHover(sender, isHovered: true);

    // 指针在行内移动（包括经过子按钮）时保持悬停背景
    private void VersionRow_PointerMoved(object sender, PointerRoutedEventArgs e) =>
        SetRowHover(sender, isHovered: true);

    private void VersionRow_PointerExited(object sender, PointerRoutedEventArgs e) =>
        SetRowHover(sender, isHovered: false);

    private void SetRowHover(object sender, bool isHovered)
    {
        if (sender is Border border)
        {
            border.Background = (Brush)Resources[isHovered
                ? "VersionRowHoverBrush"
                : "VersionRowTransparentBrush"];
        }
    }
}
