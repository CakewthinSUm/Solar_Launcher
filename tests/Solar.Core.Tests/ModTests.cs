using Solar.Core.Instances;
using Solar.Core.Mods;
using Solar.Core.Paths;
using Solar.Core.Settings;
using Xunit;

namespace Solar.Core.Tests;

/// <summary>镜像地址换算：官方 → 镜像的路径规则，以及按偏好排序。</summary>
public class ModEndpointsTests
{
    private static ModEndpoints Create(
        string? mirror = "https://mod.mcimirror.top",
        ModSourcePreference preference = ModSourcePreference.Auto,
        string? curseForgeKey = null)
    {
        var settings = new FakeSettings();
        settings.Current.ModMirrorUrl = mirror;
        settings.Current.ModSourcePreference = preference;
        settings.Current.CurseForgeApiKey = curseForgeKey;
        return new ModEndpoints(settings);
    }

    [Fact]
    public void ModrinthApiGoesUnderModrinthPrefix()
    {
        var endpoints = Create();

        Assert.Equal(
            "https://mod.mcimirror.top/modrinth/v2/search?query=sodium",
            endpoints.RewriteToMirror(new Uri("https://api.modrinth.com/v2/search?query=sodium")).ToString());
    }

    [Fact]
    public void CurseForgeApiGoesUnderCurseForgePrefix()
    {
        var endpoints = Create();

        Assert.Equal(
            "https://mod.mcimirror.top" + "/curseforge" + "/v1/mods/search?gameId=432",
            endpoints.RewriteToMirror(new Uri("https://api.curseforge.com/v1/mods/search?gameId=432")).ToString());
    }

    [Theory]
    [InlineData("https://cdn.modrinth.com/data/AANobbMI/icon.png")]
    [InlineData("https://edge.forgecdn.net/files/9092/537/wick.jar")]
    [InlineData("https://mediafilez.forgecdn.net/files/9092/537/wick.jar")]
    [InlineData("https://media.forgecdn.net/avatars/862/389/logo.png")]
    public void CdnHostsKeepTheirPathUnderTheMirrorRoot(string url)
    {
        var endpoints = Create();
        var original = new Uri(url);

        var rewritten = endpoints.RewriteToMirror(original);

        Assert.Equal("mod.mcimirror.top", rewritten.Host);
        Assert.Equal(original.AbsolutePath, rewritten.AbsolutePath);
    }

    [Fact]
    public void UnknownHostIsLeftAlone()
    {
        var endpoints = Create();
        var original = new Uri("https://example.com/a/b");

        Assert.Equal(original, endpoints.RewriteToMirror(original));
    }

    [Fact]
    public void NoMirrorMeansNoRewrite()
    {
        var endpoints = Create(mirror: null);
        var original = new Uri("https://api.modrinth.com/v2/search");

        Assert.Equal(original, endpoints.RewriteToMirror(original));

        // ApiCandidates 返回的是 API 根地址，不是某条具体请求
        var candidate = Assert.Single(endpoints.ApiCandidates(ModSource.Modrinth));
        Assert.Equal("https://api.modrinth.com/", candidate.ToString());
    }

    [Fact]
    public void AutoPreferenceTriesOfficialFirst()
    {
        var endpoints = Create(preference: ModSourcePreference.Auto);
        var candidates = endpoints.ApiCandidates(ModSource.Modrinth);

        Assert.Equal(2, candidates.Count);
        Assert.Equal("api.modrinth.com", candidates[0].Host);
        Assert.Equal("mod.mcimirror.top", candidates[1].Host);
    }

    [Fact]
    public void MirrorFirstPreferenceTriesMirrorFirst()
    {
        var endpoints = Create(preference: ModSourcePreference.MirrorFirst);
        var candidates = endpoints.ApiCandidates(ModSource.Modrinth);

        Assert.Equal("mod.mcimirror.top", candidates[0].Host);
        Assert.Equal("api.modrinth.com", candidates[1].Host);
    }

    [Fact]
    public void MirrorOnlyNeverTouchesOfficial()
    {
        var endpoints = Create(preference: ModSourcePreference.MirrorOnly);

        Assert.All(endpoints.ApiCandidates(ModSource.Modrinth),
            u => Assert.Equal("mod.mcimirror.top", u.Host));
    }

    [Fact]
    public void CurseForgeOfficialIsSkippedWithoutApiKey()
    {
        // 没有 Key 时官方 API 必然 403，白跑一趟还拖慢首屏
        var endpoints = Create(preference: ModSourcePreference.Auto, curseForgeKey: null);
        var candidates = endpoints.ApiCandidates(ModSource.CurseForge);

        Assert.Single(candidates);
        Assert.Equal("mod.mcimirror.top", candidates[0].Host);
    }

    [Fact]
    public void CurseForgeOfficialIsUsedWhenApiKeyPresent()
    {
        var endpoints = Create(preference: ModSourcePreference.Auto, curseForgeKey: "abc123");
        var candidates = endpoints.ApiCandidates(ModSource.CurseForge);

        Assert.Equal(2, candidates.Count);
        Assert.Equal("api.curseforge.com", candidates[0].Host);
    }

    [Fact]
    public void RequestCandidatesKeepTheFullPathAndQuery()
    {
        // 曾把 API 根地址当成请求地址发出去，结果打到了 /curseforge/ 根路径，
        // 拿回一个 HTML 页面，反序列化直接失败。
        var endpoints = Create(preference: ModSourcePreference.MirrorOnly);
        var official = new Uri("https://api.curseforge.com/v1/mods/search?gameId=432&classId=6");

        var candidate = Assert.Single(endpoints.RequestCandidates(official, ModSource.CurseForge));

        Assert.Equal("/curseforge" + "/v1/mods/search", candidate.AbsolutePath);
        Assert.Equal("?gameId=432&classId=6", candidate.Query);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("ftp://example.com")]
    public void InvalidMirrorIsIgnored(string? value)
    {
        Assert.Null(ModEndpoints.ParseMirror(value));
    }

    [Fact]
    public void TrailingSlashIsTrimmed()
    {
        Assert.Equal(
            new Uri("https://mod.mcimirror.top"),
            ModEndpoints.ParseMirror("https://mod.mcimirror.top/"));
    }

    private sealed class FakeSettings : ISettingsService
    {
        public LauncherSettings Current { get; } = new();

        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>没有配置镜像，<see cref="ModEndpoints.Candidates"/> 只会返回官方地址。</summary>
    internal sealed class SettingsWithoutMirror : ISettingsService
    {
        public LauncherSettings Current { get; } = new() { ModMirrorUrl = null };

        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}

/// <summary>两个源之间的加载器标识换算。</summary>
public class ModLoaderMappingTests
{
    [Theory]
    [InlineData(ModLoader.Forge, 1, "forge")]
    [InlineData(ModLoader.Fabric, 4, "fabric")]
    [InlineData(ModLoader.Quilt, 5, "quilt")]
    [InlineData(ModLoader.NeoForge, 6, "neoforge")]
    public void RoundTripsThroughBothSources(ModLoader loader, int curseForge, string modrinth)
    {
        Assert.Equal(curseForge, ModLoaderMapping.ToCurseForge(loader));
        Assert.Equal(modrinth, ModLoaderMapping.ToModrinth(loader));
        Assert.Equal(loader, ModLoaderMapping.FromCurseForge(curseForge));
        Assert.Equal(loader, ModLoaderMapping.FromModrinth(modrinth));
    }

    [Fact]
    public void VanillaMapsToNothing()
    {
        Assert.Null(ModLoaderMapping.ToCurseForge(ModLoader.Vanilla));
        Assert.Null(ModLoaderMapping.ToModrinth(ModLoader.Vanilla));
    }

    [Fact]
    public void UnknownModrinthLoaderIsNotGuessed()
    {
        // legacy-fabric / babric 之类不是本启动器支持的加载器
        Assert.Null(ModLoaderMapping.FromModrinth("legacy-fabric"));
        Assert.Null(ModLoaderMapping.FromModrinth("babric"));
        Assert.Null(ModLoaderMapping.FromCurseForge(99));
    }

    [Theory]
    [InlineData("Forge", ModLoader.Forge)]
    [InlineData("neoforge", ModLoader.NeoForge)]
    [InlineData("Fabric", ModLoader.Fabric)]
    [InlineData("Client", null)]
    [InlineData("1.21.4", null)]
    public void CurseForgeGameVersionsDistinguishLoadersFromVersions(string value, ModLoader? expected)
    {
        Assert.Equal(expected, ModLoaderMapping.FromCurseForgeGameVersion(value));
    }

    [Fact]
    public void MatchesAcceptsMatchingVersionAndLoader()
    {
        var version = new ModVersion
        {
            Source = ModSource.Modrinth,
            Id = "v",
            ProjectId = "p",
            Name = "n",
            File = new ModFile("a.jar", "https://x/a.jar", null, 1),
            GameVersions = ["1.21.4"],
            Loaders = [ModLoader.Fabric],
        };

        Assert.True(ModLoaderMapping.Matches(version, "1.21.4", ModLoader.Fabric));
        Assert.False(ModLoaderMapping.Matches(version, "1.20.1", ModLoader.Fabric));
        Assert.False(ModLoaderMapping.Matches(version, "1.21.4", ModLoader.Forge));
    }

    [Fact]
    public void MatchesIsLenientWhenVersionDoesNotDeclareLoader()
    {
        // 1.12 时代的老模组大多不标加载器，严格过滤会把它们全部排除
        var version = new ModVersion
        {
            Source = ModSource.CurseForge,
            Id = "v",
            ProjectId = "p",
            Name = "n",
            File = new ModFile("a.jar", "https://x/a.jar", null, 1),
            GameVersions = ["1.12.2"],
            Loaders = [],
        };

        Assert.True(ModLoaderMapping.Matches(version, "1.12.2", ModLoader.Forge));
    }

    [Fact]
    public void MatchesIgnoresFiltersWhenNotSpecified()
    {
        var version = new ModVersion
        {
            Source = ModSource.Modrinth,
            Id = "v",
            ProjectId = "p",
            Name = "n",
            File = new ModFile("a.jar", "https://x/a.jar", null, 1),
            GameVersions = ["1.21.4"],
            Loaders = [ModLoader.Fabric],
        };

        Assert.True(ModLoaderMapping.Matches(version, null, null));
        Assert.True(ModLoaderMapping.Matches(version, "1.21.4", ModLoader.Vanilla));
    }
}

/// <summary>Modrinth 的 facets 拼接。</summary>
public class ModrinthFacetTests
{
    [Fact]
    public void BareQueryOnlyFiltersProjectType()
    {
        var facets = ModrinthCatalog.BuildFacets(new ModSearchQuery());

        Assert.Equal("[[\"project_type:mod\"]]", facets);
    }

    [Fact]
    public void LoaderGameVersionAndCategoryBecomeSeparateAndGroups()
    {
        var facets = ModrinthCatalog.BuildFacets(new ModSearchQuery
        {
            GameVersion = "1.21.4",
            Loader = ModLoader.Fabric,
            Category = new ModCategory("optimization", "性能优化", ModSource.Modrinth),
        });

        Assert.Equal(
            "[[\"project_type:mod\"],[\"categories:fabric\"],[\"categories:optimization\"],[\"versions:1.21.4\"]]",
            facets);
    }

    [Fact]
    public void CategoryFromTheOtherSourceIsIgnored()
    {
        // CurseForge 的分类是数字 id，塞进 Modrinth 的 facet 会一个结果都搜不到
        var facets = ModrinthCatalog.BuildFacets(new ModSearchQuery
        {
            Category = new ModCategory("6814", "性能优化", ModSource.CurseForge),
        });

        Assert.Equal("[[\"project_type:mod\"]]", facets);
    }

    [Fact]
    public void VanillaLoaderAddsNoFacet()
    {
        var facets = ModrinthCatalog.BuildFacets(new ModSearchQuery { Loader = ModLoader.Vanilla });

        Assert.Equal("[[\"project_type:mod\"]]", facets);
    }

    [Fact]
    public void PngIconIsPreferredOverWebp()
    {
        // WinUI 的 Image 走 WIC 解码，WebP 在 Win10 上不一定有编解码器
        Assert.Equal(
            "https://cdn.modrinth.com/data/x/abc.png",
            ModrinthCatalog.PreferPng("https://cdn.modrinth.com/data/x/abc_96.webp",
                "https://cdn.modrinth.com/data/x/abc.png"));
    }

    [Fact]
    public void FallsBackToWebpWhenNoRawIcon()
    {
        const string webp = "https://cdn.modrinth.com/data/x/abc_96.webp";
        Assert.Equal(webp, ModrinthCatalog.PreferPng(webp, null));
    }

    [Theory]
    [InlineData("required", ModDependencyKind.Required)]
    [InlineData("optional", ModDependencyKind.Optional)]
    [InlineData("incompatible", ModDependencyKind.Incompatible)]
    [InlineData("embedded", ModDependencyKind.Embedded)]
    [InlineData("something-new", ModDependencyKind.Other)]
    public void DependencyTypesMapToTheUnifiedEnum(string raw, ModDependencyKind expected)
    {
        Assert.Equal(expected, ModrinthCatalog.MapDependency(raw));
    }

    [Fact]
    public void SearchHitJsonUsesSnakeCase()
    {
        // Modrinth 的字段是 project_id / total_hits 这种下划线风格。
        // 用 camelCase 策略反序列化不会报错，只会静默地全变成默认值——
        // 曾经因此导致每条结果的 Id 为空、下载量为 0，去重时还把整页合并成一条。
        const string json = """
            {
              "hits": [
                {
                  "project_id": "AANobbMI",
                  "slug": "sodium",
                  "title": "Sodium",
                  "description": "渲染优化",
                  "downloads": 123,
                  "date_modified": "2026-10-16T01:51:36Z",
                  "categories": ["fabric", "optimization"],
                  "versions": ["1.21.4"]
                }
              ],
              "offset": 0,
              "limit": 10,
              "total_hits": 42
            }
            """;

        var page = System.Text.Json.JsonSerializer
            .Deserialize<ModrinthDtos.SearchResponse>(json, ModrinthCatalog.Json)!;

        var hit = Assert.Single(page.Hits);
        Assert.Equal("AANobbMI", hit.ProjectId);
        Assert.Equal(123, hit.Downloads);
        Assert.Equal(42, page.TotalHits);
        Assert.NotNull(hit.DateModified);
    }

    [Fact]
    public void VersionJsonReadsFilesHashesAndDependencies()
    {
        const string json = """
            [
              {
                "id": "c3YkZvne",
                "project_id": "AANobbMI",
                "name": "Sodium 0.6.13",
                "version_number": "mc1.21.4-0.6.13",
                "version_type": "release",
                "game_versions": ["1.21.4"],
                "loaders": ["fabric"],
                "date_published": "2025-04-04T00:09:49Z",
                "dependencies": [
                  { "project_id": "YL57xq9U", "version_id": null, "dependency_type": "required" }
                ],
                "files": [
                  {
                    "url": "https://cdn.modrinth.com/a.jar",
                    "filename": "sodium.jar",
                    "size": 1306799,
                    "primary": true,
                    "hashes": { "sha1": "c881d2db" }
                  }
                ]
              }
            ]
            """;

        var dtos = System.Text.Json.JsonSerializer
            .Deserialize<List<ModrinthDtos.VersionDto>>(json, ModrinthCatalog.Json)!;

        var dto = Assert.Single(dtos);
        Assert.Equal("c3YkZvne", dto.Id);
        Assert.Equal("AANobbMI", dto.ProjectId);
        Assert.Equal("c881d2db", dto.Files[0].Hashes!["sha1"]);
        Assert.Equal(ModDependencyKind.Required, ModrinthCatalog.MapDependency(dto.Dependencies[0].DependencyType));
    }
}

/// <summary>CurseForge 的参数与字段映射。</summary>
public class CurseForgeMappingTests
{
    [Theory]
    [InlineData("1.21.4", true)]
    [InlineData("26.1", true)]
    [InlineData("Forge", false)]
    [InlineData("NeoForge", false)]
    [InlineData("Client", false)]
    [InlineData("", false)]
    public void GameVersionsAreToldApartFromLoaderNames(string value, bool expected)
    {
        Assert.Equal(expected, CurseForgeCatalog.LooksLikeGameVersion(value));
    }

    [Theory]
    [InlineData(1, ModDependencyKind.Embedded)]
    [InlineData(2, ModDependencyKind.Optional)]
    [InlineData(3, ModDependencyKind.Required)]
    [InlineData(4, ModDependencyKind.Other)]
    [InlineData(5, ModDependencyKind.Incompatible)]
    public void RelationTypesMapToTheUnifiedEnum(int relation, ModDependencyKind expected)
    {
        Assert.Equal(expected, CurseForgeCatalog.MapRelation(relation));
    }

    [Theory]
    [InlineData(ModSort.Downloads, 6)]
    [InlineData(ModSort.Updated, 3)]
    [InlineData(ModSort.Newest, 11)]
    [InlineData(ModSort.Relevance, 2)]
    public void SortMapsToCurseForgeFieldIds(ModSort sort, int expected)
    {
        Assert.Equal(expected, CurseForgeCatalog.ToSortField(sort));
    }
}

/// <summary>跨源去重与版本挑选。</summary>
public class ModCatalogServiceTests
{
    private static ModProject Project(
        ModSource source,
        string id,
        string title,
        long downloads = 100,
        DateTimeOffset? updated = null,
        params ModLoader[] loaders) => new()
    {
        Source = source,
        Id = id,
        Title = title,
        Downloads = downloads,
        UpdatedAt = updated ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        Loaders = loaders,
    };

    [Fact]
    public void SameTitleOnBothSourcesIsMerged()
    {
        var curseForge = Project(ModSource.CurseForge, "1", "Sodium", 200, loaders: ModLoader.Fabric);
        var modrinth = Project(ModSource.Modrinth, "AANobbMI", "Sodium", 500, loaders: ModLoader.Fabric);

        var merged = ModCatalogService.Deduplicate([curseForge, modrinth]);

        var single = Assert.Single(merged);
        Assert.Equal(ModSource.CurseForge, single.Source);
        Assert.Equal(500, single.Downloads);   // 取两边较大的下载量
        Assert.Single(single.AlsoOn);
        Assert.Equal(ModSource.Modrinth, single.AlsoOn[0].Source);
    }

    [Fact]
    public void DifferentTitlesStaySeparate()
    {
        var a = Project(ModSource.CurseForge, "1", "Sodium");
        var b = Project(ModSource.Modrinth, "2", "Sodium Extra");

        Assert.Equal(2, ModCatalogService.Deduplicate([a, b]).Count);
    }

    [Fact]
    public void PunctuationAndCaseDoNotPreventMatching()
    {
        var a = Project(ModSource.CurseForge, "1", "JEI (Just Enough Items)");
        var b = Project(ModSource.Modrinth, "2", "jei just enough items");

        Assert.Single(ModCatalogService.Deduplicate([a, b]));
    }

    [Fact]
    public void DisjointLoadersMeanDifferentProjects()
    {
        var a = Project(ModSource.CurseForge, "1", "Sodium", loaders: ModLoader.Forge);
        var b = Project(ModSource.Modrinth, "2", "Sodium", loaders: ModLoader.Fabric);

        Assert.Equal(2, ModCatalogService.Deduplicate([a, b]).Count);
    }

    [Fact]
    public void UpdatesFarApartAreNotTheSameProject()
    {
        var a = Project(ModSource.CurseForge, "1", "Sodium", updated: new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var b = Project(ModSource.Modrinth, "2", "Sodium", updated: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(2, ModCatalogService.Deduplicate([a, b]).Count);
    }

    [Fact]
    public void SameSourceDifferentIdsAreNeverMerged()
    {
        var a = Project(ModSource.Modrinth, "1", "Sodium");
        var b = Project(ModSource.Modrinth, "2", "Sodium");

        Assert.Equal(2, ModCatalogService.Deduplicate([a, b]).Count);
    }

    [Fact]
    public void EntriesWithEmptyIdsAreNeverMerged()
    {
        // 字段名对不上时（例如 API 改成 snake_case 而反序列化仍按 camelCase），
        // 所有条目的 Id 都会是空串。如果按“Id 相同”判定，一整页结果会被合并成一条。
        var a = Project(ModSource.Modrinth, "", "Sodium");
        var b = Project(ModSource.Modrinth, "", "Sodium");
        var c = Project(ModSource.Modrinth, "", "Iris");

        Assert.Equal(3, ModCatalogService.Deduplicate([a, b, c]).Count);
    }

    [Fact]
    public void BestVersionPrefersReleaseOverBeta()
    {
        var release = Version("r", ModReleaseType.Release, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var beta = Version("b", ModReleaseType.Beta, new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal("r", ModCatalogService.PickBestVersion([beta, release])!.Id);
    }

    [Fact]
    public void BestVersionFallsBackToNewestWhenSameChannel()
    {
        var older = Version("a", ModReleaseType.Release, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var newer = Version("b", ModReleaseType.Release, new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal("b", ModCatalogService.PickBestVersion([older, newer])!.Id);
    }

    [Fact]
    public void BestVersionOfEmptyListIsNull()
    {
        Assert.Null(ModCatalogService.PickBestVersion([]));
    }

    private static ModVersion Version(string id, ModReleaseType type, DateTimeOffset published) => new()
    {
        Source = ModSource.Modrinth,
        Id = id,
        ProjectId = "p",
        Name = id,
        File = new ModFile("a.jar", "https://x/a.jar", null, 1),
        ReleaseType = type,
        PublishedAt = published,
    };
}

/// <summary>缓存读写与过期。</summary>
public class ModCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "solar-modcache-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ModCache _cache;

    public ModCacheTests()
    {
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        _cache = new ModCache(paths, Microsoft.Extensions.Logging.Abstractions.NullLogger<ModCache>.Instance);
    }

    [Fact]
    public async Task ProjectSurvivesRoundTrip()
    {
        var project = new ModProject
        {
            Source = ModSource.Modrinth,
            Id = "AANobbMI",
            Title = "Sodium",
            Summary = "渲染优化",
            Downloads = 123,
            Loaders = [ModLoader.Fabric],
        };

        await _cache.WriteProjectAsync(project);
        var loaded = await _cache.ReadProjectAsync(ModSource.Modrinth, "AANobbMI");

        Assert.NotNull(loaded);
        Assert.Equal("Sodium", loaded!.Title);
        Assert.Equal(123, loaded.Downloads);
        Assert.Equal([ModLoader.Fabric], loaded.Loaders);
    }

    [Fact]
    public async Task MissingEntryIsAMissNotAnError()
    {
        Assert.Null(await _cache.ReadProjectAsync(ModSource.CurseForge, "999"));
    }

    [Fact]
    public async Task CorruptFileIsTreatedAsMiss()
    {
        await _cache.WriteProjectAsync(new ModProject
        {
            Source = ModSource.Modrinth,
            Id = "x",
            Title = "x",
        });

        // 找到写出来的文件并写坏它
        var file = Directory.GetFiles(Path.Combine(_root, "SolarData", "modcache", "project")).Single();
        await File.WriteAllTextAsync(file, "{ this is not json");

        Assert.Null(await _cache.ReadProjectAsync(ModSource.Modrinth, "x"));
    }

    [Fact]
    public async Task ExpiredEntryIsAMiss()
    {
        await _cache.WriteVersionsAsync(ModSource.Modrinth, "p_1.21.4_fabric", []);
        var file = Directory.GetFiles(Path.Combine(_root, "SolarData", "modcache", "versions")).Single();
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(-2));   // 版本列表有效期 1 小时

        Assert.Null(await _cache.ReadVersionsAsync(ModSource.Modrinth, "p_1.21.4_fabric"));
    }

    [Fact]
    public async Task CacheKeyWithDotsAndSlashIsSafeAsFileName()
    {
        await _cache.WriteVersionsAsync(ModSource.CurseForge, "238222_1.21.4/neoforge", []);

        var files = Directory.GetFiles(Path.Combine(_root, "SolarData", "modcache", "versions"));
        Assert.Single(files);
        Assert.DoesNotContain('/', Path.GetFileName(files[0]));
    }

    [Fact]
    public async Task SourcesDoNotCollide()
    {
        await _cache.WriteCategoriesAsync(ModSource.Modrinth, [new ModCategory("a", "A", ModSource.Modrinth)]);
        await _cache.WriteCategoriesAsync(ModSource.CurseForge, [new ModCategory("1", "B", ModSource.CurseForge)]);

        var modrinth = await _cache.ReadCategoriesAsync(ModSource.Modrinth);
        var curseForge = await _cache.ReadCategoriesAsync(ModSource.CurseForge);

        Assert.Equal("A", Assert.Single(modrinth!).Name);
        Assert.Equal("B", Assert.Single(curseForge!).Name);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

/// <summary>
/// 版本列表的缓存语义。缓存里存的是过滤后的完整列表，Limit 只在返回时截断——
/// 否则先来一次 Limit=1 的调用（例如安装清单里查前置）会把缓存写成一条，
/// 详情页之后要 100 条也只能拿到一条，而且这个错误状态会持续一小时。
/// </summary>
public class ModVersionCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "solar-modver-" + Guid.NewGuid().ToString("N")[..8]);

    public ModVersionCacheTests()
    {
        new AppPaths(_root).EnsureCreated();
    }

    [Fact]
    public async Task SmallLimitDoesNotPoisonTheCache()
    {
        var handler = new CountingHandler(ThreeVersionsJson);
        var catalog = CreateCatalog(handler);

        var first = await catalog.GetVersionsAsync("AANobbMI", new ModVersionQuery { Limit = 1 });
        var second = await catalog.GetVersionsAsync("AANobbMI", new ModVersionQuery { Limit = 10 });

        Assert.Single(first);
        Assert.Equal(3, second.Count);

        // 第二次必须命中缓存，不能再发一次请求
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task DifferentFiltersUseDifferentCacheEntries()
    {
        var handler = new CountingHandler(ThreeVersionsJson);
        var catalog = CreateCatalog(handler);

        await catalog.GetVersionsAsync("AANobbMI", new ModVersionQuery { GameVersion = "1.21.4", Limit = 10 });
        await catalog.GetVersionsAsync("AANobbMI", new ModVersionQuery { IncludeIncompatible = true, Limit = 10 });

        Assert.Equal(2, handler.Requests);
    }

    private ModrinthCatalog CreateCatalog(HttpMessageHandler handler)
    {
        var paths = new AppPaths(_root);
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<ModHttp>.Instance;

        // 不要镜像：请求直接打到假 handler，ModEndpoints 的候选列表只有一条
        var settings = new ModEndpointsTests.SettingsWithoutMirror();
        var http = new ModHttp(new HttpClient(handler), new ModEndpoints(settings), logger);
        var cache = new ModCache(paths, Microsoft.Extensions.Logging.Abstractions.NullLogger<ModCache>.Instance);

        return new ModrinthCatalog(http, cache);
    }

    /// <summary>三个版本的 Modrinth 响应，字段名与真实接口一致（下划线风格）。</summary>
    private const string ThreeVersionsJson = """
    [
      {
        "id": "v3", "project_id": "AANobbMI", "name": "Sodium 3", "version_number": "0.6.13",
        "version_type": "release", "date_published": "2026-10-01T00:00:00Z", "downloads": 10,
        "game_versions": ["1.21.4"], "loaders": ["fabric"],
        "dependencies": [],
        "files": [
          { "url": "https://cdn.modrinth.com/a/sodium-3.jar", "filename": "sodium-3.jar",
            "primary": true, "size": 100, "hashes": { "sha1": "aaa" } }
        ]
      },
      {
        "id": "v2", "project_id": "AANobbMI", "name": "Sodium 2", "version_number": "0.6.12",
        "version_type": "beta", "date_published": "2026-09-01T00:00:00Z", "downloads": 20,
        "game_versions": ["1.21.4"], "loaders": ["fabric"],
        "dependencies": [],
        "files": [
          { "url": "https://cdn.modrinth.com/a/sodium-2.jar", "filename": "sodium-2.jar",
            "primary": true, "size": 100, "hashes": { "sha1": "bbb" } }
        ]
      },
      {
        "id": "v1", "project_id": "AANobbMI", "name": "Sodium 1", "version_number": "0.6.11",
        "version_type": "release", "date_published": "2026-08-01T00:00:00Z", "downloads": 30,
        "game_versions": ["1.21.4"], "loaders": ["fabric"],
        "dependencies": [],
        "files": [
          { "url": "https://cdn.modrinth.com/a/sodium-1.jar", "filename": "sodium-1.jar",
            "primary": true, "size": 100, "hashes": { "sha1": "ccc" } }
        ]
      }
    ]
    """;

    /// <summary>不关心请求内容，只数次数——用来断言缓存是否生效。</summary>
    private sealed class CountingHandler(string json) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

/// <summary>详情正文的极简 Markdown 解析。</summary>
public class MarkdownLiteTests
{
    [Fact]
    public void HeadingsParagraphsAndListsBecomeSeparateBlocks()
    {
        var blocks = Solar.Core.Text.MarkdownLite.Parse("""
        # Sodium

        A rendering engine.

        - Fast
        - Small
        """);

        Assert.Collection(
            blocks,
            b => Assert.Equal(Solar.Core.Text.MarkdownBlockKind.Heading, b.Kind),
            b => Assert.Equal(Solar.Core.Text.MarkdownBlockKind.Paragraph, b.Kind),
            b => Assert.Equal(Solar.Core.Text.MarkdownBlockKind.ListItem, b.Kind),
            b => Assert.Equal(Solar.Core.Text.MarkdownBlockKind.ListItem, b.Kind));
    }

    [Fact]
    public void SoftWrappedLinesAreJoinedIntoOneParagraph()
    {
        var blocks = Solar.Core.Text.MarkdownLite.Parse("line one\nline two\n\nnext");

        Assert.Equal(2, blocks.Count);
        Assert.Equal("line one line two", blocks[0].Text);
    }

    [Fact]
    public void InlineBoldCodeAndLinksAreSplit()
    {
        var spans = Solar.Core.Text.MarkdownLite.ParseInline("a **b** `c` [d](https://example.com)");

        Assert.Collection(
            spans,
            s => Assert.Equal(("a ", Solar.Core.Text.MarkdownSpanKind.Plain), (s.Text, s.Kind)),
            s => Assert.Equal(("b", Solar.Core.Text.MarkdownSpanKind.Bold), (s.Text, s.Kind)),
            s => Assert.Equal((" ", Solar.Core.Text.MarkdownSpanKind.Plain), (s.Text, s.Kind)),
            s => Assert.Equal(("c", Solar.Core.Text.MarkdownSpanKind.Code), (s.Text, s.Kind)),
            s => Assert.Equal((" ", Solar.Core.Text.MarkdownSpanKind.Plain), (s.Text, s.Kind)),
            s =>
            {
                Assert.Equal(("d", Solar.Core.Text.MarkdownSpanKind.Link), (s.Text, s.Kind));
                Assert.Equal("https://example.com/", s.Url);
            });
    }

    [Fact]
    public void ImagesAreDropped()
    {
        var blocks = Solar.Core.Text.MarkdownLite.Parse("before ![shot](https://x/a.png) after");

        Assert.Equal("before after", Assert.Single(blocks).Text);
        Assert.DoesNotContain(blocks, b => b.Text.Contains("a.png"));
    }

    [Fact]
    public void LinkedBadgeImagesLoseTheWholeMarkup()
    {
        // Modrinth 的 README 顶部几乎都是这种：一张图片套在链接里
        var blocks = Solar.Core.Text.MarkdownLite.Parse(
            "[![Build](https://x/badge.svg)](https://x/ci) keep this");

        var text = Assert.Single(blocks).Text;
        Assert.Equal("keep this", text);
        Assert.DoesNotContain("https://x", text);
    }

    [Fact]
    public void InlineHtmlIsRemoved()
    {
        var blocks = Solar.Core.Text.MarkdownLite.Parse(
            "<center> <img src=\"https://x/a.png\"> </center>\n\nReal text");

        // 整行只有 HTML 的块被丢掉，只留真正的正文
        Assert.Equal("Real text", Assert.Single(blocks).Text);
    }

    [Fact]
    public void LessThanInProseSurvives()
    {
        var blocks = Solar.Core.Text.MarkdownLite.Parse("if a < b and c > d");

        Assert.Equal("if a < b and c > d", Assert.Single(blocks).Text);
    }

    [Fact]
    public void FencedCodeIsKeptVerbatim()
    {
        var blocks = Solar.Core.Text.MarkdownLite.Parse("```\n-x\n```");

        Assert.Equal(Solar.Core.Text.MarkdownBlockKind.Code, Assert.Single(blocks).Kind);
        Assert.Equal("-x", blocks[0].Text);
    }

    [Fact]
    public void EmptyInputYieldsNoBlocks()
    {
        Assert.Empty(Solar.Core.Text.MarkdownLite.Parse(null));
        Assert.Empty(Solar.Core.Text.MarkdownLite.Parse("   \n\n "));
    }
}
