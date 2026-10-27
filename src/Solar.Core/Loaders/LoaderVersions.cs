using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Solar.Core.Instances;

namespace Solar.Core.Loaders;

/// <summary>某个 Minecraft 版本可用的加载器版本。</summary>
public sealed record LoaderVersion(string Loader, string Version, bool IsStable)
{
    public override string ToString() => Version;
}

public interface ILoaderVersionService
{
    /// <summary>列出指定 MC 版本可用的加载器版本，推荐版本排在最前。</summary>
    Task<IReadOnlyList<LoaderVersion>> GetVersionsAsync(
        ModLoader loader,
        string minecraftVersion,
        CancellationToken ct = default);
}

/// <summary>
/// 从各家官方元数据接口获取加载器版本列表。
/// <para>
/// 四家的接口形状差异很大，这里统一成 <see cref="LoaderVersion"/>：
/// Fabric/Quilt 用 meta v2/v3，NeoForge 用 maven 版本索引，
/// Forge 用 promotions 文件（只给 recommended/latest，没有完整列表）。
/// </para>
/// </summary>
public sealed class LoaderVersionService(
    HttpClient http,
    ILogger<LoaderVersionService> logger) : ILoaderVersionService
{
    private const string FabricMeta = "https://meta.fabricmc.net/v2/versions/loader";
    private const string QuiltMeta = "https://meta.quiltmc.org/v3/versions/loader";
    private const string NeoForgeVersions =
        "https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/neoforge";
    private const string ForgePromotionsUrl =
        "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json";

    public async Task<IReadOnlyList<LoaderVersion>> GetVersionsAsync(
        ModLoader loader,
        string minecraftVersion,
        CancellationToken ct = default)
    {
        try
        {
            return loader switch
            {
                ModLoader.Fabric => await GetFabricLikeAsync(FabricMeta, "fabric", minecraftVersion, ct)
                    .ConfigureAwait(false),
                ModLoader.Quilt => await GetFabricLikeAsync(QuiltMeta, "quilt", minecraftVersion, ct)
                    .ConfigureAwait(false),
                ModLoader.NeoForge => await GetNeoForgeAsync(minecraftVersion, ct).ConfigureAwait(false),
                ModLoader.Forge => await GetForgeAsync(minecraftVersion, ct).ConfigureAwait(false),
                _ => [],
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "获取 {Loader} 版本列表失败（MC {Version}）", loader, minecraftVersion);
            throw new InvalidOperationException(
                $"无法获取 {loader} 的版本列表，请检查网络连接。", ex);
        }
    }

    private async Task<IReadOnlyList<LoaderVersion>> GetFabricLikeAsync(
        string baseUrl,
        string name,
        string minecraftVersion,
        CancellationToken ct)
    {
        // 返回结构：[{ loader: { version, stable }, ... }, ...]，已按推荐度排序
        var items = await http
            .GetFromJsonAsync<List<FabricLoaderEntry>>($"{baseUrl}/{minecraftVersion}", ct)
            .ConfigureAwait(false);

        return items?
            .Where(i => i.Loader?.Version is { Length: > 0 })
            .Select(i => new LoaderVersion(name, i.Loader!.Version!, i.Loader.Stable))
            .ToList() ?? [];
    }

    private async Task<IReadOnlyList<LoaderVersion>> GetNeoForgeAsync(
        string minecraftVersion,
        CancellationToken ct)
    {
        var response = await http
            .GetFromJsonAsync<MavenVersions>($"{NeoForgeVersions}", ct)
            .ConfigureAwait(false);

        if (response?.Versions is not { Count: > 0 })
        {
            return [];
        }

        // NeoForge 的版本号形如 21.4.161，前两段对应 MC 1.21.4：
        // 1.21.4 → 21.4，1.20.4 → 20.4，1.21 → 21.0
        var prefix = ToNeoForgePrefix(minecraftVersion);
        if (prefix is null)
        {
            return [];
        }

        return response.Versions
            .Where(v => v.StartsWith(prefix + ".", StringComparison.Ordinal))
            .Reverse()   // maven 索引是升序，界面上最新的排在前面
            .Select(v => new LoaderVersion("neoforge", v, IsNeoForgeStable(v)))
            .ToList();
    }

    private async Task<IReadOnlyList<LoaderVersion>> GetForgeAsync(
        string minecraftVersion,
        CancellationToken ct)
    {
        var promotions = await http
            .GetFromJsonAsync<ForgePromotions>(ForgePromotionsUrl, ct)
            .ConfigureAwait(false);

        if (promotions?.Promos is not { Count: > 0 })
        {
            return [];
        }

        var result = new List<LoaderVersion>();

        // promotions 只有 recommended 和 latest 两个条目，没有完整历史列表
        if (promotions.Promos.TryGetValue($"{minecraftVersion}-recommended", out var recommended))
        {
            result.Add(new LoaderVersion("forge", recommended, true));
        }

        if (promotions.Promos.TryGetValue($"{minecraftVersion}-latest", out var latest)
            && latest != recommended)
        {
            result.Add(new LoaderVersion("forge", latest, false));
        }

        return result;
    }

    /// <summary>
    /// 把 MC 版本映射成 NeoForge 版本号前缀：
    /// 1.21.4 → "21.4"；1.21 → "21.0"。
    /// NeoForge 从 1.20.1 才开始，更早的版本返回 null。
    /// </summary>
    public static string? ToNeoForgePrefix(string minecraftVersion)
    {
        var parts = minecraftVersion.Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var major) || major != 1)
        {
            return null;
        }

        if (!int.TryParse(parts[1], out var minor))
        {
            return null;
        }

        var revision = parts.Length >= 3 && int.TryParse(parts[2], out var r) ? r : 0;

        // NeoForge 首个版本对应 MC 1.20.1
        if (minor < 20 || (minor == 20 && revision == 0))
        {
            return null;
        }

        return $"{minor}.{revision}";
    }

    /// <summary>NeoForge 的 beta 版本带 -beta 后缀。</summary>
    private static bool IsNeoForgeStable(string version) =>
        !version.Contains('-', StringComparison.Ordinal);

    private sealed class FabricLoaderEntry
    {
        public FabricLoaderInfo? Loader { get; set; }
    }

    private sealed class FabricLoaderInfo
    {
        public string? Version { get; set; }
        public bool Stable { get; set; }
    }

    private sealed class MavenVersions
    {
        public List<string> Versions { get; set; } = new();
    }

    private sealed class ForgePromotions
    {
        public Dictionary<string, string> Promos { get; set; } = new();
    }
}
