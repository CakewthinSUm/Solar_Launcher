using Microsoft.Extensions.Logging.Abstractions;
using Solar.Core.Instances;
using Solar.Core.Launch;
using Solar.Core.Minecraft;
using Solar.Core.Models;
using Solar.Core.Paths;
using Solar.Core.Settings;
using Xunit;

namespace Solar.Core.Tests;

/// <summary>扫描游戏目录里的 versions/ 并导入成实例。</summary>
public sealed class VersionScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "solar-scan-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly AppPaths _paths;
    private readonly FakeSettings _settings = new();
    private readonly InstanceGameDirectory _directories;
    private readonly GameFileLocator _locator;
    private readonly InstanceRepository _repository;
    private readonly VersionScanner _scanner;

    public VersionScannerTests()
    {
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();
        _directories = new InstanceGameDirectory(_paths, _settings);
        _locator = new GameFileLocator(_paths, _directories);
        _repository = new InstanceRepository(_paths);
        _scanner = new VersionScanner(_locator, _repository, NullLogger<VersionScanner>.Instance);
    }

    [Fact]
    public void ScanFindsVanillaVersion()
    {
        WriteVersion("1.21.4", """
            { "id": "1.21.4", "type": "release", "mainClass": "net.minecraft.client.main.Main",
              "libraries": [ { "name": "com.mojang:authlib:6.0.54" } ] }
            """);

        var found = Assert.Single(_scanner.Scan());

        Assert.Equal("1.21.4", found.VersionId);
        Assert.Equal(ModLoader.Vanilla, found.Loader);
        Assert.Equal("1.21.4", found.MinecraftVersion);
        Assert.Null(found.LoaderVersion);
    }

    [Fact]
    public void ScanDetectsFabricLoaderAndItsMinecraftVersion()
    {
        WriteVersion("fabric-loader-0.16.9-1.21.4", """
            { "id": "fabric-loader-0.16.9-1.21.4", "inheritsFrom": "1.21.4",
              "mainClass": "net.fabricmc.loader.impl.launch.knot.KnotClient",
              "libraries": [ { "name": "net.fabricmc:fabric-loader:0.16.9" } ] }
            """);

        var found = Assert.Single(_scanner.Scan());

        Assert.Equal(ModLoader.Fabric, found.Loader);
        Assert.Equal("1.21.4", found.MinecraftVersion);
        Assert.Equal("0.16.9", found.LoaderVersion);
    }

    [Fact]
    public void ScanDetectsForgeAndNeoForgeWithoutConfusingThem()
    {
        WriteVersion("1.20.1-forge-47.2.0", """
            { "id": "1.20.1-forge-47.2.0", "inheritsFrom": "1.20.1",
              "mainClass": "cpw.mods.bootstraplauncher.BootstrapLauncher" }
            """);

        WriteVersion("1.21.1-neoforge-21.1.72", """
            { "id": "1.21.1-neoforge-21.1.72", "inheritsFrom": "1.21.1",
              "mainClass": "cpw.mods.bootstraplauncher.BootstrapLauncher" }
            """);

        var found = _scanner.Scan();

        var forge = Assert.Single(found, v => v.VersionId == "1.20.1-forge-47.2.0");
        Assert.Equal(ModLoader.Forge, forge.Loader);
        Assert.Equal("47.2.0", forge.LoaderVersion);

        // NeoForge 的 id 里也含 "forge"，不能被认成 Forge
        var neo = Assert.Single(found, v => v.VersionId == "1.21.1-neoforge-21.1.72");
        Assert.Equal(ModLoader.NeoForge, neo.Loader);
        Assert.Equal("21.1.72", neo.LoaderVersion);
    }

    [Fact]
    public void ScanSortsNewestMinecraftVersionFirst()
    {
        WriteVersion("1.21.9", VersionJson("1.21.9"));
        WriteVersion("1.21.10", VersionJson("1.21.10"));
        WriteVersion("1.20.1", VersionJson("1.20.1"));

        var found = _scanner.Scan();

        // 字符串比较会把 1.21.9 排在 1.21.10 前面，这里必须是数值顺序
        Assert.Equal(["1.21.10", "1.21.9", "1.20.1"], found.Select(v => v.VersionId));
    }

    [Fact]
    public void ScanSkipsDirectoriesWithoutVersionJson()
    {
        // 加载器安装器会先建目录再补齐 JSON
        Directory.CreateDirectory(Path.Combine(_locator.ExternalVersions, "half-installed"));

        Assert.Empty(_scanner.Scan());
    }

    [Fact]
    public void ScanSkipsInvalidJson()
    {
        var directory = Path.Combine(_locator.ExternalVersions, "broken");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "broken.json"), "{ not json");

        Assert.Empty(_scanner.Scan());
    }

    [Fact]
    public void ScanSkipsJsonThatIsNotAVersion()
    {
        WriteVersion("empty", "{ }");

        Assert.Empty(_scanner.Scan());
    }

    [Fact]
    public void MissingVersionsDirectoryIsNotAnError()
    {
        Assert.Empty(_scanner.Scan());
    }

    [Fact]
    public async Task ImportCreatesOneInstancePerVersion()
    {
        WriteVersion("1.21.4", VersionJson("1.21.4"));
        WriteVersion("fabric-loader-0.16.9-1.21.4", """
            { "id": "fabric-loader-0.16.9-1.21.4", "inheritsFrom": "1.21.4",
              "mainClass": "net.fabricmc.loader.impl.launch.knot.KnotClient" }
            """);

        var imported = await _scanner.ImportAsync();

        Assert.Equal(2, imported.Count);

        var all = await _repository.GetAllAsync();
        Assert.Equal(2, all.Count);

        // 导入的实例直接用现成的游戏目录，不能开隔离
        Assert.All(all, i => Assert.False(i.VersionIsolation));

        // 文件已经装好了，不应再触发加载器安装
        Assert.All(all, i => Assert.True(i.LoaderInstalled));

        var fabric = Assert.Single(all, i => i.Loader == ModLoader.Fabric);
        Assert.Equal("fabric-loader-0.16.9-1.21.4", fabric.EffectiveVersionId);
        Assert.Equal("1.21.4", fabric.MinecraftVersion);
    }

    [Fact]
    public async Task ImportIsIdempotent()
    {
        WriteVersion("1.21.4", VersionJson("1.21.4"));

        await _scanner.ImportAsync();
        var second = await _scanner.ImportAsync();

        Assert.Empty(second);
        Assert.Single(await _repository.GetAllAsync());
    }

    [Fact]
    public async Task ImportDoesNotDuplicateManuallyCreatedInstance()
    {
        // 用户已经手动建了一个 1.21.4 实例，再扫描不应多出一份
        await _repository.SaveAsync(new GameInstance
        {
            Id = "manual",
            Name = "我的生存",
            MinecraftVersion = "1.21.4",
        });

        WriteVersion("1.21.4", VersionJson("1.21.4"));

        Assert.Empty(await _scanner.ImportAsync());
        Assert.Single(await _repository.GetAllAsync());
    }

    [Fact]
    public void LocatorFindsVersionInGameDirectory()
    {
        WriteVersion("1.21.4", VersionJson("1.21.4"));
        File.WriteAllText(Path.Combine(_locator.ExternalVersions, "1.21.4", "1.21.4.jar"), "jar");

        Assert.Equal(
            Path.Combine(_locator.ExternalVersions, "1.21.4", "1.21.4.json"),
            _locator.FindVersionJson("1.21.4"));
        Assert.Equal(
            Path.Combine(_locator.ExternalVersions, "1.21.4", "1.21.4.jar"),
            _locator.FindVersionJar("1.21.4"));
    }

    [Fact]
    public void LocatorFallsBackToPrimaryWhenGameDirectoryHasNothing()
    {
        Assert.Null(_locator.FindVersionJson("1.21.4"));
        Assert.Equal(
            Path.Combine(_paths.Libraries, "org/lwjgl/lwjgl-3.3.3.jar"),
            _locator.ResolveLibrary("org/lwjgl/lwjgl-3.3.3.jar"));
    }

    [Fact]
    public void LocatorPrefersExistingLibraryFile()
    {
        var relative = Path.Combine("org", "lwjgl", "lwjgl-3.3.3.jar");
        var external = Path.Combine(_locator.ExternalLibraries, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(external)!);
        File.WriteAllText(external, "jar");

        Assert.Equal(external, _locator.ResolveLibrary(relative));
    }

    [Fact]
    public void ClassPathUsesParentJarAndExternalLibraries()
    {
        // 加载器版本的 JSON 不指向自己的 jar；真正要进 classpath 的是父版本（原版）的 jar。
        // 它和库文件都在接管过来的游戏目录里，不能指望 SolarShared 有。
        WriteVersion("1.21.4", VersionJson("1.21.4"));
        var parentJar = Path.Combine(_locator.ExternalVersions, "1.21.4", "1.21.4.jar");
        File.WriteAllText(parentJar, "jar");

        var libraryRelative = Path.Combine("com", "mojang", "authlib", "6.0.54", "authlib-6.0.54.jar");
        var libraryPath = Path.Combine(_locator.ExternalLibraries, libraryRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(libraryPath)!);
        File.WriteAllText(libraryPath, "jar");

        var detail = new VersionDetail
        {
            Id = "fabric-loader-0.16.9-1.21.4",
            MainClass = "net.fabricmc.loader.impl.launch.knot.KnotClient",
            Libraries =
            [
                new Library
                {
                    Name = "com.mojang:authlib:6.0.54",
                    Downloads = new LibraryDownloads
                    {
                        Artifact = new DownloadArtifact { Path = libraryRelative },
                    },
                },
            ],
        };

        var version = new ResolvedVersion
        {
            Detail = detail,
            Chain = [detail, new VersionDetail { Id = "1.21.4" }],
        };

        var context = new LaunchContext
        {
            Instance = new GameInstance { Id = "inst" },
            Version = version,
            Paths = _paths,
            Settings = new LauncherSettings(),
            FileLocator = _locator,
            JavaPath = "java.exe",
            PlayerName = "Steve",
            Uuid = "uuid",
            AccessToken = "0",
        };

        var entries = ArgumentBuilder.BuildClassPath(context).Split(Path.PathSeparator);

        Assert.Contains(libraryPath, entries);
        Assert.Contains(parentJar, entries);
    }

    private static string VersionJson(string id) =>
        $$"""
          { "id": "{{id}}", "type": "release", "mainClass": "net.minecraft.client.main.Main",
            "libraries": [ { "name": "com.mojang:authlib:6.0.54" } ] }
          """;

    /// <summary>写一个位于共享游戏目录（.minecraft）versions/ 下的版本。</summary>
    private void WriteVersion(string id, string json)
    {
        var directory = Path.Combine(_locator.ExternalVersions, id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, id + ".json"), json);
    }

    private sealed class FakeSettings : ISettingsService
    {
        public LauncherSettings Current { get; } = new();

        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
