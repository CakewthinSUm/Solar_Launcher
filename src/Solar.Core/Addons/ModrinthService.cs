using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Solar.Core.Addons;

public interface IModrinthService
{
    /// <summary>
    /// 列出某个项目在指定 MC 版本 + 加载器下的版本，新的排前面。
    /// 项目用 slug（<c>fabric-api</c>）或 project id 都可以。
    /// </summary>
    Task<IReadOnlyList<AddonModVersion>> GetVersionsAsync(
        string project,
        string minecraftVersion,
        string loader,
        CancellationToken ct = default);
}

/// <summary>
/// Modrinth 的只读接口，用于 Fabric API 与 OptiFabric 这类「放进 mods 就行」的附加 Mod。
/// <para>
/// 只取 <c>files</c> 里 <c>primary</c> 的那一条；没有 primary 时取第一条。
/// 查询串里的 <c>game_versions</c> / <c>loaders</c> 必须是 JSON 数组字面量，这是 Modrinth 的约定。
/// </para>
/// </summary>
public sealed class ModrinthService(
    HttpClient http,
    ILogger<ModrinthService> logger) : IModrinthService
{
    private const string ApiRoot = "https://api.modrinth.com/v2/project";

    public async Task<IReadOnlyList<AddonModVersion>> GetVersionsAsync(
        string project,
        string minecraftVersion,
        string loader,
        CancellationToken ct = default)
    {
        // 只取正式版；快照版本没有对应的 Mod
        var url = $"{ApiRoot}/{Uri.EscapeDataString(project)}/version"
                  + $"?game_versions={Uri.EscapeDataString($"[\"{minecraftVersion}\"]")}"
                  + $"&loaders={Uri.EscapeDataString($"[\"{loader}\"]")}";

        try
        {
            var versions = await http
                .GetFromJsonAsync<List<ModrinthVersion>>(url, ct)
                .ConfigureAwait(false);

            if (versions is not { Count: > 0 })
            {
                return [];
            }

            return versions
                .Select(v =>
                {
                    var file = v.Files?.FirstOrDefault(f => f.Primary) ?? v.Files?.FirstOrDefault();
                    return file is null
                        ? null
                        : new AddonModVersion(
                            v.Id ?? "",
                            v.VersionNumber ?? file.Filename ?? "",
                            file.Filename ?? "",
                            file.Url ?? "",
                            file.Hashes?.Sha1);
                })
                .Where(v => v is not null && !string.IsNullOrEmpty(v.Url))
                .Select(v => v!)
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "获取 Modrinth 项目 {Project} 的版本失败（MC {Version}）", project, minecraftVersion);
            throw new InvalidOperationException("无法获取 Mod 版本列表，请检查网络连接。", ex);
        }
    }

    private sealed class ModrinthVersion
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("version_number")]
        public string? VersionNumber { get; set; }

        [JsonPropertyName("files")]
        public List<ModrinthFile>? Files { get; set; }
    }

    private sealed class ModrinthFile
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("filename")]
        public string? Filename { get; set; }

        [JsonPropertyName("primary")]
        public bool Primary { get; set; }

        [JsonPropertyName("hashes")]
        public ModrinthHashes? Hashes { get; set; }
    }

    private sealed class ModrinthHashes
    {
        [JsonPropertyName("sha1")]
        public string? Sha1 { get; set; }
    }
}
