using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Solar.App.ViewModels;

namespace Solar.App.Views;

public sealed partial class SettingsPage : PageBase
{
    public SettingsPage() : this(App.GetService<SettingsViewModel>())
    {
    }

    public SettingsPage(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public new SettingsViewModel ViewModel { get; }

    /// <summary>
    /// PasswordBox 没有可绑定的 Password 属性，进出都靠代码：
    /// 导航时把设置里的 Key 填进去，用户输入时再写回视图模型。
    /// 回填本身会触发 PasswordChanged，用这个标记区分，避免把刚填的值又写一遍。
    /// </summary>
    private bool _fillingPassword;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        _fillingPassword = true;
        CurseForgeKeyBox.Password = ViewModel.CurseForgeApiKey;
        _fillingPassword = false;
    }

    private void CurseForgeKey_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!_fillingPassword)
        {
            ViewModel.CurseForgeApiKey = CurseForgeKeyBox.Password;
        }
    }
}
