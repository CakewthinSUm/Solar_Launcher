using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Solar.Core.Addons;

public interface ILiteLoaderService
{
    /// <summary>列出某个 MC 版本可用的 LiteLoader 构建；没有时返回空列表。</summary>
    Task<IReadOnlyList<LiteLoaderBuild>> GetBuildsAsync(string minecraftVersion, CancellationToken ct = default);

    /// <summary>LiteLoader 的 jar 下载地址。</summary>
    string GetJarUrl(LiteLoaderBuild build);
}

/// <summary>
/// LiteLoader 的版本列表与下载。
/// <para>
/// 官方 <c>versions.json</c> 以 MC 版本为顶层键，每个版本下 <c>artefacts</c>（Ivy 仓库，1.8 及以前）
/// 或 <c>snapshots</c>（maven 仓库）里放 <c>com.mumfrey:liteloader</c>，再按构建哈希分组，
/// <c>latest</c> 指向最新构建。两种结构取法不同，这里分别处理。
/// </para>
/// <para>
/// LiteLoader 只支持 1.13 以前的版本，1.5 / 1.6 的条目不可用（与 PCL 一致）。
/// </para>
/// </summary>
public sealed class LiteLoaderService(
    HttpClient http,
    ILogger<LiteLoaderService> logger) : ILiteLoaderService
{
    private const string VersionsUrl = "https://dl.liteloader.com/versions/versions.json";
    private const string MavenRoot = "https://dl.liteloader.com/versions/";

    public async Task<IReadOnlyList<LiteLoaderBuild>> GetBuildsAsync(
        string minecraftVersion,
        CancellationToken ct = default)
    {
        try
        {
            // 顶层是 { meta, versions }，MC 版本是 versions 下的键
            var document = await http
                .GetFromJsonAsync<LiteLoaderDocument>(VersionsUrl, ct)
                .ConfigureAwait(false);

            if (document?.Versions is not { Count: > 0 } versions
                || !versions.TryGetValue(minecraftVersion, out var entry)
                || IsUnsupported(minecraftVersion))
            {
                return [];
            }

            var latest = entry.Snapshots?.LiteLoader?.Latest ?? entry.Artefacts?.LiteLoader?.Latest;
            if (latest?.Version is not { Length: > 0 } version)
            {
                return [];
            }

            var libraries = (entry.Snapshots?.Libraries ?? [])
                .Concat(latest.Libraries ?? [])
                .Select(l => l.Name)
                .Where(name => !string.IsNullOrEmpty(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return
            [
                new LiteLoaderBuild(
                    minecraftVersion,
                    version,
                    latest.TweakClass ?? "com.mumfrey.liteloader.launch.LiteLoaderTweaker",
                    libraries),
            ];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "获取 LiteLoader 版本列表失败（MC {Version}）", minecraftVersion);
            throw new InvalidOperationException("无法获取 LiteLoader 版本列表，请检查网络连接。", ex);
        }
    }

    public string GetJarUrl(LiteLoaderBuild build)
    {
        // maven 路径：com/mumfrey/liteloader/<version>/liteloader-<version>.jar
        var version = NormalizeVersion(build.MinecraftVersion, build.Version);
        return $"{MavenRoot}com/mumfrey/liteloader/{version}/liteloader-{version}.jar";
    }

    /// <summary>
    /// 把库坐标映射到实际能下载的仓库根地址。
    /// <para>
    /// LiteLoader 声明的库混在几个仓库里：它自己的在 <c>dl.liteloader.com/versions/</c>，
    /// <c>launchwrapper</c> 只有 Mojang 的 <c>libraries.minecraft.net</c> 有，
    /// <c>asm-all</c> 在 Maven Central。返回 null 表示用 Solar 默认的库地址。
    /// </para>
    /// </summary>
    public static string? GetLibraryUrl(string name)
    {
        if (name.StartsWith("com.mumfrey:", StringComparison.OrdinalIgnoreCase))
        {
            // 官方仓库会先回 302 跳到 redirector，而 CmlLib 的下载器把 302 当失败；
            // BMCLAPI 的 maven 镜像直接给出文件，用它可以绕开这一步。
            return "https://bmclapi2.bangbang93.com/maven/";
        }

        if (name.StartsWith("net.minecraft:launchwrapper", StringComparison.OrdinalIgnoreCase))
        {
            return "https://libraries.minecraft.net/";
        }

        if (name.StartsWith("org.ow2.asm:asm-all", StringComparison.OrdinalIgnoreCase))
        {
            return "https://repo1.maven.org/maven2/";
        }

        return null;
    }

    /// <summary>
    /// 1.8 / 1.9 的 LiteLoader 版本号缺最后一段（<c>1.8-SNAPSHOT</c>），
    /// maven 里实际是 <c>1.8.0-SNAPSHOT</c>。
    /// </summary>
    public static string NormalizeVersion(string minecraftVersion, string version)
    {
        var parts = minecraftVersion.Split('.');
        var needsPatch = parts.Length == 2
                         && parts[0] == "1"
                         && parts[1] is "8" or "9"
                         && !version.StartsWith(minecraftVersion + ".", StringComparison.OrdinalIgnoreCase);

        return needsPatch ? version.Replace(minecraftVersion, minecraftVersion + ".0", StringComparison.OrdinalIgnoreCase) : version;
    }

    /// <summary>1.5 / 1.6 的 LiteLoader 依赖 Ivy 布局，Solar 不处理。</summary>
    private static bool IsUnsupported(string minecraftVersion)
    {
        var parts = minecraftVersion.Split('.');
        return parts.Length >= 2
               && parts[0] == "1"
               && int.TryParse(parts[1], out var minor)
               && minor < 7;
    }

    private sealed class LiteLoaderDocument
    {
        [JsonPropertyName("versions")]
        public Dictionary<string, LiteLoaderVersionEntry>? Versions { get; set; }
    }

    private sealed class LiteLoaderVersionEntry
    {
        [JsonPropertyName("artefacts")]
        public LiteLoaderRepository? Artefacts { get; set; }

        [JsonPropertyName("snapshots")]
        public LiteLoaderRepository? Snapshots { get; set; }
    }

    private sealed class LiteLoaderRepository
    {
        [JsonPropertyName("libraries")]
        public List<LiteLoaderLibraryRef>? Libraries { get; set; }

        [JsonPropertyName("com.mumfrey:liteloader")]
        public LiteLoaderArtifacts? LiteLoader { get; set; }
    }

    private sealed class LiteLoaderLibraryRef
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";
    }

    /// <summary>
    /// 按构建哈希分组的条目，外加一个 <c>latest</c>。
    /// 其余键是 MD5，没有固定名字，用 <see cref="JsonExtensionData"/> 接住即可。
    /// </summary>
    private sealed class LiteLoaderArtifacts
    {
        [JsonPropertyName("latest")]
        public LiteLoaderBuildInfo? Latest { get; set; }

        [JsonExtensionData]
        public Dictionary<string, System.Text.Json.JsonElement>? Builds { get; set; }
    }

    private sealed class LiteLoaderBuildInfo
    {
        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("tweakClass")]
        public string? TweakClass { get; set; }

        [JsonPropertyName("libraries")]
        public List<LiteLoaderLibraryRef>? Libraries { get; set; }
    }
}
