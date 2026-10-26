namespace Solar.Core.Models;

/// <summary><c>assets/indexes/&lt;id&gt;.json</c>，记录每个资源对象的哈希与大小。</summary>
public sealed class AssetIndex
{
    public Dictionary<string, AssetObject> Objects { get; set; } = new(StringComparer.Ordinal);
}

public sealed class AssetObject
{
    public string Hash { get; set; } = "";
    public long Size { get; set; }

    /// <summary>资源在 objects/&lt;前两位&gt;/&lt;hash&gt; 下的相对路径。</summary>
    public string RelativePath => Path.Combine(Hash[..2], Hash);
}
