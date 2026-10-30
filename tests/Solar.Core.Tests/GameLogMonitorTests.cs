using Solar.Core.Launch;
using Xunit;

namespace Solar.Core.Tests;

public class GameLogMonitorTests
{
    private static string CreateLog(params string[] lines)
    {
        var path = Path.Combine(Path.GetTempPath(), "solar-log-" + Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllLines(path, lines);
        return path;
    }

    private static GameLogMonitor Poll(string path)
    {
        var monitor = new GameLogMonitor(path);
        monitor.Poll();
        return monitor;
    }

    [Fact]
    public void RecognizesLoadingMilestones()
    {
        var path = CreateLog(
            "[00:00:01] [Render thread/INFO]: Setting user: Steve",
            "[00:00:02] [Render thread/INFO]: Backend library: LWJGL version 3.3.3",
            "[00:00:03] [Sound Library Loader/INFO]: Starting up SoundSystem...",
            "[00:00:04] [Render thread/INFO]: Created: 1024x512x4 minecraft:textures/atlas/blocks.png-atlas");

        Assert.Equal(5, Poll(path).Milestones);
        File.Delete(path);
    }

    [Fact]
    public void DetectsCrashReportLine()
    {
        var path = CreateLog(
            "[00:00:01] [Render thread/INFO]: Setting user: Steve",
            "[00:00:02] [Render thread/ERROR]: This crash report has been saved to: C:\\crash.txt");

        var monitor = Poll(path);

        Assert.True(monitor.CrashDetected);
        Assert.Contains("crash report", monitor.CrashLine!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Setting user", monitor.Tail);
        File.Delete(path);
    }

    [Fact]
    public void IgnoresKeywordsTypedInChat()
    {
        var path = CreateLog(
            "[00:00:05] [Render thread/INFO]: [CHAT] <Steve> Crash report saved to somewhere");

        var monitor = Poll(path);

        Assert.False(monitor.CrashDetected);
        File.Delete(path);
    }

    [Fact]
    public void MissesNoLinesWhenAppendedIncrementally()
    {
        var path = CreateLog("[00:00:01] [Render thread/INFO]: Setting user: Steve");

        var monitor = new GameLogMonitor(path);
        monitor.Poll();
        Assert.Equal(2, monitor.Milestones);

        // 追加跨两次读取的内容（含半行），模拟日志正在写入
        File.AppendAllText(path, "[00:00:02] [Render thread/INFO]: Backend library: LWJGL version 3.3.3\n");
        monitor.Poll();

        Assert.Equal(3, monitor.Milestones);
        File.Delete(path);
    }

    [Fact]
    public void MissingFileDoesNotThrow()
    {
        var monitor = new GameLogMonitor(Path.Combine(Path.GetTempPath(), "solar-missing-" + Guid.NewGuid().ToString("N")));
        monitor.Poll();

        Assert.Equal(0, monitor.Milestones);
        Assert.False(monitor.CrashDetected);
    }
}
