using System.Text.Json;

namespace Solar.Core.Json;

public static class JsonFile
{
    /// <summary>读取 JSON 文件；文件不存在时返回 <paramref name="fallback"/>。</summary>
    public static async Task<T?> ReadAsync<T>(string path, CancellationToken ct = default, T? fallback = default)
    {
        if (!File.Exists(path))
        {
            return fallback;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, SolarJson.Options, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 原子写入：先写临时文件再替换，避免进程中断导致配置损坏。
    /// 写 Mojang 格式的文件（版本 JSON 等）时传 <see cref="SolarJson.MinecraftNative"/>，
    /// 否则外部工具会按 camelCase 读不到字段。
    /// </summary>
    public static async Task WriteAsync<T>(
        string path,
        T value,
        CancellationToken ct = default,
        JsonSerializerOptions? options = null)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, value, options ?? SolarJson.Options, ct).ConfigureAwait(false);
        }

        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// 原样写入一份来自 Mojang 的 JSON。
    /// <para>
    /// 游戏自己会读这些文件（资源索引、版本 JSON），而它的解析器区分大小写：
    /// 我们重新序列化会把 <c>objects</c> 写成 <c>Objects</c>，游戏就当索引是空的，
    /// 结果是没声音、窗口没图标。外部安装器同理，所以必须保留原始文本。
    /// </para>
    /// </summary>
    public static async Task WriteRawAsync(string path, string content, CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, content, ct).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }
}
