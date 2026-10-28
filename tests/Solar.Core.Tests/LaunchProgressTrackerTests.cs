using Solar.Core.Download;
using Solar.Core.Instances;
using Solar.Core.Launch;
using Xunit;

namespace Solar.Core.Tests;

public class LaunchProgressTrackerTests
{
    private static GameInstance Vanilla() => new() { Loader = ModLoader.Vanilla };

    private static GameInstance Modded(bool loaderInstalled) => new()
    {
        Loader = ModLoader.Fabric,
        LoaderVersion = "0.16.9",
        LoaderInstalled = loaderInstalled,
    };

    private static int IndexOf(LaunchProgressTracker tracker, LaunchStage stage) =>
        tracker.Plan.ToList().FindIndex(item => item.Stage == stage);

    private static void Report(
        LaunchProgressTracker tracker,
        LaunchStage stage,
        string message = "进行中",
        double ratio = 0) =>
        tracker.Report(new LaunchStageChanged(stage, message, ratio));

    [Fact]
    public void VanillaPlanHasNoLoaderStage()
    {
        var tracker = new LaunchProgressTracker(Vanilla());

        Assert.DoesNotContain(tracker.Plan, item => item.Stage == LaunchStage.InstallingLoader);
        Assert.Equal(
            new[]
            {
                LaunchStage.Authenticating,
                LaunchStage.DetectingJava,
                LaunchStage.ResolvingVersion,
                LaunchStage.Downloading,
                LaunchStage.Starting,
            },
            tracker.Plan.Select(item => item.Stage));
    }

    [Fact]
    public void ModdedFirstLaunchIncludesLoaderStage()
    {
        var tracker = new LaunchProgressTracker(Modded(loaderInstalled: false));

        Assert.Contains(tracker.Plan, item => item.Stage == LaunchStage.InstallingLoader);
        Assert.Equal(100d, tracker.TotalWeight);
    }

    [Fact]
    public void InstalledModdedInstanceSkipsLoaderStage()
    {
        var tracker = new LaunchProgressTracker(Modded(loaderInstalled: true));

        Assert.DoesNotContain(tracker.Plan, item => item.Stage == LaunchStage.InstallingLoader);
        Assert.Equal(80d, tracker.TotalWeight);
    }

    [Fact]
    public void AdvancingMarksPreviousStagesDone()
    {
        var tracker = new LaunchProgressTracker(Vanilla());

        Report(tracker, LaunchStage.Authenticating);
        Report(tracker, LaunchStage.DetectingJava);

        Assert.Equal(LaunchStageState.Done, tracker.States[0]);
        Assert.Equal(LaunchStageState.Active, tracker.States[1]);
        Assert.Equal(LaunchStageState.Pending, tracker.States[2]);
        Assert.Equal(LaunchStage.DetectingJava, tracker.ActiveStage);
    }

    [Fact]
    public void StageRegressionDoesNotMoveActiveBack()
    {
        var tracker = new LaunchProgressTracker(Vanilla());

        Report(tracker, LaunchStage.Authenticating);
        Report(tracker, LaunchStage.DetectingJava);
        Report(tracker, LaunchStage.ResolvingVersion);
        Report(tracker, LaunchStage.DetectingJava, "迟到的旧阶段");

        Assert.Equal(LaunchStage.ResolvingVersion, tracker.ActiveStage);
        Assert.Equal(LaunchStageState.Done, tracker.States[IndexOf(tracker, LaunchStage.DetectingJava)]);
        Assert.Equal(LaunchStageState.Active, tracker.States[IndexOf(tracker, LaunchStage.ResolvingVersion)]);
    }

    [Fact]
    public void RepeatedLoaderStageStaysActiveAndUpdatesRatio()
    {
        var tracker = new LaunchProgressTracker(Modded(loaderInstalled: false));
        var loader = IndexOf(tracker, LaunchStage.InstallingLoader);

        Report(tracker, LaunchStage.Authenticating);
        Report(tracker, LaunchStage.DetectingJava);
        Report(tracker, LaunchStage.InstallingLoader, "正在下载加载器库");
        var before = tracker.OverallRatio;

        Report(tracker, LaunchStage.InstallingLoader, "正在执行安装步骤 (1/3)", 0.5);

        Assert.Equal(LaunchStage.InstallingLoader, tracker.ActiveStage);
        Assert.Equal(LaunchStageState.Active, tracker.States[loader]);
        Assert.True(tracker.OverallRatio > before);
    }

    [Fact]
    public void DownloadBeforeDownloadingStageIsBuffered()
    {
        var tracker = new LaunchProgressTracker(Vanilla());

        // 下载回调先于阶段事件到达
        tracker.Report(new DownloadProgress(2, 4, 50, 100) { CurrentFile = "client.jar" });

        Report(tracker, LaunchStage.Authenticating);
        Report(tracker, LaunchStage.DetectingJava);
        Report(tracker, LaunchStage.ResolvingVersion);
        Report(tracker, LaunchStage.Downloading);

        Assert.Equal(2, tracker.CompletedFiles);
        Assert.Equal(4, tracker.TotalFiles);
        Assert.Equal(50, tracker.DownloadedBytes);
        Assert.Equal("client.jar", tracker.CurrentFile);
        Assert.True(tracker.OverallRatio > 0.5);
    }

    [Fact]
    public void DownloadAfterStartingIsIgnored()
    {
        var tracker = new LaunchProgressTracker(Vanilla());

        Report(tracker, LaunchStage.Authenticating);
        Report(tracker, LaunchStage.DetectingJava);
        Report(tracker, LaunchStage.ResolvingVersion);
        Report(tracker, LaunchStage.Downloading);
        tracker.Report(new DownloadProgress(1, 2, 10, 20) { CurrentFile = "a.jar" });

        Report(tracker, LaunchStage.Starting);
        tracker.Report(new DownloadProgress(99, 100, 999, 1000) { CurrentFile = "late.jar" });

        Assert.Equal(1, tracker.CompletedFiles);
        Assert.Equal("a.jar", tracker.CurrentFile);
    }

    [Fact]
    public void DownloadUsesBytesWhenAvailable()
    {
        var tracker = new LaunchProgressTracker(Vanilla());
        var download = IndexOf(tracker, LaunchStage.Downloading);

        Report(tracker, LaunchStage.Authenticating);
        Report(tracker, LaunchStage.DetectingJava);
        Report(tracker, LaunchStage.ResolvingVersion);
        Report(tracker, LaunchStage.Downloading);
        tracker.Report(new DownloadProgress(1, 100, 25, 100));

        // 25/100 字节优先于 1/100 文件数
        Assert.Equal(
            (5 + 10 + 10 + 50 * 0.25) / 80,
            tracker.OverallRatio,
            precision: 6);
    }

    [Fact]
    public void OverallRatioIsMonotonic()
    {
        var tracker = new LaunchProgressTracker(Vanilla());

        Report(tracker, LaunchStage.Authenticating);
        Report(tracker, LaunchStage.DetectingJava);
        Report(tracker, LaunchStage.ResolvingVersion);
        Report(tracker, LaunchStage.Downloading);
        tracker.Report(new DownloadProgress(0, 0, 80, 100));
        var high = tracker.OverallRatio;

        // 加载器/下载源的子步骤可能重新从 0 开始，总体进度不能倒退
        tracker.Report(new DownloadProgress(0, 0, 10, 100));

        Assert.Equal(high, tracker.OverallRatio);
    }

    [Fact]
    public void CompleteMarksAllStagesDone()
    {
        var tracker = new LaunchProgressTracker(Modded(loaderInstalled: false));

        Report(tracker, LaunchStage.Authenticating);
        Report(tracker, LaunchStage.DetectingJava);
        Report(tracker, LaunchStage.InstallingLoader);
        Report(tracker, LaunchStage.ResolvingVersion);
        Report(tracker, LaunchStage.Downloading);

        tracker.Complete();

        Assert.All(tracker.States, state => Assert.Equal(LaunchStageState.Done, state));
        Assert.Equal(LaunchOutcome.Succeeded, tracker.Outcome);
        Assert.Equal(1, tracker.OverallRatio);
        Assert.Null(tracker.ActiveStage);
    }

    [Fact]
    public void FailMarksActiveStageFailed()
    {
        var tracker = new LaunchProgressTracker(Vanilla());

        Report(tracker, LaunchStage.Authenticating);
        Report(tracker, LaunchStage.DetectingJava);
        tracker.Fail("找不到 Java");

        Assert.Equal(LaunchOutcome.Failed, tracker.Outcome);
        Assert.Equal("找不到 Java", tracker.ErrorMessage);
        Assert.Equal(LaunchStageState.Done, tracker.States[0]);
        Assert.Equal(LaunchStageState.Failed, tracker.States[1]);
        Assert.Equal(LaunchStageState.Pending, tracker.States[2]);
    }

    [Fact]
    public void FailBeforeAnyStageMarksFirstStage()
    {
        var tracker = new LaunchProgressTracker(Vanilla());

        tracker.Fail("登录已过期");

        Assert.Equal(LaunchStageState.Failed, tracker.States[0]);
        Assert.Equal(LaunchOutcome.Failed, tracker.Outcome);
    }

    [Fact]
    public void CancelMarksActiveStageCanceled()
    {
        var tracker = new LaunchProgressTracker(Vanilla());
        var download = IndexOf(tracker, LaunchStage.Downloading);

        Report(tracker, LaunchStage.Authenticating);
        Report(tracker, LaunchStage.DetectingJava);
        Report(tracker, LaunchStage.ResolvingVersion);
        Report(tracker, LaunchStage.Downloading);
        tracker.Cancel();

        Assert.Equal(LaunchOutcome.Canceled, tracker.Outcome);
        Assert.Equal(LaunchStageState.Canceled, tracker.States[download]);
    }

    [Fact]
    public void LateEventsAfterTerminalAreIgnored()
    {
        var tracker = new LaunchProgressTracker(Vanilla());

        Report(tracker, LaunchStage.Authenticating);
        tracker.Complete();

        Report(tracker, LaunchStage.Starting);
        tracker.Report(new DownloadProgress(10, 10, 100, 100));
        tracker.Fail("迟到");
        tracker.Cancel();

        Assert.Equal(LaunchOutcome.Succeeded, tracker.Outcome);
        Assert.All(tracker.States, state => Assert.Equal(LaunchStageState.Done, state));
        Assert.Equal(1, tracker.OverallRatio);
        Assert.Null(tracker.ErrorMessage);
    }
}
