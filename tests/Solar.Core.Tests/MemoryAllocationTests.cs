using Microsoft.Extensions.Logging.Abstractions;
using Solar.Core.Versions;
using Solar.Core.Memory;
using Solar.Core.Paths;
using Solar.Core.Settings;
using Xunit;

namespace Solar.Core.Tests;

public sealed class MemoryAllocationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "solar-mem-" + Guid.NewGuid().ToString("N")[..8]);

    [Theory]
    [InlineData(16384, 12288, 0, 2048)]     // 16GB 机器、原版：2GB 就够
    [InlineData(16384, 12288, 100, 4096)]   // 100 个模组要 4GB
    [InlineData(16384, 12288, 200, 6144)]   // 200 个模组：需求上到 6GB
    [InlineData(32768, 28672, 400, 10240)]  // 大内存机器，模组越多给得越多
    public void RecommendGrowsWithModCount(int total, int available, int mods, int expected)
    {
        Assert.Equal(expected, MemoryAllocator.Recommend(total, available, mods));
    }

    [Fact]
    public void RecommendIsCappedByAvailableMemory()
    {
        // 32GB 机器但只剩 4GB 可用（后台占满）：不能按总量给 16GB
        var result = MemoryAllocator.Recommend(totalMb: 32768, availableMb: 4096, modCount: 0);

        Assert.True(result <= 4096, $"实际 {result}MB 超过了可用内存");
        Assert.True(result >= MemoryAllocator.MinimumMb);
    }

    [Fact]
    public void RecommendStaysAboveMinimumWhenMemoryIsTight()
    {
        // 内存非常小的机器（2GB）：总量的一半扣掉系统余量后仍是负数，落到下限
        var result = MemoryAllocator.Recommend(totalMb: 2048, availableMb: 128, modCount: 0);
        Assert.Equal(MemoryAllocator.MinimumMb, result);
    }

    [Fact]
    public void RecommendStaysUsableWhenAvailableLooksTiny()
    {
        // 真实场景：16GB 机器开着浏览器与 IDE 时，Windows 报的可用量只有几百 MB
        // （待机缓存不计入可用）。这时不能给出 512MB，否则游戏连加载都过不去。
        var result = MemoryAllocator.Recommend(totalMb: 16384, availableMb: 438, modCount: 0);
        Assert.Equal(2048, result);
    }

    [Fact]
    public void RecommendFallsBackWhenProbeFails()
    {
        Assert.Equal(4096, MemoryAllocator.Recommend(totalMb: 0, availableMb: 0, modCount: 0));
    }

    [Fact]
    public void RecommendNeverExceedsJvmSaneMaximum()
    {
        var result = MemoryAllocator.Recommend(totalMb: 262144, availableMb: 262144, modCount: 5000);
        Assert.True(result <= 16384);
    }

    [Fact]
    public void ManualModeUsesConfiguredValue()
    {
        var settings = new FakeSettings();
        settings.Current.MemoryAllocationMode = MemoryAllocationMode.Manual;
        settings.Current.MaxMemoryMb = 6144;

        var service = CreateService(settings, totalMb: 32768, availableMb: 28672);

        Assert.Equal(6144, service.Resolve(new GameVersion { Id = "inst" }));
    }

    [Fact]
    public void AutomaticModeIgnoresConfiguredValue()
    {
        var settings = new FakeSettings();
        settings.Current.MemoryAllocationMode = MemoryAllocationMode.Automatic;
        settings.Current.MaxMemoryMb = 8192;

        var service = CreateService(settings, totalMb: 16384, availableMb: 12288);

        // 原版自动值 2GB，而不是手动设定的 8GB
        Assert.Equal(2048, service.Resolve(new GameVersion { Id = "inst" }));
    }

    [Fact]
    public void InstanceOverrideWinsInBothModes()
    {
        foreach (var mode in new[] { MemoryAllocationMode.Automatic, MemoryAllocationMode.Manual })
        {
            var settings = new FakeSettings();
            settings.Current.MemoryAllocationMode = mode;

            var service = CreateService(settings, totalMb: 16384, availableMb: 12288);
            var instance = new GameVersion { Id = "inst", MaxMemoryMb = 8192 };

            Assert.Equal(8192, service.Resolve(instance));
        }
    }

    [Fact]
    public void ManualValueIsClampedToAllowedRange()
    {
        var settings = new FakeSettings();
        settings.Current.MemoryAllocationMode = MemoryAllocationMode.Manual;
        settings.Current.MaxMemoryMb = 128;   // 配置文件被手改过

        var service = CreateService(settings, totalMb: 16384, availableMb: 12288);
        Assert.Equal(MemoryAllocator.MinimumMb, service.Resolve(new GameVersion { Id = "inst" }));
    }

    [Fact]
    public void ModCountComesFromMinecraftPaths()
    {
        var settings = new FakeSettings();
        settings.Current.MemoryAllocationMode = MemoryAllocationMode.Automatic;

        // 未隔离实例用共享目录：在那里放 60 个模组，需求应升到 3GB
        var shared = Path.Combine(_root, ".minecraft");
        var mods = Path.Combine(shared, "mods");
        Directory.CreateDirectory(mods);
        for (var i = 0; i < 60; i++)
        {
            File.WriteAllText(Path.Combine(mods, $"mod{i}.jar"), "");
        }

        // 禁用中的模组不加载，不该被计入
        File.WriteAllText(Path.Combine(mods, "disabled.jar.disabled"), "");

        var service = CreateService(settings, totalMb: 16384, availableMb: 12288);
        Assert.Equal(3072, service.Resolve(new GameVersion { Id = "inst" }));
    }

    [Fact]
    public void MissingModsDirectoryCountsAsZero()
    {
        Assert.Equal(0, MemoryAllocationService.CountMods(Path.Combine(_root, "nothing-here")));
    }

    private MemoryAllocationService CreateService(FakeSettings settings, int totalMb, int availableMb)
    {
        var paths = new AppPaths(_root);
        return new MemoryAllocationService(
            settings,
            new MinecraftPaths(paths, settings),
            new FakeMemoryInfo(totalMb, availableMb),
            NullLogger<MemoryAllocationService>.Instance);
    }

    private sealed class FakeSettings : ISettingsService
    {
        public LauncherSettings Current { get; } = new();

        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeMemoryInfo(int totalMb, int availableMb) : ISystemMemoryInfo
    {
        public SystemMemoryInfo GetCurrent() => new(totalMb, availableMb);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
