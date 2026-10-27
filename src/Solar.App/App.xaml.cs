using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Solar.App.Services;
using Solar.App.ViewModels;
using Solar.App.Views;
using Solar.Core;
using Solar.Core.Accounts;
using Solar.Core.Instances;
using Solar.Core.Java;
using Solar.Core.Minecraft;
using Solar.Core.Paths;
using Solar.Core.Settings;

namespace Solar.App;

public partial class App : Application
{
    private Window? _window;

    /// <summary>全局服务容器。页面通过 <see cref="Services"/> 或构造函数注入取用。</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
        Services = ConfigureServices();

        // 未捕获异常写入日志目录，便于排查启动期 XAML 问题
        UnhandledException += (_, e) =>
        {
            try
            {
                var logDirectory = Path.Combine(AppContext.BaseDirectory, "SolarData", "logs");
                Directory.CreateDirectory(logDirectory);
                File.AppendAllText(
                    Path.Combine(logDirectory, "crash.log"),
                    $"[{DateTimeOffset.Now:O}] {e.Exception}\n\n");
            }
            catch
            {
                // 记录失败时不影响原有崩溃流程
            }
        };
    }

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        services.AddLogging(builder =>
        {
            builder.AddDebug();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        services.AddSolarCore();

        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IDialogService, DialogService>();

        services.AddTransient<HomeViewModel>();
        services.AddTransient<InstancesViewModel>();
        services.AddTransient<InstanceSetupViewModel>();
        services.AddTransient<DownloadViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<AccountsViewModel>();
        services.AddTransient<HomePage>();
        services.AddTransient<InstancesPage>();
        services.AddTransient<InstanceSetupPage>();
        services.AddTransient<DownloadPage>();
        services.AddTransient<SettingsPage>();
        services.AddTransient<AccountsPage>();

        return services.BuildServiceProvider();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var paths = Services.GetRequiredService<AppPaths>();
        var logger = Services.GetRequiredService<ILogger<App>>();
        logger.LogInformation("Solar Launcher 启动，数据目录: {Root}", paths.Root);

        // 设置必须在任何服务读取之前加载：账户登录、下载镜像都依赖它。
        // 之前只在设置页导航时加载，导致启动后直接用空设置。
        try
        {
            await Services.GetRequiredService<ISettingsService>().LoadAsync();
            await Services.GetRequiredService<IAccountStore>().LoadAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "加载本地配置失败，将使用默认值");
        }

        _window = new MainWindow();
        _window.Activate();
    }

    /// <summary>主窗口引用，供对话框等需要 XamlRoot 的地方使用。</summary>
    public static Window? MainWindow { get; private set; }

    /// <summary>
    /// 从容器取服务。页面需要无参构造函数才能被 Frame 激活，
    /// 因此由无参构造函数转发到这里完成依赖注入。
    /// </summary>
    public static T GetService<T>() where T : notnull => Services.GetRequiredService<T>();

    internal static void RegisterMainWindow(Window window) => MainWindow = window;
}
