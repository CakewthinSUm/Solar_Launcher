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

    /// <summary>原子写入：先写临时文件再替换，避免进程中断导致配置损坏。</summary>
    public static async Task WriteAsync<T>(string path, T value, CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, value, SolarJson.Options, ct).ConfigureAwait(false);
        }

        File.Move(temp, path, overwrite: true);
    }
}
