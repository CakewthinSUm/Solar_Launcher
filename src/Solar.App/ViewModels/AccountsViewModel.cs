using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Solar.App.Services;
using Solar.Core.Accounts;

namespace Solar.App.ViewModels;

public sealed partial class AccountsViewModel : ViewModelBase
{
    private readonly IAccountStore _store;
    private readonly IMicrosoftAuthService _authService;
    private readonly IDialogService _dialogs;
    private readonly ILogger<AccountsViewModel> _logger;

    private CancellationTokenSource? _loginCts;

    [ObservableProperty]
    private GameAccount? _selectedAccount;

    [ObservableProperty]
    private bool _isSigningIn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDeviceCode))]
    private string _deviceCode = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDeviceCode))]
    private string _deviceCodeUri = "";

    [ObservableProperty]
    private string _loginStatus = "";

    public AccountsViewModel(
        IAccountStore store,
        IMicrosoftAuthService authService,
        IDialogService dialogs,
        ILogger<AccountsViewModel> logger)
    {
        _store = store;
        _authService = authService;
        _dialogs = dialogs;
        _logger = logger;
    }

    public ObservableCollection<GameAccount> Accounts { get; } = new();

    public bool HasDeviceCode => !string.IsNullOrWhiteSpace(DeviceCode);

    public string ActiveAccountSummary
    {
        get
        {
            var active = Accounts.FirstOrDefault(a => a.Id == _store.ActiveAccountId);
            return active is null ? "未登录" : $"当前账户：{active.Username}";
        }
    }

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        await _store.LoadAsync();
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            IsBusy = true;
            ErrorMessage = null;

            var accounts = await _store.GetAllAsync();
            Accounts.Clear();
            foreach (var account in accounts)
            {
                Accounts.Add(account);
            }

            SelectedAccount = Accounts.FirstOrDefault(a => a.Id == _store.ActiveAccountId)
                              ?? Accounts.FirstOrDefault();

            OnPropertyChanged(nameof(ActiveAccountSummary));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载账户列表失败");
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SignInAsync()
    {
        if (IsSigningIn)
        {
            return;
        }

        _loginCts = new CancellationTokenSource();

        try
        {
            IsSigningIn = true;
            ErrorMessage = null;
            StatusMessage = null;
            DeviceCode = "";
            DeviceCodeUri = "";
            LoginStatus = "正在向微软请求登录代码…";

            var account = await _authService.SignInAsync(
                prompt =>
                {
                    // 回调可能来自后台线程，Progress 会切回 UI 线程
                    DeviceCode = prompt.UserCode;
                    DeviceCodeUri = prompt.VerificationUri;
                    LoginStatus = "请在浏览器中打开上面的网址并输入代码";
                    return Task.CompletedTask;
                },
                _loginCts.Token);

            LoginStatus = "";
            StatusMessage = $"已登录：{account.Username}";
            await RefreshAsync();
        }
        catch (OperationCanceledException)
        {
            LoginStatus = "已取消登录";
        }
        catch (AccountException ex)
        {
            _logger.LogError(ex, "微软登录失败");
            LoginStatus = "";
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "微软登录失败");
            LoginStatus = "";
            ErrorMessage = "登录失败：" + ex.Message;
        }
        finally
        {
            IsSigningIn = false;
            DeviceCode = "";
            DeviceCodeUri = "";
            _loginCts?.Dispose();
            _loginCts = null;
        }
    }

    [RelayCommand]
    private void CancelSignIn() => _loginCts?.Cancel();

    [RelayCommand]
    private async Task SetActiveAsync(GameAccount? account)
    {
        if (account is null)
        {
            return;
        }

        _store.ActiveAccountId = account.Id;
        await _store.LoadAsync();
        OnPropertyChanged(nameof(ActiveAccountSummary));
        StatusMessage = $"已切换到 {account.Username}";
    }

    [RelayCommand]
    private async Task RemoveAsync(GameAccount? account)
    {
        if (account is null)
        {
            return;
        }

        if (!await _dialogs.ShowConfirmAsync(
                "移除账户",
                $"确定要移除「{account.Username}」吗？本地保存的登录令牌会被一并清除。",
                "移除",
                "取消"))
        {
            return;
        }

        try
        {
            IsBusy = true;
            await _authService.SignOutAsync(account.Id);
            await RefreshAsync();
            StatusMessage = $"已移除 {account.Username}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "移除账户失败");
            await _dialogs.ShowMessageAsync("移除失败", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
