using Microsoft.Extensions.Logging;
using Solar.Core.Instances;
using Solar.Core.Mods;

namespace Solar.Core.Addons;

public interface IAddonCatalog
{
    /// <summary>
    /// Fabric API 在指定 MC 版本与加载器下的可用版本，新的排前面。
    /// </summary>
    Task<IReadOnlyList<AddonModVersion>> GetFabricApiAsync(
        string minecraftVersion,
        ModLoader loader,
        CancellationToken ct = default);

    /// <summary>OptiFabric 在指定 MC 版本下的可用版本。</summary>
    Task<IReadOnlyList<AddonModVersion>> GetOptiFabricAsync(
        string minecraftVersion,
        CancellationToken ct = default);
}

/// <summary>
/// Fabric API 与 OptiFabric 的版本查询。
/// <para>
/// 两者都在 Modrinth 上，直接复用 <see cref="IModCatalogService"/>：
/// 这样它们和「管理 → Mods」走同一套源、镜像降级与缓存，
/// 不会出现“创建实例能下、模组页下不了”这种两套逻辑各自演化的问题。
/// </para>
/// </summary>
public sealed class AddonCatalog(
    IModCatalogService catalogs,
    ILogger<AddonCatalog> logger) : IAddonCatalog
{
    /// <summary>Modrinth 上的项目 slug。</summary>
    private const string FabricApiSlug = "fabric-api";
    private const string OptiFabricSlug = "optifabric";

    public Task<IReadOnlyList<AddonModVersion>> GetFabricApiAsync(
        string minecraftVersion,
        ModLoader loader,
        CancellationToken ct = default) =>
        GetAsync(FabricApiSlug, "Fabric API", minecraftVersion, loader, ct);

    public Task<IReadOnlyList<AddonModVersion>> GetOptiFabricAsync(
        string minecraftVersion,
        CancellationToken ct = default) =>
        GetAsync(OptiFabricSlug, "OptiFabric", minecraftVersion, ModLoader.Fabric, ct);

    private async Task<IReadOnlyList<AddonModVersion>> GetAsync(
        string slug,
        string displayName,
        string minecraftVersion,
        ModLoader loader,
        CancellationToken ct)
    {
        try
        {
            // 先按 slug 取项目，拿到它的 id 再查版本；
            // 直接拿 slug 当 id 传也能通，但错误信息里会是一个看不懂的 slug。
            var project = await catalogs
                .GetProjectAsync(ModSource.Modrinth, slug, ct)
                .ConfigureAwait(false);

            var versions = await catalogs
                .GetVersionsAsync(
                    ModSource.Modrinth,
                    project.Id,
                    new ModVersionQuery { GameVersion = minecraftVersion, Loader = loader },
                    ct)
                .ConfigureAwait(false);

            return versions
                .Where(v => !string.IsNullOrEmpty(v.File.Url))
                .Select(v => new AddonModVersion(v.VersionNumber, v.File))
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "获取 {Name} 版本失败（MC {Version}）", displayName, minecraftVersion);
            throw new InvalidOperationException($"无法获取 {displayName} 的版本列表，请检查网络连接。", ex);
        }
    }
}
