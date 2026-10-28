using Solar.Core.Download;
using Solar.Core.Instances;

namespace Solar.Core.Launch;

/// <summary>步骤条上单个阶段的状态。</summary>
public enum LaunchStageState
{
    Pending,
    Active,
    Done,
    Failed,
    Canceled,
}

/// <summary>一次启动的最终结果。</summary>
public enum LaunchOutcome
{
    Running,
    Succeeded,
    Failed,
    Canceled,
}

/// <summary>步骤条的一项：阶段、显示名与它在总体进度里的权重。</summary>
public sealed record LaunchStagePlan(LaunchStage Stage, string Title, double Weight);

/// <summary>
/// 把「阶段变化」和「下载进度」两条异步通道合并成一份可显示的启动状态。
/// <para>
/// 几条必须在这里处理的规则：
/// 加载器只在首次启动模组实例时安装，所以阶段列表是动态的；
/// 服务端只上报阶段切换、不发送「完成」事件，前进即代表上一步完成；
/// <see cref="Progress{T}"/> 是异步投递的，下载回调可能晚于 <see cref="LaunchStage.Starting"/> 到达，
/// 也可能早于 <see cref="LaunchStage.Downloading"/> 到达，需要缓存/丢弃；
/// 启动结束（成功、失败、取消）后仍可能有迟到回调，必须全部忽略。
/// 放在 Core 里是为了能直接写单元测试，UI 只负责把状态画出来。
/// </para>
/// </summary>
public sealed class LaunchProgressTracker
{
    private readonly List<LaunchStagePlan> _plan;
    private readonly LaunchStageState[] _states;
    private readonly double[] _ratios;
    private readonly int _downloadIndex;

    private int _activeIndex = -1;
    private double _overallRatio;
    private DownloadProgress? _pendingDownload;

    public LaunchProgressTracker(GameInstance instance)
    {
        _plan = BuildPlan(instance);
        _states = new LaunchStageState[_plan.Count];
        _ratios = new double[_plan.Count];
        Array.Fill(_states, LaunchStageState.Pending);

        TotalWeight = _plan.Sum(stage => stage.Weight);
        _downloadIndex = _plan.FindIndex(stage => stage.Stage == LaunchStage.Downloading);
    }

    /// <summary>本次启动实际会经过的阶段，顺序与 <see cref="States"/> 一致。</summary>
    public IReadOnlyList<LaunchStagePlan> Plan => _plan;

    /// <summary>每个阶段当前的状态，供步骤条逐项绑定。</summary>
    public IReadOnlyList<LaunchStageState> States => _states;

    public double TotalWeight { get; }

    public LaunchStage? ActiveStage =>
        _activeIndex >= 0 ? _plan[_activeIndex].Stage : null;

    /// <summary>当前阶段上报的文案（如「正在解析版本…」）。</summary>
    public string ActiveMessage { get; private set; } = "";

    /// <summary>下载阶段里最近一次上报的文件名；其它阶段保留上一条，便于界面单独展示。</summary>
    public string CurrentFile { get; private set; } = "";

    public int CompletedFiles { get; private set; }

    public int TotalFiles { get; private set; }

    public long DownloadedBytes { get; private set; }

    public long TotalBytes { get; private set; }

    /// <summary>按阶段权重合成的总体进度，0~1；只增不减，成功时固定为 1。</summary>
    public double OverallRatio => _overallRatio;

    public LaunchOutcome Outcome { get; private set; } = LaunchOutcome.Running;

    public string? ErrorMessage { get; private set; }

    /// <summary>处理一条阶段变化。</summary>
    public void Report(LaunchStageChanged change)
    {
        if (Outcome != LaunchOutcome.Running)
        {
            return;
        }

        var index = IndexOf(change.Stage);
        if (index < 0 || index < _activeIndex)
        {
            // 计划外的阶段（理论上不会发生）或倒序回调：保持当前步骤不动
            return;
        }

        if (index > _activeIndex)
        {
            // 没有 Done 事件，走到下一步就说明之前每一步都完成了
            for (var i = 0; i < index; i++)
            {
                if (_states[i] is LaunchStageState.Pending or LaunchStageState.Active)
                {
                    _states[i] = LaunchStageState.Done;
                }
            }

            _activeIndex = index;
            _states[index] = LaunchStageState.Active;
            _ratios[index] = 0;
        }

        ActiveMessage = change.Message;

        if (change.Ratio > 0)
        {
            _ratios[index] = Math.Clamp(change.Ratio, 0, 1);
        }

        // 下载进度早于阶段事件到达时先存着，等下载阶段真正激活再补上
        if (change.Stage == LaunchStage.Downloading && _pendingDownload is { } pending)
        {
            ApplyDownload(pending);
            _pendingDownload = null;
        }

        Recalculate();
    }

    /// <summary>处理一条下载进度。</summary>
    public void Report(DownloadProgress progress)
    {
        if (Outcome != LaunchOutcome.Running)
        {
            return;
        }

        if (_downloadIndex < 0)
        {
            return;
        }

        if (_activeIndex < _downloadIndex)
        {
            // 还没走到下载阶段：缓存最新一份，阶段激活时再应用
            _pendingDownload = progress;
            return;
        }

        if (_activeIndex > _downloadIndex)
        {
            // 已经进入启动阶段，晚到的下载进度不再有意义
            return;
        }

        ApplyDownload(progress);
        Recalculate();
    }

    /// <summary>启动成功：所有阶段完成，总体进度到 100%。</summary>
    public void Complete()
    {
        if (Outcome != LaunchOutcome.Running)
        {
            return;
        }

        for (var i = 0; i < _states.Length; i++)
        {
            if (_states[i] is LaunchStageState.Pending or LaunchStageState.Active)
            {
                _states[i] = LaunchStageState.Done;
            }
        }

        _activeIndex = -1;
        _pendingDownload = null;
        Outcome = LaunchOutcome.Succeeded;
        _overallRatio = 1;
    }

    /// <summary>启动失败：当前阶段标红，保留错误消息。</summary>
    public void Fail(string message)
    {
        if (Outcome != LaunchOutcome.Running)
        {
            return;
        }

        // 异常可能发生在第一条阶段回调被投递之前，此时至少标出第一步
        var index = _activeIndex >= 0 ? _activeIndex : _states.Length > 0 ? 0 : -1;
        if (index >= 0)
        {
            _states[index] = LaunchStageState.Failed;
        }

        Outcome = LaunchOutcome.Failed;
        ErrorMessage = message;
    }

    /// <summary>用户取消：当前阶段用中性状态表示，不当成失败。</summary>
    public void Cancel()
    {
        if (Outcome != LaunchOutcome.Running)
        {
            return;
        }

        if (_activeIndex >= 0)
        {
            _states[_activeIndex] = LaunchStageState.Canceled;
        }

        Outcome = LaunchOutcome.Canceled;
    }

    private static List<LaunchStagePlan> BuildPlan(GameInstance instance)
    {
        var plan = new List<LaunchStagePlan>
        {
            new(LaunchStage.Authenticating, "验证账户", 5),
            new(LaunchStage.DetectingJava, "检测 Java", 10),
        };

        // 只有尚未安装加载器的模组实例，首次启动才会走安装步骤
        if (instance.Loader != ModLoader.Vanilla && !instance.LoaderInstalled)
        {
            plan.Add(new LaunchStagePlan(LaunchStage.InstallingLoader, "安装加载器", 20));
        }

        plan.Add(new LaunchStagePlan(LaunchStage.ResolvingVersion, "解析版本", 10));
        plan.Add(new LaunchStagePlan(LaunchStage.Downloading, "下载文件", 50));
        plan.Add(new LaunchStagePlan(LaunchStage.Starting, "启动游戏", 5));

        return plan;
    }

    private void ApplyDownload(DownloadProgress progress)
    {
        CompletedFiles = progress.CompletedFiles;
        TotalFiles = progress.TotalFiles;
        DownloadedBytes = progress.DownloadedBytes;
        TotalBytes = progress.TotalBytes;

        if (!string.IsNullOrEmpty(progress.CurrentFile))
        {
            CurrentFile = progress.CurrentFile;
        }

        // 字节级进度比文件数更细，有总字节数时优先用它
        var ratio = progress.TotalBytes > 0
            ? progress.Ratio
            : progress.TotalFiles > 0
                ? (double)progress.CompletedFiles / progress.TotalFiles
                : 0;

        if (_downloadIndex >= 0)
        {
            _ratios[_downloadIndex] = Math.Clamp(ratio, 0, 1);
        }
    }

    private void Recalculate()
    {
        if (TotalWeight <= 0)
        {
            return;
        }

        var completed = 0.0;
        for (var i = 0; i < _plan.Count; i++)
        {
            if (_states[i] == LaunchStageState.Done)
            {
                completed += _plan[i].Weight;
            }
        }

        if (_activeIndex >= 0 && _states[_activeIndex] == LaunchStageState.Active)
        {
            completed += _plan[_activeIndex].Weight * _ratios[_activeIndex];
        }

        // 加载器的子步骤会各自从 0 开始（如 processor 的 i/N），
        // 直接覆盖会让进度条倒退；总体进度只允许单调上升。
        _overallRatio = Math.Max(_overallRatio, Math.Clamp(completed / TotalWeight, 0, 1));
    }

    private int IndexOf(LaunchStage stage)
    {
        for (var i = 0; i < _plan.Count; i++)
        {
            if (_plan[i].Stage == stage)
            {
                return i;
            }
        }

        return -1;
    }
}
