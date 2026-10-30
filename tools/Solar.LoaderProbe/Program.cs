using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Solar.Core;
using Solar.Core.Addons;
using Solar.Core.Versions;
using Solar.Core.Java;
using Solar.Core.Loaders;
using Solar.Core.Minecraft;
using Solar.Core.Paths;

// 端到端验证加载器与附加组件的安装：
//   dotnet run --project tools/Solar.LoaderProbe -- <loader> <mcVersion> <loaderVersion>
// 例：dotnet run --project tools/Solar.LoaderProbe -- fabric 1.21.4 0.20.1
//     dotnet run --project tools/Solar.LoaderProbe -- optifine 1.20.1
//     dotnet run --project tools/Solar.LoaderProbe -- liteloader 1.12.2
//     dotnet run --project tools/Solar.LoaderProbe -- fabricapi 1.21.4

var loaderName = args.Length > 0 ? args[0] : "fabric";
var mcVersion = args.Length > 1 ? args[1] : "1.21.4";
var loaderVersion = args.Length > 2 ? args[2] : "";

var root = Path.Combine(Path.GetTempPath(), "solar-loader");
var services = new ServiceCollection();
services.AddLogging(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Information));
services.AddSolarCore(root);

using var provider = services.BuildServiceProvider();

// 附加组件走各自的命令，不参与下面的加载器流程
switch (loaderName.ToLowerInvariant())
{
    case "optifine":
        return await ProbeOptiFineAsync(provider, mcVersion, loaderVersion);

    case "liteloader":
        return await ProbeLiteLoaderAsync(provider, mcVersion);

    case "fabricapi":
        return await ProbeFabricApiAsync(provider, mcVersion);
}

var loader = loaderName.ToLowerInvariant() switch
{
    "fabric" => ModLoader.Fabric,
    "quilt" => ModLoader.Quilt,
    "forge" => ModLoader.Forge,
    "neoforge" => ModLoader.NeoForge,
    _ => throw new ArgumentException($"未知加载器: {loaderName}"),
};

var versions = provider.GetRequiredService<ILoaderVersionService>();
var installer = provider.GetRequiredService<ILoaderInstaller>();
var javaDetector = provider.GetRequiredService<IJavaDetector>();

if (string.IsNullOrEmpty(loaderVersion))
{
    Console.WriteLine($"正在获取 {loader} 在 MC {mcVersion} 上可用的版本…");
    var available = await versions.GetVersionsAsync(loader, mcVersion);

    if (available.Count == 0)
    {
        Console.Error.WriteLine($"没有可用的 {loader} 版本。");
        return 1;
    }

    Console.WriteLine($"共 {available.Count} 个，前 5 个：");
    foreach (var v in available.Take(5))
    {
        Console.WriteLine($"  {v.Version}{(v.IsStable ? "" : " (beta)")}");
    }

    loaderVersion = available[0].Version;
    Console.WriteLine($"选用: {loaderVersion}");
}

var runtimes = await javaDetector.DetectAsync();
if (runtimes.Count == 0)
{
    Console.Error.WriteLine("没有 Java，无法安装。");
    return 1;
}

var javaPath = runtimes[0].Path;
Console.WriteLine($"Java: {runtimes[0]}");
Console.WriteLine();

var lastStage = "";
var progress = new Progress<LoaderInstallProgress>(p =>
{
    var line = $"{p.Stage} ({p.Current}/{p.Total})";
    if (line != lastStage)
    {
        lastStage = line;
        Console.WriteLine($"  {line}");
    }
});

try
{
    var versionId = await installer.InstallAsync(loader, mcVersion, loaderVersion, javaPath, progress);

    Console.WriteLine();
    Console.WriteLine($"安装完成，版本 Id: {versionId}");

    var versionJson = Path.Combine(root, ".minecraft", "versions", versionId, versionId + ".json");
    Console.WriteLine($"版本 JSON: {(File.Exists(versionJson) ? "存在" : "缺失")}");

    // 检查 processor 产出的客户端 artifact
    var librariesRoot = Path.Combine(root, ".minecraft", "libraries");
    var clientJars = Directory.Exists(librariesRoot)
        ? Directory.GetFiles(librariesRoot, "*.jar", SearchOption.AllDirectories)
            .Where(f => f.Contains("client", StringComparison.OrdinalIgnoreCase))
            .ToList()
        : [];

    Console.WriteLine($"含 client 的 jar: {clientJars.Count} 个");
    foreach (var jar in clientJars.Take(5))
    {
        var size = new FileInfo(jar).Length;
        Console.WriteLine($"  {Path.GetFileName(jar)} ({size:N0} 字节)");
    }

    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"安装失败: {ex}");
    return 1;
}

// ---- 附加组件的探针 ----

static async Task<int> ProbeOptiFineAsync(IServiceProvider provider, string mcVersion, string wanted)
{
    var optifine = provider.GetRequiredService<IOptiFineService>();
    var builds = await optifine.GetBuildsAsync(mcVersion);

    if (builds.Count == 0)
    {
        Console.Error.WriteLine($"MC {mcVersion} 没有可用的 OptiFine。");
        return 1;
    }

    Console.WriteLine($"共 {builds.Count} 个构建，前 5 个：");
    foreach (var build in builds.Take(5))
    {
        Console.WriteLine($"  {build.DisplayVersion}  ({build.FileName})");
    }

    var selected = string.IsNullOrEmpty(wanted)
        ? builds.FirstOrDefault(b => !OptiFineService.IsPreview(b.Patch)) ?? builds[0]
        : builds.FirstOrDefault(b => b.DisplayVersion.Equals(wanted, StringComparison.OrdinalIgnoreCase))
          ?? builds[0];

    Console.WriteLine($"选用: {selected.DisplayVersion}");
    Console.WriteLine($"安装器: {optifine.GetInstallerUrl(selected)}");

    // 安装 OptiFine 需要原版 jar 与 JSON，先把它们下载到位
    var resolver = provider.GetRequiredService<IVersionResolver>();
    var downloader = provider.GetRequiredService<Solar.Core.Download.IGameDownloader>();
    var javaDetector = provider.GetRequiredService<IJavaDetector>();

    var vanilla = await resolver.ResolveAsync(mcVersion);
    var scratch = new GameVersion { Id = "probe-optifine", MinecraftVersion = mcVersion };
    await downloader.DownloadAsync(scratch, vanilla);

    var locator = new GameFileLocator(provider.GetRequiredService<MinecraftPaths>());
    var javaPath = (await javaDetector.DetectAsync()).FirstOrDefault()?.Path
                   ?? throw new InvalidOperationException("没有 Java。");

    var lastStage = "";
    var progress = new Progress<LoaderInstallProgress>(p =>
    {
        var line = $"{p.Stage} ({p.Current}/{p.Total})";
        if (line != lastStage)
        {
            lastStage = line;
            Console.WriteLine($"  {line}");
        }
    });

    var versionId = await optifine.InstallAsync(
        selected,
        mcVersion,
        locator.FindVersionJar(mcVersion) ?? throw new InvalidOperationException("原版 jar 缺失"),
        locator.FindVersionJson(mcVersion) ?? throw new InvalidOperationException("原版 JSON 缺失"),
        javaPath,
        progress);

    Console.WriteLine($"安装完成，版本 Id: {versionId}");
    return 0;
}

static async Task<int> ProbeLiteLoaderAsync(IServiceProvider provider, string mcVersion)
{
    var liteLoader = provider.GetRequiredService<ILiteLoaderService>();
    var builds = await liteLoader.GetBuildsAsync(mcVersion);

    if (builds.Count == 0)
    {
        Console.Error.WriteLine($"MC {mcVersion} 没有可用的 LiteLoader。");
        return 1;
    }

    foreach (var build in builds)
    {
        Console.WriteLine($"  {build.Version}  tweakClass={build.TweakClass}");
        Console.WriteLine($"  jar: {liteLoader.GetJarUrl(build)}");
    }

    var installer = provider.GetRequiredService<ILiteLoaderInstaller>();
    var versionId = await installer.InstallAsync(builds[0], mcVersion);

    Console.WriteLine($"安装完成，版本 Id: {versionId}");
    return 0;
}

static async Task<int> ProbeFabricApiAsync(IServiceProvider provider, string mcVersion)
{
    var addons = provider.GetRequiredService<IAddonCatalog>();
    var versions = await addons.GetFabricApiAsync(mcVersion, ModLoader.Fabric);

    if (versions.Count == 0)
    {
        Console.Error.WriteLine($"MC {mcVersion} 没有可用的 Fabric API。");
        return 1;
    }

    foreach (var version in versions.Take(5))
    {
        Console.WriteLine($"  {version.VersionNumber}  {version.FileName}");
        Console.WriteLine($"    {version.Url}");
    }

    return 0;
}
