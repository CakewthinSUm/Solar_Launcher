using System.Text.Json;
using System.Text.Json.Serialization;

namespace Solar.Core.Models;

/// <summary><c>assets/indexes/&lt;id&gt;.json</c>，记录每个资源对象的哈希与大小。</summary>
public sealed class AssetIndex
{
    public Dictionary<string, AssetObject> Objects { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// 1.6 以前的旧格式：资源要按原文件名摊平到 <c>resources/</c>，而不是按哈希存放。
    /// 值为 true 或 <c>"legacy"</c> 时启用。
    /// </summary>
    [JsonPropertyName("map_to_resources")]
    public object? MapToResources { get; set; }

    /// <summary>
    /// 1.7 以前：资源要复制一份到 <c>assets/virtual/&lt;indexId&gt;/</c> 才能被游戏找到。
    /// 值为 true 或 <c>"pre-1.6"</c> 时启用。
    /// </summary>
    public object? Virtual { get; set; }

    public bool IsMapToResources => IsTruthy(MapToResources);

    public bool IsVirtual => IsTruthy(Virtual);

    private static bool IsTruthy(object? value) => value switch
    {
        null => false,
        bool b => b,
        string s => IsTruthyString(s),
        // System.Text.Json 把 object 类型的值反序列化成 JsonElement，
        // 而不是装箱的 bool/string，必须单独处理。
        JsonElement { ValueKind: JsonValueKind.True } => true,
        JsonElement { ValueKind: JsonValueKind.False } => false,
        JsonElement { ValueKind: JsonValueKind.String } e => IsTruthyString(e.GetString() ?? ""),
        _ => false,
    };

    private static bool IsTruthyString(string s) =>
        s.Equals("true", StringComparison.OrdinalIgnoreCase)
        || s.Equals("legacy", StringComparison.OrdinalIgnoreCase)
        || s.Equals("pre-1.6", StringComparison.OrdinalIgnoreCase);
}

public sealed class AssetObject
{
    public string Hash { get; set; } = "";
    public long Size { get; set; }

    /// <summary>资源在 objects/&lt;前两位&gt;/&lt;hash&gt; 下的相对路径。</summary>
    public string RelativePath => Hash.Length >= 2 ? Path.Combine(Hash[..2], Hash) : Hash;
}
