using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Solar.App.Services;
using Solar.Core.Accounts;
using Solar.Core.Download;
using Solar.Core.Instances;
using Solar.Core.Java;
using Solar.Core.Launch;

namespace Solar.App.ViewModels;

public sealed partial class HomeViewModel : ViewModelBase
{
    private readonly IInstanceRepository _instances;
    private readonly IJavaDetector _javaDetector;
    private readonly IGameLaunchService _launchService;
    private readonly IAccountStore _accounts;
    private readonly IDialogService _dialogs;
    private readonly ILogger<HomeViewModel> _logger;

    private CancellationTokenSource? _launchCts;

    [ObservableProperty]
    private GameInstance? _selectedInstance;

    [ObservableProperty]
    private string _javaStatus = "未检测";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLaunchProgress))]
    private double _launchProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLaunchProgress))]
    private string _launchStatus = "";

    [ObservableProperty]
    private bool _isLaunching;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignedIn))]
    private string _accountSummary = "未登录";

    public HomeViewModel(
        IInstanceRepository instances,
        IJavaDetector javaDetector,
        IGameLaunchService launchService,
        IAccountStore accounts,
        IDialogService dialogs,
        ILogger<HomeViewModel> logger)
    {
        _instances = instances;
        _javaDetector = javaDetector;
        _launchService = launchService;
        _accounts = accounts;
        _dialogs = dialogs;
        _logger = logger;
    }

    /// <summary>没有登录账户时启动必定失败，提前在界面上标出来。</summary>
    public bool IsSignedIn => !string.IsNullOrEmpty(_accounts.ActiveAccountId);

    public ObservableCollection<GameInstance> Instances { get; } = new();

    public bool HasLaunchProgress => IsLaunching || LaunchProgress > 0;

    public string SelectedInstanceSummary => SelectedInstance is { } instance
        ? $"{instance.MinecraftVersion} · {instance.DisplayLoader}"
        : "先到“管理 → 实例”创建一个实例";

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        await RefreshAsync();
    }

    partial void OnSelectedInstanceChanged(GameInstance? value) =>
        OnPropertyChanged(nameof(SelectedInstanceSummary));

    partial void OnIsLaunchingChanged(bool value) =>
        OnPropertyChanged(nameof(HasLaunchProgress));

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            IsBusy = true;
            ErrorMessage = null;

            var instances = await _instances.GetAllAsync();
            Instances.Clear();
            foreach (var instance in instances)
            {
                Instances.Add(instance);
            }

            SelectedInstance = Instances.FirstOrDefault();

            var runtimes = await _javaDetector.DetectAsync();
            JavaStatus = runtimes.Count == 0
                ? "未找到 Java"
                : $"已检测 {runtimes.Count} 个 Java · 推荐 Java {runtimes[0].MajorVersion}";

            await RefreshAccountAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "刷新主页失败");
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshAccountAsync()
    {
        var accounts = await _accounts.GetAllAsync();
        var active = accounts.FirstOrDefault(a => a.Id == _accounts.ActiveAccountId);

        AccountSummary = active is null ? "未登录" : active.Username;
        OnPropertyChanged(nameof(IsSignedIn));
    }

    [RelayCommand]
    private async Task LaunchAsync()
    {
        if (SelectedInstance is not { } instance)
        {
            StatusMessage = "还没有可启动的实例，请先到“管理 → 实例”创建一个。";
            return;
        }

        if (!IsSignedIn)
        {
            ErrorMessage = "还没有登录微软账户，请先到「账户」页登录后再启动。";
            return;
        }

        if (IsLaunching)
        {
            return;
        }

        _launchCts = new CancellationTokenSource();

        try
        {
            IsLaunching = true;
            LaunchProgress = 0;
            ErrorMessage = null;
            StatusMessage = null;

            var downloadProgress = new Progress<DownloadProgress>(p =>
            {
                // 文件级进度和字节级进度交替上报，取更有信息量的一种
                LaunchProgress = p.TotalBytes > 0
                    ? p.Ratio
                    : p.TotalFiles > 0 ? (double)p.CompletedFiles / p.TotalFiles : 0;

                if (!string.IsNullOrEmpty(p.CurrentFile))
                {
                    LaunchStatus = p.CurrentFile;
                }
            });

            var stageProgress = new Progress<LaunchStageChanged>(s => LaunchStatus = s.Message);

            var process = await _launchService.LaunchAsync(
                instance, downloadProgress, stageProgress, _launchCts.Token);

            LaunchStatus = "游戏已启动";
            StatusMessage = $"已启动“{instance.Name}”";

            // 进程退出后刷新“最近游玩”时间
            _ = process.WaitForExitAsync(_launchCts.Token).ContinueWith(
                _ => _logger.LogInformation("游戏进程已退出: {Name}", instance.Name),
                TaskScheduler.Default);
        }
        catch (OperationCanceledException)
        {
            LaunchStatus = "已取消";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动实例 {Name} 失败", instance.Name);
            ErrorMessage = ex.Message;
            LaunchStatus = "";
            await _dialogs.ShowMessageAsync("启动失败", ex.Message);
        }
        finally
        {
            IsLaunching = false;
            _launchCts?.Dispose();
            _launchCts = null;
        }
    }

    [RelayCommand]
    private void CancelLaunch()
    {
        _launchCts?.Cancel();
    }
}
