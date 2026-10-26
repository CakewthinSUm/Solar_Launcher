using System.Text.Json.Serialization;

namespace Solar.Core.Instances;

public enum ModLoader
{
    Vanilla = 0,
    Forge,
    NeoForge,
    Fabric,
    Quilt,
}

/// <summary>
/// 一个“游戏实例”：某个 MC 版本 + 模组加载器 + 独立存档/模组/配置。
/// 对应 <c>SolarInstances/&lt;id&gt;/</c> 目录。
/// </summary>
public sealed class GameInstance
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "新实例";

    /// <summary>基础游戏版本，如 <c>1.21.4</c>。</summary>
    public string MinecraftVersion { get; set; } = "";

    public ModLoader Loader { get; set; } = ModLoader.Vanilla;
    public string? LoaderVersion { get; set; }

    /// <summary>实际要启动的版本目录名（如 <c>1.21.4-forge-54.0.1</c>）；为空时等于 MinecraftVersion。</summary>
    public string? VersionId { get; set; }

    /// <summary>覆盖全局的 Java 路径。</summary>
    public string? JavaPath { get; set; }

    /// <summary>覆盖全局的内存上限（MB）。</summary>
    public int? MaxMemoryMb { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? LastPlayedAt { get; set; }

    [JsonIgnore]
    public string EffectiveVersionId => string.IsNullOrWhiteSpace(VersionId) ? MinecraftVersion : VersionId!;

    [JsonIgnore]
    public string DisplayLoader => Loader == ModLoader.Vanilla ? "原版" : $"{Loader} {LoaderVersion}".Trim();
}
