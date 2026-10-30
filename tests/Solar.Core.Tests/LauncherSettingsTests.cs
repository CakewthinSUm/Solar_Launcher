using Solar.Core.Json;
using Solar.Core.Settings;
using Xunit;

namespace Solar.Core.Tests;

/// <summary>settings.json 的默认值与枚举读写。</summary>
public sealed class LauncherSettingsTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), "solar-settings-" + Guid.NewGuid().ToString("N")[..8] + ".json");

    [Fact]
    public async Task MissingFileDefaultsToAll()
    {
        var settings = (await JsonFile.ReadAsync<LauncherSettings>(_path, fallback: new LauncherSettings()))!;

        Assert.Equal(VersionIsolationScope.All, settings.DefaultIsolationScope);
    }

    [Fact]
    public async Task EmptyJsonDefaultsToAll()
    {
        await File.WriteAllTextAsync(_path, "{ }");

        var settings = await JsonFile.ReadAsync<LauncherSettings>(_path);

        Assert.NotNull(settings);
        Assert.Equal(VersionIsolationScope.All, settings.DefaultIsolationScope);
    }

    [Fact]
    public async Task ScopeRoundTrips()
    {
        await File.WriteAllTextAsync(_path, """{ "DefaultIsolationScope": 1 }""");

        var settings = (await JsonFile.ReadAsync<LauncherSettings>(_path))!;
        Assert.Equal(VersionIsolationScope.Modable, settings.DefaultIsolationScope);

        await JsonFile.WriteAsync(_path, settings);
        Assert.Contains("DefaultIsolationScope", await File.ReadAllTextAsync(_path));
    }

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}
