using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Solar.Core.Json;
using Solar.Core.Models;
using Solar.Core.Paths;
using Solar.Core.Settings;

namespace Solar.Core.Minecraft;

public interface IVersionManifestService
{
    /// <summary>拉取官方版本清单并缓存到本地。</summary>
    Task<VersionManifest> GetManifestAsync(bool forceRefresh = false, CancellationToken ct = default);
}

public sealed class VersionManifestService(
    HttpClient http,
    AppPaths paths,
    ISettingsService settings,
    ILogger<VersionManifestService> logger) : IVersionManifestService
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(1);
    private VersionManifest? _cached;

    private string CacheFile => Path.Combine(paths.Data, "version_manifest_v2.json");

    public async Task<VersionManifest> GetManifestAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        if (!forceRefresh && _cached is not null)
        {
            return _cached;
        }

        var cacheInfo = new FileInfo(CacheFile);
        if (!forceRefresh && cacheInfo.Exists && DateTimeOffset.Now - cacheInfo.LastWriteTimeUtc < CacheLifetime)
        {
            var local = await JsonFile.ReadAsync<VersionManifest>(CacheFile, ct).ConfigureAwait(false);
            if (local is { Versions.Count: > 0 })
            {
                return _cached = local;
            }
        }

        try
        {
            logger.LogInformation("下载版本清单: {Url}", settings.Current.VersionManifestUrl);
            var manifest = await http.GetFromJsonAsync<VersionManifest>(settings.Current.VersionManifestUrl, SolarJson.Options, ct)
                .ConfigureAwait(false);

            if (manifest is { Versions.Count: > 0 })
            {
                await JsonFile.WriteAsync(CacheFile, manifest, ct).ConfigureAwait(false);
                return _cached = manifest;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "版本清单下载失败，尝试使用本地缓存");
        }

        var fallback = await JsonFile.ReadAsync<VersionManifest>(CacheFile, ct).ConfigureAwait(false);
        if (fallback is { Versions.Count: > 0 })
        {
            return _cached = fallback;
        }

        throw new InvalidOperationException("无法获取 Minecraft 版本清单，请检查网络连接。");
    }
}
