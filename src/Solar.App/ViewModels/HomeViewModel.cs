using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Solar.App.Services;
using Solar.Core.Accounts;
using Solar.Core.Download;
using Solar.Core.Instances;
using Solar.Core.Java;
using Solar.Core.Launch;
using Solar.Core.Settings;

namespace Solar.App.ViewModels;

public sealed partial class HomeViewModel : ViewModelBase
{
    private readonly IInstanceRepository _instances;
    private readonly IVersionScanner _scanner;
    private readonly IJavaDetector _javaDetector;
    private readonly IGameLaunchService _launchService;
    private readonly IAccountStore _accounts;
    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly TaskbarProgressService _taskbar;
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
        IVersionScanner scanner,
        IJavaDetector javaDetector,
        IGameLaunchService launchService,
        IAccountStore accounts,
        ISettingsService settings,
        IDialogService dialogs,
        TaskbarProgressService taskbar,
        ILogger<HomeViewModel> logger)
    {
        _instances = instances;
        _scanner = scanner;
        _javaDetector = javaDetector;
        _launchService = launchService;
        _accounts = accounts;
        _settings = settings;
        _dialogs = dialogs;
        _taskbar = taskbar;
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

            // 主页是打开后最先看到的页面，在这里导入游戏目录里的版本，
            // 用户不必先去实例页也能直接启动已有版本。
            // 失败不影响主页：扫描只是补充，读不到版本时照常显示已有实例。
            try
            {
                await _scanner.ImportAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "扫描游戏目录失败，跳过导入");
            }

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
                double? ratio = p.TotalBytes > 0
                    ? p.Ratio
                    : p.TotalFiles > 0 ? (double)p.CompletedFiles / p.TotalFiles : null;

                if (ratio is { } value)
                {
                    LaunchProgress = value;
                }

                // 总量还没算出来时给个不确定进度，任务栏上至少能看出在忙
                _taskbar.Report(ratio);

                if (!string.IsNullOrEmpty(p.CurrentFile))
                {
                    LaunchStatus = p.CurrentFile;
                }
            });

            // 只有本次真的装过加载器时，才需要在后续阶段把任务栏从安装进度切回待定状态
            var loaderStageSeen = false;

            var stageProgress = new Progress<LaunchStageChanged>(s =>
            {
                LaunchStatus = s.Message;

                switch (s.Stage)
                {
                    case LaunchStage.InstallingLoader:
                        loaderStageSeen = true;
                        // 加载器安装不走 DownloadProgress，用阶段自带的比例喂任务栏。
                        // 比例为 0 表示这一步没有可细分的进度，显示滚动的待定状态。
                        _taskbar.Report(s.Ratio > 0 ? s.Ratio : null);
                        break;

                    case LaunchStage.ResolvingVersion when loaderStageSeen:
                        // 装完还要解析版本、补齐文件；先切回待定状态，
                        // 否则进度条会停在安装阶段的旧比例上
                        _taskbar.Report(null);
                        break;

                    case LaunchStage.Starting:
                        // 补齐文件结束、转入启动游戏时闪一下任务栏，提示下载已完成
                        _taskbar.Flash();
                        break;
                }
            });

            var process = await _launchService.LaunchAsync(
                instance, downloadProgress, stageProgress, _launchCts.Token);

            LaunchStatus = "游戏已启动";
            StatusMessage = $"已启动“{instance.Name}”";

            // 进程起来不等于窗口已经出现，设置里的“最小化/关闭启动器”等窗口出来再执行。
            // 不 await：启动流程到此结束，按钮要立刻恢复可用。
            var afterLaunch = _settings.Current.AfterLaunchAction;
            if (afterLaunch != AfterLaunchAction.Nothing)
            {
                _ = ApplyAfterLaunchActionAsync(process, afterLaunch);
            }

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
            _taskbar.Reset();
            _launchCts?.Dispose();
            _launchCts = null;
        }
    }

    /// <summary>
    /// 等游戏窗口真的出现，再按设置最小化或关闭启动器。
    /// <para>
    /// 只看进程是否启动成功是不够的：Java 初始化、加载模组都发生在窗口出来之前，
    /// 这时就把启动器收起来，用户既看不到启动进度，也看不到启动失败的报错。
    /// </para>
    /// </summary>
    private async Task ApplyAfterLaunchActionAsync(Process process, AfterLaunchAction action)
    {
        try
        {
            if (!await WaitForGameWindowAsync(process).ConfigureAwait(false))
            {
                // 窗口出现前进程就退出了，多半是启动失败：保持启动器可见，方便排查
                _logger.LogWarning("游戏窗口未出现，跳过启动器窗口处理");
                return;
            }

            if (App.MainWindow is not { } window)
            {
                return;
            }

            // 窗口操作必须在 UI 线程上做
            window.DispatcherQueue.TryEnqueue(() =>
            {
                switch (action)
                {
                    case AfterLaunchAction.Minimize:
                        if (window.AppWindow.Presenter is OverlappedPresenter presenter)
                        {
                            presenter.Minimize();
                        }

                        break;

                    case AfterLaunchAction.Close:
                        window.Close();
                        break;
                }
            });
        }
        catch (Exception ex)
        {
            // 收起窗口失败不该影响已经启动的游戏
            _logger.LogWarning(ex, "游戏启动后处理启动器窗口失败");
        }
    }

    /// <summary>
    /// 轮询到游戏进程建出主窗口为止；进程退出则返回 false。
    /// 不设超时：大型整合包几十秒才出窗口很常见，提前放弃等于设置失效。
    /// 循环随进程结束自然退出，且每次启动只跑一次。
    /// </summary>
    private static async Task<bool> WaitForGameWindowAsync(Process process)
    {
        while (!process.HasExited)
        {
            try
            {
                process.Refresh();
                if (process.MainWindowHandle != IntPtr.Zero)
                {
                    return true;
                }
            }
            catch (InvalidOperationException)
            {
                // 刚好在这一刻退出，与上面的 HasExited 同理
                return false;
            }

            await Task.Delay(250).ConfigureAwait(false);
        }

        return false;
    }

    [RelayCommand]
    private void CancelLaunch()
    {
        _launchCts?.Cancel();
    }
}
