using Microsoft.Extensions.Logging;
using Solar.Core.Download;
using Solar.Core.Instances;

namespace Solar.Core.Mods;

public interface IModInstaller
{
    /// <summary>
    /// 某类内容在实例里的落点：
    /// <c>mods</c> / <c>resourcepacks</c> / <c>shaderpacks</c> / <c>saves/&lt;world&gt;/datapacks</c>。
    /// 整合包没有固定目录（它自己就是一个实例），调用会抛异常。
    /// </summary>
    string GetContentDirectory(GameInstance instance, ModProjectType type, string? world = null);

    /// <summary>某个文件是否已经装在这个实例里（按文件名判断）。</summary>
    bool IsInstalled(GameInstance instance, ModProjectType type, ModFile file, string? world = null);

    /// <summary>实例的存档列表（<c>saves</c> 下带 <c>level.dat</c> 的目录）。</summary>
    IReadOnlyList<string> GetWorlds(GameInstance instance);

    /// <summary>安装清单。已存在的文件按 <paramref name="overwrite"/> 决定覆盖还是跳过。</summary>
    Task<ModInstallResult> InstallAsync(
        GameInstance instance,
        ModProjectType type,
        ModInstallPlan plan,
        string? world = null,
        bool overwrite = false,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default);
}

/// <summary>
/// 把下载好的内容放进实例实际使用的游戏目录。
/// <para>
/// 四个约束：
/// 一是必须写到实例实际使用的游戏目录（<see cref="InstanceGameDirectory.Resolve"/>），
/// 否则开了版本隔离的实例读不到；
/// 二是保留上游文件名，不做重命名——更新检测和社区排障都依赖原始文件名；
/// 三是数据包只对具体存档生效，必须写进 <c>saves/&lt;world&gt;/datapacks</c>；
/// 四是先下到 <c>.part</c> 再改名，中断时不会留下一个看起来正常的半截文件。
/// </para>
/// </summary>
public sealed class ModInstaller(
    InstanceGameDirectory gameDirectories,
    IModFileDownloader downloader,
    ILogger<ModInstaller> logger) : IModInstaller
{
    public string GetContentDirectory(GameInstance instance, ModProjectType type, string? world = null)
    {
        var root = gameDirectories.Resolve(instance);

        if (type == ModProjectType.DataPack)
        {
            if (string.IsNullOrWhiteSpace(world))
            {
                throw new InvalidOperationException("数据包必须指定要安装到的存档。");
            }

            // world 来自存档列表，但仍做一次包含性检查，避免外部传入 ../ 之类
            var name = Path.GetFileName(world.Trim());
            if (!string.Equals(name, world.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"无效的存档名：{world}");
            }

            return Path.Combine(root, "saves", name, "datapacks");
        }

        var folder = type.ContentFolder()
                     ?? throw new InvalidOperationException(
                         $"{type.DisplayName()} 没有可安装到的目录；整合包需要创建新实例。");

        return Path.Combine(root, folder);
    }

    public bool IsInstalled(GameInstance instance, ModProjectType type, ModFile file, string? world = null)
    {
        if (type == ModProjectType.DataPack && string.IsNullOrWhiteSpace(world))
        {
            return false;
        }

        return File.Exists(Path.Combine(GetContentDirectory(instance, type, world), file.FileName));
    }

    public IReadOnlyList<string> GetWorlds(GameInstance instance)
    {
        var saves = Path.Combine(gameDirectories.Resolve(instance), "saves");
        if (!Directory.Exists(saves))
        {
            return [];
        }

        var worlds = new List<(string Name, DateTime Modified)>();

        foreach (var directory in Directory.EnumerateDirectories(saves))
        {
            // 只有带 level.dat 的才是存档；`saves` 里偶尔会有用户放的其他目录
            if (!File.Exists(Path.Combine(directory, "level.dat")))
            {
                continue;
            }

            var name = Path.GetFileName(directory);
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith('.'))
            {
                continue;
            }

            worlds.Add((name, Directory.GetLastWriteTimeUtc(directory)));
        }

        return worlds
            .OrderByDescending(w => w.Modified)
            .Select(w => w.Name)
            .ToList();
    }

    public async Task<ModInstallResult> InstallAsync(
        GameInstance instance,
        ModProjectType type,
        ModInstallPlan plan,
        string? world = null,
        bool overwrite = false,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var directory = GetContentDirectory(instance, type, world);
        Directory.CreateDirectory(directory);

        var pending = plan.Pending.ToList();
        var installed = 0;
        var skipped = 0;
        var files = new List<string>();

        for (var i = 0; i < pending.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var item = pending[i];
            var version = item.Version!;
            var target = Path.Combine(directory, version.File.FileName);

            if (File.Exists(target) && !overwrite)
            {
                skipped++;
                files.Add(version.File.FileName);
                logger.LogInformation("跳过已存在的内容文件: {File}", version.File.FileName);
                continue;
            }

            progress?.Report(new DownloadProgress(installed, pending.Count, 0, 0)
            {
                CurrentFile = version.File.FileName,
            });

            await downloader.DownloadAsync(version.File, target, ct).ConfigureAwait(false);

            installed++;
            files.Add(version.File.FileName);
            logger.LogInformation("已安装 {Type} {Name} → {Path}", type.DisplayName(), item.Project.Title, target);
        }

        progress?.Report(new DownloadProgress(installed, pending.Count, 0, 0));
        return new ModInstallResult(installed, skipped, files);
    }
}
