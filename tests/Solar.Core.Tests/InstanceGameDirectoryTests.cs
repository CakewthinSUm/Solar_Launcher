using Solar.Core.Instances;
using Solar.Core.Paths;
using Solar.Core.Settings;
using Xunit;

namespace Solar.Core.Tests;

/// <summary>开启版本隔离时把共享目录内容复制到实例目录的行为。</summary>
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
        var instance = new GameInstance { Id = "inst" };

        Assert.Equal(Path.Combine(_root, "shared-mc"), _directories.Resolve(instance));
    }

    [Fact]
    public void IsolatedInstanceUsesItsOwnDirectory()
    {
        _settings.Current.MinecraftDirectory = Path.Combine(_root, "shared-mc");
        var instance = new GameInstance { Id = "inst", VersionIsolation = true };

        Assert.Equal(Path.Combine(_root, "SolarInstances", "inst", ".minecraft"), _directories.Resolve(instance));
    }

    [Fact]
    public void CopyBringsModsAndSavesOver()
    {
        var shared = _directories.Shared;
        WriteFile(Path.Combine(shared, "mods", "sodium.jar"), "mod");
        WriteFile(Path.Combine(shared, "saves", "world", "level.dat"), "save");
        WriteFile(Path.Combine(shared, "options.txt"), "options");

        var summary = _directories.CopySharedContent("inst");

        Assert.Equal(3, summary.CopiedFiles);
        Assert.Equal(0, summary.SkippedFiles);
        Assert.Equal("mod", ReadFile(Path.Combine(_directories.GetIsolatedPath("inst"), "mods", "sodium.jar")));
        Assert.Equal("save", ReadFile(Path.Combine(_directories.GetIsolatedPath("inst"), "saves", "world", "level.dat")));
    }

    [Fact]
    public void ExistingFilesAreNotOverwritten()
    {
        var shared = _directories.Shared;
        WriteFile(Path.Combine(shared, "mods", "a.jar"), "shared");
        WriteFile(Path.Combine(shared, "mods", "b.jar"), "shared");

        // 实例目录里已经有一份不同的 a.jar（比如之前隔离过）
        WriteFile(Path.Combine(_directories.GetIsolatedPath("inst"), "mods", "a.jar"), "instance");

        var summary = _directories.CopySharedContent("inst");

        Assert.Equal(1, summary.CopiedFiles);
        Assert.Equal(1, summary.SkippedFiles);
        Assert.Equal("instance", ReadFile(Path.Combine(_directories.GetIsolatedPath("inst"), "mods", "a.jar")));
        Assert.Equal("shared", ReadFile(Path.Combine(_directories.GetIsolatedPath("inst"), "mods", "b.jar")));
    }

    [Fact]
    public void SourceDirectoryIsLeftUntouched()
    {
        var shared = _directories.Shared;
        WriteFile(Path.Combine(shared, "mods", "a.jar"), "shared");

        _directories.CopySharedContent("inst");

        Assert.Equal("shared", ReadFile(Path.Combine(shared, "mods", "a.jar")));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(shared, "mods")));
    }

    [Fact]
    public void MissingSharedDirectoryIsNotAnError()
    {
        // 设置指向一个还不存在的目录
        _settings.Current.MinecraftDirectory = Path.Combine(_root, "nope");

        var summary = _directories.CopySharedContent("inst");

        Assert.Equal(0, summary.CopiedFiles);
        Assert.Equal(0, summary.SkippedFiles);
    }

    [Fact]
    public void EmptySharedDirectoryOnlyCreatesTarget()
    {
        Directory.CreateDirectory(_directories.Shared);

        var summary = _directories.CopySharedContent("inst");

        Assert.Equal(0, summary.CopiedFiles);
        Assert.True(Directory.Exists(_directories.GetIsolatedPath("inst")));
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
