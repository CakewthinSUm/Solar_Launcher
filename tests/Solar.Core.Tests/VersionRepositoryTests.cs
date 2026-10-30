using Microsoft.Extensions.Logging.Abstractions;
using Solar.Core.Paths;
using Solar.Core.Settings;
using Solar.Core.Versions;
using Xunit;

namespace Solar.Core.Tests;

/// <summary>版本仓库：versions/ 就是唯一事实来源，设置写在版本文件夹的 .solar 里。</summary>
public sealed class VersionRepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "solar-test-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly AppPaths _app;
    private readonly FakeSettings _settings = new();
    private readonly MinecraftPaths _paths;
    private readonly VersionRepository _repository;

    public VersionRepositoryTests()
    {
        _app = new AppPaths(_root);
        _app.EnsureCreated();
        _paths = new MinecraftPaths(_app, _settings);
        _paths.EnsureCreated();

        var scanner = new VersionScanner(_paths, NullLogger<VersionScanner>.Instance);
        _repository = new VersionRepository(
            _paths, scanner, _settings, NullLogger<VersionRepository>.Instance);
    }

    [Fact]
    public async Task ScanReturnsEveryVersionFolderWithJson()
    {
        WriteVersion("1.21.4");
        WriteVersion("1.20.1-forge-47.2.0", "1.20.1");
        Directory.CreateDirectory(Path.Combine(_paths.Versions, "no-json"));

        var all = await _repository.GetAllAsync();

        Assert.Equal(2, all.Count);
        Assert.Contains(all, v => v.Id == "1.21.4" && v.Loader == ModLoader.Vanilla);
        Assert.Contains(all, v => v.Id == "1.20.1-forge-47.2.0" && v.Loader == ModLoader.Forge);
    }

    [Fact]
    public async Task SettingsRoundTripUnderSolarFolder()
    {
        WriteVersion("1.21.4");

        var version = await _repository.GetAsync("1.21.4");
        Assert.NotNull(version);

        version!.VersionIsolation = true;
        version.JavaPath = @"C:\Java\bin\java.exe";
        version.MaxMemoryMb = 6144;
        version.Name = "我的版本";
        version.CompanionMods.Add("fabric-api.jar");

        await _repository.SaveSettingsAsync(version);

        var file = _paths.GetVersionSettingsFile("1.21.4");
        Assert.True(File.Exists(file));
        Assert.Contains(".solar", file);

        var loaded = await _repository.GetAsync("1.21.4");
        Assert.NotNull(loaded);
        Assert.True(loaded!.VersionIsolation);
        Assert.Equal(@"C:\Java\bin\java.exe", loaded.JavaPath);
        Assert.Equal(6144, loaded.MaxMemoryMb);
        Assert.Equal("我的版本", loaded.Name);
        Assert.Contains("fabric-api.jar", loaded.CompanionMods);
    }

    [Fact]
    public async Task MissingSettingsUsesIsolationScope()
    {
        WriteVersion("1.21.4");

        _settings.Current.DefaultIsolationScope = VersionIsolationScope.Off;
        var off = await _repository.GetAsync("1.21.4");
        Assert.False(off!.VersionIsolation);

        _settings.Current.DefaultIsolationScope = VersionIsolationScope.All;
        var all = await _repository.GetAsync("1.21.4");
        Assert.True(all!.VersionIsolation);
    }

    [Fact]
    public async Task PlayerContentForcesIsolationEvenWhenScopeIsOff()
    {
        WriteVersion("1.21.4");
        Directory.CreateDirectory(Path.Combine(_paths.GetVersionDirectory("1.21.4"), "mods"));
        File.WriteAllText(Path.Combine(_paths.GetVersionDirectory("1.21.4"), "mods", "a.jar"), "");

        _settings.Current.DefaultIsolationScope = VersionIsolationScope.Off;
        var version = await _repository.GetAsync("1.21.4");

        Assert.True(version!.VersionIsolation);
    }

    [Fact]
    public async Task DeleteRemovesTheWholeVersionFolder()
    {
        WriteVersion("1.21.4");
        var directory = _paths.GetVersionDirectory("1.21.4");
        File.WriteAllText(Path.Combine(directory, "saves.txt"), "save");

        await _repository.DeleteAsync("1.21.4");

        Assert.False(Directory.Exists(directory));
        Assert.Null(await _repository.GetAsync("1.21.4"));
    }

    [Fact]
    public async Task RenameMovesFolderAndRewritesId()
    {
        WriteVersion("1.21.4");
        File.WriteAllText(Path.Combine(_paths.GetVersionDirectory("1.21.4"), "1.21.4.jar"), "jar");

        var renamed = await _repository.RenameAsync("1.21.4", "我的整合包");

        Assert.Equal("我的整合包", renamed.Id);
        Assert.False(Directory.Exists(_paths.GetVersionDirectory("1.21.4")));

        var directory = _paths.GetVersionDirectory("我的整合包");
        Assert.True(File.Exists(Path.Combine(directory, "我的整合包.json")));
        Assert.True(File.Exists(Path.Combine(directory, "我的整合包.jar")));

        var json = System.Text.Json.Nodes.JsonNode.Parse(
            File.ReadAllText(Path.Combine(directory, "我的整合包.json")))!;
        Assert.Equal("我的整合包", json["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task RenameRefusesExistingTarget()
    {
        WriteVersion("a");
        WriteVersion("b");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _repository.RenameAsync("a", "b"));
    }

    [Fact]
    public async Task GetReturnsNullForMissingVersion()
    {
        Assert.Null(await _repository.GetAsync("nope"));
    }

    private void WriteVersion(string id, string? inheritsFrom = null)
    {
        var directory = _paths.GetVersionDirectory(id);
        Directory.CreateDirectory(directory);

        var json = inheritsFrom is null
            ? $$"""{"id":"{{id}}","mainClass":"net.minecraft.client.main.Main"}"""
            : $$"""{"id":"{{id}}","inheritsFrom":"{{inheritsFrom}}","mainClass":"net.minecraft.client.main.Main"}""";

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
