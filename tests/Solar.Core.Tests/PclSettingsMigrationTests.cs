using Solar.Core.Paths;
using Solar.Core.Settings;
using Solar.Core.Versions;
using Xunit;

namespace Solar.Core.Tests;

/// <summary>读取 PCL 写在版本文件夹里的 PCL/Setup.ini，供“从 PCL 迁移设置”使用。</summary>
public sealed class PclSettingsMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "solar-pcl-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly MinecraftPaths _paths;

    public PclSettingsMigrationTests()
    {
        var app = new AppPaths(_root);
        app.EnsureCreated();
        _paths = new MinecraftPaths(app, new FakeSettings());
        _paths.EnsureCreated();
    }

    [Fact]
    public void ReadsFavoriteAndIsolation()
    {
        WriteSetup("1.21.5", """
            VersionFabric:0.19.3
            Info:正式版 1.21.5, Fabric 0.19.3
            VersionArgumentIndieV2:True
            IsStar:True
            """);

        var settings = PclSettingsMigration.TryRead(_paths, "1.21.5");

        Assert.NotNull(settings);
        Assert.True(settings!.IsFavorite);
        Assert.True(settings.VersionIsolation);
        Assert.Null(settings.CustomIconPath);
    }

    [Fact]
    public void FileWithOnlyDetectionCacheHasNothingToMigrate()
    {
        // 用户实际文件：只有识别缓存，没有收藏、隔离或自定义图标
        WriteSetup("1.21.5-Fabric 0.19.3", """
            VersionLiteLoader:False
            VersionFabric:0.19.3
            Info:正式版 1.21.5, Fabric 0.19.3
            VersionVanilla:21.0.5
            VersionVanillaName:1.21.5
            ReleaseTime:2025-03-25 20:14
            Logo:pack://application:,,,/Plain Craft Launcher 2;component/Images/Blocks/Fabric.png
            State:9
            """);

        Assert.Null(PclSettingsMigration.TryRead(_paths, "1.21.5-Fabric 0.19.3"));
    }

    [Fact]
    public void IsolationKeysMapToBool()
    {
        WriteSetup("v2-true", "VersionArgumentIndieV2:True");
        Assert.True(PclSettingsMigration.TryRead(_paths, "v2-true")!.VersionIsolation);

        WriteSetup("v2-false", "VersionArgumentIndieV2:False");
        Assert.False(PclSettingsMigration.TryRead(_paths, "v2-false")!.VersionIsolation);

        // 旧键：1 手动开启，2 手动关闭
        WriteSetup("old-on", "VersionArgumentIndie:1");
        Assert.True(PclSettingsMigration.TryRead(_paths, "old-on")!.VersionIsolation);

        WriteSetup("old-off", "VersionArgumentIndie:2");
        Assert.False(PclSettingsMigration.TryRead(_paths, "old-off")!.VersionIsolation);

        // -1（未决定）和 0（跟随全局）没有明确值，不迁移
        WriteSetup("old-undecided", "VersionArgumentIndie:0");
        Assert.Null(PclSettingsMigration.TryRead(_paths, "old-undecided"));
    }

    [Fact]
    public void ReadsCustomLogoFromPclFolder()
    {
        WriteSetup("1.20.1", """
            LogoCustom:True
            Logo:PCL\Logo.png
            """);

        var logo = Path.Combine(_paths.GetVersionDirectory("1.20.1"), "PCL", "Logo.png");
        File.WriteAllBytes(logo, [1, 2, 3]);

        var settings = PclSettingsMigration.TryRead(_paths, "1.20.1");

        Assert.NotNull(settings);
        Assert.Equal(logo, settings!.CustomIconPath);
    }

    [Fact]
    public void BuiltInLogoIsNotMigrated()
    {
        WriteSetup("1.20.1", """
            Logo:pack://application:,,,/Plain Craft Launcher 2;component/Images/Blocks/Forge.png
            """);

        // 内置图标是 WPF pack URI，WinUI 用不了，也没有可复制的文件
        Assert.Null(PclSettingsMigration.TryRead(_paths, "1.20.1"));
    }

    [Fact]
    public void MissingFileReturnsNull()
    {
        Assert.Null(PclSettingsMigration.TryRead(_paths, "not-there"));
    }

    [Fact]
    public void ValuesKeepColonsAfterTheFirstSeparator()
    {
        var entries = PclSettingsMigration.Parse([
            "Info:正式版 1.21.5, Fabric 0.19.3",
            "Url:a:b",
            "没有冒号的行",
            ":空键",
        ]);

        Assert.Equal("正式版 1.21.5, Fabric 0.19.3", entries["Info"]);
        Assert.Equal("a:b", entries["Url"]);
        Assert.Equal(2, entries.Count);
    }

    private void WriteSetup(string versionId, string content)
    {
        var directory = Path.Combine(_paths.GetVersionDirectory(versionId), "PCL");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "Setup.ini"), content);
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
        public LauncherSettings Current { get; } = new();

        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
