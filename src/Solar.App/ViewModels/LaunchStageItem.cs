using CommunityToolkit.Mvvm.ComponentModel;
using Solar.Core.Launch;

namespace Solar.App.ViewModels;

/// <summary>
/// 步骤条里的一项。状态由 <see cref="LaunchProgressTracker"/> 同步过来，
/// 视图只根据这些布尔值切换图标，不在 ViewModel 里返回画刷。
/// </summary>
public sealed partial class LaunchStageItem : ObservableObject
{
    public LaunchStageItem(LaunchStagePlan plan, bool isLast)
    {
        Stage = plan.Stage;
        Title = plan.Title;
        IsLast = isLast;
    }

    public LaunchStage Stage { get; }

    public string Title { get; }

    public bool IsLast { get; }

    public bool HasConnector => !IsLast;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending))]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    [NotifyPropertyChangedFor(nameof(IsDone))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    [NotifyPropertyChangedFor(nameof(IsCanceled))]
    private LaunchStageState _state = LaunchStageState.Pending;

    public bool IsPending => State == LaunchStageState.Pending;

    public bool IsActive => State == LaunchStageState.Active;

    public bool IsDone => State == LaunchStageState.Done;

    public bool IsFailed => State == LaunchStageState.Failed;

    public bool IsCanceled => State == LaunchStageState.Canceled;
}
