namespace Solar.Core.Mods;

/// <summary>
/// 单个模组源。两个实现各自负责把自家 JSON 映射成统一模型，
/// 上层（聚合服务、UI）永远只看到 <see cref="ModProject"/> / <see cref="ModVersion"/>。
/// </summary>
public interface IModCatalog
{
    ModSource Source { get; }

    Task<ModSearchPage> SearchAsync(ModSearchQuery query, CancellationToken ct = default);

    Task<ModProject> GetProjectAsync(string projectId, CancellationToken ct = default);

    Task<IReadOnlyList<ModVersion>> GetVersionsAsync(
        string projectId,
        ModVersionQuery query,
        CancellationToken ct = default);

    Task<IReadOnlyList<ModCategory>> GetCategoriesAsync(
        ModProjectType type = ModProjectType.Mod,
        CancellationToken ct = default);
}

/// <summary>
/// 聚合层：并行查两个源、跨源去重、单源失败降级。
/// <para>
/// 降级是刻意做成“部分可用”的：Modrinth 或 CurseForge 任一不可达时，
/// 仍然返回另一边的结果，只把失败的源记在 <see cref="ModSearchPage.FailedSources"/> 里，
/// 由 UI 提示用户。只有两边都失败才抛异常。
/// </para>
/// </summary>
public interface IModCatalogService
{
    Task<ModSearchPage> SearchAsync(ModSearchQuery query, CancellationToken ct = default);

    Task<ModProject> GetProjectAsync(ModSource source, string projectId, CancellationToken ct = default);

    Task<IReadOnlyList<ModVersion>> GetVersionsAsync(
        ModSource source,
        string projectId,
        ModVersionQuery query,
        CancellationToken ct = default);

    /// <summary>两个源的分类合并后的结果，按源分组由 UI 决定怎么展示。</summary>
    Task<IReadOnlyList<ModCategory>> GetCategoriesAsync(
        ModSource source,
        ModProjectType type = ModProjectType.Mod,
        CancellationToken ct = default);

    /// <summary>生成安装清单：目标版本 + 递归展开的必选前置。</summary>
    Task<ModInstallPlan> BuildInstallPlanAsync(
        ModProject project,
        ModVersion version,
        string? gameVersion,
        Instances.ModLoader? loader,
        CancellationToken ct = default);
}
