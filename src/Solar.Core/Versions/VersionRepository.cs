using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Solar.Core.Json;
using Solar.Core.Paths;

namespace Solar.Core.Versions;

public interface IVersionRepository
{
    /// <summary>扫描 MC 文件夹里的全部版本，合并各自的 <c>.solar/version.json</c>。</summary>
    Task<IReadOnlyList<GameVersion>> GetAllAsync(CancellationToken ct = default);

    Task<GameVersion?> GetAsync(string id, CancellationToken ct = default);

    /// <summary>把版本设置写进 <c>versions/&lt;id&gt;/.solar/version.json</c>。</summary>
    Task SaveSettingsAsync(GameVersion version, CancellationToken ct = default);

    /// <summary>删除整个版本文件夹（存档、模组一并删除）。</summary>
    Task DeleteAsync(string id, CancellationToken ct = default);

    /// <summary>重命名版本文件夹，同时改写版本 JSON 与 jar 的文件名。</summary>
    Task<GameVersion> RenameAsync(string oldId, string newId, CancellationToken ct = default);
}

/// <summary>
/// 版本仓库：<c>versions/</c> 目录就是唯一事实来源，没有索引文件。
/// 每个版本的用户设置单独存在 <c>versions/&lt;id&gt;/.solar/version.json</c>。
/// </summary>
public sealed class VersionRepository(
    MinecraftPaths paths,
    IVersionScanner scanner,
    ILogger<VersionRepository> logger) : IVersionRepository
{
    public Task<IReadOnlyList<GameVersion>> GetAllAsync(CancellationToken ct = default)
    {
        var result = scanner.Scan(ct)
            .Select(Load)
            .ToList();

        return Task.FromResult<IReadOnlyList<GameVersion>>(result);
    }

    public Task<GameVersion?> GetAsync(string id, CancellationToken ct = default)
    {
        var jsonPath = paths.GetVersionJsonPath(id);
        if (!VersionScanner.TryRead(jsonPath, out var detail))
        {
            return Task.FromResult<GameVersion?>(null);
        }

        var scanned = VersionScanner.Classify(id, detail, paths.GetVersionDirectory(id));
        return Task.FromResult<GameVersion?>(Load(scanned));
    }

    public async Task SaveSettingsAsync(GameVersion version, CancellationToken ct = default)
    {
        var directory = paths.GetVersionSettingsDirectory(version.Id);
        Directory.CreateDirectory(directory);

        await JsonFile
            .WriteAsync(paths.GetVersionSettingsFile(version.Id), version.Settings, ct)
            .ConfigureAwait(false);
    }

    public Task DeleteAsync(string id, CancellationToken ct = default)
    {
        var directory = Path.GetFullPath(paths.GetVersionDirectory(id));
        var root = Path.GetFullPath(paths.Versions).TrimEnd(Path.DirectorySeparatorChar)
                   + Path.DirectorySeparatorChar;

        if (!directory.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"拒绝删除版本目录之外的路径：{directory}");
        }

        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
            logger.LogInformation("已删除版本文件夹: {Directory}", directory);
        }

        return Task.CompletedTask;
    }

    public async Task<GameVersion> RenameAsync(string oldId, string newId, CancellationToken ct = default)
    {
        var targetId = VersionScanner.BuildId(newId);
        if (string.Equals(oldId, targetId, StringComparison.Ordinal))
        {
            return await GetAsync(oldId, ct).ConfigureAwait(false)
                   ?? throw new InvalidOperationException($"找不到版本 {oldId}。");
        }

        var source = paths.GetVersionDirectory(oldId);
        var target = paths.GetVersionDirectory(targetId);

        if (!Directory.Exists(source))
        {
            throw new InvalidOperationException($"找不到版本 {oldId} 的文件夹。");
        }

        if (Directory.Exists(target))
        {
            throw new InvalidOperationException($"已存在同名版本 {targetId}。");
        }

        try
        {
            Directory.Move(source, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"无法重命名版本 {oldId}（可能正在运行或被占用）：{ex.Message}", ex);
        }

        RenameVersionFiles(target, oldId, targetId);

        logger.LogInformation("版本 {OldId} 已重命名为 {NewId}", oldId, targetId);

        return await GetAsync(targetId, ct).ConfigureAwait(false)
               ?? throw new InvalidOperationException($"重命名后无法读取版本 {targetId}。");
    }

    /// <summary>把 <c>&lt;old&gt;.json</c> / <c>&lt;old&gt;.jar</c> 改成新名字，并改写 JSON 里的 id。</summary>
    private static void RenameVersionFiles(string directory, string oldId, string newId)
    {
        var oldJson = Path.Combine(directory, oldId + ".json");
        var newJson = Path.Combine(directory, newId + ".json");
        if (File.Exists(oldJson))
        {
            TryRewriteId(oldJson, newId);
            File.Move(oldJson, newJson);
        }

        var oldJar = Path.Combine(directory, oldId + ".jar");
        var newJar = Path.Combine(directory, newId + ".jar");
        if (File.Exists(oldJar))
        {
            File.Move(oldJar, newJar);
        }
    }

    private static void TryRewriteId(string jsonPath, string newId)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(jsonPath)) is not JsonObject json)
            {
                return;
            }

            json["id"] = newId;
            File.WriteAllText(jsonPath, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // 改不了就保持原样：文件夹名仍是权威，版本 JSON 里的 id 只影响显示
        }
    }

    private GameVersion Load(ScannedVersion scanned)
    {
        var version = new GameVersion
        {
            Id = scanned.VersionId,
            MinecraftVersion = scanned.MinecraftVersion,
            Loader = scanned.Loader,
            LoaderVersion = scanned.LoaderVersion,
        };

        var settingsFile = paths.GetVersionSettingsFile(scanned.VersionId);
        if (File.Exists(settingsFile))
        {
            try
            {
                version.Settings = JsonSerializer.Deserialize<VersionSettings>(
                                        File.ReadAllText(settingsFile), SolarJson.Options)
                                    ?? new VersionSettings();
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                logger.LogWarning(ex, "读取版本设置失败，使用默认值: {Path}", settingsFile);
            }
        }
        else
        {
            // 没有 .solar/version.json 的版本一律按不隔离处理：
            // .solar 只在用户第一次修改该版本设置（或启动器写入运行记录）时生成，
            // 没有它就意味着这个版本从来没被单独设置过，和 PCL 一样共用游戏目录。
            version.Settings.VersionIsolation = false;
        }

        version.Settings.CompanionMods ??= [];
        return version;
    }
}
