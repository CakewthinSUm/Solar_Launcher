using Solar.Core.Json;
using Solar.Core.Settings;
using Xunit;

namespace Solar.Core.Tests;

/// <summary>settings.json 的旧值迁移与新枚举读写。</summary>
public sealed class LauncherSettingsTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), "solar-settings-" + Guid.NewGuid().ToString("N")[..8] + ".json");

    [Theory]
    [InlineData("0", VersionIsolationScope.All)]  // 旧 Ask → 对齐 PCL 默认
    [InlineData("1", VersionIsolationScope.All)]  // 旧 Always
    [InlineData("2", VersionIsolationScope.Off)]  // 旧 Never
    public async Task LegacyIsolationValueMigratesToScope(string legacy, VersionIsolationScope expected)
    {
        await File.WriteAllTextAsync(_path, $$"""{ "NewInstanceIsolation": {{legacy}} }""");

        var settings = await JsonFile.ReadAsync<LauncherSettings>(_path);

        Assert.NotNull(settings);
        Assert.Equal(expected, settings.NewInstanceIsolationScope);
        // 迁移后旧字段清空，下次保存整键消失
        Assert.Null(settings.NewInstanceIsolation);
    }

    [Fact]
    public async Task SavingDropsLegacyKeyAndWritesScopeKey()
    {
        await File.WriteAllTextAsync(_path, """{ "NewInstanceIsolation": 2 }""");
        var settings = (await JsonFile.ReadAsync<LauncherSettings>(_path))!;

        await JsonFile.WriteAsync(_path, settings);
        var text = await File.ReadAllTextAsync(_path);

        Assert.DoesNotContain("NewInstanceIsolation\"", text);
        Assert.Contains("NewInstanceIsolationScope", text);
    }

    [Fact]
    public async Task MissingFileDefaultsToAll()
    {
        var settings = (await JsonFile.ReadAsync<LauncherSettings>(_path, fallback: new LauncherSettings()))!;

        Assert.Equal(VersionIsolationScope.All, settings.NewInstanceIsolationScope);
    }

    [Fact]
    public async Task EmptyJsonDefaultsToAll()
    {
        await File.WriteAllTextAsync(_path, "{ }");

        var settings = await JsonFile.ReadAsync<LauncherSettings>(_path);

        Assert.NotNull(settings);
        Assert.Equal(VersionIsolationScope.All, settings.NewInstanceIsolationScope);
    }

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}
