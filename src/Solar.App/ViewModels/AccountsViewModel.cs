using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Solar.App.Services;
using Solar.Core.Accounts;
using Windows.ApplicationModel.DataTransfer;

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

            // 设备码回调来自 MSAL 的后台线程，直接改属性会跨线程更新 x:Bind
            //（TextBlock.Text 抛 RPC_E_WRONG_THREAD）。Progress 在构造时捕获
            // UI 线程的 SynchronizationContext，Report 时自动切回去。
            IProgress<DeviceCodePrompt> deviceCodeProgress = new Progress<DeviceCodePrompt>(prompt =>
            {
                DeviceCode = prompt.UserCode;
                DeviceCodeUri = prompt.VerificationUri;

                // 省掉用户手抄代码：复制到剪贴板后直接把验证页打开
                var copied = TryCopyToClipboard(prompt.UserCode);
                TryOpenVerificationPage(prompt.VerificationUri);

                LoginStatus = copied
                    ? "验证码已复制到剪贴板，在浏览器里粘贴并确认即可"
                    : "请在浏览器中打开上面的网址并输入代码";
            });

            var account = await _authService.SignInAsync(
                prompt =>
                {
                    deviceCodeProgress.Report(prompt);
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

    /// <summary>把设备码放进剪贴板；失败时不打断登录，页面上仍显示代码。</summary>
    private bool TryCopyToClipboard(string text)
    {
        try
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(text);
            Clipboard.SetContent(package);
            // 让剪贴板内容在本程序退出后仍然有效
            Clipboard.Flush();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "复制设备码到剪贴板失败");
            return false;
        }
    }

    /// <summary>用默认浏览器打开微软的设备码验证页；失败时页面上的网址仍可手动打开。</summary>
    private void TryOpenVerificationPage(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = uri,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "打开设备码验证页失败: {Uri}", uri);
        }
    }

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
