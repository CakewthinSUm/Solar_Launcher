using Solar.Core.Instances;
using Solar.Core.Paths;
using Xunit;

namespace Solar.Core.Tests;

public sealed class InstanceRepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "solar-test-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly AppPaths _paths;
    private readonly InstanceRepository _repository;

    public InstanceRepositoryTests()
    {
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();
        _repository = new InstanceRepository(_paths);
    }

    [Fact]
    public async Task SaveThenGet_RoundTrips()
    {
        var instance = new GameInstance
        {
            Id = "abc123",
            Name = "测试实例",
            MinecraftVersion = "1.21.4",
            Loader = ModLoader.Fabric,
            LoaderVersion = "0.16.9",
        };

        await _repository.SaveAsync(instance);

        var loaded = await _repository.GetAsync("abc123");
        Assert.NotNull(loaded);
        Assert.Equal("测试实例", loaded!.Name);
        Assert.Equal(ModLoader.Fabric, loaded.Loader);
        Assert.Equal("1.21.4", loaded.EffectiveVersionId);
        Assert.True(Directory.Exists(_paths.GetInstanceDirectory("abc123")));
    }

    [Fact]
    public async Task GetAll_ReturnsSavedInstancesInOrder()
    {
        await _repository.SaveAsync(new GameInstance { Id = "a", Name = "A" });
        await _repository.SaveAsync(new GameInstance { Id = "b", Name = "B" });

        var all = await _repository.GetAllAsync();

        Assert.Equal(["A", "B"], all.Select(i => i.Name));
    }

    [Fact]
    public async Task Delete_RemovesFromIndexAndDisk()
    {
        await _repository.SaveAsync(new GameInstance { Id = "a", Name = "A" });

        await _repository.DeleteAsync("a");

        Assert.Empty(await _repository.GetAllAsync());
        Assert.False(Directory.Exists(_paths.GetInstanceDirectory("a")));
    }

    [Fact]
    public void EffectiveVersionId_FallsBackToMinecraftVersion()
    {
        var instance = new GameInstance { MinecraftVersion = "1.20.1" };
        Assert.Equal("1.20.1", instance.EffectiveVersionId);

        instance.VersionId = "1.20.1-forge-47.2.0";
        Assert.Equal("1.20.1-forge-47.2.0", instance.EffectiveVersionId);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
