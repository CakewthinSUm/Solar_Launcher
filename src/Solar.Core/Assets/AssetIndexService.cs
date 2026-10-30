using System.Text.Json;
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

        if (File.Exists(path))
        {
            var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            var cached = TryParseCached(text, reference.Id);
            if (cached is not null)
            {
                return cached;
            }

            logger.LogWarning(
                "资源索引 {Id} 不是游戏可读的原生格式（旧版本写过 PascalCase），重新下载",
                reference.Id);
        }

        if (string.IsNullOrEmpty(reference.Url))
        {
            throw new InvalidOperationException($"版本缺少资源索引 {reference.Id} 的下载地址。");
        }

        logger.LogInformation("下载资源索引: {Id}", reference.Id);
        var json = await http.GetStringAsync(reference.Url, ct).ConfigureAwait(false);
        var index = JsonSerializer.Deserialize<AssetIndex>(json, SolarJson.Options)
                    ?? throw new InvalidOperationException($"资源索引 {reference.Id} 内容无效。");

        if (index.Objects.Count == 0)
        {
            throw new InvalidOperationException($"资源索引 {reference.Id} 不包含任何资源对象。");
        }

        // 原样保存：游戏按 camelCase 读这个文件，重新序列化会让它读到空索引
        await JsonFile.WriteRawAsync(path, json, ct).ConfigureAwait(false);
        return index;
    }

    /// <summary>
    /// 读取本地缓存的索引；不是 Mojang 原生格式时返回 null，让调用方重新下载。
    /// 判断依据是字段名大小写：原生文件里一定有 <c>"objects"</c>。
    /// </summary>
    private AssetIndex? TryParseCached(string text, string indexId)
    {
        if (!text.Contains("\"objects\"", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var index = JsonSerializer.Deserialize<AssetIndex>(text, SolarJson.Options);
            return index is { Objects.Count: > 0 } ? index : null;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "资源索引 {Id} 解析失败，重新下载", indexId);
            return null;
        }
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
