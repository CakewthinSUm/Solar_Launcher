using Microsoft.Extensions.Logging;
using Solar.Core.Addons;
using Solar.Core.Download;
using Solar.Core.Java;
using Solar.Core.Loaders;
using Solar.Core.Minecraft;
using Solar.Core.Mods;
using Solar.Core.Paths;

namespace Solar.Core.Instances;

/// <summary>创建实例的安装阶段，供页面显示进度文案。</summary>
public enum InstanceCreateStage
{
    Preparing,
    InstallingLoader,
    InstallingAddons,
    DownloadingGame,
    InstallingMods,
    Saving,
}

/// <summary>创建进度。进度条用 <see cref="Ratio"/>，0 表示该阶段不报百分比。</summary>
public sealed record InstanceCreateProgress(InstanceCreateStage Stage, string Message, double Ratio = 0);

public sealed record InstanceCreateRequest(
    string Name,
    string MinecraftVersion,
    ComponentSelection Components,
    bool VersionIsolation);

public interface IInstanceCreationService
{
    /// <summary>
    /// 创建实例并把所选组件全部装好。
    /// <para>
    /// 实例记录在第一步就落盘：中途失败或取消时它仍留在列表里，
    /// 首次启动会由 <see cref="Launch.GameLaunchService"/> 把加载器补装完。
    /// </para>
    /// </summary>
    Task<GameInstance> CreateAsync(
        InstanceCreateRequest request,
        IProgress<InstanceCreateProgress>? progress = null,
        CancellationToken ct = default);
}

/// <summary>
/// 把「下载页选中的版本 + 组件」变成一个装好的实例。
/// <para>
/// 顺序是有依赖的：主加载器要先装（它决定 <see cref="GameInstance.VersionId"/>），
/// 然后是 OptiFine / LiteLoader 这类附加 profile，最后才是依赖下载与 mods 里的附加 Mod。
/// </para>
/// </summary>
public sealed class InstanceCreationService(
    AppPaths paths,
    IInstanceRepository repository,
    IVersionResolver resolver,
    IGameDownloader downloader,
    ILoaderInstaller loaderInstaller,
    IOptiFineService optiFine,
    ILiteLoaderInstaller liteLoaderInstaller,
    IModInstaller modInstaller,
    InstanceGameDirectory gameDirectories,
    IJavaDetector javaDetector,
    HttpClient http,
    ILogger<InstanceCreationService> logger) : IInstanceCreationService
{
    public async Task<GameInstance> CreateAsync(
        InstanceCreateRequest request,
        IProgress<InstanceCreateProgress>? progress = null,
        CancellationToken ct = default)
    {
        var components = request.Components;
        var minecraftVersion = request.MinecraftVersion;

        progress?.Report(new InstanceCreateProgress(InstanceCreateStage.Preparing, "正在创建实例…"));

        var instance = new GameInstance
        {
            Name = string.IsNullOrWhiteSpace(request.Name)
                ? components.BuildDefaultName(minecraftVersion)
                : request.Name.Trim(),
            MinecraftVersion = minecraftVersion,
            Loader = components.Loader,
            LoaderVersion = components.Loader == ModLoader.Vanilla ? null : components.LoaderVersion,
            OptiFineVersion = components.OptiFine?.Version,
            LiteLoaderVersion = components.LiteLoader?.Version,
            VersionIsolation = request.VersionIsolation,

            // 原版实例不需要安装加载器；带加载器的要等下面装完才置位
            LoaderInstalled = components.Loader == ModLoader.Vanilla,
        };

        await repository.SaveAsync(instance, ct).ConfigureAwait(false);

        try
        {
            await InstallAsync(instance, components, progress, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "创建实例 {Name} 时安装失败，保留记录供首次启动续装", instance.Name);
            throw;
        }

        return instance;
    }

    private async Task InstallAsync(
        GameInstance instance,
        ComponentSelection components,
        IProgress<InstanceCreateProgress>? progress,
        CancellationToken ct)
    {
        var minecraftVersion = instance.MinecraftVersion;

        // 1. 原版就绪：加载器与 OptiFine 安装器都依赖它。
        //    OptiFine 的安装器需要磁盘上真实存在的原版 jar，所以这种情况下要先把依赖下完。
        progress?.Report(new InstanceCreateProgress(InstanceCreateStage.Preparing, "正在准备原版文件…"));
        var vanilla = await resolver.ResolveAsync(minecraftVersion, ct).ConfigureAwait(false);

        if (components.OptiFine is not null && components.Loader == ModLoader.Vanilla)
        {
            progress?.Report(new InstanceCreateProgress(InstanceCreateStage.DownloadingGame, "正在下载原版文件…"));
            await downloader.DownloadAsync(instance, vanilla, WrapDownloadProgress(progress), ct)
                .ConfigureAwait(false);
        }

        // 2. 主加载器
        if (components.Loader != ModLoader.Vanilla)
        {
            var javaPath = await SelectJavaAsync(null, ct).ConfigureAwait(false);

            var versionId = await loaderInstaller.InstallAsync(
                components.Loader,
                minecraftVersion,
                components.LoaderVersion
                    ?? throw new InvalidOperationException("没有指定加载器版本。"),
                javaPath,
                WrapLoaderProgress(progress),
                ct,
                instance.Id).ConfigureAwait(false);

            instance.VersionId = versionId;
            instance.LoaderInstalled = true;
            await repository.SaveAsync(instance, ct).ConfigureAwait(false);
        }

        // 3. 附加 profile（OptiFine / LiteLoader），只在没有主加载器时生成
        if (components.Loader == ModLoader.Vanilla)
        {
            await InstallAddonProfilesAsync(instance, components, vanilla, progress, ct).ConfigureAwait(false);
        }

        // 4. 依赖下载（原版已经下过一遍时这里只是校验，很快）
        progress?.Report(new InstanceCreateProgress(InstanceCreateStage.DownloadingGame, "正在检查并补齐文件…"));
        var resolved = await resolver.ResolveAsync(instance.EffectiveVersionId, ct).ConfigureAwait(false);
        await downloader.DownloadAsync(instance, resolved, WrapDownloadProgress(progress), ct)
            .ConfigureAwait(false);

        // 5. mods 里的附加 Mod
        await InstallCompanionModsAsync(instance, components, progress, ct).ConfigureAwait(false);

        progress?.Report(new InstanceCreateProgress(InstanceCreateStage.Saving, "正在保存…"));
        await repository.SaveAsync(instance, ct).ConfigureAwait(false);

        logger.LogInformation(
            "实例 {Name} 创建完成：{Version} {Loader}",
            instance.Name, instance.EffectiveVersionId, instance.DisplayLoader);
    }

    private async Task InstallAddonProfilesAsync(
        GameInstance instance,
        ComponentSelection components,
        ResolvedVersion vanilla,
        IProgress<InstanceCreateProgress>? progress,
        CancellationToken ct)
    {
        if (components.OptiFine is { } optifine)
        {
            progress?.Report(new InstanceCreateProgress(
                InstanceCreateStage.InstallingAddons, $"正在安装 OptiFine {optifine.DisplayVersion}…"));

            var javaPath = await SelectJavaAsync(null, ct).ConfigureAwait(false);
            var locator = new GameFileLocator(paths, gameDirectories);
            var vanillaJar = locator.FindVersionJar(instance.MinecraftVersion)
                             ?? throw new InvalidOperationException(
                                 "原版客户端 jar 尚未就绪，无法安装 OptiFine。");
            var vanillaJson = locator.FindVersionJson(instance.MinecraftVersion)
                              ?? throw new InvalidOperationException(
                                  "原版版本 JSON 尚未就绪，无法安装 OptiFine。");

            var versionId = await optiFine.InstallAsync(
                optifine,
                instance.MinecraftVersion,
                vanillaJar,
                vanillaJson,
                javaPath,
                WrapLoaderProgress(progress),
                ct).ConfigureAwait(false);

            instance.VersionId = versionId;
            instance.LoaderInstalled = true;
            await repository.SaveAsync(instance, ct).ConfigureAwait(false);
        }
        else if (components.LiteLoader is { } liteLoader)
        {
            progress?.Report(new InstanceCreateProgress(
                InstanceCreateStage.InstallingAddons, "正在安装 LiteLoader…"));

            var versionId = await liteLoaderInstaller.InstallAsync(
                liteLoader, instance.MinecraftVersion, WrapLoaderProgress(progress), ct).ConfigureAwait(false);

            instance.VersionId = versionId;
            instance.LoaderInstalled = true;
            await repository.SaveAsync(instance, ct).ConfigureAwait(false);
        }

        _ = vanilla;
    }

    /// <summary>
    /// 把附加 Mod 放进实例实际使用的 mods 目录。
    /// <para>
    /// OptiFine 在有主加载器时也走这里：1.13 起 OptiFine 的安装器 jar 本身就是
    /// 一个合法的 Forge Mod，直接丢进 mods 即可，不必再跑安装器。
    /// </para>
    /// </summary>
    private async Task InstallCompanionModsAsync(
        GameInstance instance,
        ComponentSelection components,
        IProgress<InstanceCreateProgress>? progress,
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
            progress?.Report(new InstanceCreateProgress(
                InstanceCreateStage.InstallingMods, "正在下载附加 Mod…"));

            var plan = new ModInstallPlan(managed);
            var result = await modInstaller
                .InstallAsync(
                    instance,
                    ModProjectType.Mod,
                    plan,
                    overwrite: false,
                    progress: WrapDownloadProgress(progress),
                    ct: ct)
                .ConfigureAwait(false);

            instance.CompanionMods.AddRange(result.Files);
        }

        // OptiFine 作为 Mod 时只有 BMCLAPI 一个来源，且没有可校验的哈希，单独下载
        if (components.OptiFine is { } optifine && components.OptiFineAsMod)
        {
            var directory = Path.Combine(gameDirectories.Resolve(instance), "mods");
            Directory.CreateDirectory(directory);

            var target = Path.Combine(directory, optifine.FileName);
            if (!File.Exists(target))
            {
                progress?.Report(new InstanceCreateProgress(
                    InstanceCreateStage.InstallingMods, $"正在下载 {optifine.FileName}…"));

                await DownloadFileAsync(OptiFineService.GetInstallerUrlFor(optifine), target, ct)
                    .ConfigureAwait(false);
            }

            instance.CompanionMods.Add(optifine.FileName);
        }
    }

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
            ? (javaDetector.Select(runtimes, major) ?? runtimes[0]).Path
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
        IProgress<InstanceCreateProgress>? progress) =>
        progress is null
            ? null
            : new Progress<LoaderInstallProgress>(p => progress.Report(new InstanceCreateProgress(
                InstanceCreateStage.InstallingLoader,
                p.Total > 0 ? $"{p.Stage} ({p.Current}/{p.Total})" : p.Stage)));

    private static IProgress<DownloadProgress>? WrapDownloadProgress(
        IProgress<InstanceCreateProgress>? progress) =>
        progress is null
            ? null
            : new Progress<DownloadProgress>(p => progress.Report(new InstanceCreateProgress(
                InstanceCreateStage.DownloadingGame,
                string.IsNullOrEmpty(p.CurrentFile) ? "正在下载依赖…" : p.CurrentFile,
                p.TotalBytes > 0 ? p.Ratio : 0)));
}
