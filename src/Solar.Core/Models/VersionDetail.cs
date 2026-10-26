namespace Solar.Core.Models;

/// <summary>
/// 单个版本的 JSON（<c>versions/&lt;id&gt;/&lt;id&gt;.json</c>）。
/// 字段名与 Mojang 官方格式一一对应，缺失字段保持默认值。
/// </summary>
public sealed class VersionDetail
{
    public string Id { get; set; } = "";
    public string? Type { get; set; }
    public string? MainClass { get; set; }
    public string? Assets { get; set; }
    public AssetIndexRef? AssetIndex { get; set; }
    public int? ComplianceLevel { get; set; }
    public int? JavaVersion { get; set; }
    public string? InheritsFrom { get; set; }
    public string? MinecraftArguments { get; set; }

    public List<Library> Libraries { get; set; } = new();
    public Arguments? Arguments { get; set; }
    public Dictionary<string, DownloadArtifact>? Downloads { get; set; }
    public string? ReleaseTime { get; set; }
    public string? Time { get; set; }
}

public sealed class Arguments
{
    public List<ArgumentEntry> Game { get; set; } = new();
    public List<ArgumentEntry> Jvm { get; set; } = new();
}

/// <summary>
/// 参数项既可能是纯字符串，也可能是带规则的对象，因此用自定义转换器承载。
/// </summary>
public sealed class ArgumentEntry
{
    public string? Value { get; set; }
    public List<string>? Values { get; set; }
    public List<Rule>? Rules { get; set; }

    public IEnumerable<string> Enumerate() => Values ?? (Value is null ? [] : [Value]);
}

public sealed class Library
{
    public string Name { get; set; } = "";
    public LibraryDownloads? Downloads { get; set; }
    public string? Url { get; set; }
    public string? ServerUrl { get; set; }
    public List<Rule>? Rules { get; set; }
    public LibraryExtract? Extract { get; set; }
    public string? Natives { get; set; }

    /// <summary>Forge/NeoForge 等会在这里放额外的下载项。</summary>
    public Dictionary<string, DownloadArtifact>? Classifiers { get; set; }

    /// <summary>Maven 坐标的 group:artifact:version[:classifier]。</summary>
    public (string Group, string Artifact, string Version, string? Classifier) ParseName()
    {
        var parts = Name.Split(':');
        var group = parts.Length > 0 ? parts[0] : "";
        var artifact = parts.Length > 1 ? parts[1] : "";
        var version = parts.Length > 2 ? parts[2] : "";
        var classifier = parts.Length > 3 ? parts[3] : null;
        return (group, artifact, version, classifier);
    }
}

public sealed class LibraryDownloads
{
    public DownloadArtifact? Artifact { get; set; }
    public Dictionary<string, DownloadArtifact>? Classifiers { get; set; }
}

public sealed class LibraryExtract
{
    public List<string>? Exclude { get; set; }
}

public sealed class DownloadArtifact
{
    public string? Path { get; set; }
    public string? Sha1 { get; set; }
    public long Size { get; set; }
    public string? Url { get; set; }
}

public sealed class AssetIndexRef
{
    public string Id { get; set; } = "";
    public string? Sha1 { get; set; }
    public long Size { get; set; }
    public long TotalSize { get; set; }
    public string? Url { get; set; }
}
