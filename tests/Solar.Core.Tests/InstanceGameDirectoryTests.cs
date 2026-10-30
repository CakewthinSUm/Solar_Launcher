using Solar.Core.Instances;
using Solar.Core.Paths;
using Solar.Core.Settings;
using Xunit;

namespace Solar.Core.Tests;

/// <summary>版本隔离的游戏目录解析：隔离用版本文件夹，关闭用共享目录。</summary>
public sealed class InstanceGameDirectoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "solar-test-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly AppPaths _paths;
    private readonly FakeSettings _settings = new();
    private readonly InstanceGameDirectory _directories;

    public InstanceGameDirectoryTests()
    {
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();
        _directories = new InstanceGameDirectory(_paths, _settings);
    }

    [Fact]
    public void NonIsolatedInstanceUsesSharedDirectory()
    {
        _settings.Current.MinecraftDirectory = Path.Combine(_root, "shared-mc");
        var instance = new GameInstance { Id = "inst", MinecraftVersion = "1.21.4" };

        Assert.Equal(Path.Combine(_root, "shared-mc"), _directories.Resolve(instance));
    }

    [Fact]
    public void IsolatedInstanceUsesItsVersionFolder()
    {
        _settings.Current.MinecraftDirectory = Path.Combine(_root, "shared-mc");
        var instance = new GameInstance { Id = "inst", MinecraftVersion = "1.21.4", VersionIsolation = true };

        var expected = WriteVersion(_paths.Versions, "1.21.4");

        Assert.Equal(expected, _directories.Resolve(instance));
        Assert.NotEqual(_directories.Shared, _directories.Resolve(instance));
    }

    [Fact]
    public void IsolatedInstanceFallsBackToSolarVersionsBeforeDownload()
    {
        // 新建实例时版本 JSON 可能还没下载，目录必须稳定在下载落点
        var instance = new GameInstance { Id = "inst", MinecraftVersion = "1.21.4", VersionIsolation = true };

        Assert.Equal(Path.Combine(_paths.Versions, "1.21.4"), _directories.Resolve(instance));
    }

    [Fact]
    public void IsolatedInstanceFindsVersionInSharedGameDirectory()
    {
        var shared = Path.Combine(_root, "shared-mc");
        _settings.Current.MinecraftDirectory = shared;
        var instance = new GameInstance { Id = "inst", MinecraftVersion = "1.21.4", VersionIsolation = true };

        var expected = WriteVersion(Path.Combine(shared, "versions"), "1.21.4");

        Assert.Equal(expected, _directories.Resolve(instance));
    }

    [Fact]
    public void SolarSharedTakesPrecedenceOverSharedGameDirectory()
    {
        var shared = Path.Combine(_root, "shared-mc");
        _settings.Current.MinecraftDirectory = shared;
        var instance = new GameInstance { Id = "inst", MinecraftVersion = "1.21.4", VersionIsolation = true };

        WriteVersion(Path.Combine(shared, "versions"), "1.21.4");
        var expected = WriteVersion(_paths.Versions, "1.21.4");

        Assert.Equal(expected, _directories.Resolve(instance));
    }

    [Fact]
    public void OverrideTakesPrecedenceOverVersionFolder()
    {
        var versionFolder = Path.Combine(_root, "versions", "1.21.4");
        var instance = new GameInstance
        {
            Id = "inst",
            MinecraftVersion = "1.21.4",
            VersionIsolation = true,
            GameDirectoryOverride = versionFolder,
        };

        Assert.Equal(versionFolder, _directories.Resolve(instance));
    }

    [Fact]
    public void OverrideIsIgnoredWhenIsolationIsOff()
    {
        // 覆盖路径只在隔离时生效：关掉开关就该回到共享目录
        _settings.Current.MinecraftDirectory = Path.Combine(_root, "shared-mc");
        var instance = new GameInstance
        {
            Id = "inst",
            MinecraftVersion = "1.21.4",
            GameDirectoryOverride = Path.Combine(_root, "versions", "1.21.4"),
        };

        Assert.Equal(Path.Combine(_root, "shared-mc"), _directories.Resolve(instance));
    }

    [Fact]
    public void RelativeOverrideResolvesAgainstRoot()
    {
        var instance = new GameInstance
        {
            Id = "inst",
            MinecraftVersion = "1.21.4",
            VersionIsolation = true,
            GameDirectoryOverride = "versions\\1.21.4",
        };

        Assert.Equal(Path.Combine(_root, "versions", "1.21.4"), _directories.Resolve(instance));
    }

    [Fact]
    public void BlankOverrideFallsBackToVersionFolder()
    {
        var instance = new GameInstance
        {
            Id = "inst",
            MinecraftVersion = "1.21.4",
            VersionIsolation = true,
            GameDirectoryOverride = "   ",
        };

        Assert.Equal(Path.Combine(_paths.Versions, "1.21.4"), _directories.Resolve(instance));
    }

    [Fact]
    public void TwoInstancesOfTheSameVersionShareTheVersionFolder()
    {
        // PCL 语义：同版本只有一个版本文件夹
        var first = new GameInstance { Id = "a", MinecraftVersion = "1.21.4", VersionIsolation = true };
        var second = new GameInstance { Id = "b", MinecraftVersion = "1.21.4", VersionIsolation = true };

        Assert.Equal(_directories.Resolve(first), _directories.Resolve(second));
    }

    [Fact]
    public void CopyBringsModsAndSavesIntoVersionFolder()
    {
        var shared = _directories.Shared;
        WriteFile(Path.Combine(shared, "mods", "sodium.jar"), "mod");
        WriteFile(Path.Combine(shared, "saves", "world", "level.dat"), "save");
        WriteFile(Path.Combine(shared, "options.txt"), "options");

        var instance = new GameInstance { Id = "inst", MinecraftVersion = "1.21.4", VersionIsolation = true };
        var summary = _directories.CopySharedContent(instance);

        var target = _directories.GetIsolatedPath(instance);
        Assert.Equal(Path.Combine(_paths.Versions, "1.21.4"), target);
        Assert.Equal(3, summary.CopiedFiles);
        Assert.Equal(0, summary.SkippedFiles);
        Assert.Equal("mod", ReadFile(Path.Combine(target, "mods", "sodium.jar")));
        Assert.Equal("save", ReadFile(Path.Combine(target, "saves", "world", "level.dat")));
    }

    [Fact]
    public void ExistingFilesAreNotOverwritten()
    {
        var shared = _directories.Shared;
        WriteFile(Path.Combine(shared, "mods", "a.jar"), "shared");
        WriteFile(Path.Combine(shared, "mods", "b.jar"), "shared");

        var instance = new GameInstance { Id = "inst", MinecraftVersion = "1.21.4", VersionIsolation = true };
        var target = _directories.GetIsolatedPath(instance);

        // 版本文件夹里已经有一份不同的 a.jar（比如之前隔离过）
        WriteFile(Path.Combine(target, "mods", "a.jar"), "instance");

        var summary = _directories.CopySharedContent(instance);

        Assert.Equal(1, summary.CopiedFiles);
        Assert.Equal(1, summary.SkippedFiles);
        Assert.Equal("instance", ReadFile(Path.Combine(target, "mods", "a.jar")));
        Assert.Equal("shared", ReadFile(Path.Combine(target, "mods", "b.jar")));
    }

    [Fact]
    public void SourceDirectoryIsLeftUntouched()
    {
        var shared = _directories.Shared;
        WriteFile(Path.Combine(shared, "mods", "a.jar"), "shared");

        _directories.CopySharedContent(
            new GameInstance { Id = "inst", MinecraftVersion = "1.21.4", VersionIsolation = true });

        Assert.Equal("shared", ReadFile(Path.Combine(shared, "mods", "a.jar")));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(shared, "mods")));
    }

    [Fact]
    public void MissingSharedDirectoryIsNotAnError()
    {
        // 设置指向一个还不存在的目录
        _settings.Current.MinecraftDirectory = Path.Combine(_root, "nope");

        var summary = _directories.CopySharedContent(
            new GameInstance { Id = "inst", MinecraftVersion = "1.21.4", VersionIsolation = true });

        Assert.Equal(0, summary.CopiedFiles);
        Assert.Equal(0, summary.SkippedFiles);
    }

    [Fact]
    public void EmptySharedDirectoryOnlyCreatesTarget()
    {
        Directory.CreateDirectory(_directories.Shared);

        var instance = new GameInstance { Id = "inst", MinecraftVersion = "1.21.4", VersionIsolation = true };
        var summary = _directories.CopySharedContent(instance);

        Assert.Equal(0, summary.CopiedFiles);
        Assert.True(Directory.Exists(_directories.GetIsolatedPath(instance)));
    }

    /// <summary>在指定版本根下造一个版本文件夹（含同名 JSON），返回它的路径。</summary>
    private static string WriteVersion(string root, string versionId)
    {
        var directory = Path.Combine(root, versionId);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, versionId + ".json"), "{}");
        return directory;
    }

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string ReadFile(string path) => File.ReadAllText(path);

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
