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

    /// <summary>加载器是否已安装完成。未安装时启动会先触发安装。</summary>
    public bool LoaderInstalled { get; set; }

    /// <summary>
    /// OptiFine 构建名（如 <c>1.20.1_HD_U_I6</c>）。它和主加载器是正交的：
    /// 没有主加载器时生成独立的版本 profile，有主加载器时作为 Mod 放进 mods。
    /// </summary>
    public string? OptiFineVersion { get; set; }

    /// <summary>LiteLoader 版本号；只支持 1.13 以前的版本。</summary>
    public string? LiteLoaderVersion { get; set; }

    /// <summary>
    /// 创建实例时替用户装进 mods 的附加 Mod（Fabric API / OptiFabric / OptiFine-as-mod）。
    /// 只记录文件名，用于实例设置页展示，不参与启动。
    /// </summary>
    public List<string> CompanionMods { get; set; } = [];

    /// <summary>
    /// 版本隔离：开启后该实例使用自己的游戏目录
    /// <c>SolarInstances/&lt;id&gt;/.minecraft</c>，存档、模组、配置都与其它实例分开。
    /// 关闭时与其它未隔离的实例共用设置里的那份 .minecraft。
    /// </summary>
    public bool VersionIsolation { get; set; }

    /// <summary>覆盖全局的 Java 路径。</summary>
    public string? JavaPath { get; set; }

    /// <summary>
    /// 自定义游戏窗口标题。游戏把标题拼成 "Minecraft* &lt;--version 参数值&gt;"，
    /// 而该值来自版本 JSON 的 <c>${version_name}</c> 占位符，覆盖它即可改标题。
    /// 为空时保持默认（版本 id）。
    /// </summary>
    public string? CustomWindowTitle { get; set; }

    /// <summary>覆盖全局的内存上限（MB）。</summary>
    public int? MaxMemoryMb { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? LastPlayedAt { get; set; }

    [JsonIgnore]
    public string EffectiveVersionId => string.IsNullOrWhiteSpace(VersionId) ? MinecraftVersion : VersionId!;

    [JsonIgnore]
    public string DisplayLoader
    {
        get
        {
            var parts = new List<string>();
            if (Loader == ModLoader.Vanilla)
            {
                // 纯原版时不写“原版”，由调用方与版本号拼接；
                // 但有 OptiFine / LiteLoader 时要把它们列出来，否则界面会误显示为原版。
                if (string.IsNullOrEmpty(OptiFineVersion) && string.IsNullOrEmpty(LiteLoaderVersion))
                {
                    return "原版";
                }
            }
            else
            {
                parts.Add($"{Loader} {LoaderVersion}".Trim());
            }

            if (!string.IsNullOrEmpty(OptiFineVersion))
            {
                parts.Add($"OptiFine {OptiFineVersion}");
            }

            if (!string.IsNullOrEmpty(LiteLoaderVersion))
            {
                parts.Add($"LiteLoader {LiteLoaderVersion}");
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>列表里的附加标记；未开启隔离时为空，便于直接拼在说明行末尾。</summary>
    [JsonIgnore]
    public string IsolationBadge => VersionIsolation ? " · 已隔离" : "";
}
