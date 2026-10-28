using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Solar.Core;
using Solar.Core.Instances;
using Solar.Core.Modpacks;
using Solar.Core.Mods;

// 端到端验证内容数据层（不打真网的部分由单元测试覆盖，这里验证真实接口）：
//   dotnet run --project tools/Solar.ModProbe -- search sodium fabric 1.21.4
//   dotnet run --project tools/Solar.ModProbe -- search complementary 1.21.4 --type=shader
//   dotnet run --project tools/Solar.ModProbe -- detail modrinth AANobbMI fabric 1.21.4
//   dotnet run --project tools/Solar.ModProbe -- plan modrinth AANobbMI fabric 1.21.4
//   dotnet run --project tools/Solar.ModProbe -- install modrinth AANobbMI fabric 1.21.4
//   dotnet run --project tools/Solar.ModProbe -- modpack modrinth 1KVo5zza 1.21.4

// --type=<mod|shader|resourcepack|datapack|modpack> 可以出现在任意位置
var type = ParseType(args.FirstOrDefault(a => a.StartsWith("--type=", StringComparison.Ordinal))?[7..]);
var rest = args.Where(a => !a.StartsWith("--type=", StringComparison.Ordinal)).ToArray();

var command = rest.Length > 0 ? rest[0] : "search";
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
            rest.ElementAtOrDefault(1) ?? "sodium",
            rest.ElementAtOrDefault(2),
            rest.ElementAtOrDefault(3));

    case "detail":
        return await DetailAsync(
            ParseSource(rest.ElementAtOrDefault(1)),
            Require(rest, 2, "项目 Id"),
            rest.ElementAtOrDefault(3),
            rest.ElementAtOrDefault(4));

    case "plan":
        return await PlanAsync(
            ParseSource(rest.ElementAtOrDefault(1)),
            Require(rest, 2, "项目 Id"),
            rest.ElementAtOrDefault(3),
            rest.ElementAtOrDefault(4));

    case "install":
        return await InstallAsync(
            ParseSource(rest.ElementAtOrDefault(1)),
            Require(rest, 2, "项目 Id"),
            rest.ElementAtOrDefault(3),
            rest.ElementAtOrDefault(4));

    case "modpack":
        return await ModpackPlanAsync(
            ParseSource(rest.ElementAtOrDefault(1)),
            Require(rest, 2, "项目 Id"),
            rest.ElementAtOrDefault(3));

    default:
        Console.Error.WriteLine("未知命令。可用：search / detail / plan / install / modpack");
        return 1;
}

ModProjectType ParseType(string? value) => value?.ToLowerInvariant() switch
{
    "shader" or "shaders" => ModProjectType.Shader,
    "resourcepack" or "resourcepacks" => ModProjectType.ResourcePack,
    "datapack" or "datapacks" => ModProjectType.DataPack,
    "modpack" or "modpacks" => ModProjectType.Modpack,
    _ => ModProjectType.Mod,
};

async Task<int> SearchAsync(string text, string? loaderName, string? gameVersion)
{
    var loader = ParseLoader(loaderName);
    var query = new ModSearchQuery
    {
        Type = type,
        Text = text,
        Loader = loader,
        GameVersion = gameVersion,
        Limit = 10,
    };

    Console.WriteLine(
        $"搜索 “{text}” 类型={type.DisplayName()} 加载器={loader?.ToString() ?? "不限"} 版本={gameVersion ?? "不限"}");

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

    // Modrinth 没有独立的 datapack 项目类型；允许用 --type 覆盖，按 datapack 版本过滤
    var effectiveType = type != ModProjectType.Mod ? type : project.ProjectType;

    var versions = await catalogs.GetVersionsAsync(source, projectId, new ModVersionQuery
    {
        Type = effectiveType,
        GameVersion = gameVersion,
        Loader = loader,
    });

    Console.WriteLine($"  版本（{effectiveType.DisplayName()} / {gameVersion ?? "不限"} / {loader?.ToString() ?? "不限"}）: {versions.Count} 个");
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

    Console.WriteLine($"目标目录: {installer.GetContentDirectory(instance, project.ProjectType)}");

    var progress = new Progress<Solar.Core.Download.DownloadProgress>(p =>
        Console.WriteLine($"  [{p.CompletedFiles}/{p.TotalFiles}] {p.CurrentFile}"));

    // 默认不覆盖：已存在的同名文件应当被跳过而不是重下
    var result = await installer.InstallAsync(
        instance,
        project.ProjectType,
        plan,
        overwrite: false,
        progress: progress);

    Console.WriteLine($"完成：安装 {result.Installed} 个，跳过 {result.Skipped} 个");
    foreach (var file in result.Files)
    {
        Console.WriteLine($"  {file}");
    }

    return 0;
}

async Task<int> ModpackPlanAsync(ModSource source, string projectId, string? gameVersion)
{
    var project = await catalogs.GetProjectAsync(source, projectId);

    var versions = await catalogs.GetVersionsAsync(source, projectId, new ModVersionQuery
    {
        Type = ModProjectType.Modpack,
        GameVersion = gameVersion,
        IncludeIncompatible = string.IsNullOrWhiteSpace(gameVersion),
    });

    if (versions.Count == 0)
    {
        Console.Error.WriteLine($"没有找到适配 {gameVersion ?? "任意版本"} 的整合包版本。");
        return 2;
    }

    var version = versions
        .OrderBy(v => v.ReleaseType)
        .ThenByDescending(v => v.PublishedAt)
        .First();

    Console.WriteLine($"选用 {version.Name}（{version.File.FileName}, {version.File.Size / 1024} KB）");

    var service = provider.GetRequiredService<IModpackService>();
    var progress = new Progress<ModpackInstallProgress>(p => Console.WriteLine($"  [{p.Stage}] {p.Message}"));

    var plan = await service.CreatePlanAsync(version, progress);

    Console.WriteLine($"整合包: {plan.Name} {plan.Version} [{plan.Format}]");
    Console.WriteLine($"  MC {plan.MinecraftVersion} · {plan.Loader} {plan.LoaderVersion}");
    Console.WriteLine($"  文件 {plan.Files.Count} 个，共 {plan.TotalBytes / (1024.0 * 1024):0.#} MB，失败 {plan.FailedFiles.Count} 个");

    foreach (var file in plan.Files.Take(5))
    {
        Console.WriteLine($"    {file.RelativePath} ← {file.Url}");
    }

    foreach (var failed in plan.FailedFiles.Take(5))
    {
        Console.WriteLine($"    ✗ {failed}");
    }

    service.Cleanup(plan);
    return plan.FailedFiles.Count > 0 ? 2 : 0;
}

async Task<(ModProject Project, ModVersion Version)> PickAsync(
    ModSource source, string projectId, ModLoader? loader, string? gameVersion)
{
    var project = await catalogs.GetProjectAsync(source, projectId);
    var effectiveType = type != ModProjectType.Mod ? type : project.ProjectType;

    var versions = await catalogs.GetVersionsAsync(source, projectId, new ModVersionQuery
    {
        Type = effectiveType,
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
