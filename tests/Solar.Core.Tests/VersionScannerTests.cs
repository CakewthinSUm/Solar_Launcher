using Microsoft.Extensions.Logging.Abstractions;
using Solar.Core.Models;
using Solar.Core.Paths;
using Solar.Core.Settings;
using Solar.Core.Versions;
using Xunit;

namespace Solar.Core.Tests;

/// <summary>扫描 MC 文件夹里的 versions/，识别原版与加载器版本。</summary>
public sealed class VersionScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "solar-scan-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly AppPaths _app;
    private readonly MinecraftPaths _paths;
    private readonly VersionScanner _scanner;

    public VersionScannerTests()
    {
        _app = new AppPaths(_root);
        _app.EnsureCreated();
        _paths = new MinecraftPaths(_app, new FakeSettings());
        _scanner = new VersionScanner(_paths, NullLogger<VersionScanner>.Instance);
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
        Directory.CreateDirectory(Path.Combine(_paths.Versions, "half-installed"));

        Assert.Empty(_scanner.Scan());
    }

    [Fact]
    public void ScanSkipsInvalidJson()
    {
        var directory = Path.Combine(_paths.Versions, "broken");
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
    public void ClassifyKeepsVersionType()
    {
        var detail = new VersionDetail
        {
            Id = "24w40a",
            Type = "snapshot",
            MainClass = "net.minecraft.client.main.Main",
        };

        var scanned = VersionScanner.Classify("24w40a", detail, @"C:\game\versions\24w40a");

        Assert.Equal("snapshot", scanned.Type);
        Assert.Equal(@"C:\game\versions\24w40a", scanned.Directory);
    }

    [Fact]
    public void BuildIdStripsInvalidFileNameCharacters()
    {
        Assert.Equal("我的_版本", VersionScanner.BuildId("我的/版本"));
        Assert.Equal("a_b_c", VersionScanner.BuildId("a:b*c"));
    }

    [Fact]
    public void BuildUniqueIdAppendsCounterWhenNameExists()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Assert.Equal("1.21.4", VersionScanner.BuildUniqueId("1.21.4", taken.Contains));

        taken.Add("1.21.4");
        Assert.Equal("1.21.4#2", VersionScanner.BuildUniqueId("1.21.4", taken.Contains));

        taken.Add("1.21.4#2");
        Assert.Equal("1.21.4#3", VersionScanner.BuildUniqueId("1.21.4", taken.Contains));
    }

    [Fact]
    public void BuildUniqueIdSanitizesBeforeChecking()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "a_b" };

        Assert.Equal("a_b#2", VersionScanner.BuildUniqueId("a:b", taken.Contains));
    }

    [Fact]
    public void BuildUniqueIdKeepsSuffixWithinLengthLimit()
    {
        // 原名已经到 64 字符上限时，追加后缀要先把正文截短，否则截断后又会变回原名
        var longName = new string('a', 64);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { longName };

        var unique = VersionScanner.BuildUniqueId(longName, taken.Contains);

        Assert.Equal(64, unique.Length);
        Assert.EndsWith("#2", unique);
        Assert.NotEqual(longName, unique);
    }

    private void WriteVersion(string id, string json)
    {
        var directory = Path.Combine(_paths.Versions, id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, id + ".json"), json);
    }

    private static string VersionJson(string id) => $$"""
        { "id": "{{id}}", "type": "release", "mainClass": "net.minecraft.client.main.Main",
          "libraries": [ { "name": "com.mojang:authlib:6.0.54" } ] }
        """;

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
