using System.Collections.Concurrent;
using CmlLib.Core;
using CmlLib.Core.Files;
using CmlLib.Core.Installers;
using Microsoft.Extensions.Logging;
using Solar.Core.Assets;
using Solar.Core.Minecraft;
using Solar.Core.Models;
using Solar.Core.Paths;
using Solar.Core.Settings;
using Solar.Core.Versions;

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
        GameVersion instance,
        ResolvedVersion version,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default);
}

public sealed class GameDownloader(
    MinecraftPaths paths,
    ISettingsService settings,
    IAssetIndexService assetIndexService,
    GameFileLocator fileLocator,
    ILogger<GameDownloader> logger) : IGameDownloader
{
    public async Task DownloadAsync(
        GameVersion instance,
        ResolvedVersion version,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var context = new RuleContext();
        var nativesDirectory = paths.GetNativesDirectory(instance.Id);
        Directory.CreateDirectory(nativesDirectory);

        var builder = new GameFileBuilder(paths, fileLocator);
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

            // 与 PCL 一致：启动时资源只查存在 + 大小。资源按内容寻址，文件名就是哈希，
            // 大小对上基本可以认为完好；完整 SHA1 校验留给「修复」场景，不拖慢每次启动。
            // （BuildAssets 会顺手把其它启动器已有的同一份资源硬链接过来。）
            var assets = builder.BuildAssets(index).ToList();
            var pending = assets.Where(file => !IsAssetReady(file)).ToList();

            if (pending.Count < assets.Count)
            {
                logger.LogInformation(
                    "资源已就绪 {Ready}/{Total}，跳过哈希校验",
                    assets.Count - pending.Count,
                    assets.Count);
            }

            files.AddRange(pending);
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

        // natives 目录里残留的旧 dll 会被游戏加载到，和 PCL 一样在解压后清掉
        CleanupNatives(nativesDirectory, builder.ExtractedNatives);

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

    /// <summary>删掉 natives 目录里不属于当前版本的旧文件。</summary>
    private void CleanupNatives(string directory, ConcurrentDictionary<string, byte> expected)
    {
        if (expected.IsEmpty || !Directory.Exists(directory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(directory))
        {
            if (expected.ContainsKey(Path.GetFileName(file)))
            {
                continue;
            }

            try
            {
                File.Delete(file);
                logger.LogInformation("删除多余的 natives 文件: {File}", file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "删除多余的 natives 文件失败: {File}", file);
            }
        }
    }

    /// <summary>资源是否已经就绪：存在且大小一致。大小未知（0）时只要求存在。</summary>
    private static bool IsAssetReady(GameFile file)
    {
        if (string.IsNullOrEmpty(file.Path))
        {
            return false;
        }

        var info = new FileInfo(file.Path);
        return info.Exists && (file.Size <= 0 || info.Length == file.Size);
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
