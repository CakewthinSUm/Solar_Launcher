using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Solar.Core.Instances;
using Solar.Core.Modpacks;
using Solar.Core.Mods;
using Solar.Core.Paths;
using Solar.Core.Settings;
using Xunit;

namespace Solar.Core.Tests;

/// <summary>五种项目类型在两个源上的标识，以及搜索 facet 的拼接。</summary>
public class AddonProjectTypeTests
{
    [Theory]
    [InlineData(ModProjectType.Mod, "mod", 6)]
    [InlineData(ModProjectType.Modpack, "modpack", 4471)]
    [InlineData(ModProjectType.ResourcePack, "resourcepack", 12)]
    [InlineData(ModProjectType.Shader, "shader", 6552)]
    [InlineData(ModProjectType.DataPack, "datapack", 6945)]
    public void TypesMapToModrinthSlugAndCurseForgeClassId(
        ModProjectType type, string slug, int classId)
    {
        Assert.Equal(slug, type.ToModrinth());
        Assert.Equal(classId, type.ToCurseForgeClassId());
        Assert.Equal(type, ModProjectTypes.FromModrinth(slug));
        Assert.Equal(type, ModProjectTypes.FromCurseForgeClassId(classId));
    }

    [Fact]
    public void UnknownSlugsFallBackToMod()
    {
        Assert.Equal(ModProjectType.Mod, ModProjectTypes.FromModrinth("plugin"));
        Assert.Equal(ModProjectType.Mod, ModProjectTypes.FromCurseForgeClassId(99));
    }

    [Fact]
    public void LoaderFacetIsOnlyAddedForMods()
    {
        // 光影的 categories 里没有 fabric/forge；带上加载器 facet 会直接搜空
        var shader = ModrinthCatalog.BuildFacets(new ModSearchQuery
        {
            Type = ModProjectType.Shader,
            Loader = ModLoader.Fabric,
            GameVersion = "1.21.4",
        });

        Assert.Equal("[[\"project_type:shader\"],[\"versions:1.21.4\"]]", shader);

        var mod = ModrinthCatalog.BuildFacets(new ModSearchQuery
        {
            Type = ModProjectType.Mod,
            Loader = ModLoader.Fabric,
        });

        Assert.Equal("[[\"project_type:mod\"],[\"categories:fabric\"]]", mod);
    }

    [Fact]
    public void ModpackFacetUsesItsOwnProjectType()
    {
        var facets = ModrinthCatalog.BuildFacets(new ModSearchQuery
        {
            Type = ModProjectType.Modpack,
            GameVersion = "1.20.1",
        });

        Assert.Equal("[[\"project_type:modpack\"],[\"versions:1.20.1\"]]", facets);
    }

    [Fact]
    public void CrossSourceDeduplicationNeverMergesDifferentTypes()
    {
        // 同名同作者的 Mod 与整合包是两个项目，合并后安装落点会完全错位
        var mod = Project(ModSource.CurseForge, "1", "Sodium", ModProjectType.Mod);
        var modpack = Project(ModSource.Modrinth, "2", "Sodium", ModProjectType.Modpack);

        Assert.Equal(2, ModCatalogService.Deduplicate([mod, modpack]).Count);
    }

    [Fact]
    public async Task NonModInstallPlanDoesNotExpandDependencies()
    {
        // 光影/资源包没有可安装的前置；依赖字段即使有内容也不该去查
        var service = new ModCatalogService([], NullLogger<ModCatalogService>.Instance);

        var project = Project(ModSource.Modrinth, "shader", "Complementary", ModProjectType.Shader);
        var version = new ModVersion
        {
            Source = ModSource.Modrinth,
            Id = "v",
            ProjectId = "shader",
            Name = "v1",
            File = new ModFile("shader.zip", "https://x/shader.zip", null, 1),
            Dependencies =
            [
                new ModDependency("other", null, ModDependencyKind.Required),
            ],
        };

        var plan = await service.BuildInstallPlanAsync(project, version, "1.21.4", null);

        var item = Assert.Single(plan.Items);
        Assert.Equal(ModProjectType.Shader, item.Project.ProjectType);
        Assert.True(item.IsAvailable);
    }

    private static ModProject Project(
        ModSource source, string id, string title, ModProjectType type) => new()
    {
        Source = source,
        Id = id,
        Title = title,
        ProjectType = type,
        Downloads = 100,
        UpdatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
    };
}

/// <summary>两种整合包 manifest 的解析，以及 zip 路径安全。</summary>
public class ModpackManifestTests
{
    private const string ModrinthIndex = """
        {
          "formatVersion": 1,
          "game": "minecraft",
          "versionId": "1.2.0",
          "name": "Test Pack",
          "summary": "A pack",
          "files": [
            {
              "path": "mods/a.jar",
              "hashes": { "sha1": "abc123", "sha512": "def456" },
              "env": { "client": "required", "server": "required" },
              "downloads": ["https://cdn.modrinth.com/a.jar"],
              "fileSize": 100
            },
            {
              "path": "mods/server-only.jar",
              "env": { "client": "unsupported" },
              "downloads": ["https://cdn.modrinth.com/server.jar"],
              "fileSize": 1
            },
            {
              "path": "mods/optional.jar",
              "env": { "client": "optional" },
              "downloads": ["https://cdn.modrinth.com/optional.jar"],
              "fileSize": 2
            },
            {
              "path": "../escape.jar",
              "downloads": ["https://cdn.modrinth.com/evil.jar"],
              "fileSize": 3
            }
          ],
          "dependencies": {
            "minecraft": "1.21.1",
            "fabric-loader": "0.16.0"
          }
        }
        """;

    private const string CurseForgeManifest = """
        {
          "minecraft": {
            "version": "1.12.2",
            "modLoaders": [
              { "id": "forge-14.23.5.2860", "primary": true }
            ]
          },
          "manifestType": "minecraftModpack",
          "manifestVersion": 1,
          "name": "RLCraft",
          "version": "2.9.3",
          "author": "Shivaxi",
          "files": [
            { "projectID": 284754, "fileID": 2725062, "required": true },
            { "projectID": 0, "fileID": 0, "required": false }
          ],
          "overrides": "overrides"
        }
        """;

    [Fact]
    public void ModrinthIndexIsParsedWithDependenciesAndEnvFiltering()
    {
        var data = ModpackManifestReader.ReadModrinthIndex(ModrinthIndex);

        Assert.Equal("Test Pack", data.Name);
        Assert.Equal("1.21.1", data.MinecraftVersion);
        Assert.Equal(ModLoader.Fabric, data.Loader);
        Assert.Equal("0.16.0", data.LoaderVersion);

        // server-only 被跳过，../escape.jar 进了失败清单
        Assert.Equal(2, data.Files.Count);
        Assert.Equal("mods/a.jar", data.Files[0].RelativePath);
        Assert.Equal("abc123", data.Files[0].Sha1);
        Assert.Equal("mods/optional.jar", data.Files[1].RelativePath);
        Assert.Single(data.FailedFiles);
    }

    [Fact]
    public void CurseForgeManifestIsParsedWithPrimaryLoader()
    {
        var data = ModpackManifestReader.ReadCurseForgeManifest(CurseForgeManifest);

        Assert.Equal("RLCraft", data.Name);
        Assert.Equal("1.12.2", data.MinecraftVersion);
        Assert.Equal(ModLoader.Forge, data.Loader);
        Assert.Equal("14.23.5.2860", data.LoaderVersion);
        Assert.Equal("overrides", data.OverridesFolder);

        // projectID/fileID 都为 0 的占位条目被丢掉
        var entry = Assert.Single(data.Files);
        Assert.Equal(284754, entry.ProjectId);
        Assert.Equal(2725062, entry.FileId);
    }

    [Theory]
    [InlineData("forge-14.23.5.2860", ModLoader.Forge, "14.23.5.2860")]
    [InlineData("fabric-0.15.11", ModLoader.Fabric, "0.15.11")]
    [InlineData("quilt-0.24.0", ModLoader.Quilt, "0.24.0")]
    [InlineData("neoforge-21.1.80", ModLoader.NeoForge, "21.1.80")]
    [InlineData("unknown-1.0", ModLoader.Vanilla, "1.0")]
    [InlineData("", ModLoader.Vanilla, null)]
    public void CurseForgeLoaderIdsAreSplit(string id, ModLoader loader, string? version)
    {
        Assert.Equal((loader, version), ModpackManifestReader.ParseLoaderId(id));
    }

    [Theory]
    [InlineData("mods/a.jar", true)]
    [InlineData("./mods/a.jar", true)]
    [InlineData("../evil.jar", false)]
    [InlineData("mods/../../evil.jar", false)]
    [InlineData("/etc/passwd", false)]
    [InlineData("C:/Windows/system32", false)]
    [InlineData("mods//a.jar", false)]
    [InlineData("", false)]
    public void UnsafeZipPathsAreRejected(string path, bool safe)
    {
        Assert.Equal(safe, ModpackManifestReader.IsSafeRelativePath(path));
    }

    [Fact]
    public void CombineUnderRootStaysInsideTheGameDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "solar-pack-" + Guid.NewGuid().ToString("N")[..8]);

        var inside = ModpackManifestReader.CombineUnderRoot(root, "mods/a.jar");
        Assert.NotNull(inside);
        Assert.StartsWith(Path.GetFullPath(root), inside);

        Assert.Null(ModpackManifestReader.CombineUnderRoot(root, "../evil.jar"));
        Assert.Null(ModpackManifestReader.CombineUnderRoot(root, "/abs/evil.jar"));
    }
}

/// <summary>安装落点：四种内容类型各自的目录，以及数据包的存档解析。</summary>
public class ContentInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "solar-content-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly ModInstaller _installer;
    private readonly InstanceGameDirectory _directories;

    public ContentInstallerTests()
    {
        var paths = new AppPaths(_root);
        paths.EnsureCreated();

        var settings = new FakeSettings();
        _directories = new InstanceGameDirectory(paths, settings);
        _installer = new ModInstaller(
            _directories,
            new ModFileDownloader(new ModEndpoints(settings), NullLogger<ModFileDownloader>.Instance),
            NullLogger<ModInstaller>.Instance);
    }

    private static GameInstance Instance() => new()
    {
        Id = "abc",
        Name = "test",
        MinecraftVersion = "1.21.4",
        VersionIsolation = true,
    };

    [Fact]
    public void EachTypeMapsToItsOwnFolderUnderSharedMinecraft()
    {
        var game = _directories.Shared;

        Assert.Equal(Path.Combine(game, "mods"),
            _installer.GetContentDirectory(null, ModProjectType.Mod));
        Assert.Equal(Path.Combine(game, "resourcepacks"),
            _installer.GetContentDirectory(null, ModProjectType.ResourcePack));
        Assert.Equal(Path.Combine(game, "shaderpacks"),
            _installer.GetContentDirectory(null, ModProjectType.Shader));
        Assert.Equal(Path.Combine(game, "saves", "World", "datapacks"),
            _installer.GetContentDirectory(null, ModProjectType.DataPack, "World"));
    }

    [Fact]
    public void InstanceTargetStillUsesItsOwnGameDirectory()
    {
        // 创建实例时装附加 Mod 走这条：隔离实例必须写进自己的目录才读得到
        var instance = Instance();

        Assert.Equal(Path.Combine(_directories.Resolve(instance), "mods"),
            _installer.GetContentDirectory(instance, ModProjectType.Mod));
    }

    [Fact]
    public void DataPackWithoutWorldIsAnError()
    {
        Assert.Throws<InvalidOperationException>(() =>
            _installer.GetContentDirectory(null, ModProjectType.DataPack));
    }

    [Fact]
    public void ModpackHasNoInstallDirectory()
    {
        Assert.Throws<InvalidOperationException>(() =>
            _installer.GetContentDirectory(null, ModProjectType.Modpack));
    }

    [Fact]
    public void WorldsAreDirectoriesWithLevelDatNewestFirst()
    {
        var saves = Path.Combine(_directories.Shared, "saves");
        Directory.CreateDirectory(Path.Combine(saves, "Old World"));
        File.WriteAllText(Path.Combine(saves, "Old World", "level.dat"), "x");
        Directory.CreateDirectory(Path.Combine(saves, "New World"));
        File.WriteAllText(Path.Combine(saves, "New World", "level.dat"), "x");

        // 没有 level.dat 的目录不是存档
        Directory.CreateDirectory(Path.Combine(saves, "Not A World"));
        File.WriteAllText(Path.Combine(saves, "Not A World", "notes.txt"), "x");

        Directory.SetLastWriteTimeUtc(Path.Combine(saves, "New World"), DateTime.UtcNow);
        Directory.SetLastWriteTimeUtc(Path.Combine(saves, "Old World"), DateTime.UtcNow.AddDays(-2));

        var worlds = _installer.GetWorlds(null);

        Assert.Equal(["New World", "Old World"], worlds);
    }

    [Fact]
    public void InstalledCheckForDataPackIsWorldSpecific()
    {
        var file = new ModFile("pack.zip", "https://x/pack.zip", null, 1);

        var directory = _installer.GetContentDirectory(null, ModProjectType.DataPack, "World");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, file.FileName), "x");

        Assert.True(_installer.IsInstalled(null, ModProjectType.DataPack, file, "World"));
        Assert.False(_installer.IsInstalled(null, ModProjectType.DataPack, file, "Other"));
    }

    [Fact]
    public void Sha1MismatchIsDetected()
    {
        var path = Path.Combine(_root, "verify.bin");
        File.WriteAllText(path, "hello");

        // sha1("hello")
        var good = new ModFile("a.jar", "https://x/a.jar", "aaf4c61ddcc5e8a2dabede0f3b482cd9aea9434d", 5);
        ModFileDownloader.VerifyOrThrow(path, good);

        var bad = good with { Sha1 = "0000000000000000000000000000000000000000" };
        Assert.Throws<InvalidOperationException>(() => ModFileDownloader.VerifyOrThrow(path, bad));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FakeSettings : ISettingsService
    {
        public LauncherSettings Current { get; } = new() { MinecraftDirectory = null };

        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}

/// <summary>
/// 整合包的安装：创建实例、下载清单里的文件、解压 overrides，
/// 以及拒绝把文件写到游戏目录之外。
/// </summary>
public class ModpackInstallTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "solar-modpack-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly AppPaths _paths;
    private readonly InstanceRepository _repository;
    private readonly ModpackService _service;
    private readonly FakeDownloader _downloader = new();

    public ModpackInstallTests()
    {
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();

        var settings = new InstallFakeSettings();
        var endpoints = new ModEndpoints(settings);
        var http = new ModHttp(
            new HttpClient(new NeverUsedHandler()), endpoints, NullLogger<ModHttp>.Instance);

        _repository = new InstanceRepository(_paths);
        _service = new ModpackService(
            http,
            _downloader,
            _repository,
            new InstanceGameDirectory(_paths, settings),
            NullLogger<ModpackService>.Instance);
    }

    [Fact]
    public async Task FilesAndOverridesAreInstalledIntoTheIsolatedInstance()
    {
        var archive = CreateArchive("modrinth",
            ("overrides/config/test.txt", "base"),
            ("overrides/mods/pack.jar", "override"),
            ("client-overrides/config/test.txt", "client"));

        var plan = new ModpackPlan
        {
            ArchivePath = archive,
            Name = "Test Pack",
            MinecraftVersion = "1.21.4",
            Loader = ModLoader.Fabric,
            LoaderVersion = "0.16.0",
            Format = ModpackFormat.Modrinth,
            Files =
            [
                new ModpackFile("mods/a.jar", "https://x/a.jar", null, 1),
                new ModpackFile("../evil.jar", "https://x/evil.jar", null, 1),
            ],
        };

        var result = await _service.InstallAsync(plan, "Test Pack");

        Assert.Equal(1, result.InstalledFiles);
        Assert.Equal(0, result.SkippedFiles);
        Assert.Contains("../evil.jar", result.FailedFiles);

        var instance = result.Instance;
        Assert.True(instance.VersionIsolation);
        Assert.Equal(ModLoader.Fabric, instance.Loader);
        Assert.Equal("0.16.0", instance.LoaderVersion);
        Assert.False(instance.LoaderInstalled);

        // 实例确实落盘了，能被其它页面看到
        var saved = Assert.Single(await _repository.GetAllAsync());
        Assert.Equal(instance.Id, saved.Id);

        var game = _paths.GetInstanceMinecraftDirectory(instance.Id);
        Assert.Equal("stub", await File.ReadAllTextAsync(Path.Combine(game, "mods", "a.jar")));
        Assert.Equal("override", await File.ReadAllTextAsync(Path.Combine(game, "mods", "pack.jar")));

        // client-overrides 后解压，覆盖 overrides 里的同名文件
        Assert.Equal("client", await File.ReadAllTextAsync(Path.Combine(game, "config", "test.txt")));

        // 越界路径不能被下载
        Assert.DoesNotContain(_downloader.Downloaded, f => f.Contains("evil"));
        Assert.False(File.Exists(Path.Combine(_root, "evil.jar")));
    }

    [Fact]
    public async Task CurseForgeOverridesFolderIsHonoured()
    {
        var archive = CreateArchive("cf",
            ("pack-overrides/config/test.txt", "cf"),
            ("overrides/config/ignored.txt", "ignored"));

        var plan = new ModpackPlan
        {
            ArchivePath = archive,
            Name = "CF Pack",
            MinecraftVersion = "1.12.2",
            Loader = ModLoader.Forge,
            LoaderVersion = "14.23.5.2860",
            Format = ModpackFormat.CurseForge,
            OverridesFolder = "pack-overrides",
            Files = [],
        };

        var result = await _service.InstallAsync(plan, "CF Pack");

        var game = _paths.GetInstanceMinecraftDirectory(result.Instance.Id);
        Assert.Equal("cf", await File.ReadAllTextAsync(Path.Combine(game, "config", "test.txt")));
        Assert.False(File.Exists(Path.Combine(game, "config", "ignored.txt")));
        Assert.Equal(1, result.OverrideFiles);
    }

    private string CreateArchive(string name, params (string Path, string Content)[] entries)
    {
        var path = Path.Combine(_root, name + ".zip");

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entryPath, content) in entries)
        {
            var entry = zip.CreateEntry(entryPath);
            using var stream = entry.Open();
            using var writer = new StreamWriter(stream, Encoding.UTF8);
            writer.Write(content);
        }

        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FakeDownloader : IModFileDownloader
    {
        public List<string> Downloaded { get; } = [];

        public Task DownloadAsync(ModFile file, string targetPath, CancellationToken ct = default)
        {
            Downloaded.Add(file.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.WriteAllText(targetPath, "stub");
            return Task.CompletedTask;
        }
    }

    private sealed class InstallFakeSettings : ISettingsService
    {
        public LauncherSettings Current { get; } = new() { MinecraftDirectory = null };

        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>安装流程不需要发 HTTP 请求；真发了说明测试设计错了。</summary>
    private sealed class NeverUsedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("安装测试不应该发起网络请求：" + request.RequestUri);
    }
}
