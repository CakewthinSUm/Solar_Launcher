using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Solar.Core.Json;
using Solar.Core.Models;
using Solar.Core.Paths;

namespace Solar.Core.Assets;

public interface IAssetIndexService
{
    /// <summary>确保资源索引已下载到本地并解析。</summary>
    Task<AssetIndex> GetIndexAsync(AssetIndexRef reference, CancellationToken ct = default);

    /// <summary>索引在本地保存的路径。</summary>
    string GetIndexPath(string id);

    /// <summary>
    /// 老版本兼容：把 objects 里的资源按原路径复制到 <c>assets/virtual/&lt;id&gt;/</c>，
    /// 或（1.6 以前）摊平到 <c>resources/</c>。新版本是空操作。
    /// </summary>
    void EnsureLegacyLayout(AssetIndex index, string indexId, bool virtualLayout, bool mapToResources);
}

public sealed class AssetIndexService(
    HttpClient http,
    AppPaths paths,
    ILogger<AssetIndexService> logger) : IAssetIndexService
{
    public string GetIndexPath(string id) => Path.Combine(paths.Assets, "indexes", id + ".json");

    public async Task<AssetIndex> GetIndexAsync(AssetIndexRef reference, CancellationToken ct = default)
    {
        var path = GetIndexPath(reference.Id);

        var cached = await JsonFile.ReadAsync<AssetIndex>(path, ct).ConfigureAwait(false);
        if (cached is { Objects.Count: > 0 })
        {
            return cached;
        }

        if (string.IsNullOrEmpty(reference.Url))
        {
            throw new InvalidOperationException($"版本缺少资源索引 {reference.Id} 的下载地址。");
        }

        logger.LogInformation("下载资源索引: {Id}", reference.Id);
        var index = await http.GetFromJsonAsync<AssetIndex>(reference.Url, SolarJson.Options, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"资源索引 {reference.Id} 内容无效。");

        if (index.Objects.Count == 0)
        {
            throw new InvalidOperationException($"资源索引 {reference.Id} 不包含任何资源对象。");
        }

        await JsonFile.WriteAsync(path, index, ct).ConfigureAwait(false);
        return index;
    }

    public void EnsureLegacyLayout(AssetIndex index, string indexId, bool virtualLayout, bool mapToResources)
    {
        if (!virtualLayout && !mapToResources)
        {
            return;
        }

        var objectsRoot = Path.Combine(paths.Assets, "objects");
        var targetRoot = mapToResources
            ? Path.Combine(paths.Assets, "resources")
            : Path.Combine(paths.Assets, "virtual", indexId);

        foreach (var (name, obj) in index.Objects)
        {
            var source = Path.Combine(objectsRoot, obj.RelativePath);
            if (!File.Exists(source))
            {
                continue;
            }

            var destination = Path.Combine(targetRoot, name.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(destination))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination);
        }
    }
}
