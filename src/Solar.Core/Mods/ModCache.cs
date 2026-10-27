using System.Text.Json;
using Microsoft.Extensions.Logging;
using Solar.Core.Json;
using Solar.Core.Paths;

namespace Solar.Core.Mods;

/// <summary>
/// 模组信息的磁盘缓存。
/// <para>
/// 有效期按内容类型分开：分类标签几乎不变（7 天），项目详情一天一变（24 小时），
/// 版本列表跟着作者发版走（1 小时）。缓存文件损坏时当作未命中，不抛异常——
/// 模组信息不值得因为一个坏文件让整页打不开。
/// </para>
/// </summary>
public sealed class ModCache(AppPaths paths, ILogger<ModCache> logger)
{
    private static readonly TimeSpan ProjectLifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan VersionsLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan CategoriesLifetime = TimeSpan.FromDays(7);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private string Root => Path.Combine(paths.Data, "modcache");

    public Task<ModProject?> ReadProjectAsync(ModSource source, string projectId, CancellationToken ct = default) =>
        ReadAsync<ModProject>(ProjectFile(source, projectId), ProjectLifetime, ct);

    public Task WriteProjectAsync(ModProject project, CancellationToken ct = default) =>
        WriteAsync(ProjectFile(project.Source, project.Id), project, ct);

    public async Task<IReadOnlyList<ModVersion>?> ReadVersionsAsync(
        ModSource source,
        string cacheKey,
        CancellationToken ct = default) =>
        await ReadAsync<List<ModVersion>>(VersionsFile(source, cacheKey), VersionsLifetime, ct)
            .ConfigureAwait(false);

    public Task WriteVersionsAsync(
        ModSource source,
        string cacheKey,
        IReadOnlyList<ModVersion> versions,
        CancellationToken ct = default) =>
        WriteAsync(VersionsFile(source, cacheKey), versions.ToList(), ct);

    public async Task<IReadOnlyList<ModCategory>?> ReadCategoriesAsync(
        ModSource source,
        CancellationToken ct = default) =>
        await ReadAsync<List<ModCategory>>(CategoriesFile(source), CategoriesLifetime, ct)
            .ConfigureAwait(false);

    public Task WriteCategoriesAsync(
        ModSource source,
        IReadOnlyList<ModCategory> categories,
        CancellationToken ct = default) =>
        WriteAsync(CategoriesFile(source), categories.ToList(), ct);

    /// <summary>清空缓存。设置页换镜像后调一次，避免拿到旧源的内容。</summary>
    public void Clear()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "清空 Mod 缓存失败");
        }
    }

    private async Task<T?> ReadAsync<T>(string path, TimeSpan lifetime, CancellationToken ct) where T : class
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || DateTimeOffset.UtcNow - info.LastWriteTimeUtc > lifetime)
            {
                return null;
            }

            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, Json, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "读取 Mod 缓存失败，按未命中处理: {Path}", path);
            return null;
        }
    }

    private async Task WriteAsync<T>(string path, T value, CancellationToken ct)
    {
        try
        {
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);

            // 先写临时文件再替换，避免进程中断留下半截 JSON
            var temp = path + ".tmp";
            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, value, Json, ct).ConfigureAwait(false);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 缓存写不进去不影响功能，只是下次还要重新拉
            logger.LogDebug(ex, "写入 Mod 缓存失败: {Path}", path);
        }
    }

    private string ProjectFile(ModSource source, string projectId) =>
        Path.Combine(Root, "project", $"{Prefix(source)}_{Sanitize(projectId)}.json");

    private string VersionsFile(ModSource source, string cacheKey) =>
        Path.Combine(Root, "versions", $"{Prefix(source)}_{Sanitize(cacheKey)}.json");

    private string CategoriesFile(ModSource source) =>
        Path.Combine(Root, "categories", $"{Prefix(source)}.json");

    private static string Prefix(ModSource source) => source == ModSource.Modrinth ? "mr" : "cf";

    /// <summary>
    /// 缓存键里会拼游戏版本和加载器（如 <c>AANobbMI_1.21.4_fabric</c>），
    /// 其中的点与斜杠不能直接当文件名。
    /// </summary>
    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }
}
