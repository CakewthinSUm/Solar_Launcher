using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Solar.Core.Download;
using Solar.Core.Java;
using Solar.Core.Loaders;
using Solar.Core.Minecraft;
using Solar.Core.Mods;
using Solar.Core.Paths;
using Solar.Core.Versions;

namespace Solar.Core.Modpacks;

/// <summary>
/// 整合包的解析与安装。
/// <para>
/// 安装策略与 PCL 一致：先在临时目录下载整合包本体，读出 manifest，
/// 再为它创建一个**版本文件夹**（加载器也在这里装好），把所有文件下进去。
/// 整合包版本始终隔离——它的游戏目录就是自己的版本文件夹。
/// </para>
/// </summary>
public sealed class ModpackService(
    ModHttp http,
    IModFileDownloader downloader,
    IVersionRepository repository,
    MinecraftPaths paths,
    IVersionResolver resolver,
    IGameDownloader gameDownloader,
    ILoaderInstaller loaderInstaller,
    IJavaDetector javaDetector,
    ILogger<ModpackService> logger) : IModpackService
{
    /// <summary>整合包临时目录的根。清理时只删这个根下面的内容。</summary>
    private static readonly string TempRoot = Path.Combine(
        Path.GetTempPath(), "SolarLauncher", "modpacks");

    /// <summary>并行下载的文件数。整合包动辄上百个文件，4 路已经能跑满常见带宽。</summary>
    private const int DownloadConcurrency = 4;

    private static readonly Uri CurseForgeApiBase = new("https://api.curseforge.com/v1/");

    private static readonly System.Text.Json.JsonSerializerOptions CurseForgeJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<ModpackPlan> CreatePlanAsync(
        ModVersion version,
        IProgress<ModpackInstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        var directory = Path.Combine(TempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var archive = Path.Combine(directory, SafeFileName(version.File.FileName));

        try
        {
            progress?.Report(new ModpackInstallProgress(
                ModpackInstallStage.Preparing, $"正在下载整合包 {version.File.FileName}…"));

            await downloader.DownloadAsync(version.File, archive, ct).ConfigureAwait(false);

            progress?.Report(new ModpackInstallProgress(
                ModpackInstallStage.Preparing, "正在解析整合包…"));

            using var zip = ZipFile.OpenRead(archive);
            return await ReadPlanAsync(zip, archive, version, ct).ConfigureAwait(false);
        }
        catch
        {
            TryDeleteDirectory(directory);
            throw;
        }
    }

    private async Task<ModpackPlan> ReadPlanAsync(
        ZipArchive zip,
        string archive,
        ModVersion version,
        CancellationToken ct)
    {
        if (zip.GetEntry("modrinth.index.json") is { } indexEntry)
        {
            var data = ModpackManifestReader.ReadModrinthIndex(await ReadEntryTextAsync(indexEntry, ct)
                .ConfigureAwait(false));

            if (string.IsNullOrWhiteSpace(data.MinecraftVersion))
            {
                throw new InvalidOperationException("整合包没有声明 Minecraft 版本。");
            }

            if (data.Loader != ModLoader.Vanilla && string.IsNullOrWhiteSpace(data.LoaderVersion))
            {
                throw new InvalidOperationException(
                    $"整合包声明了 {data.Loader}，但没有给出加载器版本，无法安装。");
            }

            return new ModpackPlan
            {
                ArchivePath = archive,
                Name = string.IsNullOrWhiteSpace(data.Name) ? version.Name : data.Name,
                MinecraftVersion = data.MinecraftVersion,
                Loader = data.Loader,
                LoaderVersion = data.LoaderVersion,
                Version = data.VersionId,
                Summary = data.Summary,
                Format = ModpackFormat.Modrinth,
                Files = data.Files,
                FailedFiles = data.FailedFiles,
            };
        }

        if (zip.GetEntry("manifest.json") is { } manifestEntry)
        {
            var data = ModpackManifestReader.ReadCurseForgeManifest(await ReadEntryTextAsync(manifestEntry, ct)
                .ConfigureAwait(false));

            if (data.Loader != ModLoader.Vanilla && string.IsNullOrWhiteSpace(data.LoaderVersion))
            {
                throw new InvalidOperationException(
                    $"整合包声明了 {data.Loader}，但没有给出加载器版本，无法安装。");
            }

            var (files, failed) = await ResolveCurseForgeFilesAsync(data.Files, ct).ConfigureAwait(false);

            return new ModpackPlan
            {
                ArchivePath = archive,
                Name = data.Name,
                MinecraftVersion = data.MinecraftVersion,
                Loader = data.Loader,
                LoaderVersion = data.LoaderVersion,
                Version = data.Version,
                Format = ModpackFormat.CurseForge,
                OverridesFolder = data.OverridesFolder,
                Files = files,
                FailedFiles = failed,
            };
        }

        throw new InvalidOperationException(
            "这不是可识别的整合包：压缩包里既没有 modrinth.index.json，也没有 manifest.json。");
    }

    /// <summary>
    /// CurseForge 的 manifest 只给 projectID/fileID，文件名与下载地址要逐个查。
    /// 文件被作者关闭分发时下载地址为空，记进失败清单而不是让整包安装失败。
    /// </summary>
    private async Task<(IReadOnlyList<ModpackFile> Files, IReadOnlyList<string> Failed)> ResolveCurseForgeFilesAsync(
        IReadOnlyList<CurseForgeManifestEntry> entries,
        CancellationToken ct)
    {
        var resolved = new ModpackFile?[entries.Count];

        await Parallel.ForEachAsync(
            Enumerable.Range(0, entries.Count),
            new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct },
            async (index, token) =>
            {
                var entry = entries[index];

                try
                {
                    resolved[index] = await ResolveCurseForgeFileAsync(entry, token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "解析 CurseForge 文件 {ProjectId}/{FileId} 失败", entry.ProjectId, entry.FileId);
                }
            }).ConfigureAwait(false);

        var files = new List<ModpackFile>(entries.Count);
        var failed = new List<string>();

        for (var i = 0; i < entries.Count; i++)
        {
            if (resolved[i] is { } file)
            {
                files.Add(file);
            }
            else
            {
                // 没有文件名，只能用 id 标识；用户至少能从日志/提示里定位
                failed.Add($"curseforge:{entries[i].ProjectId}/{entries[i].FileId}");
            }
        }

        return (files, failed);
    }

    private async Task<ModpackFile?> ResolveCurseForgeFileAsync(
        CurseForgeManifestEntry entry,
        CancellationToken ct)
    {
        var url = new Uri(CurseForgeApiBase, $"mods/{entry.ProjectId}/files/{entry.FileId}");
        var response = await http
            .GetCurseForgeJsonAsync<CurseForgeFileResponse>(url, CurseForgeJson, ct)
            .ConfigureAwait(false);

        var file = response.Data;
        if (file?.DownloadUrl is null)
        {
            return null;
        }

        // 文件名来自服务端，不能让它带路径分隔符拼到 mods/ 之外
        var name = Path.GetFileName(file.FileName);
        if (string.IsNullOrWhiteSpace(name) || !string.Equals(name, file.FileName, StringComparison.Ordinal))
        {
            return null;
        }

        var sha1 = file.Hashes?.FirstOrDefault(h => h.Algo == 1)?.Value;   // Algo 1 = SHA1
        return new ModpackFile($"mods/{name}", file.DownloadUrl, sha1, file.FileLength);
    }

    public async Task<ModpackInstallResult> InstallAsync(
        ModpackPlan plan,
        string? versionName = null,
        IProgress<ModpackInstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(plan.MinecraftVersion))
        {
            throw new InvalidOperationException("整合包没有声明 Minecraft 版本。");
        }

        // 版本文件夹名就是版本名；与现有版本重名时自动加序号
        var versionId = await UniqueVersionIdAsync(versionName ?? plan.Name, ct).ConfigureAwait(false);
        var root = paths.GetVersionDirectory(versionId);
        Directory.CreateDirectory(root);

        var version = new GameVersion
        {
            Id = versionId,
            MinecraftVersion = plan.MinecraftVersion,
            Loader = plan.Loader,
            LoaderVersion = plan.Loader == ModLoader.Vanilla ? null : plan.LoaderVersion,

            // 整合包的游戏目录就是自己的版本文件夹
            VersionIsolation = true,
            LoaderInstalled = true,
        };
        version.Name = versionId;

        // 先把游戏本体与加载器装进这个文件夹：装完版本 JSON 才存在，
        // 版本立刻能被扫描到，不必等首次启动补装。
        await InstallGameAsync(plan, versionId, progress, ct).ConfigureAwait(false);

        // 落盘记录：下载到一半取消时，已经下好的文件仍然属于一个可识别的版本
        await repository.SaveSettingsAsync(version, ct).ConfigureAwait(false);

        var (installed, skipped, failures) = await DownloadFilesAsync(plan, root, progress, ct)
            .ConfigureAwait(false);

        progress?.Report(new ModpackInstallProgress(
            ModpackInstallStage.ExtractingOverrides, "正在解压配置与覆盖文件…"));

        var overrides = await Task.Run(() => ExtractOverrides(plan, root, ct), ct).ConfigureAwait(false);

        progress?.Report(new ModpackInstallProgress(ModpackInstallStage.Saving, "正在保存版本…"));
        await repository.SaveSettingsAsync(version, ct).ConfigureAwait(false);

        var allFailures = plan.FailedFiles
            .Concat(failures)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        logger.LogInformation(
            "整合包 {Name} 安装完成：{Installed} 个文件，{Skipped} 个跳过，{Overrides} 个覆盖文件，{Failed} 个失败",
            plan.Name, installed, skipped, overrides, allFailures.Count);

        return new ModpackInstallResult(version, installed, skipped, overrides, allFailures);
    }

    /// <summary>把整合包声明的游戏本体与加载器装进版本文件夹。</summary>
    private async Task InstallGameAsync(
        ModpackPlan plan,
        string versionId,
        IProgress<ModpackInstallProgress>? progress,
        CancellationToken ct)
    {
        progress?.Report(new ModpackInstallProgress(
            ModpackInstallStage.InstallingLoader, "正在准备游戏本体与加载器…"));

        if (plan.Loader != ModLoader.Vanilla)
        {
            var runtimes = await javaDetector.DetectAsync(ct).ConfigureAwait(false);
            if (runtimes.Count == 0)
            {
                throw new InvalidOperationException(
                    "没有找到可用的 Java。请先安装 Java，或在设置中指定 java.exe 路径。");
            }

            progress?.Report(new ModpackInstallProgress(
                ModpackInstallStage.InstallingLoader,
                $"正在安装 {plan.Loader} {plan.LoaderVersion}…"));

            await loaderInstaller.InstallAsync(
                plan.Loader,
                plan.MinecraftVersion,
                plan.LoaderVersion
                    ?? throw new InvalidOperationException("整合包没有给出加载器版本，无法安装。"),
                runtimes[0].Path,
                null,
                ct,
                targetVersionId: versionId).ConfigureAwait(false);

            return;
        }

        // 原版整合包：写一份继承原版的薄 profile，再把原版文件补齐到该版本文件夹
        var profile = new { id = versionId, inheritsFrom = plan.MinecraftVersion, type = "release" };
        await File.WriteAllTextAsync(
            paths.GetVersionJsonPath(versionId),
            JsonSerializer.Serialize(profile),
            ct).ConfigureAwait(false);

        var resolved = await resolver.ResolveAsync(versionId, ct).ConfigureAwait(false);
        var scratch = new GameVersion { Id = versionId, MinecraftVersion = plan.MinecraftVersion };
        await gameDownloader.DownloadAsync(scratch, resolved, null, ct).ConfigureAwait(false);
    }

    private async Task<(int Installed, int Skipped, IReadOnlyList<string> Failed)> DownloadFilesAsync(
        ModpackPlan plan,
        string root,
        IProgress<ModpackInstallProgress>? progress,
        CancellationToken ct)
    {
        var files = new List<ModpackFile>();
        var failures = new ConcurrentBag<string>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in plan.Files)
        {
            // 同一个路径只下一次；重复条目并行下载会互相覆盖 .part
            if (!seenPaths.Add(file.RelativePath))
            {
                continue;
            }

            // 解析阶段已经过滤过；这里再挡一次，防止调用方直接构造出不安全的清单
            if (ModpackManifestReader.CombineUnderRoot(root, file.RelativePath) is null)
            {
                logger.LogWarning("整合包里有不安全的路径，已跳过: {Path}", file.RelativePath);
                failures.Add(file.RelativePath);
                continue;
            }

            files.Add(file);
        }

        var total = files.Count;
        var completed = 0;
        var installed = 0;
        var skipped = 0;

        await Parallel.ForEachAsync(
            files,
            new ParallelOptions { MaxDegreeOfParallelism = DownloadConcurrency, CancellationToken = ct },
            async (file, token) =>
            {
                var target = ModpackManifestReader.CombineUnderRoot(root, file.RelativePath);
                if (target is null)
                {
                    failures.Add(file.RelativePath);
                }
                else
                {
                    try
                    {
                        if (File.Exists(target))
                        {
                            Interlocked.Increment(ref skipped);
                        }
                        else
                        {
                            var download = new ModFile(
                                Path.GetFileName(file.RelativePath), file.Url, file.Sha1, file.Size);

                            await downloader.DownloadAsync(download, target, token).ConfigureAwait(false);
                            Interlocked.Increment(ref installed);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
                    {
                        logger.LogWarning(ex, "下载整合包文件失败: {Path}", file.RelativePath);
                        failures.Add(file.RelativePath);
                    }
                }

                var done = Interlocked.Increment(ref completed);
                progress?.Report(new ModpackInstallProgress(
                    ModpackInstallStage.DownloadingFiles,
                    $"正在下载 {done}/{total}：{file.RelativePath}",
                    done,
                    total,
                    file.RelativePath));
            }).ConfigureAwait(false);

        return (installed, skipped, failures.ToList());
    }

    /// <summary>
    /// 把 overrides 解压到版本目录。Modrinth 有 <c>overrides/</c> 与 <c>client-overrides/</c>，
    /// 后者后解压，同名文件以客户端专用版本为准。
    /// </summary>
    private int ExtractOverrides(ModpackPlan plan, string root, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(plan.ArchivePath);

        var prefixes = plan.Format == ModpackFormat.Modrinth
            ? new[] { "overrides/", "client-overrides/" }
            : new[] { (plan.OverridesFolder ?? "overrides").Trim('/') + "/" };

        var extracted = 0;

        foreach (var prefix in prefixes)
        {
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();

                // 目录条目以 / 结尾，Name 为空
                if (entry.Name.Length == 0
                    || !entry.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var relative = entry.FullName[prefix.Length..];
                var target = ModpackManifestReader.CombineUnderRoot(root, relative);
                if (target is null)
                {
                    logger.LogWarning("整合包 overrides 里有不安全的路径，已跳过: {Path}", entry.FullName);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
                extracted++;
            }
        }

        return extracted;
    }

    public void Cleanup(ModpackPlan plan)
    {
        var directory = Path.GetDirectoryName(plan.ArchivePath);
        if (directory is null)
        {
            return;
        }

        // 只删自己创建的临时目录，避免误删用户文件
        var fullRoot = Path.GetFullPath(TempRoot).TrimEnd(Path.DirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(directory);

        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        TryDeleteDirectory(directory);
    }

    private Task<string> UniqueVersionIdAsync(string name, CancellationToken ct)
    {
        var baseId = VersionScanner.BuildId(string.IsNullOrWhiteSpace(name) ? "整合包" : name.Trim());

        if (!Directory.Exists(paths.GetVersionDirectory(baseId)))
        {
            return Task.FromResult(baseId);
        }

        for (var index = 2; ; index++)
        {
            var candidate = $"{baseId} ({index})";
            if (!Directory.Exists(paths.GetVersionDirectory(candidate)))
            {
                return Task.FromResult(candidate);
            }
        }
    }

    private static async Task<string> ReadEntryTextAsync(ZipArchiveEntry entry, CancellationToken ct)
    {
        await using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }

    private static string SafeFileName(string name) =>
        string.IsNullOrWhiteSpace(name) ? "modpack.zip" : Path.GetFileName(name);

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 临时目录删不掉不影响安装结果
        }
    }

    /// <summary>CurseForge 单文件接口的响应外壳（<c>{ "data": { ... } }</c>）。</summary>
    private sealed class CurseForgeFileResponse
    {
        public CurseForgeDtos.FileDto? Data { get; set; }
    }
}
