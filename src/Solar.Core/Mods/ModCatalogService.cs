using Microsoft.Extensions.Logging;
using Solar.Core.Instances;

namespace Solar.Core.Mods;

/// <summary>
/// 两个源的聚合。搜索并行发出，结果合并后按“同项目”规则去重。
/// </summary>
public sealed class ModCatalogService(
    IEnumerable<IModCatalog> catalogs,
    ILogger<ModCatalogService> logger) : IModCatalogService
{
    /// <summary>跨源去重允许的更新时间差。两个源的收录时间常有几天出入。</summary>
    private static readonly TimeSpan DedupWindow = TimeSpan.FromDays(7);

    private readonly IReadOnlyList<IModCatalog> _catalogs = catalogs.ToList();

    public async Task<ModSearchPage> SearchAsync(ModSearchQuery query, CancellationToken ct = default)
    {
        var targets = query.Source is { } single
            ? _catalogs.Where(c => c.Source == single).ToList()
            : _catalogs.ToList();

        if (targets.Count == 0)
        {
            return new ModSearchPage();
        }

        // 单源查询不需要合并，直接返回，省掉去重开销
        if (targets.Count == 1)
        {
            return await targets[0].SearchAsync(query, ct).ConfigureAwait(false);
        }

        // 两个源各要一页，合并后按 (Offset+Limit) 截断，保证“加载更多”语义一致
        var take = Math.Max(1, query.Limit);
        var tasks = targets.Select(c => SearchSafelyAsync(c, query with { Limit = take }, ct)).ToList();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        var failed = results.Where(r => r.Error is not null).Select(r => r.Source).ToList();
        var succeeded = results.Where(r => r.Page is not null).ToList();

        if (succeeded.Count == 0)
        {
            // 全部失败时抛出第一个原因，让 UI 能显示具体是网络还是配置问题
            var first = results.Select(r => r.Error).FirstOrDefault(e => e is not null);
            throw first ?? new InvalidOperationException("所有 Mod 源都不可用。");
        }

        var merged = Deduplicate(succeeded.SelectMany(r => r.Page!.Items));
        var items = merged
            .OrderByDescending(p => p.Downloads)
            .Take(take)
            .ToList();

        return new ModSearchPage
        {
            Items = items,
            // 两个源的总数相加会重复计数，但去重需要拿到全量才行，
            // 这里取较大的那个作为“至少这么多”的下界，避免出现比实际结果还小的数。
            TotalHits = succeeded.Max(r => r.Page!.TotalHits),
            Offset = query.Offset,
            Limit = query.Limit,
            FailedSources = failed,
        };
    }

    public Task<ModProject> GetProjectAsync(ModSource source, string projectId, CancellationToken ct = default) =>
        Catalog(source).GetProjectAsync(projectId, ct);

    public Task<IReadOnlyList<ModVersion>> GetVersionsAsync(
        ModSource source,
        string projectId,
        ModVersionQuery query,
        CancellationToken ct = default) =>
        Catalog(source).GetVersionsAsync(projectId, query, ct);

    public Task<IReadOnlyList<ModCategory>> GetCategoriesAsync(
        ModSource source,
        ModProjectType type = ModProjectType.Mod,
        CancellationToken ct = default) =>
        Catalog(source).GetCategoriesAsync(type, ct);

    /// <summary>
    /// 展开安装清单。
    /// <para>
    /// 只展开 <see cref="ModDependencyKind.Required"/>：可选前置让用户自己决定，
    /// 不可兼容项只做提示。深度限制 5 层并记录已访问的项目，防止两个模组互相声明依赖时死循环。
    /// </para>
    /// </summary>
    public async Task<ModInstallPlan> BuildInstallPlanAsync(
        ModProject project,
        ModVersion version,
        string? gameVersion,
        ModLoader? loader,
        CancellationToken ct = default)
    {
        var items = new List<ModInstallItem>
        {
            new(project, version, ModDependencyKind.Required, AlreadyInstalled: false, IsAvailable: true),
        };

        // 只有 Mod 有“前置”这个概念。光影/资源包/数据包的依赖字段即使有内容
        // 也不是可安装的 Mod（例如数据包可能依赖另一个数据包），v1 不展开。
        if (!project.ProjectType.UsesLoader())
        {
            return new ModInstallPlan(items);
        }

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { $"{project.Source}:{project.Id}" };
        var queue = new Queue<(ModDependency Dependency, int Depth)>(
            version.Dependencies
                .Where(d => d.Kind == ModDependencyKind.Required)
                .Select(d => (d, 1)));

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            var (dependency, depth) = queue.Dequeue();
            if (depth > 5 || !visited.Add($"{project.Source}:{dependency.ProjectId}"))
            {
                continue;
            }

            ModProject? dependencyProject;
            try
            {
                dependencyProject = await GetProjectAsync(project.Source, dependency.ProjectId, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 前置查不到不影响主模组安装，标记成不可用让 UI 提示
                logger.LogWarning(ex, "查询前置模组 {ProjectId} 失败", dependency.ProjectId);
                items.Add(new ModInstallItem(
                    new ModProject
                    {
                        Source = project.Source,
                        Id = dependency.ProjectId,
                        Title = $"前置 {dependency.ProjectId}",
                    },
                    Version: null,
                    ModDependencyKind.Required,
                    AlreadyInstalled: false,
                    IsAvailable: false));
                continue;
            }

            var candidates = await GetVersionsAsync(
                    project.Source,
                    dependency.ProjectId,
                    new ModVersionQuery { GameVersion = gameVersion, Loader = loader },
                    ct)
                .ConfigureAwait(false);

            var best = PickBestVersion(candidates);

            items.Add(new ModInstallItem(
                dependencyProject,
                best,
                ModDependencyKind.Required,
                AlreadyInstalled: false,
                IsAvailable: best is not null));

            if (best is not null)
            {
                foreach (var nested in best.Dependencies.Where(d => d.Kind == ModDependencyKind.Required))
                {
                    queue.Enqueue((nested, depth + 1));
                }
            }
        }

        return new ModInstallPlan(items);
    }

    /// <summary>
    /// 选一个版本：正式版优先，其次按发布时间倒序。
    /// 不按版本号字符串排序——<c>1.10</c> 会排在 <c>1.9</c> 前面。
    /// </summary>
    internal static ModVersion? PickBestVersion(IReadOnlyList<ModVersion> versions)
    {
        if (versions.Count == 0)
        {
            return null;
        }

        return versions
            .OrderBy(v => v.ReleaseType)
            .ThenByDescending(v => v.PublishedAt ?? DateTimeOffset.MinValue)
            .First();
    }

    private IModCatalog Catalog(ModSource source) =>
        _catalogs.FirstOrDefault(c => c.Source == source)
        ?? throw new InvalidOperationException($"没有注册 {source} 的目录实现。");

    private async Task<(ModSource Source, ModSearchPage? Page, Exception? Error)> SearchSafelyAsync(
        IModCatalog catalog,
        ModSearchQuery query,
        CancellationToken ct)
    {
        try
        {
            var page = await catalog.SearchAsync(query, ct).ConfigureAwait(false);
            return (catalog.Source, page, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "{Source} 搜索失败，已降级", catalog.Source);
            return (catalog.Source, null, ex);
        }
    }

    /// <summary>
    /// 跨源去重。同一份内容在两个站点上的标题、加载器、支持版本几乎一致，
    /// 用“规范化标题相同 + 加载器有交集 + 更新时间相近”判定，保留先出现的那条
    /// （<see cref="ModSource.CurseForge"/> 排在前面，它的元数据更完整）。
    /// </summary>
    internal static IReadOnlyList<ModProject> Deduplicate(IEnumerable<ModProject> projects)
    {
        var kept = new List<ModProject>();

        // CurseForge 先处理：它的作者、截图等元数据更完整，作为保留的那一条。
        // 不能依赖枚举的数值顺序，那只是实现细节。
        foreach (var project in projects.OrderBy(p => p.Source == ModSource.CurseForge ? 0 : 1))
        {
            var index = kept.FindIndex(k => IsSameProject(k, project));

            if (index < 0)
            {
                kept.Add(project);
                continue;
            }

            var existing = kept[index];

            // 合并掉的那条只保留来源信息，避免彻底丢失
            kept[index] = existing with
            {
                AlsoOn = existing.AlsoOn
                    .Append(new ModProjectRef(project.Source, project.Id))
                    .Distinct()
                    .ToList(),
                Downloads = Math.Max(existing.Downloads, project.Downloads),
                IconUrl = existing.IconUrl ?? project.IconUrl,
                Body = existing.Body ?? project.Body,
                Gallery = existing.Gallery.Count > 0 ? existing.Gallery : project.Gallery,
                GameVersions = existing.GameVersions
                    .Union(project.GameVersions, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            };
        }

        return kept;
    }

    internal static bool IsSameProject(ModProject a, ModProject b)
    {
        // 空 Id 说明这个条目本身就没解析出来（字段名对不上、响应结构变了）。
        // 不能让它们互相“相等”，否则一整页结果会被合并成一条。
        if (string.IsNullOrEmpty(a.Id) || string.IsNullOrEmpty(b.Id))
        {
            return false;
        }

        // 不同类型永远不是同一个项目：Modrinth 上同名同 slug 的 Mod 与整合包是两回事
        if (a.ProjectType != b.ProjectType)
        {
            return false;
        }

        if (a.Source == b.Source)
        {
            return string.Equals(a.Id, b.Id, StringComparison.Ordinal);
        }

        if (NormalizeTitle(a.Title) != NormalizeTitle(b.Title) || NormalizeTitle(a.Title).Length == 0)
        {
            return false;
        }

        // 加载器没有交集说明是两个不同的东西（同名但一个 Fabric 一个 Forge 的情况极少）
        if (a.Loaders.Count > 0 && b.Loaders.Count > 0 && !a.Loaders.Intersect(b.Loaders).Any())
        {
            return false;
        }

        if (a.UpdatedAt is { } ta && b.UpdatedAt is { } tb && (ta - tb).Duration() > DedupWindow)
        {
            return false;
        }

        return true;
    }

    /// <summary>去掉大小写、空白和标点后比较。两个源对同一模组的写法常有细微差别。</summary>
    internal static string NormalizeTitle(string title)
    {
        var builder = new System.Text.StringBuilder(title.Length);

        foreach (var c in title)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }
}
