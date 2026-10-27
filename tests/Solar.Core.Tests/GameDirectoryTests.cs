using Solar.Core.Accounts;
using Solar.Core.Instances;
using Solar.Core.Launch;
using Solar.Core.Minecraft;
using Solar.Core.Models;
using Solar.Core.Paths;
using Solar.Core.Settings;
using Xunit;

namespace Solar.Core.Tests;

/// <summary>游戏目录（.minecraft）来自全局设置，所有实例共用一份。</summary>
public sealed class GameDirectoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "solar-test-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly AppPaths _paths;

    public GameDirectoryTests()
    {
        _paths = new AppPaths(_root);
    }

    [Fact]
    public void EmptySettingFallsBackToAppDirectory()
    {
        Assert.Equal(_paths.DefaultMinecraft, _paths.ResolveMinecraftDirectory(null));
        Assert.Equal(_paths.DefaultMinecraft, _paths.ResolveMinecraftDirectory("   "));
        Assert.Equal(Path.Combine(_root, ".minecraft"), _paths.DefaultMinecraft);
    }

    [Fact]
    public void ConfiguredPathIsUsed()
    {
        var custom = Path.Combine(_root, "elsewhere");
        Assert.Equal(custom, _paths.ResolveMinecraftDirectory(custom));
    }

    [Fact]
    public void RelativePathResolvesAgainstAppDirectory()
    {
        // 相对路径必须相对程序目录，不能依赖进程的当前工作目录
        var resolved = _paths.ResolveMinecraftDirectory("mc-data");
        Assert.Equal(Path.Combine(_root, "mc-data"), resolved);
    }

    [Fact]
    public void EnvironmentVariablesAreExpanded()
    {
        var resolved = _paths.ResolveMinecraftDirectory("%TEMP%\\solar-mc");
        Assert.DoesNotContain("%TEMP%", resolved);
        Assert.EndsWith(@"solar-mc", resolved);
    }

    [Fact]
    public void AllInstancesShareTheSameGameDirectory()
    {
        var settings = new FakeSettings();
        settings.Current.MinecraftDirectory = Path.Combine(_root, "shared-mc");
        var factory = CreateFactory(settings);
        var version = CreateVersion();
        var account = new AccountSession("Steve", "uuid", "0", "", "msa");

        var first = factory.Create(new GameInstance { Id = "inst-a" }, version, "java.exe", account);
        var second = factory.Create(new GameInstance { Id = "inst-b" }, version, "java.exe", account);

        Assert.Equal(first.GameDirectory, second.GameDirectory);
        Assert.Equal(Path.Combine(_root, "shared-mc"), first.GameDirectory);

        // natives 仍需按实例隔离，不同版本的原生库不能混用
        Assert.NotEqual(first.NativesDirectory, second.NativesDirectory);
    }

    [Fact]
    public void IsolatedInstanceUsesItsOwnGameDirectory()
    {
        var settings = new FakeSettings();
        settings.Current.MinecraftDirectory = Path.Combine(_root, "shared-mc");
        var factory = CreateFactory(settings);
        var version = CreateVersion();
        var account = new AccountSession("Steve", "uuid", "0", "", "msa");

        var isolated = factory.Create(
            new GameInstance { Id = "inst-a", VersionIsolation = true }, version, "java.exe", account);
        var shared = factory.Create(
            new GameInstance { Id = "inst-b" }, version, "java.exe", account);

        Assert.Equal(Path.Combine(_root, "SolarInstances", "inst-a", ".minecraft"), isolated.GameDirectory);
        Assert.NotEqual(isolated.GameDirectory, shared.GameDirectory);

        // 两个隔离实例之间也互不影响
        var otherIsolated = factory.Create(
            new GameInstance { Id = "inst-c", VersionIsolation = true }, version, "java.exe", account);
        Assert.NotEqual(isolated.GameDirectory, otherIsolated.GameDirectory);
    }

    [Fact]
    public void UnsetSettingUsesDefaultForLaunch()
    {
        var settings = new FakeSettings();
        var factory = CreateFactory(settings);

        var context = factory.Create(
            new GameInstance { Id = "inst" },
            CreateVersion(),
            "java.exe",
            new AccountSession("Steve", "uuid", "0", "", "msa"));

        Assert.Equal(_paths.DefaultMinecraft, context.GameDirectory);
    }

    private LaunchContextFactory CreateFactory(FakeSettings settings)
    {
        var directories = new InstanceGameDirectory(_paths, settings);
        return new(_paths, settings, directories, new GameFileLocator(_paths, directories));
    }

    private static ResolvedVersion CreateVersion() => new()
    {
        Detail = new VersionDetail { Id = "1.21.4", MainClass = "Main" },
        Chain = [],
    };

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
