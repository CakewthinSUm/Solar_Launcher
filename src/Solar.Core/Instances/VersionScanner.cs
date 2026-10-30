using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Solar.Core.Json;
using Solar.Core.Models;
using Solar.Core.Paths;
using Solar.Core.Settings;

namespace Solar.Core.Instances;

/// <summary>在游戏目录里发现的一个版本（<c>versions/&lt;id&gt;/&lt;id&gt;.json</c>）。</summary>
public sealed record ScannedVersion(
    string VersionId,
    ModLoader Loader,
    string? LoaderVersion,
    string MinecraftVersion,
    string? Type = null,
    string? Directory = null,
    bool InGameDirectory = false)
{
    /// <summary>显示名：加载器版本带上加载器名，原版直接用版本号。</summary>
    public string DisplayName => Loader == ModLoader.Vanilla
        ? MinecraftVersion
        : $"{MinecraftVersion} {Loader} {LoaderVersion}".Trim();
}

public interface IVersionScanner
{
    /// <summary>列出游戏目录里可用的全部版本；目录不存在时返回空列表。</summary>
    IReadOnlyList<ScannedVersion> Scan(CancellationToken ct = default);

    /// <summary>
    /// 把游戏目录里还没登记成实例的版本导入实例列表，返回新导入的实例。
    /// 已经存在的版本（同一个 <see cref="GameInstance.EffectiveVersionId"/>）不会重复导入。
    /// </summary>
    Task<IReadOnlyList<GameInstance>> ImportAsync(CancellationToken ct = default);
}

/// <summary>
/// 扫描游戏目录的 <c>versions/</c>，把里面的版本变成实例。
/// <para>
/// 用途是接管已有安装：用户从官方启动器或别的启动器装过的版本，
/// 不必在 Solar 里重新下载一遍。对齐 PCL 的逻辑：
/// 版本隔离就是「版本文件夹即游戏目录」，所以导入时只需要决定隔离开关——
/// 版本夹里已有非空的 mods/saves 时无论全局档位都隔离（零拷贝直接可见），
/// 否则按档位与版本特征（可装 Mod / 非正式版）判定。
/// </para>
/// </summary>
public sealed partial class VersionScanner(
    GameFileLocator locator,
    IInstanceRepository repository,
    ISettingsService settings,
    ILogger<VersionScanner> logger) : IVersionScanner
{
    public IReadOnlyList<ScannedVersion> Scan(CancellationToken ct = default)
    {
        var result = new List<ScannedVersion>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var externalVersions = Path.GetFullPath(locator.ExternalVersions);

        foreach (var root in locator.VersionRoots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            // 共享游戏目录下的 versions/ 才有"版本夹即目录"的语义；
            // SolarShared/versions 里的版本夹混着共享资源，不能直接当游戏目录
            var inGameDirectory = string.Equals(
                Path.GetFullPath(root), externalVersions, StringComparison.OrdinalIgnoreCase);

            foreach (var directory in EnumerateVersionDirectories(root, ct))
            {
                ct.ThrowIfCancellationRequested();

                var versionId = Path.GetFileName(directory);
                var jsonPath = Path.Combine(directory, versionId + ".json");

                // 目录存在但没有 JSON 的不算版本：加载器安装器会先建目录再补齐
                if (!File.Exists(jsonPath) || !seen.Add(versionId))
                {
                    continue;
                }

                if (TryRead(jsonPath, out var detail))
                {
                    result.Add(Classify(versionId, detail, directory, inGameDirectory));
                }
                else
                {
                    logger.LogWarning("跳过无法解析的版本 JSON: {Path}", jsonPath);
                }
            }
        }

        // 新版本排前面；同一游戏版本下原版在前，加载器版本随后
        return result
            .OrderByDescending(v => v.MinecraftVersion, VersionComparer.Instance)
            .ThenBy(v => v.Loader == ModLoader.Vanilla ? 0 : 1)
            .ThenBy(v => v.VersionId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<GameInstance>> ImportAsync(CancellationToken ct = default)
    {
        var existing = await repository.GetAllAsync(ct).ConfigureAwait(false);
        var known = existing
            .Select(i => i.EffectiveVersionId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var imported = new List<GameInstance>();

        foreach (var version in Scan(ct))
        {
            if (!known.Add(version.VersionId))
            {
                continue;
            }

            var scope = settings.Current.NewInstanceIsolationScope;
            var unstable = VersionIsolationPolicy.IsUnstable(version.Type, version.VersionId)
                           || VersionIsolationPolicy.IsUnstable(version.Type, version.MinecraftVersion);
            var modable = version.Loader != ModLoader.Vanilla;

            // PCL 的自动检测：版本夹里已有玩家内容（非空 mods/saves）时，
            // 无论全局档位都把游戏目录指过去，内容零拷贝直接可见。
            var hasContent = version.Directory is not null && HasPlayerContent(version.Directory);

            var instance = new GameInstance
            {
                // Id 由版本号派生，重复扫描不会产生第二份
                Id = BuildId(version.VersionId),
                Name = version.DisplayName,
                MinecraftVersion = version.MinecraftVersion,
                Loader = version.Loader,
                LoaderVersion = version.LoaderVersion,
                VersionId = version.VersionId,

                // 文件已经在这份游戏目录里了，不需要再装一次
                LoaderInstalled = true,

                // 隔离后游戏目录就是该版本的版本文件夹，两处版本根都能定位到
                VersionIsolation = hasContent
                                   || VersionIsolationPolicy.ShouldIsolate(scope, modable, unstable),
            };

            try
            {
                await repository.SaveAsync(instance, ct).ConfigureAwait(false);
                imported.Add(instance);
            }
            catch (Exception ex)
            {
                // 单个版本写盘失败不应该让整次扫描失败
                logger.LogWarning(ex, "导入版本 {VersionId} 失败", version.VersionId);
            }
        }

        if (imported.Count > 0)
        {
            logger.LogInformation("从游戏目录导入 {Count} 个版本", imported.Count);
        }

        return imported;
    }

    private static IEnumerable<string> EnumerateVersionDirectories(string root, CancellationToken ct)
    {
        string[] directories;
        try
        {
            directories = Directory.GetDirectories(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 目录被占用或没有权限时按"没有版本"处理，不让启动器打不开
            yield break;
        }

        foreach (var directory in directories)
        {
            ct.ThrowIfCancellationRequested();
            yield return directory;
        }
    }

    private static bool TryRead(string jsonPath, out VersionDetail detail)
    {
        try
        {
            var json = File.ReadAllText(jsonPath);
            var parsed = JsonSerializer.Deserialize<VersionDetail>(json, SolarJson.Options);

            // 空对象或纯数组之类的文件不是版本 JSON
            if (parsed is not null && (!string.IsNullOrWhiteSpace(parsed.MainClass)
                                       || !string.IsNullOrWhiteSpace(parsed.InheritsFrom)
                                       || !string.IsNullOrWhiteSpace(parsed.MinecraftArguments)
                                       || parsed.Libraries.Count > 0))
            {
                detail = parsed;
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // 落到下面的 false
        }

        detail = null!;
        return false;
    }

    /// <summary>
    /// 从版本 JSON 判断它是原版还是某个加载器，并取出版本号。
    /// 优先看 <c>inheritsFrom</c>（加载器版本都会写它），
    /// 再看 id 里的关键字——个别第三方整合包不写 inheritsFrom。
    /// </summary>
    public static ScannedVersion Classify(string versionId, VersionDetail detail,
        string? directory = null, bool inGameDirectory = false)
    {
        var loader = DetectLoader(versionId, detail);
        var minecraftVersion = DetectMinecraftVersion(versionId, detail, loader);
        var loaderVersion = loader == ModLoader.Vanilla
            ? null
            : ExtractLoaderVersion(versionId, loader, minecraftVersion);

        return new ScannedVersion(versionId, loader, loaderVersion, minecraftVersion,
            detail.Type, directory, inGameDirectory);
    }

    /// <summary>版本文件夹下是否已有玩家的内容（非空 mods/ 或 saves/），对应 PCL 的自动检测。</summary>
    private static bool HasPlayerContent(string directory)
    {
        foreach (var name in (ReadOnlySpan<string>)["mods", "saves"])
        {
            var path = Path.Combine(directory, name);
            if (!Directory.Exists(path))
            {
                continue;
            }

            try
            {
                if (Directory.EnumerateFileSystemEntries(path).Any())
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 目录被占用或没有权限时按"没有内容"处理
            }
        }

        return false;
    }

    private static ModLoader DetectLoader(string versionId, VersionDetail detail)
    {
        // 四个 id 拼起来找关键字，任何一处命中都算
        var haystack = string.Join('\n',
            versionId.ToLowerInvariant(),
            detail.InheritsFrom?.ToLowerInvariant() ?? "",
            detail.MainClass?.ToLowerInvariant() ?? "");

        // NeoForge 必须排在 Forge 前面：它的 id 里也含 "forge"
        if (haystack.Contains("neoforge"))
        {
            return ModLoader.NeoForge;
        }

        if (haystack.Contains("forge"))
        {
            return ModLoader.Forge;
        }

        if (haystack.Contains("quilt"))
        {
            return ModLoader.Quilt;
        }

        if (haystack.Contains("fabric"))
        {
            return ModLoader.Fabric;
        }

        return ModLoader.Vanilla;
    }

    private static string DetectMinecraftVersion(string versionId, VersionDetail detail, ModLoader loader)
    {
        if (!string.IsNullOrWhiteSpace(detail.InheritsFrom))
        {
            return detail.InheritsFrom!;
        }

        if (loader == ModLoader.Vanilla)
        {
            return versionId;
        }

        // 没有 inheritsFrom 的整合包：从 id 里截出形如 1.20.1 的那一段
        var match = MinecraftVersionPattern().Match(versionId);
        return match.Success ? match.Value : versionId;
    }

    /// <summary>
    /// 从版本 id 里取加载器自己的版本号：
    /// <c>1.20.1-forge-47.2.0</c> → <c>47.2.0</c>；
    /// <c>fabric-loader-0.16.9-1.21.4</c> → <c>0.16.9</c>（末尾的游戏版本要剥掉）。
    /// </summary>
    public static string? ExtractLoaderVersion(string versionId, ModLoader loader, string minecraftVersion)
    {
        foreach (var marker in GetVersionMarkers(loader))
        {
            var index = versionId.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            var remainder = versionId[(index + marker.Length)..];

            // 1.7.10-Forge10.13.4 这种没有分隔符，先剥掉可选的前缀 v
            remainder = remainder.TrimStart('-', '_', ' ', '.');
            if (remainder.Length > 1 && (remainder[0] is 'v' or 'V') && char.IsDigit(remainder[1]))
            {
                remainder = remainder[1..];
            }

            // fabric-loader-0.16.9-1.21.4：把结尾的游戏版本去掉
            var suffix = "-" + minecraftVersion;
            if (!string.IsNullOrEmpty(minecraftVersion)
                && remainder.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                remainder = remainder[..^suffix.Length];
            }

            remainder = remainder.TrimEnd('-', '_', ' ', '.');

            if (remainder.Length > 0 && char.IsDigit(remainder[0]))
            {
                return remainder;
            }
        }

        return null;
    }

    /// <summary>
    /// 每个加载器可能的 id 片段，按从具体到宽泛排列。
    /// Fabric/Quilt 先试带 <c>-loader</c> 的完整片段，否则会把 "loader" 当成版本号。
    /// </summary>
    private static IReadOnlyList<string> GetVersionMarkers(ModLoader loader) => loader switch
    {
        ModLoader.Fabric => ["fabric-loader", "fabric"],
        ModLoader.Quilt => ["quilt-loader", "quilt"],
        ModLoader.NeoForge => ["neoforge"],
        ModLoader.Forge => ["forge"],
        _ => [],
    };

    /// <summary>版本号会被用作目录名，去掉 Windows 不允许的字符并限长。</summary>
    public static string BuildId(string versionId)
    {
        var builder = new StringBuilder(versionId.Length);
        foreach (var ch in versionId)
        {
            builder.Append(Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch);
        }

        var id = builder.ToString().Trim().TrimEnd('.');
        return id.Length switch
        {
            0 => Guid.NewGuid().ToString("N")[..12],
            > 64 => id[..64],
            _ => id,
        };
    }

    [GeneratedRegex(@"\d+\.\d+(?:\.\d+)?")]
    private static partial Regex MinecraftVersionPattern();

    /// <summary>
    /// 版本号比较：按点分段，数字段按数值比，非数字段按字符串比。
    /// 这样 1.21.10 才会排在 1.21.9 前面（纯字符串比较会反过来）。
    /// </summary>
    private sealed class VersionComparer : IComparer<string>
    {
        public static readonly VersionComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            var left = x.Split('.', '-', '_');
            var right = y.Split('.', '-', '_');
            var length = Math.Min(left.Length, right.Length);

            for (var i = 0; i < length; i++)
            {
                var result = CompareSegment(left[i], right[i]);
                if (result != 0)
                {
                    return result;
                }
            }

            return left.Length.CompareTo(right.Length);
        }

        private static int CompareSegment(string left, string right)
        {
            var leftIsNumber = int.TryParse(left, out var leftNumber);
            var rightIsNumber = int.TryParse(right, out var rightNumber);

            return (leftIsNumber, rightIsNumber) switch
            {
                (true, true) => leftNumber.CompareTo(rightNumber),

                // 正式版比同号的预发布版新（1.21.4 与 1.21.4-pre1）
                (true, false) => 1,
                (false, true) => -1,
                _ => string.Compare(left, right, StringComparison.OrdinalIgnoreCase),
            };
        }
    }
}
