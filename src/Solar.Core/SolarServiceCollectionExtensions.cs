using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
using Solar.Core.Mods;
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

        AddModServices(services);
        AddAddonServices(services);

        return services;
    }

    /// <summary>
    /// 模组下载相关的服务。
    /// <para>
    /// <see cref="ModHttp"/> 负责“换源重试”，它内部的单次请求自己设 10 秒超时，
    /// 所以这里的 HttpClient 超时只是兜底，不能设得比单次超时还短。
    /// 文件下载走 <see cref="ModInstaller"/> 里独立的 client，不经过这里。
    /// </para>
    /// </summary>
    private static void AddModServices(IServiceCollection services)
    {
        services.AddSingleton<ModEndpoints>();
        services.AddSingleton<ModCache>();

        // 目录与安装器都是单例，ModHttp 也必须跟着单例，
        // 否则会变成“单例持有瞬时依赖”。HttpClient 由工厂创建，底层 handler 仍会被池化。
        services.AddHttpClient(ModHttpClientName, ConfigureModHttpClient);
        services.AddSingleton(sp => new ModHttp(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(ModHttpClientName),
            sp.GetRequiredService<ModEndpoints>(),
            sp.GetRequiredService<ILogger<ModHttp>>()));

        services.AddSingleton<IModCatalog, ModrinthCatalog>();
        services.AddSingleton<IModCatalog, CurseForgeCatalog>();
        services.AddSingleton<IModCatalogService, ModCatalogService>();
        services.AddSingleton<IModInstaller, ModInstaller>();
    }

    /// <summary>
    /// 创建实例时用到的附加组件（OptiFine / LiteLoader）与编排服务。
    /// <para>
    /// Fabric API 与 OptiFabric 不在这里单独注册客户端：它们复用
    /// <see cref="Mods.IModCatalogService"/>，和「管理 → Mods」共用同一套源与镜像降级。
    /// </para>
    /// </summary>
    private static void AddAddonServices(IServiceCollection services)
    {
        services.AddSingleton<IInstanceCreationService, InstanceCreationService>();
        services.AddSingleton<ILiteLoaderInstaller, LiteLoaderInstaller>();
        services.AddSingleton<IAddonCatalog, AddonCatalog>();

        // OptiFine 安装器有 7~9MB，超时放宽到与加载器安装器一致
        services.AddHttpClient<IOptiFineService, OptiFineService>(client =>
        {
            ConfigureHttpClient(client);
            client.Timeout = TimeSpan.FromMinutes(15);
        });

        services.AddHttpClient<ILiteLoaderService, LiteLoaderService>(ConfigureHttpClient);
    }

    /// <summary>模组 API 客户端的名字，与文件下载用的 client 区分开。</summary>
    private const string ModHttpClientName = "mods";

    private static void ConfigureModHttpClient(HttpClient client)
    {
        // Modrinth 明确要求 UA 能识别调用方，通用的库字符串容易被限流。
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "SolarLauncher/0.1 (+https://github.com/CakewthinSUm/Solar_Launcher)");
        client.Timeout = TimeSpan.FromMinutes(2);
    }

    private static void ConfigureHttpClient(HttpClient client)
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SolarLauncher/0.1");
        client.Timeout = TimeSpan.FromMinutes(5);
    }
}
