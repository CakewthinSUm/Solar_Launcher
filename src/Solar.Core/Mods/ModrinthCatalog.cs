using System.Text.Json;
using System.Text.Json.Serialization;
using Solar.Core.Instances;

namespace Solar.Core.Mods;

/// <summary>Modrinth API 的原始 DTO。只声明用得到的字段，其余忽略。</summary>
internal static class ModrinthDtos
{
    internal sealed class SearchResponse
    {
        public List<SearchHit> Hits { get; set; } = [];
        public int Offset { get; set; }
        public int Limit { get; set; }
        public int TotalHits { get; set; }
    }

    internal sealed class SearchHit
    {
        public string ProjectId { get; set; } = "";
        public string? Slug { get; set; }
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public string? Author { get; set; }
        public string? IconUrl { get; set; }
        public long Downloads { get; set; }
        public DateTimeOffset? DateModified { get; set; }
        public List<string> Categories { get; set; } = [];
        public List<string> Versions { get; set; } = [];
        public List<string>? Loaders { get; set; }
    }

    internal sealed class ProjectDto
    {
        public string Id { get; set; } = "";
        public string? Slug { get; set; }
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";

        /// <summary>mod / modpack / resourcepack / shader / datapack。</summary>
        public string? ProjectType { get; set; }

        /// <summary>Markdown 正文。列表接口没有这个字段。</summary>
        public string? Body { get; set; }

        public string? IconUrl { get; set; }

        /// <summary>原始 PNG 图标。镜像的批量接口缺这个字段，所以可能为空。</summary>
        public string? RawIconUrl { get; set; }

        public long Downloads { get; set; }
        public DateTimeOffset? Updated { get; set; }
        public List<string> Categories { get; set; } = [];
        public List<string> AdditionalCategories { get; set; } = [];
        public List<string> Loaders { get; set; } = [];
        public List<string> GameVersions { get; set; } = [];
        public List<GalleryEntry> Gallery { get; set; } = [];
    }

    internal sealed class GalleryEntry
    {
        public string? Url { get; set; }
        public string? RawUrl { get; set; }
        public string? Title { get; set; }
    }

    internal sealed class VersionDto
    {
        public string Id { get; set; } = "";
        public string ProjectId { get; set; } = "";
        public string Name { get; set; } = "";
        public string VersionNumber { get; set; } = "";
        public string? Changelog { get; set; }
        public List<string> GameVersions { get; set; } = [];
        public List<string> Loaders { get; set; } = [];
        public List<DependencyDto> Dependencies { get; set; } = [];
        public List<FileDto> Files { get; set; } = [];
        public DateTimeOffset? DatePublished { get; set; }
        public long Downloads { get; set; }

        /// <summary>release / beta / alpha。</summary>
        public string? VersionType { get; set; }
    }

    internal sealed class DependencyDto
    {
        public string? VersionId { get; set; }
        public string? ProjectId { get; set; }
        public string? FileName { get; set; }

        /// <summary>required / optional / incompatible / embedded。</summary>
        public string? DependencyType { get; set; }
    }

    internal sealed class FileDto
    {
        public string? Url { get; set; }
        public string? Filename { get; set; }
        public long Size { get; set; }
        public bool Primary { get; set; }
        public Dictionary<string, string>? Hashes { get; set; }
    }

    internal sealed class CategoryDto
    {
        public string? Name { get; set; }
        public string? ProjectType { get; set; }
        public string? Header { get; set; }
    }
}

/// <summary>
/// Modrinth 目录实现。
/// <para>
/// 搜索用 facets 表达过滤，语法是「数组的数组」：外层 AND，内层 OR。
/// 加载器在 facets 里属于 <c>categories</c>，游戏版本属于 <c>versions</c>。
/// </para>
/// </summary>
public sealed class ModrinthCatalog(ModHttp http, ModCache cache) : IModCatalog
{
    /// <summary>
    /// 结尾的斜杠不能省：<c>new Uri(new Uri("…/v2"), "search")</c> 会把 <c>v2</c> 当成
    /// 最后一段路径替换掉，得到 <c>…/search</c>，所有请求都会 404。
    /// </summary>
    private static readonly Uri Base = new("https://api.modrinth.com/v2/");

    /// <summary>
    /// 不用 <c>SolarJson.Options</c>：那份带参数数组转换器与缩进输出，是为版本 JSON
    /// 与本地配置文件准备的。
    /// <para>
    /// 命名策略必须是 <see cref="JsonNamingPolicy.SnakeCaseLower"/>：Modrinth 的字段是
    /// <c>project_id</c>、<c>total_hits</c>、<c>game_versions</c> 这种下划线风格，
    /// 而 <see cref="JsonSerializerDefaults.Web"/> 默认按 camelCase 匹配，
    /// 加上 <c>PropertyNameCaseInsensitive</c> 也救不了——下划线对不上，
    /// 所有字段会静默变成默认值（Id 为空、下载量为 0）。
    /// </para>
    /// </summary>
    internal static readonly System.Text.Json.JsonSerializerOptions Json =
        new(System.Text.Json.JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

    public ModSource Source => ModSource.Modrinth;

    public async Task<ModSearchPage> SearchAsync(ModSearchQuery query, CancellationToken ct = default)
    {
        var url = new Uri(Base,
            $"search?query={Uri.EscapeDataString(query.Text ?? "")}" +
            $"&limit={Math.Clamp(query.Limit, 1, 100)}" +
            $"&offset={Math.Max(0, query.Offset)}" +
            $"&index={ToIndex(query.Sort)}" +
            $"&facets={Uri.EscapeDataString(BuildFacets(query))}");

        var response = await http.GetJsonAsync<ModrinthDtos.SearchResponse>(url, Json, ct).ConfigureAwait(false);

        return new ModSearchPage
        {
            Items = response.Hits.Select(hit => ToProject(hit, query.Type)).ToList(),
            TotalHits = response.TotalHits,
            Offset = response.Offset,
            Limit = response.Limit,
        };
    }

    public async Task<ModProject> GetProjectAsync(string projectId, CancellationToken ct = default)
    {
        var cached = await cache.ReadProjectAsync(Source, projectId, ct).ConfigureAwait(false);
        if (cached is not null)
        {
            return cached;
        }

        var url = new Uri(Base, $"project/{Uri.EscapeDataString(projectId)}");
        var dto = await http.GetJsonAsync<ModrinthDtos.ProjectDto>(url, Json, ct).ConfigureAwait(false);

        var project = ToProject(dto);
        await cache.WriteProjectAsync(project, ct).ConfigureAwait(false);
        return project;
    }

    public async Task<IReadOnlyList<ModVersion>> GetVersionsAsync(
        string projectId,
        ModVersionQuery query,
        CancellationToken ct = default)
    {
        var cacheKey = BuildVersionCacheKey(projectId, query);

        // 只有 Mod 才有加载器这个概念；把 loaders 参数带到光影/资源包上会直接搜空
        var usesLoader = query.Type.UsesLoader();

        // 缓存里存的是过滤后的完整列表，Limit 只在返回时截断。
        // 否则先来一次 Limit=10 的调用会把缓存写成十条，之后要 100 条也只能拿到十条。
        var cached = await cache.ReadVersionsAsync(Source, cacheKey, ct).ConfigureAwait(false);
        if (cached is not null)
        {
            return cached.Take(Math.Max(1, query.Limit)).ToList();
        }

        var builder = new System.Text.StringBuilder($"project/{Uri.EscapeDataString(projectId)}/version?include_changelog=false");

        // 数据包必须限定 datapack loader：Modrinth 上没有独立的 datapack 项目类型，
        // 同一个项目往往既有 Mod jar 版本、又有 datapack zip 版本。
        // 这个过滤不能放进下面的兼容性开关里，否则“显示全部版本”会把 jar 也列出来。
        if (query.Type == ModProjectType.DataPack)
        {
            builder.Append($"&loaders={Uri.EscapeDataString("[\"datapack\"]")}");
        }

        // 不加过滤就返回全部历史版本；加了过滤能让服务端直接筛掉不兼容的
        if (!query.IncludeIncompatible)
        {
            if (usesLoader && ModLoaderMapping.ToModrinth(query.Loader) is { } loader)
            {
                builder.Append($"&loaders={Uri.EscapeDataString($"[\"{loader}\"]")}");
            }

            if (!string.IsNullOrWhiteSpace(query.GameVersion))
            {
                builder.Append($"&game_versions={Uri.EscapeDataString($"[\"{query.GameVersion}\"]")}");
            }
        }

        var url = new Uri(Base, builder.ToString());
        var dtos = await http.GetJsonAsync<List<ModrinthDtos.VersionDto>>(url, Json, ct).ConfigureAwait(false);

        var versions = dtos
            .Select(ToVersion)
            .Where(v => v is not null)
            .Select(v => v!)
            .ToList();

        await cache.WriteVersionsAsync(Source, cacheKey, versions, ct).ConfigureAwait(false);
        return versions.Take(Math.Max(1, query.Limit)).ToList();
    }

    public async Task<IReadOnlyList<ModCategory>> GetCategoriesAsync(
        ModProjectType type = ModProjectType.Mod,
        CancellationToken ct = default)
    {
        var cacheKey = $"{Source}_{type.ToModrinth()}";
        var cached = await cache.ReadCategoriesAsync(Source, ct, cacheKey).ConfigureAwait(false);
        if (cached is not null)
        {
            return cached;
        }

        var url = new Uri(Base, "tag/category");
        var dtos = await http.GetJsonAsync<List<ModrinthDtos.CategoryDto>>(url, Json, ct).ConfigureAwait(false);

        var slug = type.ToModrinth();
        var categories = dtos
            .Where(c => string.Equals(c.ProjectType, slug, StringComparison.OrdinalIgnoreCase))
            .Where(c => !string.IsNullOrWhiteSpace(c.Name))
            .Select(c => new ModCategory(c.Name!, DisplayCategory(c.Name!), Source))
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ToList();

        await cache.WriteCategoriesAsync(Source, categories, ct, cacheKey).ConfigureAwait(false);
        return categories;
    }

    /// <summary>
    /// 构造 facets。外层每个元素是一组 OR 条件，组与组之间是 AND。
    /// 加载器在 Modrinth 里是 category，游戏版本是 version。
    /// </summary>
    internal static string BuildFacets(ModSearchQuery query)
    {
        var groups = new List<string> { $"[\"project_type:{query.Type.ToModrinth()}\"]" };

        // 加载器只对 Mod 有意义：光影/资源包/整合包都没有 fabric 之类的 category
        if (query.Type.UsesLoader() && ModLoaderMapping.ToModrinth(query.Loader) is { } loader)
        {
            groups.Add($"[\"categories:{loader}\"]");
        }

        if (query.Category is { } category && category.Source == ModSource.Modrinth)
        {
            groups.Add($"[\"categories:{category.Key}\"]");
        }

        if (!string.IsNullOrWhiteSpace(query.GameVersion))
        {
            groups.Add($"[\"versions:{query.GameVersion.Trim()}\"]");
        }

        return "[" + string.Join(",", groups) + "]";
    }

    private static string ToIndex(ModSort sort) => sort switch
    {
        ModSort.Downloads => "downloads",
        ModSort.Updated => "updated",
        ModSort.Newest => "newest",
        _ => "relevance",
    };

    private static string BuildVersionCacheKey(string projectId, ModVersionQuery query)
    {
        var type = query.Type.ToModrinth();

        if (query.IncludeIncompatible)
        {
            return $"{type}_{projectId}_all";
        }

        var loader = query.Type switch
        {
            ModProjectType.DataPack => "datapack",
            _ when query.Type.UsesLoader() => ModLoaderMapping.ToModrinth(query.Loader) ?? "any",
            _ => "any",
        };

        var version = string.IsNullOrWhiteSpace(query.GameVersion) ? "any" : query.GameVersion.Trim();
        return $"{type}_{projectId}_{version}_{loader}";
    }

    private static ModProject ToProject(ModrinthDtos.SearchHit hit, ModProjectType type) => new()
    {
        Source = ModSource.Modrinth,
        ProjectType = type,
        Id = hit.ProjectId,
        Slug = hit.Slug,
        Title = hit.Title,
        Summary = hit.Description,
        Author = hit.Author,
        IconUrl = PreferPng(hit.IconUrl, null),
        WebsiteUrl = WebsiteUrl(type, hit.Slug),
        Downloads = hit.Downloads,
        UpdatedAt = hit.DateModified,
        Loaders = MapLoaders(hit.Categories, hit.Loaders),
        GameVersions = hit.Versions,
        Categories = hit.Categories.Where(c => ModLoaderMapping.FromModrinth(c) is null).ToList(),
    };

    private static ModProject ToProject(ModrinthDtos.ProjectDto dto)
    {
        var type = ModProjectTypes.FromModrinth(dto.ProjectType);

        return new ModProject
        {
            Source = ModSource.Modrinth,
            ProjectType = type,
            Id = dto.Id,
            Slug = dto.Slug,
            Title = dto.Title,
            Summary = dto.Description,
            Body = dto.Body,
            IconUrl = PreferPng(dto.IconUrl, dto.RawIconUrl),
            WebsiteUrl = WebsiteUrl(type, dto.Slug),
            Downloads = dto.Downloads,
            UpdatedAt = dto.Updated,
            Loaders = MapLoaders(dto.Categories, dto.Loaders),
            GameVersions = dto.GameVersions,
            Categories = dto.Categories
                .Concat(dto.AdditionalCategories)
                .Where(c => ModLoaderMapping.FromModrinth(c) is null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Gallery = dto.Gallery
                .Select(g => g.RawUrl ?? g.Url)
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .Select(u => u!)
                .ToList(),
        };
    }

    /// <summary>不同项目类型在 Modrinth 上是不同的路径段（/mod/、/shader/…）。</summary>
    private static string? WebsiteUrl(ModProjectType type, string? slug) =>
        slug is null ? null : $"https://modrinth.com/{type.ToModrinth()}/{slug}";

    /// <summary>
    /// 优先 PNG。Modrinth 的 icon_url 是 WebP，而 WinUI 的 Image 依赖系统 WIC 解码器，
    /// WebP 在 Win10 上不一定装了扩展包；raw_icon_url 一定是 PNG。
    /// </summary>
    internal static string? PreferPng(string? iconUrl, string? rawIconUrl)
    {
        if (!string.IsNullOrWhiteSpace(rawIconUrl))
        {
            return rawIconUrl;
        }

        return iconUrl;
    }

    /// <summary>搜索结果的加载器既可能在 categories 里，也可能在独立的 loaders 字段里。</summary>
    private static List<ModLoader> MapLoaders(IEnumerable<string> categories, IEnumerable<string>? loaders)
    {
        var result = new List<ModLoader>();

        foreach (var value in categories.Concat(loaders ?? []))
        {
            if (ModLoaderMapping.FromModrinth(value) is { } loader && !result.Contains(loader))
            {
                result.Add(loader);
            }
        }

        return result;
    }

    private static ModVersion? ToVersion(ModrinthDtos.VersionDto dto)
    {
        // 一个版本可能带多个文件，只取主文件；没标 primary 时取第一个
        var file = dto.Files.FirstOrDefault(f => f.Primary) ?? dto.Files.FirstOrDefault();
        if (file?.Url is null || file.Filename is null)
        {
            return null;
        }

        string? sha1 = null;
        file.Hashes?.TryGetValue("sha1", out sha1);

        return new ModVersion
        {
            Source = ModSource.Modrinth,
            Id = dto.Id,
            ProjectId = dto.ProjectId,
            Name = string.IsNullOrWhiteSpace(dto.Name) ? dto.VersionNumber : dto.Name,
            VersionNumber = dto.VersionNumber,
            File = new ModFile(file.Filename, file.Url, sha1, file.Size),
            GameVersions = dto.GameVersions,
            Loaders = dto.Loaders
                .Select(ModLoaderMapping.FromModrinth)
                .Where(l => l is not null)
                .Select(l => l!.Value)
                .Distinct()
                .ToList(),
            Dependencies = dto.Dependencies
                .Where(d => !string.IsNullOrWhiteSpace(d.ProjectId))
                .Select(d => new ModDependency(d.ProjectId!, d.VersionId, MapDependency(d.DependencyType)))
                .ToList(),
            ReleaseType = dto.VersionType?.ToLowerInvariant() switch
            {
                "beta" => ModReleaseType.Beta,
                "alpha" => ModReleaseType.Alpha,
                _ => ModReleaseType.Release,
            },
            PublishedAt = dto.DatePublished,
            Downloads = dto.Downloads,
            Changelog = dto.Changelog,
        };
    }

    internal static ModDependencyKind MapDependency(string? type) => type?.ToLowerInvariant() switch
    {
        "required" => ModDependencyKind.Required,
        "optional" => ModDependencyKind.Optional,
        "incompatible" => ModDependencyKind.Incompatible,
        "embedded" => ModDependencyKind.Embedded,
        _ => ModDependencyKind.Other,
    };

    /// <summary>把 Modrinth 的英文分类名换成界面上的中文名。未收录的保持原样。</summary>
    internal static string DisplayCategory(string slug) => slug switch
    {
        "adventure" => "冒险",
        "cursed" => "整活",
        "decoration" => "装饰",
        "economy" => "经济",
        "equipment" => "装备",
        "food" => "食物",
        "game-mechanics" => "机制",
        "library" => "前置库",
        "magic" => "魔法",
        "management" => "管理",
        "minigame" => "小游戏",
        "mobs" => "生物",
        "optimization" => "性能优化",
        "social" => "社交",
        "storage" => "存储",
        "technology" => "科技",
        "transportation" => "交通",
        "utility" => "实用",
        "worldgen" => "世界生成",
        _ => slug,
    };
}
