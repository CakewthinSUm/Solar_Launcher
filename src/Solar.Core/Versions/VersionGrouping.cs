namespace Solar.Core.Versions;

/// <summary>版本列表的分组类型。</summary>
public enum VersionGroupKind
{
    /// <summary>用户收藏的版本。</summary>
    Favorites,

    /// <summary>装了模组加载器或 LiteLoader，可以往 mods 里放 Mod。</summary>
    Modable,

    /// <summary>其余版本。</summary>
    Regular,
}

/// <summary>一个分组的标题与内容；组内保持扫描器的原始顺序。</summary>
public sealed record VersionGroup(
    VersionGroupKind Kind,
    string Title,
    IReadOnlyList<GameVersion> Versions);

/// <summary>
/// 把扫描结果按 PCL 的习惯分组。纯分类，不读磁盘。
/// </summary>
public static class VersionGrouping
{
    public const string FavoritesTitle = "收藏夹";
    public const string ModableTitle = "可安装 Mod";
    public const string RegularTitle = "常规版本";

    public static string GetTitle(VersionGroupKind kind) => kind switch
    {
        VersionGroupKind.Favorites => FavoritesTitle,
        VersionGroupKind.Modable => ModableTitle,
        _ => RegularTitle,
    };

    /// <summary>
    /// 按 收藏夹 → 可安装 Mod → 常规版本 的顺序返回。
    /// 收藏夹是额外的一份：收藏的版本同时留在它原本的组里，不会从那里消失。
    /// 收藏夹始终返回（可能为空），另外两组为空时省略。
    /// </summary>
    public static IReadOnlyList<VersionGroup> Group(IEnumerable<GameVersion> versions)
    {
        ArgumentNullException.ThrowIfNull(versions);

        var favorites = new List<GameVersion>();
        var modable = new List<GameVersion>();
        var regular = new List<GameVersion>();

        foreach (var version in versions)
        {
            if (version.IsFavorite)
            {
                favorites.Add(version);
            }

            // 与收藏无关：收藏只是多显示一份，不影响版本原本归到哪一组
            if (IsModable(version))
            {
                modable.Add(version);
            }
            else
            {
                regular.Add(version);
            }
        }

        var groups = new List<VersionGroup>
        {
            new(VersionGroupKind.Favorites, FavoritesTitle, favorites),
        };

        if (modable.Count > 0)
        {
            groups.Add(new VersionGroup(VersionGroupKind.Modable, ModableTitle, modable));
        }

        if (regular.Count > 0)
        {
            groups.Add(new VersionGroup(VersionGroupKind.Regular, RegularTitle, regular));
        }

        return groups;
    }

    /// <summary>
    /// 是否可装 Mod：有模组加载器或 LiteLoader。OptiFine 不算——与 PCL 的 modable 口径一致。
    /// <para>
    /// 不看持久化的 <see cref="GameVersion.IsModable"/>：它只在 Solar 自己创建版本时写入，
    /// 官方启动器装的 Forge/Fabric 版本是 <c>false</c>，按它分组会把这些版本错放进常规版本。
    /// </para>
    /// </summary>
    public static bool IsModable(GameVersion version) =>
        version.Loader != ModLoader.Vanilla || !string.IsNullOrEmpty(version.LiteLoaderVersion);
}
