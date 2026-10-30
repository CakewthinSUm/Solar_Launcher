using Solar.Core.Download;
using Solar.Core.Json;
using Solar.Core.Loaders;
using Solar.Core.Minecraft;
using Solar.Core.Models;
using Solar.Core.Paths;

namespace Solar.Core.Addons;

public interface ILiteLoaderInstaller
{
    /// <summary>
    /// 生成 LiteLoader 的版本 profile 并补齐依赖，返回版本 Id。
    /// <para>
    /// LiteLoader 没有需要打补丁的客户端 jar：它的 jar 本身就是库，
    /// 启动时由 launchwrapper 的 tweakClass 挂上去，所以只要写一份带
    /// <c>inheritsFrom</c> 的 JSON 就够。
    /// </para>
    /// </summary>
    Task<string> InstallAsync(
        LiteLoaderBuild build,
        string minecraftVersion,
        IProgress<LoaderInstallProgress>? progress = null,
        CancellationToken ct = default);
}

public sealed class LiteLoaderInstaller(
    AppPaths paths,
    IVersionResolver resolver,
    IGameDownloader downloader) : ILiteLoaderInstaller
{
    public async Task<string> InstallAsync(
        LiteLoaderBuild build,
        string minecraftVersion,
        IProgress<LoaderInstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        progress?.Report(new LoaderInstallProgress("正在准备 LiteLoader", 0, 1));

        var versionId = $"{minecraftVersion}-LiteLoader";
        var versionDirectory = Path.Combine(paths.Versions, versionId);
        Directory.CreateDirectory(versionDirectory);

        var detail = BuildProfile(build, minecraftVersion, versionId);
        // 版本 JSON 按 Mojang 原生格式写：launchwrapper 与外部工具按 camelCase 读
        await JsonFile.WriteAsync(
            Path.Combine(versionDirectory, versionId + ".json"),
            detail,
            ct,
            SolarJson.MinecraftNative).ConfigureAwait(false);

        progress?.Report(new LoaderInstallProgress("正在下载 LiteLoader 库", 0, 1));
        var resolved = await resolver.ResolveAsync(versionId, ct).ConfigureAwait(false);
        var instance = new Instances.GameInstance { Id = "installer-temp", MinecraftVersion = minecraftVersion };
        await downloader.DownloadAsync(instance, resolved, null, ct).ConfigureAwait(false);

        return versionId;
    }

    /// <summary>
    /// LiteLoader 的 profile：mainClass 换成 launchwrapper，游戏参数里挂 tweakClass，
    /// 库列表里放 LiteLoader 自己与它声明的依赖。
    /// </summary>
    public static VersionDetail BuildProfile(
        LiteLoaderBuild build,
        string minecraftVersion,
        string versionId)
    {
        var version = LiteLoaderService.NormalizeVersion(minecraftVersion, build.Version);

        // 每个库的仓库地址不一样，逐个映射；映射不到时留空，
        // 下载器会退回官方 libraries.minecraft.net。
        var libraries = build.Libraries
            .Select(name => new Library
            {
                Name = name,
                Url = LiteLoaderService.GetLibraryUrl(name),
            })
            .ToList();

        libraries.Add(new Library
        {
            Name = $"com.mumfrey:liteloader:{version}",
            Url = LiteLoaderService.GetLibraryUrl($"com.mumfrey:liteloader:{version}"),
        });

        return new VersionDetail
        {
            Id = versionId,
            Type = "release",
            InheritsFrom = minecraftVersion,
            MainClass = "net.minecraft.launchwrapper.Launch",
            Libraries = libraries,
            Arguments = new Arguments
            {
                Game =
                [
                    new ArgumentEntry { Value = "--tweakClass" },
                    new ArgumentEntry { Value = build.TweakClass },
                ],
            },
        };
    }
}
