using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Solar.Core;
using Solar.Core.Instances;
using Solar.Core.Mods;

// 端到端验证模组数据层（不打真网的部分由单元测试覆盖，这里验证真实接口）：
//   dotnet run --project tools/Solar.ModProbe -- search sodium fabric 1.21.4
//   dotnet run --project tools/Solar.ModProbe -- detail modrinth AANobbMI fabric 1.21.4
//   dotnet run --project tools/Solar.ModProbe -- plan modrinth AANobbMI fabric 1.21.4
//   dotnet run --project tools/Solar.ModProbe -- install modrinth AANobbMI fabric 1.21.4

var command = args.Length > 0 ? args[0] : "search";
var root = Path.Combine(Path.GetTempPath(), "solar-modprobe");

var services = new ServiceCollection();
services.AddLogging(b => b.AddSimpleConsole(o => o.SingleLine = true)
    .SetMinimumLevel(Environment.GetEnvironmentVariable("SOLAR_PROBE_VERBOSE") == "1" ? LogLevel.Debug : LogLevel.Warning));
services.AddSolarCore(root);

using var provider = services.BuildServiceProvider();
var catalogs = provider.GetRequiredService<IModCatalogService>();

switch (command)
{
    case "search":
        return await SearchAsync(
            args.ElementAtOrDefault(1) ?? "sodium",
            args.ElementAtOrDefault(2),
            args.ElementAtOrDefault(3));

    case "detail":
        return await DetailAsync(
            ParseSource(args.ElementAtOrDefault(1)),
            Require(args, 2, "项目 Id"),
            args.ElementAtOrDefault(3),
            args.ElementAtOrDefault(4));

    case "plan":
        return await PlanAsync(
            ParseSource(args.ElementAtOrDefault(1)),
            Require(args, 2, "项目 Id"),
            args.ElementAtOrDefault(3),
            args.ElementAtOrDefault(4));

    case "install":
        return await InstallAsync(
            ParseSource(args.ElementAtOrDefault(1)),
            Require(args, 2, "项目 Id"),
            args.ElementAtOrDefault(3),
            args.ElementAtOrDefault(4));

    default:
        Console.Error.WriteLine("未知命令。可用：search / detail / plan / install");
        return 1;
}

async Task<int> SearchAsync(string text, string? loaderName, string? gameVersion)
{
    var loader = ParseLoader(loaderName);
    var query = new ModSearchQuery
    {
        Text = text,
        Loader = loader,
        GameVersion = gameVersion,
        Limit = 10,
    };

    Console.WriteLine($"搜索 “{text}” 加载器={loader?.ToString() ?? "不限"} 版本={gameVersion ?? "不限"}");

    var page = await catalogs.SearchAsync(query);

    if (page.FailedSources.Count > 0)
    {
        Console.WriteLine($"⚠ 以下源不可用，结果可能不全：{string.Join(", ", page.FailedSources)}");
    }

    Console.WriteLine($"共 {page.TotalHits} 条，本次返回 {page.Items.Count} 条：");

    foreach (var item in page.Items)
    {
        var alsoOn = item.AlsoOn.Count > 0 ? $" (+{string.Join("/", item.AlsoOn.Select(a => a.Source))})" : "";
        Console.WriteLine($"  [{item.Source}] {item.Title}{alsoOn}");
        Console.WriteLine($"      {Truncate(item.Summary, 70)}");
        Console.WriteLine($"      下载 {item.Downloads:N0} · 加载器 {string.Join("/", item.Loaders)} · 图标 {item.IconUrl}");
    }

    return 0;
}

async Task<int> DetailAsync(ModSource source, string projectId, string? loaderName, string? gameVersion)
{
    var loader = ParseLoader(loaderName);
    var project = await catalogs.GetProjectAsync(source, projectId);

    Console.WriteLine($"{project.Title} [{project.Source}:{project.Id}]");
    Console.WriteLine($"  简介: {project.Summary}");
    Console.WriteLine($"  作者: {project.Author ?? "—"}");
    Console.WriteLine($"  下载: {project.Downloads:N0}");
    Console.WriteLine($"  加载器: {string.Join("/", project.Loaders)}");
    Console.WriteLine($"  分类: {string.Join("/", project.Categories)}");
    Console.WriteLine($"  正文: {(project.Body is null ? "无" : project.Body.Length + " 字符")}");
    Console.WriteLine($"  图集: {project.Gallery.Count} 张");

    var versions = await catalogs.GetVersionsAsync(source, projectId, new ModVersionQuery
    {
        GameVersion = gameVersion,
        Loader = loader,
    });

    Console.WriteLine($"  版本（{gameVersion ?? "不限"} / {loader?.ToString() ?? "不限"}）: {versions.Count} 个");
    foreach (var version in versions.Take(5))
    {
        var deps = version.Dependencies.Count(d => d.Kind == ModDependencyKind.Required);
        Console.WriteLine($"    {version.Name} [{version.ReleaseType}] {version.File.FileName} " +
                          $"({version.File.Size / 1024} KB, 前置 {deps})");
    }

    return 0;
}

async Task<int> PlanAsync(ModSource source, string projectId, string? loaderName, string? gameVersion)
{
    var loader = ParseLoader(loaderName);
    var (project, version) = await PickAsync(source, projectId, loader, gameVersion);

    var plan = await catalogs.BuildInstallPlanAsync(project, version, gameVersion, loader);

    Console.WriteLine($"安装 {project.Title} {version.Name} 的清单：");
    foreach (var item in plan.Items)
    {
        var state = item.IsAvailable
            ? item.AlreadyInstalled ? "已安装" : $"{item.Version!.File.FileName} ({item.Version.File.Size / 1024} KB)"
            : "找不到兼容版本";
        Console.WriteLine($"  [{item.Kind}] {item.Project.Title} → {state}");
    }

    Console.WriteLine($"需要下载 {plan.Pending.Count()} 个文件");
    return plan.HasUnavailable ? 2 : 0;
}

async Task<int> InstallAsync(ModSource source, string projectId, string? loaderName, string? gameVersion)
{
    var loader = ParseLoader(loaderName);
    var (project, version) = await PickAsync(source, projectId, loader, gameVersion);

    var plan = await catalogs.BuildInstallPlanAsync(project, version, gameVersion, loader);
    var installer = provider.GetRequiredService<IModInstaller>();

    // 用临时目录当“共享游戏目录”，避免污染真实数据
    var instance = new GameInstance
    {
        Id = "probe",
        Name = "probe",
        MinecraftVersion = gameVersion ?? "1.21.4",
        Loader = loader ?? ModLoader.Vanilla,
    };

    Console.WriteLine($"目标目录: {installer.GetModsDirectory(instance)}");

    var progress = new Progress<Solar.Core.Download.DownloadProgress>(p =>
        Console.WriteLine($"  [{p.CompletedFiles}/{p.TotalFiles}] {p.CurrentFile}"));

    // 默认不覆盖：已存在的同名文件应当被跳过而不是重下
    var result = await installer.InstallAsync(instance, plan, overwrite: false, progress);

    Console.WriteLine($"完成：安装 {result.Installed} 个，跳过 {result.Skipped} 个");
    foreach (var file in result.Files)
    {
        Console.WriteLine($"  {file}");
    }

    return 0;
}

async Task<(ModProject Project, ModVersion Version)> PickAsync(
    ModSource source, string projectId, ModLoader? loader, string? gameVersion)
{
    var project = await catalogs.GetProjectAsync(source, projectId);
    var versions = await catalogs.GetVersionsAsync(source, projectId, new ModVersionQuery
    {
        GameVersion = gameVersion,
        Loader = loader,
    });

    if (versions.Count == 0)
    {
        throw new InvalidOperationException(
            $"没有找到适配 {gameVersion ?? "任意版本"} / {loader?.ToString() ?? "任意加载器"} 的版本。");
    }

    // 与安装清单里选前置换版本用的是同一条规则
    var version = versions
        .OrderBy(v => v.ReleaseType)
        .ThenByDescending(v => v.PublishedAt)
        .First();

    Console.WriteLine($"选用版本: {version.Name} ({version.File.FileName})");
    return (project, version);
}

static ModSource ParseSource(string? value) => value?.ToLowerInvariant() switch
{
    "modrinth" or "mr" or null => ModSource.Modrinth,
    "curseforge" or "cf" => ModSource.CurseForge,
    _ => throw new ArgumentException($"未知来源: {value}（可用 modrinth / curseforge）"),
};

static ModLoader? ParseLoader(string? value) => value?.ToLowerInvariant() switch
{
    "fabric" => ModLoader.Fabric,
    "forge" => ModLoader.Forge,
    "neoforge" => ModLoader.NeoForge,
    "quilt" => ModLoader.Quilt,
    "vanilla" => ModLoader.Vanilla,
    _ => null,
};

static string Require(string[] values, int index, string name) =>
    values.ElementAtOrDefault(index)
    ?? throw new ArgumentException($"缺少参数：{name}");

static string Truncate(string value, int max) =>
    value.Length <= max ? value : value[..max] + "…";
