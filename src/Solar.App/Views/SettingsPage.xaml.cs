using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Solar.App.Services;
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

        // 重置设置会把 Key 清空，而 PasswordBox 不能绑定，只能跟着视图模型同步
        viewModel.PropertyChanged += ViewModel_PropertyChanged;
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

        FillPasswordBox();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.CurseForgeApiKey))
        {
            FillPasswordBox();
        }
    }

    private void FillPasswordBox()
    {
        if (CurseForgeKeyBox.Password == ViewModel.CurseForgeApiKey)
        {
            return;
        }

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

    /// <summary>
    /// 离开本页前调用。有未保存的修改时问用户是否保存。
    /// 返回 true 表示可以离开（已保存成功，或用户选择不保存），false 表示留在本页。
    /// </summary>
    public async Task<bool> ConfirmLeaveAsync()
    {
        if (!ViewModel.HasUnsavedChanges)
        {
            return true;
        }

        var choice = await ViewModel.AskSaveBeforeLeavingAsync();
        return choice switch
        {
            // 保存失败（例如游戏目录不可用）时不离开，让用户改完再走
            SaveChangesChoice.Save => await ViewModel.TrySaveAsync(),
            SaveChangesChoice.Discard => true,
            _ => false,
        };
    }
}
