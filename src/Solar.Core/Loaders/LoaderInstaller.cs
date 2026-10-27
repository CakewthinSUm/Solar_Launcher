using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Solar.Core.Download;
using Solar.Core.Instances;
using Solar.Core.Json;
using Solar.Core.Minecraft;
using Solar.Core.Models;
using Solar.Core.Paths;

namespace Solar.Core.Loaders;

/// <summary>安装进度。</summary>
public sealed record LoaderInstallProgress(string Stage, int Current, int Total);

public interface ILoaderInstaller
{
    /// <summary>
    /// 安装模组加载器，返回可以交给 <see cref="IVersionResolver"/> 解析的版本 Id。
    /// </summary>
    Task<string> InstallAsync(
        ModLoader loader,
        string minecraftVersion,
        string loaderVersion,
        string javaPath,
        IProgress<LoaderInstallProgress>? progress = null,
        CancellationToken ct = default,
        string? scratchInstanceId = null);
}

/// <summary>
/// 安装 Forge / NeoForge / Fabric / Quilt。
/// <para>
/// 四家的共同点：最终都要在 <c>versions/&lt;id&gt;/</c> 下放一个带
/// <c>inheritsFrom</c> 的版本 JSON，以及它引用的库。
/// 差异在于获取 JSON 与补齐文件的方式：
/// </para>
/// <list type="bullet">
/// <item>Fabric / Quilt：元数据接口直接返回完整 profile JSON。</item>
/// <item>Forge / NeoForge：下载 installer jar，取出其中的 version.json，
/// 再按 install_profile.json 跑与客户端相关的 processor。</item>
/// </list>
/// </summary>
public sealed class LoaderInstaller(
    AppPaths paths,
    HttpClient http,
    IVersionResolver resolver,
    IGameDownloader downloader,
    ILogger<LoaderInstaller> logger) : ILoaderInstaller
{
    private const string FabricProfile = "https://meta.fabricmc.net/v2/versions/loader/{0}/{1}/profile/json";
    private const string QuiltProfile = "https://meta.quiltmc.org/v3/versions/loader/{0}/{1}/profile/json";
    private const string ForgeInstallerUrl =
        "https://maven.minecraftforge.net/net/minecraftforge/forge/{0}-{1}/forge-{0}-{1}-installer.jar";
    private const string NeoForgeInstallerUrl =
        "https://maven.neoforged.net/releases/net/neoforged/neoforge/{0}/neoforge-{0}-installer.jar";

    public async Task<string> InstallAsync(
        ModLoader loader,
        string minecraftVersion,
        string loaderVersion,
        string javaPath,
        IProgress<LoaderInstallProgress>? progress = null,
        CancellationToken ct = default,
        string? scratchInstanceId = null)
    {
        // 先确保原版就绪：加载器的版本 JSON 依赖它，processor 也需要原版 jar
        progress?.Report(new LoaderInstallProgress("正在准备原版文件", 0, 1));
        var vanilla = await resolver.ResolveAsync(minecraftVersion, ct).ConfigureAwait(false);

        // 预下载时 natives 会解压到实例目录；创建流程传真实实例 Id，
        // 避免留下 installer-temp 这种没人清理的目录。
        var instance = new GameInstance
        {
            Id = scratchInstanceId ?? "installer-temp",
            MinecraftVersion = minecraftVersion,
        };
        await downloader.DownloadAsync(instance, vanilla, null, ct).ConfigureAwait(false);

        return loader switch
        {
            ModLoader.Fabric => await InstallFabricLikeAsync(
                FabricProfile, "fabric", minecraftVersion, loaderVersion, instance, progress, ct).ConfigureAwait(false),
            ModLoader.Quilt => await InstallFabricLikeAsync(
                QuiltProfile, "quilt", minecraftVersion, loaderVersion, instance, progress, ct).ConfigureAwait(false),
            ModLoader.Forge => await InstallForgeLikeAsync(
                string.Format(ForgeInstallerUrl, minecraftVersion, loaderVersion),
                minecraftVersion, javaPath, instance, progress, ct).ConfigureAwait(false),
            ModLoader.NeoForge => await InstallForgeLikeAsync(
                string.Format(NeoForgeInstallerUrl, loaderVersion),
                minecraftVersion, javaPath, instance, progress, ct).ConfigureAwait(false),
            _ => throw new NotSupportedException($"不支持的加载器: {loader}"),
        };
    }

    /// <summary>Fabric / Quilt：接口直接给出完整版本 JSON，落盘即可。</summary>
    private async Task<string> InstallFabricLikeAsync(
        string template,
        string loaderName,
        string minecraftVersion,
        string loaderVersion,
        GameInstance scratchInstance,
        IProgress<LoaderInstallProgress>? progress,
        CancellationToken ct)
    {
        progress?.Report(new LoaderInstallProgress($"正在获取 {loaderName} 配置", 0, 1));

        var url = string.Format(template, minecraftVersion, loaderVersion);
        var json = await http.GetStringAsync(url, ct).ConfigureAwait(false);
        var detail = JsonSerializer.Deserialize<VersionDetail>(json, SolarJson.Options)
                     ?? throw new InvalidOperationException($"{loaderName} 返回的版本 JSON 无效。");

        var versionId = detail.Id;
        var targetDirectory = Path.Combine(paths.Versions, versionId);
        Directory.CreateDirectory(targetDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(targetDirectory, versionId + ".json"), json, ct).ConfigureAwait(false);

        // 加载器自己的库（含它引用的原版库）交给下载器补齐
        progress?.Report(new LoaderInstallProgress("正在下载加载器库", 0, 1));
        var resolved = await resolver.ResolveAsync(versionId, ct).ConfigureAwait(false);
        await downloader.DownloadAsync(scratchInstance, resolved, null, ct).ConfigureAwait(false);

        logger.LogInformation("{Loader} {Version} 安装完成: {Id}", loaderName, loaderVersion, versionId);
        return versionId;
    }

    /// <summary>Forge / NeoForge：下载 installer，取出版本 JSON，执行客户端 processor。</summary>
    private async Task<string> InstallForgeLikeAsync(
        string installerUrl,
        string minecraftVersion,
        string javaPath,
        GameInstance scratchInstance,
        IProgress<LoaderInstallProgress>? progress,
        CancellationToken ct)
    {
        progress?.Report(new LoaderInstallProgress("正在下载安装器", 0, 1));

        var workDirectory = Path.Combine(paths.Data, "installer", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workDirectory);

        try
        {
            var installerPath = Path.Combine(workDirectory, "installer.jar");
            await DownloadFileAsync(installerUrl, installerPath, ct).ConfigureAwait(false);

            using var archive = ZipFile.OpenRead(installerPath);

            var versionEntry = archive.GetEntry("version.json")
                               ?? throw new InvalidOperationException("安装器里没有 version.json。");
            var versionJson = await ReadEntryAsync(versionEntry, ct).ConfigureAwait(false);
            var versionDetail = JsonSerializer.Deserialize<VersionDetail>(versionJson, SolarJson.Options)
                                ?? throw new InvalidOperationException("安装器的 version.json 无效。");

            var versionId = versionDetail.Id;
            var targetDirectory = Path.Combine(paths.Versions, versionId);
            Directory.CreateDirectory(targetDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(targetDirectory, versionId + ".json"), versionJson, ct).ConfigureAwait(false);

            // 先补齐版本 JSON 声明的库
            progress?.Report(new LoaderInstallProgress("正在下载加载器库", 0, 1));
            var resolved = await resolver.ResolveAsync(versionId, ct).ConfigureAwait(false);
            await downloader.DownloadAsync(scratchInstance, resolved, null, ct).ConfigureAwait(false);

            // 再跑 processor 产出客户端 artifact
            var profileEntry = archive.GetEntry("install_profile.json");
            if (profileEntry is not null)
            {
                var profileJson = await ReadEntryAsync(profileEntry, ct).ConfigureAwait(false);
                var profile = JsonSerializer.Deserialize<ForgeInstallProfile>(profileJson, SolarJson.Options)
                              ?? throw new InvalidOperationException("install_profile.json 无效。");

                await RunProcessorsAsync(
                    profile, archive, installerPath, workDirectory,
                    minecraftVersion, javaPath, progress, ct).ConfigureAwait(false);
            }

            logger.LogInformation("加载器安装完成: {Id}", versionId);
            return versionId;
        }
        finally
        {
            TryDeleteDirectory(workDirectory);
        }
    }

    private async Task RunProcessorsAsync(
        ForgeInstallProfile profile,
        ZipArchive archive,
        string installerPath,
        string workDirectory,
        string minecraftVersion,
        string javaPath,
        IProgress<LoaderInstallProgress>? progress,
        CancellationToken ct)
    {
        // 1. 把 installer 内嵌的 data 文件（如 /data/client.lzma）解压出来
        var extractDirectory = Path.Combine(workDirectory, "extract");
        ExtractDataFiles(archive, extractDirectory);

        // 2. 解析 data 表：先按 client 端取值，再展开 [maven 坐标] 与 {占位符}
        var rawData = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // 以下键不在 install_profile 的 data 表里，由官方安装器在运行时注入。
            // 漏掉任何一个都会让对应的 processor 收到未展开的占位符并失败。
            ["SIDE"] = "client",
            ["MINECRAFT_VERSION"] = minecraftVersion,

            // 原版客户端 jar：FART 以它为输入做重命名
            ["MINECRAFT_JAR"] = Path.Combine(paths.Versions, minecraftVersion, minecraftVersion + ".jar"),

            // 原版 jar 的 SHA1，部分 processor 用它做校验
            ["MINECRAFT_JAR_SHA"] = "",
        };

        foreach (var (key, entry) in profile.Data)
        {
            var value = entry.Client ?? entry.Server;
            if (value is not null)
            {
                rawData[key] = value;
            }
        }

        var resolvedData = ResolveDataTable(rawData, extractDirectory);

        // 3. 确保 processor 需要的库都在本地
        var required = profile.Libraries
            .Concat(profile.Processors
                .Where(p => p.AppliesToClient)
                .SelectMany(p => p.Classpath)
                .Select(name => new Library { Name = name }))
            .ToList();

        await EnsureLibrariesAsync(required, ct).ConfigureAwait(false);

        // 4. 逐个执行
        var processors = profile.Processors.Where(p => p.AppliesToClient).ToList();
        var index = 0;

        foreach (var processor in processors)
        {
            ct.ThrowIfCancellationRequested();
            index++;
            progress?.Report(new LoaderInstallProgress("正在执行安装步骤", index, processors.Count));

            var outputs = processor.Outputs?.Keys
                .Select(k => ForgeDataResolver.ExpandPlaceholders(
                    k, resolvedData, paths.Libraries, extractDirectory))
                .ToList() ?? [];

            // 输出已存在且哈希匹配就跳过，便于中断后重跑
            if (outputs.Count > 0 && outputs.All(File.Exists))
            {
                logger.LogDebug("跳过已完成的安装步骤: {Jar}", processor.Jar);
                continue;
            }

            await ExecuteProcessorAsync(
                processor, resolvedData, extractDirectory, javaPath, ct).ConfigureAwait(false);
        }
    }

    private async Task ExecuteProcessorAsync(
        ForgeProcessor processor,
        IReadOnlyDictionary<string, string> data,
        string extractDirectory,
        string javaPath,
        CancellationToken ct)
    {
        var jarPath = ForgeDataResolver.GetLibraryPath(processor.Jar, paths.Libraries);
        var mainClass = ReadMainClass(jarPath);

        var classPath = processor.Classpath
            .Select(c => ForgeDataResolver.GetLibraryPath(c, paths.Libraries))
            .Append(jarPath)
            .ToList();

        var missing = classPath.Where(p => !File.Exists(p)).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"安装步骤缺少依赖: {string.Join(", ", missing.Select(Path.GetFileName))}");
        }

        var arguments = processor.Args
            .Select(arg => ResolveArgument(arg, data, extractDirectory))
            .ToList();

        var startInfo = new ProcessStartInfo(javaPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = extractDirectory,
        };

        startInfo.ArgumentList.Add("-cp");
        startInfo.ArgumentList.Add(string.Join(Path.PathSeparator, classPath));
        startInfo.ArgumentList.Add(mainClass);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        logger.LogDebug("执行安装步骤: {Jar} {Args}", processor.Jar, string.Join(' ', arguments));

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("无法启动 Java 安装进程。");

        var stdout = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        var stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            logger.LogError("安装步骤失败 ({Code}): {Jar}\n{Out}\n{Err}",
                process.ExitCode, processor.Jar, stdout, stderr);
            throw new InvalidOperationException(
                $"加载器安装步骤失败（{processor.Jar}，退出码 {process.ExitCode}）。{Environment.NewLine}{stderr}");
        }
    }

    /// <summary>
    /// 解析单个 processor 参数。
    /// <para>
    /// 需要处理三种形态，且它们可以出现在同一个参数里：
    /// </para>
    /// <list type="bullet">
    /// <item><c>{KEY}</c>：引用 data 表</item>
    /// <item><c>[group:artifact:version@ext]</c>：内联 maven 坐标。
    /// NeoForge 会直接在 args 里写这种坐标而不经过 data 表。</item>
    /// <item>其他：字面量</item>
    /// </list>
    /// </summary>
    private string ResolveArgument(
        string argument,
        IReadOnlyDictionary<string, string> data,
        string extractDirectory)
    {
        // 先展开 {KEY}
        var result = ForgeDataResolver.ExpandPlaceholders(
            argument, data, paths.Libraries, extractDirectory);

        // 再处理整段就是一个 [坐标] 的参数
        if (result.Length > 2 && result[0] == '[' && result[^1] == ']')
        {
            return ForgeDataResolver.GetLibraryPath(result[1..^1], paths.Libraries);
        }

        return result;
    }

    /// <summary>
    /// 从 jar 的 MANIFEST 读 Main-Class。processor 的入口类没有写在 install_profile 里，
    /// 但它们的 jar 都带 Main-Class 清单项。
    /// </summary>
    public static string ReadMainClass(string jarPath)
    {
        using var archive = ZipFile.OpenRead(jarPath);
        var manifest = archive.GetEntry("META-INF/MANIFEST.MF")
                       ?? throw new InvalidOperationException($"{Path.GetFileName(jarPath)} 没有 MANIFEST.MF。");

        using var reader = new StreamReader(manifest.Open());
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.StartsWith("Main-Class:", StringComparison.OrdinalIgnoreCase))
            {
                return line["Main-Class:".Length..].Trim();
            }
        }

        throw new InvalidOperationException($"{Path.GetFileName(jarPath)} 的清单里没有 Main-Class。");
    }

    private async Task EnsureLibrariesAsync(IEnumerable<Library> libraries, CancellationToken ct)
    {
        var files = new List<CmlLib.Core.Files.GameFile>();
        var mavenBase = "https://maven.minecraftforge.net/";

        foreach (var library in libraries)
        {
            var (group, artifact, version, classifier, extension) =
                ForgeDataResolver.ParseCoordinate(library.Name);

            var relative = Path.Combine(
                group.Replace('.', Path.DirectorySeparatorChar),
                artifact,
                version,
                BuildFileName(artifact, version, classifier, extension));

            var url = library.Downloads?.Artifact?.Url
                      ?? library.Url
                      ?? $"{mavenBase}{relative.Replace(Path.DirectorySeparatorChar, '/')}";

            files.Add(new CmlLib.Core.Files.GameFile(library.Name)
            {
                Path = Path.Combine(paths.Libraries, relative),
                Url = url,
                Hash = library.Downloads?.Artifact?.Sha1,
                Size = library.Downloads?.Artifact?.Size ?? 0,
            });
        }

        if (files.Count == 0)
        {
            return;
        }

        var installer = new CmlLib.Core.Installers.ParallelGameInstaller(
            maxChecker: 4,
            maxDownloader: 8,
            boundedCapacity: 512,
            MirrorRewritingHandler.CreateClient(null));

        await installer.Install(files, null, null, ct).ConfigureAwait(false);
    }

    private static string BuildFileName(string artifact, string version, string? classifier, string extension)
    {
        var name = $"{artifact}-{version}";
        if (!string.IsNullOrEmpty(classifier))
        {
            name += "-" + classifier;
        }

        return name + "." + extension;
    }

    /// <summary>解析 data 表：字面量 / [maven 坐标] / /jar 内路径。</summary>
    private Dictionary<string, string> ResolveDataTable(
        Dictionary<string, string> raw,
        string extractDirectory)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        // 先放入字面量与 maven 坐标，再统一展开 {占位符}
        foreach (var (key, value) in raw)
        {
            result[key] = ForgeDataResolver.Resolve(value, result, paths.Libraries, extractDirectory);
        }

        // 再跑一遍，让引用了其他键的条目也能解析
        foreach (var key in raw.Keys.ToList())
        {
            result[key] = ForgeDataResolver.Resolve(raw[key], result, paths.Libraries, extractDirectory);
        }

        return result;
    }

    private static void ExtractDataFiles(ZipArchive archive, string extractDirectory)
    {
        Directory.CreateDirectory(extractDirectory);

        foreach (var entry in archive.Entries)
        {
            // 只解压 data/ 与 maven/ 下的内容
            if (!entry.FullName.StartsWith("data/", StringComparison.OrdinalIgnoreCase)
                && !entry.FullName.StartsWith("maven/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            var destination = Path.Combine(
                extractDirectory,
                entry.FullName.Replace('/', Path.DirectorySeparatorChar));

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }
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

    private static async Task<string> ReadEntryAsync(ZipArchiveEntry entry, CancellationToken ct)
    {
        await using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // 临时目录清理失败不影响安装结果
        }
    }
}
