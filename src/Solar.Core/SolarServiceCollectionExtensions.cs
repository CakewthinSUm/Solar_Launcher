using Microsoft.Extensions.DependencyInjection;
using Solar.Core.Instances;
using Solar.Core.Java;
using Solar.Core.Minecraft;
using Solar.Core.Paths;
using Solar.Core.Settings;

namespace Solar.Core;

public static class SolarServiceCollectionExtensions
{
    /// <summary>注册核心层服务。UI 层只需要调用这一个方法。</summary>
    public static IServiceCollection AddSolarCore(this IServiceCollection services, string? dataRoot = null)
    {
        var paths = new AppPaths(dataRoot);
        paths.EnsureCreated();

        services.AddSingleton(paths);
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IInstanceRepository, InstanceRepository>();
        services.AddSingleton<IJavaDetector, JavaDetector>();

        services.AddHttpClient<IVersionManifestService, VersionManifestService>(ConfigureHttpClient);
        services.AddHttpClient<IVersionResolver, VersionResolver>(ConfigureHttpClient);

        return services;
    }

    private static void ConfigureHttpClient(HttpClient client)
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SolarLauncher/0.1");
        client.Timeout = TimeSpan.FromMinutes(5);
    }
}
