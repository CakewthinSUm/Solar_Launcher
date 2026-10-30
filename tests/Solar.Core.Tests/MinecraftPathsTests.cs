using Solar.Core.Paths;
using Solar.Core.Settings;
using Solar.Core.Versions;
using Xunit;

namespace Solar.Core.Tests;

/// <summary>MC 文件夹与版本目录的解析：隔离用版本文件夹，关闭用 MC 根。</summary>
public sealed class MinecraftPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "solar-test-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly AppPaths _app;
    private readonly FakeSettings _settings = new();
    private readonly MinecraftPaths _paths;

    public MinecraftPathsTests()
    {
        _app = new AppPaths(_root);
        _paths = new MinecraftPaths(_app, _settings);
    }

    [Fact]
    public void DefaultRootIsDotMinecraftNextToTheProgram()
    {
        Assert.Equal(Path.Combine(_root, ".minecraft"), _paths.MinecraftRoot);
        Assert.Equal(Path.Combine(_root, ".minecraft", "versions"), _paths.Versions);
        Assert.Equal(Path.Combine(_root, ".minecraft", "libraries"), _paths.Libraries);
        Assert.Equal(Path.Combine(_root, ".minecraft", "assets"), _paths.Assets);
        Assert.Equal(Path.Combine(_root, ".minecraft", "runtime"), _paths.Runtimes);
    }

    [Fact]
    public void ConfiguredRootMovesEverything()
    {
        var custom = Path.Combine(_root, "elsewhere");
        _settings.Current.MinecraftDirectory = custom;

        Assert.Equal(custom, _paths.MinecraftRoot);
        Assert.Equal(Path.Combine(custom, "versions"), _paths.Versions);
        Assert.Equal(Path.Combine(custom, "libraries"), _paths.Libraries);
    }

    [Fact]
    public void NonIsolatedVersionUsesMcRoot()
    {
        Assert.Equal(_paths.MinecraftRoot, _paths.ResolveGameDirectory("1.21.4", isolated: false));
    }

    [Fact]
    public void IsolatedVersionUsesItsVersionFolder()
    {
        var expected = Path.Combine(_paths.Versions, "1.21.4");
        Assert.Equal(expected, _paths.ResolveGameDirectory("1.21.4", isolated: true));
    }

    [Fact]
    public void SettingsAndNativesLiveUnderSolarFolder()
    {
        var settingsDirectory = _paths.GetVersionSettingsDirectory("1.21.4");

        Assert.Equal(Path.Combine(_paths.Versions, "1.21.4", ".solar"), settingsDirectory);
        Assert.Equal(Path.Combine(settingsDirectory, "version.json"), _paths.GetVersionSettingsFile("1.21.4"));
        Assert.Equal(Path.Combine(settingsDirectory, "natives"), _paths.GetNativesDirectory("1.21.4"));
    }

    [Fact]
    public void NonAsciiVersionFallsBackToLocalAppDataForNatives()
    {
        var natives = _paths.GetNativesDirectory("我的版本");

        Assert.DoesNotContain("我的版本", natives);
        Assert.StartsWith(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SolarLauncher",
                "natives"),
            natives);
    }

    [Fact]
    public void CopyBringsModsAndSavesIntoVersionFolder()
    {
        WriteFile(Path.Combine(_paths.MinecraftRoot, "mods", "sodium.jar"), "mod");
        WriteFile(Path.Combine(_paths.MinecraftRoot, "saves", "world", "level.dat"), "save");
        WriteFile(Path.Combine(_paths.MinecraftRoot, "options.txt"), "options");

        var target = _paths.GetVersionDirectory("1.21.4");
        var summary = VersionContent.CopySharedContent(_paths.MinecraftRoot, target);

        Assert.Equal(3, summary.CopiedFiles);
        Assert.Equal(0, summary.SkippedFiles);
        Assert.Equal("mod", ReadFile(Path.Combine(target, "mods", "sodium.jar")));
        Assert.Equal("save", ReadFile(Path.Combine(target, "saves", "world", "level.dat")));
        Assert.Equal("options", ReadFile(Path.Combine(target, "options.txt")));
    }

    [Fact]
    public void ExistingFilesAreNotOverwritten()
    {
        WriteFile(Path.Combine(_paths.MinecraftRoot, "mods", "a.jar"), "shared");
        WriteFile(Path.Combine(_paths.MinecraftRoot, "mods", "b.jar"), "shared");

        var target = _paths.GetVersionDirectory("1.21.4");
        WriteFile(Path.Combine(target, "mods", "a.jar"), "version");

        var summary = VersionContent.CopySharedContent(_paths.MinecraftRoot, target);

        Assert.Equal(1, summary.CopiedFiles);
        Assert.Equal(1, summary.SkippedFiles);
        Assert.Equal("version", ReadFile(Path.Combine(target, "mods", "a.jar")));
        Assert.Equal("shared", ReadFile(Path.Combine(target, "mods", "b.jar")));
    }

    [Fact]
    public void SourceDirectoryIsLeftUntouched()
    {
        WriteFile(Path.Combine(_paths.MinecraftRoot, "mods", "a.jar"), "shared");

        VersionContent.CopySharedContent(_paths.MinecraftRoot, _paths.GetVersionDirectory("1.21.4"));

        Assert.Equal("shared", ReadFile(Path.Combine(_paths.MinecraftRoot, "mods", "a.jar")));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(_paths.MinecraftRoot, "mods")));
    }

    [Fact]
    public void MissingSharedDirectoryIsNotAnError()
    {
        var summary = VersionContent.CopySharedContent(
            Path.Combine(_root, "nope"),
            _paths.GetVersionDirectory("1.21.4"));

        Assert.Equal(0, summary.CopiedFiles);
        Assert.Equal(0, summary.SkippedFiles);
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
