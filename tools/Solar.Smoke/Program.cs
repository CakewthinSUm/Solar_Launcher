using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Solar.Core;
using Solar.Core.Instances;
using Solar.Core.Java;
using Solar.Core.Launch;
using Solar.Core.Minecraft;

// 端到端冒烟测试：真实下载一个版本并启动，用于验证下载器与启动器接线正确。
// 用法: dotnet run --project tools/Solar.Smoke -- <版本号> <根目录> [--prepare-only]

var versionId = args.Length > 0 ? args[0] : "1.21.4";
var root = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "solar-smoke");
var prepareOnly = args.Contains("--prepare-only");

Console.WriteLine($"版本: {versionId}");
Console.WriteLine($"根目录: {root}");
Console.WriteLine($"模式: {(prepareOnly ? "仅下载" : "下载并启动")}");

var services = new ServiceCollection();
services.AddLogging(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Information));
services.AddSolarCore(root);

using var provider = services.BuildServiceProvider();

var repository = provider.GetRequiredService<IInstanceRepository>();
var launcher = provider.GetRequiredService<IGameLaunchService>();
var javaDetector = provider.GetRequiredService<IJavaDetector>();

var runtimes = await javaDetector.DetectAsync();
Console.WriteLine($"检测到 Java: {(runtimes.Count == 0 ? "无" : string.Join(", ", runtimes))}");
if (runtimes.Count == 0)
{
    Console.Error.WriteLine("没有 Java，无法继续。");
    return 1;
}

// 固定实例 ID，重复运行会复用同一个实例目录（natives、日志、存档都在里面）
var instance = await repository.GetAsync("smoke") ?? new GameInstance
{
    Id = "smoke",
    Name = "冒烟测试",
    MinecraftVersion = versionId,
};

instance.MinecraftVersion = versionId;
// 冒烟测试用独立游戏目录，不污染真实 .minecraft。
// 新布局下隔离开启会把游戏目录指到版本文件夹（SolarShared/versions），
// 这里用覆盖路径钉在实例目录里，保持“测试数据只留在实例目录”的原意。
instance.VersionIsolation = true;
instance.GameDirectoryOverride = provider.GetRequiredService<Solar.Core.Paths.AppPaths>()
    .GetInstanceMinecraftDirectory("smoke");
await repository.SaveAsync(instance);

var lastReport = DateTimeOffset.MinValue;

var downloadProgress = new Progress<Solar.Core.Download.DownloadProgress>(p =>
{
    if (DateTimeOffset.Now - lastReport < TimeSpan.FromSeconds(1))
    {
        return;
    }

    lastReport = DateTimeOffset.Now;
    Console.WriteLine(
        p.TotalBytes > 0
            ? $"  下载 {p.DownloadedBytes * 100 / Math.Max(1, p.TotalBytes)}%  ({p.DownloadedBytes}/{p.TotalBytes} 字节)"
            : $"  文件 {p.CompletedFiles}/{p.TotalFiles}  {p.CurrentFile}");
});

var stageProgress = new Progress<LaunchStageChanged>(s =>
    Console.WriteLine($"[{s.Stage}] {s.Message}"));

try
{
    if (prepareOnly)
    {
        await launcher.PrepareAsync(instance, downloadProgress, stageProgress);
        Console.WriteLine("下载完成。");
        return 0;
    }

    var process = await launcher.LaunchAsync(instance, downloadProgress, stageProgress);
    Console.WriteLine($"游戏已启动，pid={process.Id}，等待退出…");

    // 等 60 秒，确认没有立刻崩溃
    var exited = await Task.WhenAny(process.WaitForExitAsync(), Task.Delay(TimeSpan.FromSeconds(60)));
    if (exited is Task t && t.IsCompleted && process.HasExited)
    {
        Console.WriteLine($"游戏在 60 秒内退出，退出码 {process.ExitCode}（预期应为 0 或非 0 但有正常日志）");
        return process.ExitCode;
    }

    Console.WriteLine("游戏运行超过 60 秒，视为启动成功。");
    process.Kill(entireProcessTree: true);
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"失败: {ex}");
    return 1;
}
