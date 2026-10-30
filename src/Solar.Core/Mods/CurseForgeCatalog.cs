using System.Text.Json.Serialization;
using Solar.Core.Versions;

namespace Solar.Core.Mods;

/// <summary>CurseForge API 的原始 DTO。</summary>
internal static class CurseForgeDtos
{
    internal sealed class SearchResponse
    {
        public List<ModDto> Data { get; set; } = [];
        public PaginationDto? Pagination { get; set; }
    }

    internal sealed class ModListResponse
    {
        public List<ModDto> Data { get; set; } = [];
    }

    /// <summary>
    /// 单个项目接口返回的是对象而不是数组（批量接口才是数组），
    /// 两者不能共用一个 DTO，否则反序列化会直接失败。
    /// </summary>
    internal sealed class SingleModResponse
    {
        public ModDto? Data { get; set; }
    }

    internal sealed class PaginationDto
    {
        public int Index { get; set; }
        public int PageSize { get; set; }
        public int ResultCount { get; set; }
        public int TotalCount { get; set; }
    }

    internal sealed class ModDto
    {
        public long Id { get; set; }

        /// <summary>6=Mod、4471=整合包、12=资源包、6552=光影、6945=数据包。</summary>
        public long? ClassId { get; set; }

        public string Name { get; set; } = "";
        public string? Slug { get; set; }
        public string Summary { get; set; } = "";
        public long DownloadCount { get; set; }
        public DateTimeOffset? DateModified { get; set; }
        public DateTimeOffset? DateReleased { get; set; }
        public LogoDto? Logo { get; set; }
        public LinksDto? Links { get; set; }
        public List<CategoryDto> Categories { get; set; } = [];
        public List<AuthorDto> Authors { get; set; } = [];
        public List<ScreenshotDto> Screenshots { get; set; } = [];
        public List<FileDto> LatestFiles { get; set; } = [];
        public List<FileIndexDto> LatestFilesIndexes { get; set; } = [];
    }

    internal sealed class LogoDto
    {
        public string? ThumbnailUrl { get; set; }
        public string? Url { get; set; }
    }

    internal sealed class LinksDto
    {
        public string? WebsiteUrl { get; set; }
        public string? SourceUrl { get; set; }
        public string? IssuesUrl { get; set; }
    }

    internal sealed class CategoryDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string? Slug { get; set; }
        public int? ParentCategoryId { get; set; }
    }

    internal sealed class AuthorDto
    {
        public string Name { get; set; } = "";
    }

    internal sealed class ScreenshotDto
    {
        public string? ThumbnailUrl { get; set; }
        public string? Url { get; set; }
    }

    internal sealed class FileDto
    {
        public long Id { get; set; }
        public long ModId { get; set; }
        public string? DisplayName { get; set; }
        public string FileName { get; set; } = "";
        public int ReleaseType { get; set; }
        public long FileLength { get; set; }
        public string? DownloadUrl { get; set; }
        public DateTimeOffset? FileDate { get; set; }
        public long DownloadCount { get; set; }
        public List<string> GameVersions { get; set; } = [];
        public List<FileHashDto> Hashes { get; set; } = [];
        public List<FileDependencyDto> Dependencies { get; set; } = [];
        public bool IsAvailable { get; set; }
    }

    internal sealed class FileIndexDto
    {
        public string? GameVersion { get; set; }
        public long FileId { get; set; }
        public string? Filename { get; set; }
        public int ReleaseType { get; set; }

        /// <summary>CurseForge 的 ModLoaderType，与 <see cref="ModLoaderMapping.ToCurseForge"/> 同源。</summary>
        public int? ModLoader { get; set; }
    }

    internal sealed class FileHashDto
    {
        public int Algo { get; set; }
        public string Value { get; set; } = "";
    }

    internal sealed class FileDependencyDto
    {
        public long ModId { get; set; }
        public int RelationType { get; set; }
    }

    internal sealed class FilesResponse
    {
        public List<FileDto> Data { get; set; } = [];
        public PaginationDto? Pagination { get; set; }
    }

    internal sealed class CategoriesResponse
    {
        public List<CategoryDto> Data { get; set; } = [];
    }
}

/// <summary>
/// CurseForge 目录实现。
/// <para>
/// 与 Modrinth 的两处结构性差异：
/// 一是它把“版本”叫 file，且文件列表默认返回全部历史（需要显式用
/// <c>gameVersion</c> + <c>modLoaderType</c> 做服务端过滤）；
/// 二是加载器与游戏版本混在 <c>gameVersions</c> 数组里，需要拆开。
/// </para>
/// <para>
/// 官方 API 需要 Key，没有 Key 时 <see cref="ModEndpoints"/> 会把候选列表收窄成只有镜像。
/// </para>
/// </summary>
public sealed class CurseForgeCatalog(ModHttp http, ModCache cache) : IModCatalog
{
    private const int GameId = 432;     // 432 = Minecraft

    /// <summary>结尾斜杠必须保留，否则 <c>v1</c> 会被相对路径替换掉，全部请求 404。</summary>
    private static readonly Uri Base = new("https://api.curseforge.com/v1/");

    internal static readonly System.Text.Json.JsonSerializerOptions Json =
        new(System.Text.Json.JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

    public ModSource Source => ModSource.CurseForge;

    public async Task<ModSearchPage> SearchAsync(ModSearchQuery query, CancellationToken ct = default)
    {
        var limit = Math.Clamp(query.Limit, 1, 50);
        var builder = new System.Text.StringBuilder(
            "mods/search?gameId=" + GameId + "&classId=" + query.Type.ToCurseForgeClassId());
        builder.Append("&index=").Append(Math.Max(0, query.Offset));
        builder.Append("&pageSize=").Append(limit);
        builder.Append("&sortField=").Append(ToSortField(query.Sort));
        builder.Append("&sortOrder=desc");

        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            builder.Append("&searchFilter=").Append(Uri.EscapeDataString(query.Text.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(query.GameVersion))
        {
            builder.Append("&gameVersion=").Append(Uri.EscapeDataString(query.GameVersion.Trim()));
        }

        if (query.Type.UsesLoader() && ModLoaderMapping.ToCurseForge(query.Loader) is { } loaderType)
        {
            builder.Append("&modLoaderType=").Append(loaderType);
        }

        if (query.Category is { } category
            && category.Source == ModSource.CurseForge
            && int.TryParse(category.Key, out var categoryId))
        {
            builder.Append("&categoryId=").Append(categoryId);
        }

        var url = new Uri(Base, builder.ToString());
        var response = await http.GetCurseForgeJsonAsync<CurseForgeDtos.SearchResponse>(url, Json, ct)
            .ConfigureAwait(false);

        return new ModSearchPage
        {
            Items = response.Data.Select(dto => ToProject(dto, query.Type)).ToList(),
            TotalHits = response.Pagination?.TotalCount ?? response.Data.Count,
            Offset = response.Pagination?.Index ?? query.Offset,
            Limit = response.Pagination?.PageSize ?? limit,
        };
    }

    public async Task<ModProject> GetProjectAsync(string projectId, CancellationToken ct = default)
    {
        var cached = await cache.ReadProjectAsync(Source, projectId, ct).ConfigureAwait(false);
        if (cached is not null)
        {
            return cached;
        }

        var url = new Uri(Base, $"mods/{Uri.EscapeDataString(projectId)}");
        var response = await http.GetCurseForgeJsonAsync<CurseForgeDtos.SingleModResponse>(url, Json, ct)
            .ConfigureAwait(false);

        var dto = response.Data
                  ?? throw new InvalidOperationException($"CurseForge 上找不到项目 {projectId}。");

        var project = ToProject(dto, ModProjectTypes.FromCurseForgeClassId(dto.ClassId));
        await cache.WriteProjectAsync(project, ct).ConfigureAwait(false);
        return project;
    }

    public async Task<IReadOnlyList<ModVersion>> GetVersionsAsync(
        string projectId,
        ModVersionQuery query,
        CancellationToken ct = default)
    {
        var cacheKey = BuildVersionCacheKey(projectId, query);

        // 缓存里存的是过滤后的完整列表，Limit 只在返回时截断（理由同 ModrinthCatalog）
        var cached = await cache.ReadVersionsAsync(Source, cacheKey, ct).ConfigureAwait(false);
        if (cached is not null)
        {
            return cached.Take(Math.Max(1, query.Limit)).ToList();
        }

        // 加载器只对 Mod 有效；光影/资源包的 modLoaderType 会让服务端返回空列表
        var usesLoader = query.Type.UsesLoader();

        var builder = new System.Text.StringBuilder($"mods/{Uri.EscapeDataString(projectId)}/files?pageSize=50");

        // 服务端过滤能大幅缩小返回量：JEI 全量 3984 个文件 / 6MB，
        // 限定 1.21.4 + NeoForge 后只有 2 个。
        if (!query.IncludeIncompatible)
        {
            if (!string.IsNullOrWhiteSpace(query.GameVersion))
            {
                builder.Append("&gameVersion=").Append(Uri.EscapeDataString(query.GameVersion.Trim()));
            }

            if (usesLoader && ModLoaderMapping.ToCurseForge(query.Loader) is { } loaderType)
            {
                builder.Append("&modLoaderType=").Append(loaderType);
            }
        }

        var url = new Uri(Base, builder.ToString());
        var response = await http.GetCurseForgeJsonAsync<CurseForgeDtos.FilesResponse>(url, Json, ct)
            .ConfigureAwait(false);

        var versions = response.Data
            .Where(f => f.IsAvailable && !string.IsNullOrEmpty(f.DownloadUrl))
            .Select(ToVersion)
            .OrderByDescending(v => v.PublishedAt)
            .ToList();

        await cache.WriteVersionsAsync(Source, cacheKey, versions, ct).ConfigureAwait(false);
        return versions.Take(Math.Max(1, query.Limit)).ToList();
    }

    public async Task<IReadOnlyList<ModCategory>> GetCategoriesAsync(
        ModProjectType type = ModProjectType.Mod,
        CancellationToken ct = default)
    {
        var classId = type.ToCurseForgeClassId();
        var cacheKey = $"{Source}_{classId}";
        var cached = await cache.ReadCategoriesAsync(Source, ct, cacheKey).ConfigureAwait(false);
        if (cached is not null)
        {
            return cached;
        }

        var url = new Uri(Base, $"categories?gameId={GameId}&classId={classId}");
        var response = await http.GetCurseForgeJsonAsync<CurseForgeDtos.CategoriesResponse>(url, Json, ct)
            .ConfigureAwait(false);

        // 只保留顶层分类：子分类（parentCategoryId != classId）会让下拉框长到无法使用
        var categories = response.Data
            .Where(c => c.ParentCategoryId is null || c.ParentCategoryId == classId)
            .Select(c => new ModCategory(c.Id.ToString(), c.Name, Source))
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ToList();

        await cache.WriteCategoriesAsync(Source, categories, ct, cacheKey).ConfigureAwait(false);
        return categories;
    }

    /// <summary>CurseForge 的排序字段：1=精选、2=热度、3=最近更新、11=最近发布。</summary>
    internal static int ToSortField(ModSort sort) => sort switch
    {
        ModSort.Downloads => 6,   // 总下载量
        ModSort.Updated => 3,     // 最近更新
        ModSort.Newest => 11,     // 最近发布
        _ => 2,                   // 热度（综合）
    };

    private static string BuildVersionCacheKey(string projectId, ModVersionQuery query)
    {
        var type = query.Type.ToString().ToLowerInvariant();

        if (query.IncludeIncompatible)
        {
            return $"{type}_{projectId}_all";
        }

        var loader = query.Type.UsesLoader()
            ? ModLoaderMapping.ToCurseForge(query.Loader)?.ToString() ?? "any"
            : "any";
        var version = string.IsNullOrWhiteSpace(query.GameVersion) ? "any" : query.GameVersion.Trim();
        return $"{type}_{projectId}_{version}_{loader}";
    }

    private static ModProject ToProject(CurseForgeDtos.ModDto dto, ModProjectType type) => new()
    {
        Source = ModSource.CurseForge,
        ProjectType = type,
        Id = dto.Id.ToString(),
        Slug = dto.Slug,
        Title = dto.Name,
        Summary = dto.Summary,
        IconUrl = dto.Logo?.Url ?? dto.Logo?.ThumbnailUrl,
        WebsiteUrl = dto.Links?.WebsiteUrl?.TrimEnd('/'),
        Author = string.Join(", ", dto.Authors.Select(a => a.Name)),
        Downloads = dto.DownloadCount,

        // 搜索接口的 dateModified 对某些项目是同步时间而不是发布时间，
        // dateReleased 更接近用户理解的“更新于”。
        UpdatedAt = dto.DateReleased ?? dto.DateModified,
        Loaders = ExtractLoaders(dto),
        GameVersions = ExtractGameVersions(dto),
        Categories = dto.Categories.Select(c => c.Name).Distinct().ToList(),
        Gallery = dto.Screenshots
            .Select(s => s.Url ?? s.ThumbnailUrl)
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(u => u!)
            .ToList(),
    };

    /// <summary>
    /// CurseForge 没有独立的加载器字段，只能从 <c>latestFiles[].gameVersions</c>
    /// 与 <c>latestFilesIndexes[].modLoader</c> 里推断。
    /// </summary>
    private static List<ModLoader> ExtractLoaders(CurseForgeDtos.ModDto dto)
    {
        var result = new List<ModLoader>();

        foreach (var file in dto.LatestFiles)
        {
            foreach (var value in file.GameVersions)
            {
                if (ModLoaderMapping.FromCurseForgeGameVersion(value) is { } loader && !result.Contains(loader))
                {
                    result.Add(loader);
                }
            }
        }

        foreach (var index in dto.LatestFilesIndexes)
        {
            if (ModLoaderMapping.FromCurseForge(index.ModLoader) is { } loader && !result.Contains(loader))
            {
                result.Add(loader);
            }
        }

        return result;
    }

    /// <summary>同样从 <c>gameVersions</c> 里挑出真正的版本号（形如 1.21.4 / 26.1）。</summary>
    private static List<string> ExtractGameVersions(CurseForgeDtos.ModDto dto)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var value in dto.LatestFiles.SelectMany(f => f.GameVersions))
        {
            if (LooksLikeGameVersion(value))
            {
                result.Add(value);
            }
        }

        foreach (var value in dto.LatestFilesIndexes.Select(i => i.GameVersion))
        {
            if (value is not null && LooksLikeGameVersion(value))
            {
                result.Add(value);
            }
        }

        return result.ToList();
    }

    /// <summary>
    /// 版本号一定带点（<c>1.21.4</c>），而加载器名（Forge/Fabric/NeoForge）和
    /// 环境标记（Client/Server）都不带。用这一点区分，比维护一张排除表稳。
    /// </summary>
    internal static bool LooksLikeGameVersion(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Contains('.') && char.IsDigit(value[0]);

    private static ModVersion ToVersion(CurseForgeDtos.FileDto dto)
    {
        var sha1 = dto.Hashes?.FirstOrDefault(h => h.Algo == 1)?.Value;   // Algo 1 = SHA1

        return new ModVersion
        {
            Source = ModSource.CurseForge,
            Id = dto.Id.ToString(),
            ProjectId = dto.ModId.ToString(),
            Name = string.IsNullOrWhiteSpace(dto.DisplayName) ? dto.FileName : dto.DisplayName!,
            VersionNumber = dto.DisplayName ?? dto.FileName,
            File = new ModFile(dto.FileName, dto.DownloadUrl!, sha1, dto.FileLength),
            GameVersions = dto.GameVersions.Where(LooksLikeGameVersion).ToList(),
            Loaders = dto.GameVersions
                .Select(ModLoaderMapping.FromCurseForgeGameVersion)
                .Where(l => l is not null)
                .Select(l => l!.Value)
                .Distinct()
                .ToList(),
            Dependencies = dto.Dependencies
                .Where(d => d.ModId > 0)
                .Select(d => new ModDependency(d.ModId.ToString(), null, MapRelation(d.RelationType)))
                .ToList(),
            ReleaseType = dto.ReleaseType switch
            {
                2 => ModReleaseType.Beta,
                3 => ModReleaseType.Alpha,
                _ => ModReleaseType.Release,
            },
            PublishedAt = dto.FileDate,
            Downloads = dto.DownloadCount,
        };
    }

    /// <summary>RelationType：1=Embedded、2=Optional、3=Required、4=Tool、5=Incompatible、6=Include。</summary>
    internal static ModDependencyKind MapRelation(int relationType) => relationType switch
    {
        3 => ModDependencyKind.Required,
        2 => ModDependencyKind.Optional,
        5 => ModDependencyKind.Incompatible,
        1 => ModDependencyKind.Embedded,
        _ => ModDependencyKind.Other,
    };
}
