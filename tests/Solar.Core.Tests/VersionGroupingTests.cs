using Solar.Core.Versions;
using Xunit;

namespace Solar.Core.Tests;

/// <summary>版本列表分组：收藏夹 / 可安装 Mod / 常规版本。</summary>
public class VersionGroupingTests
{
    [Fact]
    public void EmptyListReturnsOnlyFavorites()
    {
        var groups = VersionGrouping.Group([]);

        var group = Assert.Single(groups);
        Assert.Equal(VersionGroupKind.Favorites, group.Kind);
        Assert.Empty(group.Versions);
    }

    [Fact]
    public void FavoriteAlsoStaysInItsOwnGroup()
    {
        var favorite = Version("favorite", ModLoader.Forge, favorite: true);
        var modable = Version("modable", ModLoader.Fabric);

        var groups = VersionGrouping.Group([favorite, modable]);

        // 收藏夹只是多显示一份，版本仍然留在可安装 Mod 里
        Assert.Equal([favorite], Favorites(groups));
        Assert.Equal([favorite, modable], Modable(groups));
    }

    [Fact]
    public void FavoriteVanillaAppearsInBothFavoritesAndRegular()
    {
        var favorite = Version("favorite", favorite: true);

        var groups = VersionGrouping.Group([favorite]);

        Assert.Equal([favorite], Favorites(groups));
        Assert.Equal([favorite], Regular(groups));
    }

    [Fact]
    public void LoadersAndLiteLoaderAreModable()
    {
        var versions = new[]
        {
            Version("forge", ModLoader.Forge),
            Version("neoforge", ModLoader.NeoForge),
            Version("fabric", ModLoader.Fabric),
            Version("quilt", ModLoader.Quilt),
            Version("liteloader", ModLoader.Vanilla, liteLoader: "1.12.2-RC"),
        };

        var groups = VersionGrouping.Group(versions);

        Assert.Equal(versions, Modable(groups));
        Assert.Empty(Favorites(groups));
        Assert.DoesNotContain(groups, g => g.Kind == VersionGroupKind.Regular);
    }

    [Fact]
    public void OptiFineOnlyIsRegular()
    {
        var optifine = Version("optifine", ModLoader.Vanilla, optiFine: "1.20.1_HD_U_I6");
        var vanilla = Version("vanilla");

        var groups = VersionGrouping.Group([optifine, vanilla]);

        Assert.Equal([optifine, vanilla], Regular(groups));
        Assert.DoesNotContain(groups, g => g.Kind == VersionGroupKind.Modable);
    }

    [Fact]
    public void PersistedModableFlagIsIgnored()
    {
        // 外部启动器装的版本 IsModable 一直是 false；反过来旧数据也可能写着 true 但实际是原版
        var legacy = Version("legacy", ModLoader.Vanilla, modableFlag: true);

        var groups = VersionGrouping.Group([legacy]);

        Assert.Equal([legacy], Regular(groups));
    }

    [Fact]
    public void PreservesOrderAndOmitsEmptyGroups()
    {
        var first = Version("1.21.4");
        var second = Version("1.20.1");
        var favorite = Version("1.19.2", favorite: true);

        var groups = VersionGrouping.Group([first, second, favorite]);

        Assert.Equal([VersionGroupKind.Favorites, VersionGroupKind.Regular], groups.Select(g => g.Kind));
        Assert.Equal([favorite], Favorites(groups));
        Assert.Equal([first, second, favorite], Regular(groups));
        Assert.DoesNotContain(groups, g => g.Kind == VersionGroupKind.Modable);
    }

    private static GameVersion Version(
        string id,
        ModLoader loader = ModLoader.Vanilla,
        string? liteLoader = null,
        string? optiFine = null,
        bool favorite = false,
        bool modableFlag = false) => new()
        {
            Id = id,
            MinecraftVersion = "1.21.4",
            Loader = loader,
            LiteLoaderVersion = liteLoader,
            OptiFineVersion = optiFine,
            IsFavorite = favorite,
            IsModable = modableFlag,
        };

    private static IReadOnlyList<GameVersion> Favorites(IReadOnlyList<VersionGroup> groups) =>
        groups.Single(g => g.Kind == VersionGroupKind.Favorites).Versions;

    private static IReadOnlyList<GameVersion> Modable(IReadOnlyList<VersionGroup> groups) =>
        groups.Single(g => g.Kind == VersionGroupKind.Modable).Versions;

    private static IReadOnlyList<GameVersion> Regular(IReadOnlyList<VersionGroup> groups) =>
        groups.Single(g => g.Kind == VersionGroupKind.Regular).Versions;
}
