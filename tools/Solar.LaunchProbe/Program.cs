using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Solar.Core;
using Solar.Core.Accounts;
using Solar.Core.Download;
using Solar.Core.Instances;
using Solar.Core.Java;
using Solar.Core.Launch;
using Solar.Core.Minecraft;
using Solar.Core.Paths;

// 验证"安装好的加载器版本能否真正启动进游戏"。
// 用合成账户绕过微软登录——这里只验证加载器链路，不验证登录。
//   dotnet run --project tools/Solar.LaunchProbe -- <版本Id> [根目录]

var versionId = args.Length > 0 ? args[0] : "fabric-loader-0.20.1-1.21.4";
var root = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "solar-loader");

var services = new ServiceCollection();
services.AddLogging(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
services.AddSolarCore(root);

using var provider = services.BuildServiceProvider();

var resolver = provider.GetRequiredService<IVersionResolver>();
var downloader = provider.GetRequiredService<IGameDownloader>();
var launcher = provider.GetRequiredService<IGameLauncher>();
var javaDetector = provider.GetRequiredService<IJavaDetector>();
var factory = provider.GetRequiredService<LaunchContextFactory>();
var paths = provider.GetRequiredService<AppPaths>();

Console.WriteLine($"版本: {versionId}");
Console.WriteLine($"根目录: {root}");

var runtimes = await javaDetector.DetectAsync();
if (runtimes.Count == 0)
{
    Console.Error.WriteLine("没有 Java。");
    return 1;
}

var instance = new GameInstance
{
    Id = "launchprobe",
    Name = "启动验证",
    MinecraftVersion = versionId,
};

try
{
    Console.WriteLine("解析版本…");
    var version = await resolver.ResolveAsync(versionId);
    Console.WriteLine($"  MainClass: {version.Detail.MainClass}");
    Console.WriteLine($"  合并后库数量: {version.Detail.Libraries.Count}");

    var requiredJava = version.Detail.JavaVersion?.MajorVersion ?? 8;
    var java = javaDetector.Select(runtimes, requiredJava)!;
    Console.WriteLine($"  Java: {java}");

    Console.WriteLine("补齐文件…");
    await downloader.DownloadAsync(instance, version);

    // 合成账户：离线 UUID + 占位令牌，只用于让参数完整
    var account = new AccountSession(
        Username: "TestPlayer",
        Uuid: "8667ba71-b85a-4004-af54-457a9734eed7",
        AccessToken: "0",
        Xuid: "",
        UserType: "msa");

    var context = factory.Create(instance, version, java.Path, account);

    var builder = new ArgumentBuilder();
    var classPath = ArgumentBuilder.BuildClassPath(context);
    var entries = classPath.Split(Path.PathSeparator);

    Console.WriteLine();
    Console.WriteLine($"classpath 条目: {entries.Length}");
    var missing = entries.Where(e => !File.Exists(e)).ToList();
    Console.WriteLine($"  缺失: {missing.Count}");
    foreach (var m in missing.Take(10))
    {
        Console.WriteLine($"    {m}");
    }

    if (missing.Count > 0)
    {
        Console.Error.WriteLine("classpath 有缺失文件，启动必然失败。");
        return 1;
    }

    Console.WriteLine();
    Console.WriteLine("启动游戏…");
    var process = launcher.Start(context);
    Console.WriteLine($"pid={process.Id}");

    // 加载器初始化通常 20~40 秒；能活过 90 秒基本说明进游戏了
    var exited = await Task.WhenAny(process.WaitForExitAsync(), Task.Delay(TimeSpan.FromSeconds(90)));

    var logDirectory = paths.Logs;
    var logFile = Directory.Exists(logDirectory)
        ? new DirectoryInfo(logDirectory).GetFiles("game-*.log").OrderByDescending(f => f.LastWriteTime).FirstOrDefault()
        : null;

    if (exited is Task t && t.IsCompleted && process.HasExited)
    {
        Console.WriteLine($"游戏在 90 秒内退出，退出码 {process.ExitCode}");
        if (logFile is not null)
        {
            Console.WriteLine("--- 日志尾部 ---");
            foreach (var line in ReadLogLines(logFile.FullName).TakeLast(25))
            {
                Console.WriteLine(line);
            }
        }

        return process.ExitCode;
    }

    Console.WriteLine("游戏运行超过 90 秒，视为启动成功。");

    // 先读日志再杀进程：GameLauncher 用 StreamWriter 持有日志文件，
    // 必须用 FileShare.ReadWrite 才能同时读取。
    if (logFile is not null)
    {
        Console.WriteLine("--- 日志关键行 ---");
        foreach (var line in ReadLogLines(logFile.FullName)
                     .Where(l => l.Contains("Fabric", StringComparison.OrdinalIgnoreCase)
                                 || l.Contains("Loading", StringComparison.OrdinalIgnoreCase)
                                 || l.Contains("Backend library", StringComparison.OrdinalIgnoreCase)
                                 || l.Contains("Sound engine", StringComparison.OrdinalIgnoreCase)
                                 || l.Contains("Setting user", StringComparison.OrdinalIgnoreCase))
                     .Take(15))
        {
            Console.WriteLine(line);
        }
    }

    process.Kill(entireProcessTree: true);
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"失败: {ex}");
    return 1;
}

/// <summary>
/// 读取正在被游戏进程写入的日志。
/// 默认的 File.ReadLines 会以独占方式打开，撞上 StreamWriter 的占用就抛 IOException。
/// </summary>
static string[] ReadLogLines(string path)
{
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    using var reader = new StreamReader(stream);

    var lines = new List<string>();
    while (reader.ReadLine() is { } line)
    {
        lines.Add(line);
    }

    return lines.ToArray();
}
