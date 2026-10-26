namespace Solar.Core.Models;

/// <summary>Mojang 的 version_manifest_v2.json。</summary>
public sealed class VersionManifest
{
    public LatestVersions? Latest { get; set; }
    public List<VersionSummary> Versions { get; set; } = new();
}

public sealed class LatestVersions
{
    public string? Release { get; set; }
    public string? Snapshot { get; set; }
}

public sealed class VersionSummary
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string Url { get; set; } = "";
    public string? Sha1 { get; set; }
    public DateTimeOffset? ReleaseTime { get; set; }
    public DateTimeOffset? Time { get; set; }
}
