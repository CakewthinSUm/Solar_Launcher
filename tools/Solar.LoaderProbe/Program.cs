using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Solar.Core;
using Solar.Core.Instances;
using Solar.Core.Java;
using Solar.Core.Loaders;

// 端到端验证加载器安装：
//   dotnet run --project tools/Solar.LoaderProbe -- <loader> <mcVersion> <loaderVersion>
// 例：dotnet run --project tools/Solar.LoaderProbe -- fabric 1.21.4 0.20.1

var loaderName = args.Length > 0 ? args[0] : "fabric";
var mcVersion = args.Length > 1 ? args[1] : "1.21.4";
var loaderVersion = args.Length > 2 ? args[2] : "";

var loader = loaderName.ToLowerInvariant() switch
{
    "fabric" => ModLoader.Fabric,
    "quilt" => ModLoader.Quilt,
    "forge" => ModLoader.Forge,
    "neoforge" => ModLoader.NeoForge,
    _ => throw new ArgumentException($"未知加载器: {loaderName}"),
};

var root = Path.Combine(Path.GetTempPath(), "solar-loader");
var services = new ServiceCollection();
services.AddLogging(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Information));
services.AddSolarCore(root);

using var provider = services.BuildServiceProvider();

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

    var versionJson = Path.Combine(root, "SolarShared", "versions", versionId, versionId + ".json");
    Console.WriteLine($"版本 JSON: {(File.Exists(versionJson) ? "存在" : "缺失")}");

    // 检查 processor 产出的客户端 artifact
    var librariesRoot = Path.Combine(root, "SolarShared", "libraries");
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
