using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Solar.Core;
using Solar.Core.Java;
using Solar.Core.Launch;
using Solar.Core.Modpacks;
using Solar.Core.Paths;
using Solar.Core.Versions;
using Xunit;

namespace Solar.Core.Tests;

/// <summary>DI 接线：新增/改名的服务都能从容器里解析出来。</summary>
public sealed class ServiceRegistrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "solar-di-" + Guid.NewGuid().ToString("N")[..8]);

    [Fact]
    public void CoreServicesResolve()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSolarCore(_root);

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<MinecraftPaths>());
        Assert.NotNull(provider.GetRequiredService<IVersionRepository>());
        Assert.NotNull(provider.GetRequiredService<IVersionScanner>());
        Assert.NotNull(provider.GetRequiredService<IVersionCreationService>());
        Assert.NotNull(provider.GetRequiredService<IGameLaunchService>());
        Assert.NotNull(provider.GetRequiredService<IModpackService>());
        Assert.NotNull(provider.GetRequiredService<IJavaDetector>());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
