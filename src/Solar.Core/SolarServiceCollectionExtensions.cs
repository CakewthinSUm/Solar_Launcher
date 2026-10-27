using Microsoft.Extensions.DependencyInjection;
using Solar.Core.Accounts;
using Solar.Core.Addons;
using Solar.Core.Assets;
using Solar.Core.Content;
using Solar.Core.Download;
using Solar.Core.Instances;
using Solar.Core.Java;
using Solar.Core.Launch;
using Solar.Core.Loaders;
using Solar.Core.Memory;
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
        services.AddSingleton<InstanceGameDirectory>();
        services.AddSingleton<IContentManager, ContentManager>();
        services.AddSingleton<GameFileLocator>();
        services.AddSingleton<IVersionScanner, VersionScanner>();
        services.AddSingleton<ISystemMemoryInfo, WindowsSystemMemoryInfo>();
        services.AddSingleton<IMemoryAllocationService, MemoryAllocationService>();
        services.AddSingleton<IJavaDetector, JavaDetector>();

        services.AddSingleton<IAccountStore, AccountStore>();

        // 默认配置为空；实际取值每次从 ISettingsService 读取，用户改了立即生效
        services.AddSingleton(new AuthOptions());
        services.AddSingleton<IMicrosoftAuthService, MicrosoftAuthService>();

        services.AddSingleton<IAssetIndexService, AssetIndexService>();
        services.AddSingleton<IGameDownloader, GameDownloader>();
        services.AddSingleton<IGameLauncher, GameLauncher>();
        services.AddSingleton<LaunchContextFactory>();
        services.AddSingleton<IGameLaunchService, GameLaunchService>();

        services.AddHttpClient<IVersionManifestService, VersionManifestService>(ConfigureHttpClient);
        services.AddHttpClient<IVersionResolver, VersionResolver>(ConfigureHttpClient);
        services.AddHttpClient<IAssetIndexService, AssetIndexService>(ConfigureHttpClient);

        // 加载器元数据与 installer 下载走独立 client：
        // installer jar 有 7~9MB，超时要比普通请求宽松
        services.AddHttpClient<ILoaderVersionService, LoaderVersionService>(ConfigureHttpClient);
        services.AddHttpClient<ILoaderInstaller, LoaderInstaller>(client =>
        {
            ConfigureHttpClient(client);
            client.Timeout = TimeSpan.FromMinutes(15);
        });

        // 附加组件（OptiFine / LiteLoader / Modrinth）与创建实例的编排
        services.AddSingleton<IInstanceCreationService, InstanceCreationService>();
        services.AddSingleton<ILiteLoaderInstaller, LiteLoaderInstaller>();
        services.AddHttpClient<IOptiFineService, OptiFineService>(client =>
        {
            ConfigureHttpClient(client);
            client.Timeout = TimeSpan.FromMinutes(15);
        });
        services.AddHttpClient<ILiteLoaderService, LiteLoaderService>(ConfigureHttpClient);
        services.AddHttpClient<IModrinthService, ModrinthService>(ConfigureHttpClient);

        return services;
    }

    private static void ConfigureHttpClient(HttpClient client)
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SolarLauncher/0.1");
        client.Timeout = TimeSpan.FromMinutes(5);
    }
}
