using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Solar.App.Formatting;
using Solar.App.Services;
using Solar.Core.Accounts;
using Solar.Core.Download;
using Solar.Core.Instances;
using Solar.Core.Java;
using Solar.Core.Launch;

namespace Solar.App.ViewModels;

public sealed partial class HomeViewModel : ViewModelBase
{
    /// <summary>下载与加载器会上报得很频繁，界面最多每 80ms 刷新一次。</summary>
    private const int UiSyncIntervalMs = 80;

    private readonly IInstanceRepository _instances;
    private readonly IVersionScanner _scanner;
    private readonly IJavaDetector _javaDetector;
    private readonly IGameLaunchService _launchService;
    private readonly IAccountStore _accounts;
    private readonly TaskbarProgressService _taskbar;
    private readonly ILogger<HomeViewModel> _logger;

    private CancellationTokenSource? _launchCts;
    private CancellationTokenSource? _autoCollapseCts;
    private LaunchProgressTracker? _tracker;
    private long _lastUiSyncTimestamp;

    [ObservableProperty]
    private GameInstance? _selectedInstance;

    [ObservableProperty]
    private string _javaStatus = "未检测";

    [ObservableProperty]
    private bool _isLaunching;

    /// <summary>底部进度卡片的开合状态；真正的形变动画由 LaunchDock 监听它触发。</summary>
    [ObservableProperty]
    private bool _isLaunchCardOpen;

    [ObservableProperty]
    private double _launchProgress;

    [ObservableProperty]
    private string _launchPercentText = "";

    [ObservableProperty]
    private string _launchHeadline = "";

    /// <summary>启动时快照的实例描述，避免导航回来时 RefreshAsync 改了选择导致卡片标题变化。</summary>
    [ObservableProperty]
    private string _launchingInstanceName = "";

    [ObservableProperty]
    private string _launchStageMessage = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLaunchStats))]
    private string _launchStats = "";

    [ObservableProperty]
    private string _launchDetail = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLaunchError))]
    private string _launchErrorMessage = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLaunchRunning))]
    [NotifyPropertyChangedFor(nameof(IsLaunchSucceeded))]
    [NotifyPropertyChangedFor(nameof(IsLaunchFailed))]
    [NotifyPropertyChangedFor(nameof(IsLaunchCanceled))]
    private LaunchOutcome _launchResult = LaunchOutcome.Running;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignedIn))]
    private string _accountSummary = "未登录";

    public HomeViewModel(
        IInstanceRepository instances,
        IVersionScanner scanner,
        IJavaDetector javaDetector,
        IGameLaunchService launchService,
        IAccountStore accounts,
        TaskbarProgressService taskbar,
        ILogger<HomeViewModel> logger)
    {
        _instances = instances;
        _scanner = scanner;
        _javaDetector = javaDetector;
        _launchService = launchService;
        _accounts = accounts;
        _taskbar = taskbar;
        _logger = logger;
    }

    /// <summary>没有登录账户时启动必定失败，提前在界面上标出来。</summary>
    public bool IsSignedIn => !string.IsNullOrEmpty(_accounts.ActiveAccountId);

    public ObservableCollection<GameInstance> Instances { get; } = new();

    /// <summary>步骤条数据源；每次启动重建一次，进度事件只改条目的状态。</summary>
    public ObservableCollection<LaunchStageItem> LaunchStages { get; } = new();

    public bool HasLaunchStats => !string.IsNullOrWhiteSpace(LaunchStats);

    public bool HasLaunchError => !string.IsNullOrWhiteSpace(LaunchErrorMessage);

    public bool IsLaunchRunning => LaunchResult == LaunchOutcome.Running;

    public bool IsLaunchSucceeded => LaunchResult == LaunchOutcome.Succeeded;

    public bool IsLaunchFailed => LaunchResult == LaunchOutcome.Failed;

    public bool IsLaunchCanceled => LaunchResult == LaunchOutcome.Canceled;

    public string SelectedInstanceSummary => SelectedInstance is { } instance
        ? $"{instance.MinecraftVersion} · {instance.DisplayLoader}"
        : "先到“管理 → 实例”创建一个实例";

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        await RefreshAsync();
    }

    partial void OnSelectedInstanceChanged(GameInstance? value) =>
        OnPropertyChanged(nameof(SelectedInstanceSummary));

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

            // 卡片打开期间（启动中、失败待重试）按 Id 重新绑定选中项，
            // 既不改变用户选中的实例，也不会因为列表重建让 ComboBox 丢选中。
            if (!IsLaunchCardOpen)
            {
                SelectedInstance = Instances.FirstOrDefault();
            }
            else if (SelectedInstance is { } selected)
            {
                SelectedInstance = Instances.FirstOrDefault(item => item.Id == selected.Id) ?? selected;
            }

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

        _autoCollapseCts?.Cancel();

        var tracker = new LaunchProgressTracker(instance);
        _tracker = tracker;
        PrepareLaunchUi(instance, tracker);

        _launchCts = new CancellationTokenSource();
        var token = _launchCts.Token;

        ErrorMessage = null;
        StatusMessage = null;

        // IsLaunching 先置位，卡片里的「取消」在第一帧就是可见的
        IsLaunching = true;
        IsLaunchCardOpen = true;

        var downloadProgress = new Progress<DownloadProgress>(p =>
        {
            if (!ReferenceEquals(_tracker, tracker))
            {
                return;
            }

            tracker.Report(p);

            var ratio = p.TotalBytes > 0
                ? p.Ratio
                : p.TotalFiles > 0 ? (double)p.CompletedFiles / p.TotalFiles : (double?)null;

            // 界面和任务栏一起节流；阶段切换和终止事件会强制刷新
            if (SyncLaunchState(force: false))
            {
                _taskbar.Report(ratio);
            }
        });

        // 只有本次真的装过加载器时，才需要在后续阶段把任务栏从安装进度切回待定状态
        var loaderStageSeen = false;

        var stageProgress = new Progress<LaunchStageChanged>(s =>
        {
            if (!ReferenceEquals(_tracker, tracker))
            {
                return;
            }

            tracker.Report(s);
            SyncLaunchState(force: true);

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

        try
        {
            var process = await _launchService.LaunchAsync(
                instance, downloadProgress, stageProgress, token);

            tracker.Complete();
            SyncLaunchState(force: true);
            StatusMessage = $"已启动“{instance.Name}”";

            // 成功态停留一会儿再缩回按钮
            ScheduleAutoCollapse(tracker, TimeSpan.FromSeconds(2.5));

            // 进程观察者要活过启动 CTS 的释放，所以这里用 None；目前只记录退出日志
            _ = process.WaitForExitAsync(CancellationToken.None).ContinueWith(
                _ => _logger.LogInformation("游戏进程已退出: {Name}", instance.Name),
                TaskScheduler.Default);
        }
        catch (OperationCanceledException)
        {
            tracker.Cancel();
            SyncLaunchState(force: true);
            ScheduleAutoCollapse(tracker, TimeSpan.FromSeconds(1.5));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动实例 {Name} 失败", instance.Name);
            tracker.Fail(ex.Message);
            SyncLaunchState(force: true);
            // 错误直接显示在卡片里，不再弹模态框挡住进度详情
        }
        finally
        {
            IsLaunching = false;
            _taskbar.Reset();
            _launchCts?.Dispose();
            _launchCts = null;
        }
    }

    [RelayCommand]
    private void CancelLaunch()
    {
        try
        {
            _launchCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 启动已经结束，CTS 在 finally 里释放过了
        }
    }

    [RelayCommand]
    private void DismissLaunchCard()
    {
        if (IsLaunching)
        {
            return;
        }

        _autoCollapseCts?.Cancel();
        IsLaunchCardOpen = false;
    }

    private void PrepareLaunchUi(GameInstance instance, LaunchProgressTracker tracker)
    {
        LaunchStages.Clear();
        for (var i = 0; i < tracker.Plan.Count; i++)
        {
            LaunchStages.Add(new LaunchStageItem(tracker.Plan[i], i == tracker.Plan.Count - 1));
        }

        LaunchResult = LaunchOutcome.Running;
        LaunchProgress = 0;
        LaunchPercentText = "0%";
        LaunchHeadline = "正在启动";
        LaunchingInstanceName = $"{instance.Name} · {instance.MinecraftVersion} · {instance.DisplayLoader}";
        LaunchStageMessage = "正在准备启动…";
        LaunchDetail = "";
        LaunchStats = "";
        LaunchErrorMessage = "";
        _lastUiSyncTimestamp = 0;
    }

    /// <summary>
    /// 把状态机的快照同步到可绑定属性。返回 false 表示被节流跳过，调用方可据此跳过任务栏更新。
    /// </summary>
    private bool SyncLaunchState(bool force)
    {
        if (_tracker is not { } tracker)
        {
            return false;
        }

        var now = Stopwatch.GetTimestamp();
        if (!force
            && Stopwatch.GetElapsedTime(_lastUiSyncTimestamp, now).TotalMilliseconds < UiSyncIntervalMs)
        {
            return false;
        }

        _lastUiSyncTimestamp = now;

        for (var i = 0; i < LaunchStages.Count && i < tracker.States.Count; i++)
        {
            LaunchStages[i].State = tracker.States[i];
        }

        LaunchProgress = Math.Max(LaunchProgress, tracker.OverallRatio);
        LaunchPercentText = $"{LaunchProgress:P0}";
        LaunchResult = tracker.Outcome;

        LaunchStageMessage = tracker.Outcome switch
        {
            LaunchOutcome.Succeeded => "已交给游戏进程",
            LaunchOutcome.Canceled => "已取消",
            _ => tracker.ActiveMessage,
        };

        // 文件名只在下载阶段展示，进入启动阶段后不再留着上一条
        LaunchDetail = tracker.ActiveStage == LaunchStage.Downloading
            ? tracker.CurrentFile
            : "";

        LaunchStats = FormatLaunchStats(tracker);
        LaunchErrorMessage = tracker.ErrorMessage ?? "";
        LaunchHeadline = tracker.Outcome switch
        {
            LaunchOutcome.Succeeded => "游戏已启动",
            LaunchOutcome.Failed => "启动失败",
            LaunchOutcome.Canceled => "已取消",
            _ => "正在启动",
        };

        return true;
    }

    private static string FormatLaunchStats(LaunchProgressTracker tracker)
    {
        if (tracker.TotalFiles <= 0 && tracker.TotalBytes <= 0)
        {
            return "";
        }

        var parts = new List<string>(2);
        if (tracker.TotalFiles > 0)
        {
            parts.Add($"{tracker.CompletedFiles}/{tracker.TotalFiles} 文件");
        }

        if (tracker.TotalBytes > 0)
        {
            parts.Add(
                $"{ModDisplay.FileSize(tracker.DownloadedBytes)} / {ModDisplay.FileSize(tracker.TotalBytes)}");
        }

        return string.Join(" · ", parts);
    }

    private void ScheduleAutoCollapse(LaunchProgressTracker tracker, TimeSpan delay)
    {
        _autoCollapseCts?.Cancel();

        var cts = new CancellationTokenSource();
        _autoCollapseCts = cts;
        _ = CollapseAfterDelayAsync(tracker, delay, cts);
    }

    private async Task CollapseAfterDelayAsync(
        LaunchProgressTracker tracker,
        TimeSpan delay,
        CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(delay, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            if (ReferenceEquals(_autoCollapseCts, cts))
            {
                _autoCollapseCts = null;
            }

            cts.Dispose();
        }

        // 期间可能已经开始了新一轮启动或手动收起
        if (!ReferenceEquals(_tracker, tracker) || !IsLaunchCardOpen)
        {
            return;
        }

        if (tracker.Outcome is LaunchOutcome.Succeeded or LaunchOutcome.Canceled)
        {
            IsLaunchCardOpen = false;
        }
    }
}
