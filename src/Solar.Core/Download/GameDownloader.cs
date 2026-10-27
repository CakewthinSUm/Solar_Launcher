using CmlLib.Core;
using CmlLib.Core.Files;
using CmlLib.Core.Installers;
using Microsoft.Extensions.Logging;
using Solar.Core.Assets;
using Solar.Core.Instances;
using Solar.Core.Minecraft;
using Solar.Core.Models;
using Solar.Core.Paths;
using Solar.Core.Settings;

namespace Solar.Core.Download;

/// <summary>下载进度：已完成文件数 / 总文件数 + 字节数。</summary>
public sealed record DownloadProgress(int CompletedFiles, int TotalFiles, long DownloadedBytes, long TotalBytes)
{
    public double Ratio => TotalBytes > 0 ? (double)DownloadedBytes / TotalBytes : 0;

    public string CurrentFile { get; init; } = "";
}

public interface IGameDownloader
{
    /// <summary>把版本运行所需的全部文件（本体、库、natives、资源、日志配置）下载到本地。</summary>
    Task DownloadAsync(
        GameInstance instance,
        ResolvedVersion version,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default);
}

public sealed class GameDownloader(
    AppPaths paths,
    ISettingsService settings,
    IAssetIndexService assetIndexService,
    ILogger<GameDownloader> logger) : IGameDownloader
{
    public async Task DownloadAsync(
        GameInstance instance,
        ResolvedVersion version,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var context = new RuleContext();
        var nativesDirectory = Path.Combine(paths.GetInstanceDirectory(instance.Id), "natives");
        Directory.CreateDirectory(nativesDirectory);

        var builder = new GameFileBuilder(paths);
        var files = new List<GameFile>();

        if (builder.BuildClientJar(version) is { } clientJar)
        {
            files.Add(clientJar);
        }

        if (builder.BuildLogConfig(version) is { } logConfig)
        {
            files.Add(logConfig);
        }

        files.AddRange(builder.BuildLibraries(version, context, nativesDirectory));

        // 资源索引本身要先拿到，才能知道有哪些资源对象
        AssetIndex? index = null;
        if (version.Detail.AssetIndex is { } assetIndexRef)
        {
            index = await assetIndexService.GetIndexAsync(assetIndexRef, ct).ConfigureAwait(false);
            files.AddRange(builder.BuildAssets(index));
        }
        else
        {
            logger.LogWarning("版本 {Version} 没有资源索引，跳过资源下载", version.Id);
        }

        logger.LogInformation("开始下载 {Version}：共 {Count} 个文件", version.Id, files.Count);

        var installer = new ParallelGameInstaller(
            maxChecker: 4,
            maxDownloader: Math.Clamp(settings.Current.DownloadThreads, 1, 32),
            boundedCapacity: 2048,
            MirrorRewritingHandler.CreateClient(
                MirrorRewritingHandler.ParseMirror(settings.Current.DownloadMirrorUrl)));

        // 文件级与字节级进度来自两条独立的流，各自只带一半信息。
        // 用一个快照合并后再上报，否则 UI 会在两种数值之间来回跳。
        var snapshot = new ProgressSnapshot();

        var fileProgress = progress is null
            ? null
            : new Progress<InstallerProgressChangedEventArgs>(e =>
            {
                lock (snapshot)
                {
                    snapshot.CompletedFiles = e.ProgressedTasks;
                    snapshot.TotalFiles = e.TotalTasks;
                    if (!string.IsNullOrEmpty(e.Name))
                    {
                        snapshot.CurrentFile = e.Name;
                    }

                    progress.Report(snapshot.ToProgress());
                }
            });

        var byteProgress = progress is null
            ? null
            : new Progress<ByteProgress>(b =>
            {
                lock (snapshot)
                {
                    snapshot.DownloadedBytes = b.ProgressedBytes;
                    snapshot.TotalBytes = b.TotalBytes;
                    progress.Report(snapshot.ToProgress());
                }
            });

        await installer.Install(files, fileProgress, byteProgress, ct).ConfigureAwait(false);

        // 1.7 以前的版本要求资源额外复制一份到 virtual/ 或 resources/
        if (index is not null)
        {
            assetIndexService.EnsureLegacyLayout(
                index,
                version.Detail.AssetIndex!.Id,
                index.IsVirtual,
                index.IsMapToResources);
        }

        logger.LogInformation("版本 {Version} 下载完成", version.Id);
    }

    /// <summary>合并两条进度流的可变快照。</summary>
    private sealed class ProgressSnapshot
    {
        public int CompletedFiles { get; set; }
        public int TotalFiles { get; set; }
        public long DownloadedBytes { get; set; }
        public long TotalBytes { get; set; }
        public string CurrentFile { get; set; } = "";

        public DownloadProgress ToProgress() =>
            new(CompletedFiles, TotalFiles, DownloadedBytes, TotalBytes) { CurrentFile = CurrentFile };
    }
}
