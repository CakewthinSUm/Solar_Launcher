namespace Solar.Core.Versions;

public enum ModLoader
{
    Vanilla = 0,
    Forge,
    NeoForge,
    Fabric,
    Quilt,
}

/// <summary>
/// 一个版本的启动器设置，持久化在 <c>versions/&lt;id&gt;/.solar/version.json</c>。
/// <para>
/// 版本号与加载器信息不写在这里：它们每次从 <c>&lt;id&gt;.json</c> 重新识别，
/// 不会因为手动改了版本文件而变旧。
/// </para>
/// </summary>
public sealed class VersionSettings
{
    /// <summary>自定义显示名；为空时用版本号（加载器版本带上加载器名）。</summary>
    public string? DisplayName { get; set; }

    /// <summary>OptiFine 构建名（如 <c>1.20.1_HD_U_I6</c>）。</summary>
    public string? OptiFineVersion { get; set; }

    /// <summary>LiteLoader 版本号；只支持 1.13 以前的版本。</summary>
    public string? LiteLoaderVersion { get; set; }

    /// <summary>
    /// 该版本是否「可装 Mod」：创建时选了模组加载器，或带 OptiFine / LiteLoader。
    /// 创建时写入一次，之后不随版本 JSON 重新识别，用于列表分组与筛选。
    /// </summary>
    public bool IsModable { get; set; }

    /// <summary>版本隔离：开启后游戏目录就是该版本的版本文件夹。</summary>
    public bool VersionIsolation { get; set; }

    /// <summary>覆盖全局的 Java 路径。</summary>
    public string? JavaPath { get; set; }

    /// <summary>覆盖全局的内存上限（MB）。</summary>
    public int? MaxMemoryMb { get; set; }

    /// <summary>自定义游戏窗口标题；为空时保持默认（版本 id）。</summary>
    public string? CustomWindowTitle { get; set; }

    /// <summary>自定义图标在 <c>.solar</c> 里的文件名（如 <c>icon.png</c>）；为空时用默认图标。</summary>
    public string? CustomIcon { get; set; }

    /// <summary>加载器是否已安装完成。未安装时启动会先触发安装。</summary>
    public bool LoaderInstalled { get; set; }

    /// <summary>创建版本时替用户装进 mods 的附加 Mod（Fabric API / OptiFabric / OptiFine-as-mod）。</summary>
    public List<string> CompanionMods { get; set; } = [];

    public DateTimeOffset? LastPlayedAt { get; set; }
}

/// <summary>
/// 一个「版本」：<c>versions/&lt;id&gt;/</c> 目录，与 PCL 一致——版本文件夹本身就是版本。
/// </summary>
public sealed class GameVersion
{
    /// <summary>版本文件夹名，也是唯一键。</summary>
    public string Id { get; set; } = "";

    /// <summary>基础游戏版本，如 <c>1.21.4</c>。</summary>
    public string MinecraftVersion { get; set; } = "";

    public ModLoader Loader { get; set; } = ModLoader.Vanilla;
    public string? LoaderVersion { get; set; }

    public VersionSettings Settings { get; set; } = new();

    /// <summary>
    /// 自定义图标文件的完整路径；由 <see cref="VersionRepository"/> 读取设置时填好，
    /// 未自定义或文件已被删掉时为空。
    /// </summary>
    public string? IconPath { get; set; }

    public bool HasIcon => !string.IsNullOrEmpty(IconPath);

    /// <summary>显示名：用户自定义名优先，否则是版本号/加载器名。</summary>
    public string Name
    {
        get => string.IsNullOrWhiteSpace(Settings.DisplayName) ? DefaultName : Settings.DisplayName!;
        set => Settings.DisplayName = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public string DefaultName => Loader == ModLoader.Vanilla
        ? MinecraftVersion
        : $"{MinecraftVersion} {Loader} {LoaderVersion}".Trim();

    public bool VersionIsolation
    {
        get => Settings.VersionIsolation;
        set => Settings.VersionIsolation = value;
    }

    public bool LoaderInstalled
    {
        get => Settings.LoaderInstalled;
        set => Settings.LoaderInstalled = value;
    }

    public string? JavaPath
    {
        get => Settings.JavaPath;
        set => Settings.JavaPath = value;
    }

    public int? MaxMemoryMb
    {
        get => Settings.MaxMemoryMb;
        set => Settings.MaxMemoryMb = value;
    }

    public string? CustomWindowTitle
    {
        get => Settings.CustomWindowTitle;
        set => Settings.CustomWindowTitle = value;
    }

    public string? OptiFineVersion
    {
        get => Settings.OptiFineVersion;
        set => Settings.OptiFineVersion = value;
    }

    public string? LiteLoaderVersion
    {
        get => Settings.LiteLoaderVersion;
        set => Settings.LiteLoaderVersion = value;
    }

    public bool IsModable
    {
        get => Settings.IsModable;
        set => Settings.IsModable = value;
    }

    public List<string> CompanionMods => Settings.CompanionMods;

    public DateTimeOffset? LastPlayedAt
    {
        get => Settings.LastPlayedAt;
        set => Settings.LastPlayedAt = value;
    }

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

    /// <summary>列表里的附加标记；用共享目录的版本为空，便于直接拼在说明行末尾。</summary>
    public string IsolationBadge => VersionIsolation ? " · 已隔离" : "";
}
