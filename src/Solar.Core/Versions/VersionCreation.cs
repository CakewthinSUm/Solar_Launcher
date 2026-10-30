using Microsoft.Extensions.Logging;
using Solar.Core.Addons;
using Solar.Core.Download;
using Solar.Core.Java;
using Solar.Core.Loaders;
using Solar.Core.Minecraft;
using Solar.Core.Mods;
using Solar.Core.Paths;

namespace Solar.Core.Versions;

/// <summary>创建版本的安装阶段，供页面显示进度文案。</summary>
public enum VersionCreateStage
{
    Preparing,
    InstallingLoader,
    InstallingAddons,
    DownloadingGame,
    InstallingMods,
    Saving,
}

/// <summary>创建进度。进度条用 <see cref="Ratio"/>，0 表示该阶段不报百分比。</summary>
public sealed record VersionCreateProgress(VersionCreateStage Stage, string Message, double Ratio = 0);

public sealed record VersionCreateRequest(
    string Name,
    string MinecraftVersion,
    ComponentSelection Components,
    bool VersionIsolation);

public interface IVersionCreationService
{
    /// <summary>
    /// 创建版本并把所选组件全部装好。
    /// <para>
    /// 版本在第一步就落盘：中途失败或取消时它仍留在列表里，
    /// 首次启动会由 <see cref="Launch.GameLaunchService"/> 把加载器补装完。
    /// </para>
    /// </summary>
    Task<GameVersion> CreateAsync(
        VersionCreateRequest request,
        IProgress<VersionCreateProgress>? progress = null,
        CancellationToken ct = default);
}

/// <summary>
/// 把「下载页选中的版本 + 组件」变成一个装好的版本文件夹。
/// <para>
/// 与 PCL 一致：版本文件夹名就是版本名。用户填了自定义名字时，
/// 先按规范 id 装好，再整体重命名（文件夹、JSON、jar 一起改）。
/// </para>
/// </summary>
public sealed class VersionCreationService(
    MinecraftPaths paths,
    IVersionRepository repository,
    IVersionResolver resolver,
    IGameDownloader downloader,
    ILoaderInstaller loaderInstaller,
    IOptiFineService optiFine,
    ILiteLoaderInstaller liteLoaderInstaller,
    IModInstaller modInstaller,
    IJavaDetector javaDetector,
    HttpClient http,
    ILogger<VersionCreationService> logger) : IVersionCreationService
{
    public async Task<GameVersion> CreateAsync(
        VersionCreateRequest request,
        IProgress<VersionCreateProgress>? progress = null,
        CancellationToken ct = default)
    {
        var components = request.Components;
        var minecraftVersion = request.MinecraftVersion;
        var customName = string.IsNullOrWhiteSpace(request.Name) ? null : VersionScanner.BuildId(request.Name.Trim());

        progress?.Report(new VersionCreateProgress(VersionCreateStage.Preparing, "正在创建版本…"));

        // 1. 装出规范 id 的版本文件夹（加载器 / 附加 profile）
        var (versionId, filesReady) = await InstallCoreAsync(
            minecraftVersion, components, customName, progress, ct).ConfigureAwait(false);

        // 2. 用户自定义了文件夹名：装完再整体重命名，避免给原版造继承 profile
        if (customName is not null && !string.Equals(versionId, customName, StringComparison.Ordinal))
        {
            if (Directory.Exists(paths.GetVersionDirectory(customName)))
            {
                throw new InvalidOperationException($"已存在同名版本 {customName}。");
            }

            await repository.RenameAsync(versionId, customName, ct).ConfigureAwait(false);
            versionId = customName;
        }

        // 3. 记录设置：先落盘，后续下载失败或取消时版本仍留在列表里
        var version = new GameVersion
        {
            Id = versionId,
            MinecraftVersion = minecraftVersion,
            Loader = components.Loader,
            LoaderVersion = components.Loader == ModLoader.Vanilla ? null : components.LoaderVersion,
            OptiFineVersion = components.OptiFine?.Version,
            LiteLoaderVersion = components.LiteLoader?.Version,
            VersionIsolation = request.VersionIsolation,
            LoaderInstalled = true,
        };

        if (customName is not null)
        {
            version.Name = customName;
        }

        await repository.SaveSettingsAsync(version, ct).ConfigureAwait(false);

        // 4. 补齐依赖（原版在重命名前已经下完时不必再来一遍）
        if (!filesReady)
        {
            progress?.Report(new VersionCreateProgress(VersionCreateStage.DownloadingGame, "正在检查并补齐文件…"));
            var resolved = await resolver.ResolveAsync(versionId, ct).ConfigureAwait(false);
            var scratch = Scratch(versionId, minecraftVersion);
            await downloader.DownloadAsync(scratch, resolved, WrapDownloadProgress(progress), ct)
                .ConfigureAwait(false);
        }

        // 5. 附加 Mod（Fabric API / OptiFabric / OptiFine-as-mod）放进实际游戏目录
        await InstallCompanionModsAsync(version, components, progress, ct).ConfigureAwait(false);

        progress?.Report(new VersionCreateProgress(VersionCreateStage.Saving, "正在保存…"));
        await repository.SaveSettingsAsync(version, ct).ConfigureAwait(false);

        logger.LogInformation(
            "版本 {Id} 创建完成：{Version} {Loader}",
            version.Id, version.MinecraftVersion, version.DisplayLoader);

        return version;
    }

    /// <summary>
    /// 装出规范 id 的版本文件夹，返回版本文件夹名以及依赖是否已经下好。
    /// </summary>
    private async Task<(string VersionId, bool FilesReady)> InstallCoreAsync(
        string minecraftVersion,
        ComponentSelection components,
        string? customName,
        IProgress<VersionCreateProgress>? progress,
        CancellationToken ct)
    {
        // 主加载器：安装器自己会先补齐原版
        if (components.Loader != ModLoader.Vanilla)
        {
            var javaPath = await SelectJavaAsync(null, ct).ConfigureAwait(false);

            var loaderId = await loaderInstaller.InstallAsync(
                components.Loader,
                minecraftVersion,
                components.LoaderVersion
                    ?? throw new InvalidOperationException("没有指定加载器版本。"),
                javaPath,
                WrapLoaderProgress(progress),
                ct,
                targetVersionId: customName).ConfigureAwait(false);

            return (loaderId, false);
        }

        // 自定义文件夹名要在下载之后重命名，OptiFine 的安装器也需要磁盘上的原版 jar：
        // 这两种情况先按规范 id 把原版下好。
        var needsVanillaFiles = customName is not null || components.OptiFine is not null;
        if (needsVanillaFiles)
        {
            progress?.Report(new VersionCreateProgress(VersionCreateStage.DownloadingGame, "正在下载原版文件…"));
            var vanilla = await resolver.ResolveAsync(minecraftVersion, ct).ConfigureAwait(false);
            await downloader
                .DownloadAsync(Scratch(minecraftVersion, minecraftVersion), vanilla,
                    WrapDownloadProgress(progress), ct)
                .ConfigureAwait(false);
        }

        // 附加 profile（OptiFine / LiteLoader），只在没有主加载器时生成
        var addonId = await InstallAddonProfileAsync(minecraftVersion, components, progress, ct)
            .ConfigureAwait(false);

        return addonId is not null
            ? (addonId, false)
            : (minecraftVersion, needsVanillaFiles);
    }

    /// <summary>没有主加载器时安装 OptiFine / LiteLoader profile；都没有时返回 null。</summary>
    private async Task<string?> InstallAddonProfileAsync(
        string minecraftVersion,
        ComponentSelection components,
        IProgress<VersionCreateProgress>? progress,
        CancellationToken ct)
    {
        if (components.OptiFine is { } optifine)
        {
            progress?.Report(new VersionCreateProgress(
                VersionCreateStage.InstallingAddons, $"正在安装 OptiFine {optifine.DisplayVersion}…"));

            var javaPath = await SelectJavaAsync(null, ct).ConfigureAwait(false);
            var locator = new GameFileLocator(paths);
            var vanillaJar = locator.FindVersionJar(minecraftVersion)
                             ?? throw new InvalidOperationException("原版客户端 jar 尚未就绪，无法安装 OptiFine。");
            var vanillaJson = locator.FindVersionJson(minecraftVersion)
                              ?? throw new InvalidOperationException("原版版本 JSON 尚未就绪，无法安装 OptiFine。");

            return await optiFine.InstallAsync(
                optifine,
                minecraftVersion,
                vanillaJar,
                vanillaJson,
                javaPath,
                WrapLoaderProgress(progress),
                ct).ConfigureAwait(false);
        }

        if (components.LiteLoader is { } liteLoader)
        {
            progress?.Report(new VersionCreateProgress(
                VersionCreateStage.InstallingAddons, "正在安装 LiteLoader…"));

            return await liteLoaderInstaller.InstallAsync(
                liteLoader, minecraftVersion, WrapLoaderProgress(progress), ct).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>
    /// 把附加 Mod 放进版本实际使用的 mods 目录。
    /// <para>
    /// OptiFine 在有主加载器时也走这里：1.13 起 OptiFine 的安装器 jar 本身就是
    /// 一个合法的 Forge Mod，直接丢进 mods 即可，不必再跑安装器。
    /// </para>
    /// </summary>
    private async Task InstallCompanionModsAsync(
        GameVersion version,
        ComponentSelection components,
        IProgress<VersionCreateProgress>? progress,
        CancellationToken ct)
    {
        // 前两个走 IModInstaller：它带镜像降级与 SHA1 校验，和「下载 → Mod」是同一套逻辑。
        var managed = new List<ModInstallItem>();

        if (components.FabricApi is { } fabricApi)
        {
            managed.Add(ToInstallItem(fabricApi, "Fabric API"));
        }

        if (components.OptiFabric is { } optiFabric)
        {
            managed.Add(ToInstallItem(optiFabric, "OptiFabric"));
        }

        if (managed.Count > 0)
        {
            progress?.Report(new VersionCreateProgress(
                VersionCreateStage.InstallingMods, "正在下载附加 Mod…"));

            var plan = new ModInstallPlan(managed);
            var result = await modInstaller
                .InstallAsync(
                    version,
                    ModProjectType.Mod,
                    plan,
                    overwrite: false,
                    progress: WrapDownloadProgress(progress),
                    ct: ct)
                .ConfigureAwait(false);

            version.CompanionMods.AddRange(result.Files);
        }

        // OptiFine 作为 Mod 时只有 BMCLAPI 一个来源，且没有可校验的哈希，单独下载
        if (components.OptiFine is { } optifine && components.OptiFineAsMod)
        {
            var directory = Path.Combine(
                paths.ResolveGameDirectory(version.Id, version.VersionIsolation), "mods");
            Directory.CreateDirectory(directory);

            var target = Path.Combine(directory, optifine.FileName);
            if (!File.Exists(target))
            {
                progress?.Report(new VersionCreateProgress(
                    VersionCreateStage.InstallingMods, $"正在下载 {optifine.FileName}…"));

                await DownloadFileAsync(OptiFineService.GetInstallerUrlFor(optifine), target, ct)
                    .ConfigureAwait(false);
            }

            version.CompanionMods.Add(optifine.FileName);
        }
    }

    /// <summary>下载器只需要版本 id 与游戏版本，隔离与否由调用方另行决定。</summary>
    private static GameVersion Scratch(string versionId, string minecraftVersion) => new()
    {
        Id = versionId,
        MinecraftVersion = minecraftVersion,
        LoaderInstalled = true,
    };

    /// <summary>
    /// 把一个附加 Mod 包成安装项。项目信息只用于日志与提示，安装器只用得到版本里的文件。
    /// </summary>
    private static ModInstallItem ToInstallItem(AddonModVersion addon, string title) =>
        new(
            new ModProject
            {
                Source = ModSource.Modrinth,
                Id = addon.FileName,
                Title = title,
            },
            new ModVersion
            {
                Source = ModSource.Modrinth,
                Id = addon.FileName,
                ProjectId = addon.FileName,
                Name = addon.VersionNumber,
                VersionNumber = addon.VersionNumber,
                File = addon.File,
            },
            ModDependencyKind.Required,
            AlreadyInstalled: false,
            IsAvailable: true);

    private async Task<string> SelectJavaAsync(int? requiredMajor, CancellationToken ct)
    {
        var runtimes = await javaDetector.DetectAsync(ct).ConfigureAwait(false);
        if (runtimes.Count == 0)
        {
            throw new InvalidOperationException(
                "没有找到可用的 Java。请先安装 Java，或在设置中手动指定 java.exe 路径。");
        }

        // 安装器都是现代工具，版本未解析时用最新的那个
        return requiredMajor is { } major
            ? (javaDetector.Select(runtimes, new Java.JavaRequirement(major)) ?? runtimes[0]).Path
            : runtimes[0].Path;
    }

    private async Task DownloadFileAsync(string url, string targetPath, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var target = File.Create(targetPath);
        await source.CopyToAsync(target, ct).ConfigureAwait(false);
    }

    private static IProgress<LoaderInstallProgress>? WrapLoaderProgress(
        IProgress<VersionCreateProgress>? progress) =>
        progress is null
            ? null
            : new Progress<LoaderInstallProgress>(p => progress.Report(new VersionCreateProgress(
                VersionCreateStage.InstallingLoader,
                p.Total > 0 ? $"{p.Stage} ({p.Current}/{p.Total})" : p.Stage)));

    private static IProgress<DownloadProgress>? WrapDownloadProgress(
        IProgress<VersionCreateProgress>? progress) =>
        progress is null
            ? null
            : new Progress<DownloadProgress>(p => progress.Report(new VersionCreateProgress(
                VersionCreateStage.DownloadingGame,
                string.IsNullOrEmpty(p.CurrentFile) ? "正在下载依赖…" : p.CurrentFile,
                p.TotalBytes > 0 ? p.Ratio : 0)));
}
