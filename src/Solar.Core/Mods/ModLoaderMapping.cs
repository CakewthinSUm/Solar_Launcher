using Solar.Core.Instances;

namespace Solar.Core.Mods;

/// <summary>
/// 两个源之间的加载器标识换算。
/// <para>
/// Modrinth 用字符串 slug，CurseForge 用数字枚举，本项目的 <see cref="ModLoader"/>
/// 又是第三种。三套映射集中在这里，避免散落在各个目录实现里。
/// </para>
/// </summary>
public static class ModLoaderMapping
{
    /// <summary>CurseForge 的 ModLoaderType 枚举值。</summary>
    public static int? ToCurseForge(ModLoader? loader) => loader switch
    {
        ModLoader.Forge => 1,
        ModLoader.Fabric => 4,
        ModLoader.Quilt => 5,
        ModLoader.NeoForge => 6,
        _ => null,
    };

    /// <summary>Modrinth 的加载器 slug；不限时返回 null（调用方不要加 facet）。</summary>
    public static string? ToModrinth(ModLoader? loader) => loader switch
    {
        ModLoader.Forge => "forge",
        ModLoader.Fabric => "fabric",
        ModLoader.Quilt => "quilt",
        ModLoader.NeoForge => "neoforge",
        _ => null,
    };

    /// <summary>从 CurseForge 的 ModLoaderType 数字还原；未知值返回 null。</summary>
    public static ModLoader? FromCurseForge(int? value) => value switch
    {
        1 => ModLoader.Forge,
        4 => ModLoader.Fabric,
        5 => ModLoader.Quilt,
        6 => ModLoader.NeoForge,
        _ => null,
    };

    /// <summary>
    /// 从 Modrinth 的字符串还原。<c>legacy-fabric</c>、<c>babric</c> 之类
    /// 不是本项目支持的加载器，统一当作未知。
    /// </summary>
    public static ModLoader? FromModrinth(string? value) => value?.ToLowerInvariant() switch
    {
        "forge" => ModLoader.Forge,
        "fabric" => ModLoader.Fabric,
        "quilt" => ModLoader.Quilt,
        "neoforge" => ModLoader.NeoForge,
        _ => null,
    };

    /// <summary>
    /// CurseForge 的 <c>gameVersions</c> 数组把加载器和版本号混在一起
    /// （<c>["1.21.4", "NeoForge", "Client"]</c>），需要挑出加载器那一项。
    /// </summary>
    public static ModLoader? FromCurseForgeGameVersion(string value) => value.ToLowerInvariant() switch
    {
        "forge" => ModLoader.Forge,
        "fabric" => ModLoader.Fabric,
        "quilt" => ModLoader.Quilt,
        "neoforge" => ModLoader.NeoForge,
        _ => null,
    };

    /// <summary>
    /// 判断版本是否适配实例。加载器为空（旧模组常常不标）时按“可能适配”处理，
    /// 与 PCL 的宽松策略一致，否则大量 1.12 时代的老模组会被直接过滤掉。
    /// </summary>
    public static bool Matches(ModVersion version, string? gameVersion, ModLoader? loader)
    {
        if (!string.IsNullOrWhiteSpace(gameVersion)
            && version.GameVersions.Count > 0
            && !version.GameVersions.Contains(gameVersion, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (loader is null or ModLoader.Vanilla || version.Loaders.Count == 0)
        {
            return true;
        }

        return version.Loaders.Contains(loader.Value);
    }
}
