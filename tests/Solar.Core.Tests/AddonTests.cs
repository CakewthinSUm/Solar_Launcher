using Solar.Core.Addons;
using Solar.Core.Instances;
using Xunit;

namespace Solar.Core.Tests;

public class ComponentRulesTests
{
    private const string Modern = "1.21.4";
    private const string Legacy = "1.12.2";

    private static ComponentSelection Select(ModLoader loader, string? loaderVersion = "1.0.0") =>
        new() { Loader = loader, LoaderVersion = loader == ModLoader.Vanilla ? null : loaderVersion };

    [Theory]
    [InlineData(ModLoader.Forge, ComponentKind.NeoForge)]
    [InlineData(ModLoader.Forge, ComponentKind.Fabric)]
    [InlineData(ModLoader.Forge, ComponentKind.Quilt)]
    [InlineData(ModLoader.Fabric, ComponentKind.Forge)]
    [InlineData(ModLoader.Fabric, ComponentKind.NeoForge)]
    [InlineData(ModLoader.NeoForge, ComponentKind.Forge)]
    [InlineData(ModLoader.Quilt, ComponentKind.Fabric)]
    public void LoadersAreMutuallyExclusive(ModLoader loader, ComponentKind blocked)
    {
        Assert.NotNull(ComponentRules.GetUnavailableReason(blocked, Select(loader), Modern));
    }

    [Fact]
    public void EveryLoaderIsAvailableOnItsOwn()
    {
        foreach (var kind in new[]
                 {
                     ComponentKind.Forge, ComponentKind.NeoForge,
                     ComponentKind.Fabric, ComponentKind.Quilt,
                 })
        {
            Assert.Null(ComponentRules.GetUnavailableReason(kind, new ComponentSelection(), Modern));
        }
    }

    [Theory]
    [InlineData("1.19.4")]
    [InlineData("1.16.5")]
    public void NeoForgeRequiresModernMinecraft(string version)
    {
        var reason = ComponentRules.GetUnavailableReason(
            ComponentKind.NeoForge, new ComponentSelection(), version);

        Assert.NotNull(reason);
        Assert.Contains("1.20.1", reason);
    }

    [Fact]
    public void NeoForgeIsAvailableFrom1_20_1()
    {
        Assert.Null(ComponentRules.GetUnavailableReason(
            ComponentKind.NeoForge, new ComponentSelection(), "1.20.1"));
    }

    [Fact]
    public void FabricApiNeedsFabricOrQuilt()
    {
        Assert.NotNull(ComponentRules.GetUnavailableReason(
            ComponentKind.FabricApi, new ComponentSelection(), Modern));

        Assert.Null(ComponentRules.GetUnavailableReason(
            ComponentKind.FabricApi, Select(ModLoader.Fabric), Modern));

        Assert.Null(ComponentRules.GetUnavailableReason(
            ComponentKind.FabricApi, Select(ModLoader.Quilt), Modern));
    }

    [Fact]
    public void OptiFabricNeedsBothFabricAndOptiFine()
    {
        var fabricOnly = Select(ModLoader.Fabric);
        Assert.NotNull(ComponentRules.GetUnavailableReason(ComponentKind.OptiFabric, fabricOnly, Modern));

        var optifineOnly = new ComponentSelection { OptiFine = Build(Modern) };
        Assert.NotNull(ComponentRules.GetUnavailableReason(ComponentKind.OptiFabric, optifineOnly, Modern));

        var both = fabricOnly with { OptiFine = Build(Modern) };
        Assert.Null(ComponentRules.GetUnavailableReason(ComponentKind.OptiFabric, both, Modern));
    }

    [Theory]
    [InlineData("1.13", true)]
    [InlineData("1.14.3", true)]
    [InlineData("1.12.2", false)]
    [InlineData("1.14.4", false)]
    [InlineData("1.16.5", false)]
    public void ForgeOptiFineConflictWindowIs1_13To1_14_3(string version, bool conflicts)
    {
        Assert.Equal(conflicts, ComponentRules.IsForgeOptiFineConflict(version));

        var selection = Select(ModLoader.Forge) with { OptiFine = Build(version) };
        var reason = ComponentRules.GetUnavailableReason(ComponentKind.OptiFine, selection, version);
        Assert.Equal(conflicts, reason is not null);
    }

    [Fact]
    public void OptiFineIsBlockedOnNeoForge()
    {
        var selection = Select(ModLoader.NeoForge) with { OptiFine = Build(Modern) };
        Assert.NotNull(ComponentRules.GetUnavailableReason(ComponentKind.OptiFine, selection, Modern));
    }

    [Fact]
    public void OptiFineIsBlockedOnNewerFabric()
    {
        var selection = Select(ModLoader.Fabric) with { OptiFine = Build("1.21.4") };
        Assert.NotNull(ComponentRules.GetUnavailableReason(ComponentKind.OptiFine, selection, "1.21.4"));

        var older = Select(ModLoader.Fabric) with { OptiFine = Build("1.20.4") };
        Assert.Null(ComponentRules.GetUnavailableReason(ComponentKind.OptiFine, older, "1.20.4"));
    }

    [Theory]
    [InlineData("1.12.2", true)]
    [InlineData("1.7.10", true)]
    [InlineData("1.13", false)]
    [InlineData("1.21.4", false)]
    [InlineData("1.6.4", false)]
    public void LiteLoaderCovers1_7_2Through1_12_2(string version, bool available)
    {
        var reason = ComponentRules.GetUnavailableReason(
            ComponentKind.LiteLoader, new ComponentSelection(), version);

        Assert.Equal(available, reason is null);
    }

    [Fact]
    public void LiteLoaderRequiresJava8()
    {
        // launchwrapper 在 Java 9+ 上会抛 ClassCastException，装上也起不来
        var reason = ComponentRules.GetUnavailableReason(
            ComponentKind.LiteLoader, new ComponentSelection(), Legacy, hasJava8: false);

        Assert.NotNull(reason);
        Assert.Contains("Java 8", reason);

        Assert.Null(ComponentRules.GetUnavailableReason(
            ComponentKind.LiteLoader, new ComponentSelection(), Legacy, hasJava8: true));
    }

    [Fact]
    public void MissingJava8OnlyAffectsLiteLoader()
    {
        foreach (var kind in new[]
                 {
                     ComponentKind.Forge, ComponentKind.NeoForge,
                     ComponentKind.Fabric, ComponentKind.Quilt, ComponentKind.OptiFine,
                 })
        {
            Assert.Null(ComponentRules.GetUnavailableReason(
                kind, new ComponentSelection(), Modern, hasJava8: false));
        }
    }

    [Fact]
    public void NormalizeDropsLiteLoaderWithoutJava8()
    {
        var selection = new ComponentSelection { LiteLoader = Lite(Legacy) };

        var normalized = ComponentRules.Normalize(selection, Legacy, hasJava8: false);

        Assert.Null(normalized.LiteLoader);
    }

    [Fact]
    public void LiteLoaderConflictsWithModLoadersAndOptiFine()
    {
        var withFabric = Select(ModLoader.Fabric) with { LiteLoader = Lite("1.12.2") };
        Assert.NotNull(ComponentRules.GetUnavailableReason(ComponentKind.LiteLoader, withFabric, Legacy));

        var withOptiFine = new ComponentSelection { OptiFine = Build(Legacy), LiteLoader = Lite(Legacy) };
        Assert.NotNull(ComponentRules.GetUnavailableReason(ComponentKind.LiteLoader, withOptiFine, Legacy));
    }

    [Fact]
    public void NormalizeDropsConflictingPicks()
    {
        // 选了 NeoForge 之后 OptiFine 就不该留着
        var selection = new ComponentSelection
        {
            Loader = ModLoader.NeoForge,
            LoaderVersion = "21.4.1",
            OptiFine = Build(Modern),
            OptiFabric = new AddonModVersion("id", "1.0", "optifabric.jar", "https://x/y.jar", null),
        };

        var normalized = ComponentRules.Normalize(selection, Modern);

        Assert.Null(normalized.OptiFine);
        Assert.Null(normalized.OptiFabric);
    }

    [Fact]
    public void NormalizeKeepsValidPicks()
    {
        var selection = new ComponentSelection
        {
            Loader = ModLoader.Fabric,
            LoaderVersion = "0.16.9",
            FabricApi = new AddonModVersion("id", "0.115.0", "fabric-api.jar", "https://x/y.jar", null),
        };

        var normalized = ComponentRules.Normalize(selection, Modern);

        Assert.NotNull(normalized.FabricApi);
        Assert.Equal(ModLoader.Fabric, normalized.Loader);
    }

    [Theory]
    [InlineData("1.21.4", "1.21.9", -1)]
    [InlineData("1.21.9", "1.21.10", -1)]
    [InlineData("1.20.1", "1.20.1", 0)]
    [InlineData("1.14.4", "1.14.3", 1)]
    public void VersionComparisonIsNumeric(string left, string right, int expectedSign)
    {
        Assert.Equal(expectedSign, Math.Sign(ComponentRules.CompareVersions(left, right)));
    }

    [Fact]
    public void OptiFineAsModOnlyWithLoader()
    {
        var vanilla = new ComponentSelection { OptiFine = Build(Modern) };
        Assert.False(vanilla.OptiFineAsMod);

        var fabric = Select(ModLoader.Fabric) with { OptiFine = Build(Modern) };
        Assert.True(fabric.OptiFineAsMod);
    }

    [Fact]
    public void DefaultNameFollowsPclStyle()
    {
        Assert.Equal("1.21.4", new ComponentSelection().BuildDefaultName("1.21.4"));

        Assert.Equal(
            "1.21.4-Fabric_0.16.9",
            Select(ModLoader.Fabric, "0.16.9").BuildDefaultName("1.21.4"));

        Assert.Equal(
            "1.12.2-OptiFine_HD_U_G5",
            new ComponentSelection { OptiFine = Build(Legacy, "HD_U", "G5") }.BuildDefaultName(Legacy));

        Assert.Equal(
            "1.12.2-LiteLoader",
            new ComponentSelection { LiteLoader = Lite(Legacy) }.BuildDefaultName(Legacy));
    }

    private static OptiFineBuild Build(string mc, string type = "HD_U", string patch = "I6") =>
        new(mc, type, patch, $"OptiFine_{mc}_{type}_{patch}.jar", null);

    private static LiteLoaderBuild Lite(string mc) =>
        new(mc, $"{mc}-SNAPSHOT", "com.mumfrey.liteloader.launch.LiteLoaderTweaker", []);
}

public class LiteLoaderProfileTests
{
    [Fact]
    public void ProfileUsesLaunchWrapperAndTweakClass()
    {
        var build = new LiteLoaderBuild(
            "1.12.2",
            "1.12.2-SNAPSHOT",
            "com.mumfrey.liteloader.launch.LiteLoaderTweaker",
            ["net.minecraft:launchwrapper:1.12", "org.ow2.asm:asm-all:5.2"]);

        var detail = LiteLoaderInstaller.BuildProfile(build, "1.12.2", "1.12.2-LiteLoader");

        Assert.Equal("1.12.2-LiteLoader", detail.Id);
        Assert.Equal("1.12.2", detail.InheritsFrom);
        Assert.Equal("net.minecraft.launchwrapper.Launch", detail.MainClass);

        var gameArgs = detail.Arguments!.Game.SelectMany(a => a.Enumerate()).ToList();
        Assert.Equal(["--tweakClass", "com.mumfrey.liteloader.launch.LiteLoaderTweaker"], gameArgs);

        Assert.Contains(detail.Libraries, l => l.Name == "com.mumfrey:liteloader:1.12.2-SNAPSHOT");
        Assert.Contains(detail.Libraries, l => l.Name == "org.ow2.asm:asm-all:5.2");
    }

    [Theory]
    [InlineData("1.8", "1.8-SNAPSHOT", "1.8.0-SNAPSHOT")]
    [InlineData("1.9", "1.9-SNAPSHOT", "1.9.0-SNAPSHOT")]
    [InlineData("1.12.2", "1.12.2-SNAPSHOT", "1.12.2-SNAPSHOT")]
    [InlineData("1.7.10", "1.7.10_04", "1.7.10_04")]
    public void PatchlessVersionsGetDotZero(string mc, string version, string expected)
    {
        Assert.Equal(expected, LiteLoaderService.NormalizeVersion(mc, version));
    }

    [Theory]
    [InlineData("com.mumfrey:liteloader:1.12.2-SNAPSHOT", "https://bmclapi2.bangbang93.com/maven/")]
    [InlineData("net.minecraft:launchwrapper:1.12", "https://libraries.minecraft.net/")]
    [InlineData("org.ow2.asm:asm-all:5.2", "https://repo1.maven.org/maven2/")]
    [InlineData("com.google.code.gson:gson:2.10.1", null)]
    public void LibrariesResolveToTheirRepositories(string name, string? expected)
    {
        Assert.Equal(expected, LiteLoaderService.GetLibraryUrl(name));
    }
}

public class OptiFineParsingTests
{
    [Theory]
    [InlineData("Forge N/A", null)]
    [InlineData("Forge 14.23.5.2859", "14.23.5.2859")]
    [InlineData("Forge 47.1.0", "47.1.0")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ForgeFieldIsNormalized(string? forge, string? expected)
    {
        Assert.Equal(expected, OptiFineService.NormalizeForge(forge));
    }

    [Fact]
    public void BuildVersionMatchesInstallerConvention()
    {
        var build = new OptiFineBuild("1.20.1", "HD_U", "I6", "OptiFine_1.20.1_HD_U_I6.jar", null);

        // 安装器写进 optifine:OptiFine:<ver> 的就是这个形态
        Assert.Equal("1.20.1_HD_U_I6", build.Version);
        Assert.Equal("HD_U_I6", build.DisplayVersion);
    }

    [Fact]
    public void BuildsSortNewestFirstWithStableBeforePreview()
    {
        static OptiFineBuild Build(string patch) =>
            new("1.20.1", "HD_U", patch, $"OptiFine_1.20.1_HD_U_{patch}.jar", null);

        var builds = new[]
        {
            Build("I5_pre4"),
            Build("I5_pre9"),
            Build("I5"),
            Build("I6_pre1"),
            Build("I6"),
            Build("H9"),
        };

        var sorted = builds
            .OrderByDescending(b => b, Comparer<OptiFineBuild>.Create(OptiFineService.CompareBuilds))
            .Select(b => b.Patch)
            .ToList();

        // 最新的正式版在最前；同号的预发布版排在它之后；跨字母也要排对
        Assert.Equal(["I6", "I6_pre1", "I5", "I5_pre9", "I5_pre4", "H9"], sorted);
    }

    [Theory]
    [InlineData("I6", false)]
    [InlineData("I5_pre4", true)]
    public void PreviewDetection(string patch, bool expected)
    {
        Assert.Equal(expected, OptiFineService.IsPreview(patch));
    }

    [Fact]
    public void InstallerUrlFollowsBmclapiLayout()
    {
        var build = new OptiFineBuild("1.20.1", "HD_U", "I6", "OptiFine_1.20.1_HD_U_I6.jar", null);

        Assert.Equal(
            "https://bmclapi2.bangbang93.com/optifine/1.20.1/HD_U/I6",
            OptiFineService.GetInstallerUrlFor(build));
    }
}

public class ModrinthParsingTests
{
    [Fact]
    public void PrimaryFileIsPreferred()
    {
        const string json = """
        [
          {
            "id": "p96k10UR",
            "version_number": "0.119.4+1.21.4",
            "files": [
              { "url": "https://cdn/extra.jar", "filename": "extra.jar", "primary": false,
                "hashes": { "sha1": "aaa" } },
              { "url": "https://cdn/fabric-api.jar", "filename": "fabric-api-0.119.4+1.21.4.jar",
                "primary": true, "hashes": { "sha1": "1c7871b6af04edc8b8f0dbad12606d67f6118a11" } }
            ]
          }
        ]
        """;

        var versions = System.Text.Json.JsonSerializer.Deserialize<List<TestModrinthVersion>>(
            json, Solar.Core.Json.SolarJson.Options)!;

        var file = versions[0].Files!.FirstOrDefault(f => f.Primary) ?? versions[0].Files![0];

        Assert.Equal("fabric-api-0.119.4+1.21.4.jar", file.Filename);
        Assert.Equal("1c7871b6af04edc8b8f0dbad12606d67f6118a11", file.Hashes!.Sha1);
    }

    private sealed class TestModrinthVersion
    {
        [System.Text.Json.Serialization.JsonPropertyName("id")]
        public string? Id { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("files")]
        public List<TestModrinthFile>? Files { get; set; }
    }

    private sealed class TestModrinthFile
    {
        [System.Text.Json.Serialization.JsonPropertyName("url")]
        public string? Url { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("filename")]
        public string? Filename { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("primary")]
        public bool Primary { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("hashes")]
        public TestModrinthHashes? Hashes { get; set; }
    }

    private sealed class TestModrinthHashes
    {
        [System.Text.Json.Serialization.JsonPropertyName("sha1")]
        public string? Sha1 { get; set; }
    }
}

public class LiteLoaderVersionsJsonTests
{
    [Fact]
    public void SnapshotsSectionIsParsed()
    {
        // 取自 dl.liteloader.com/versions/versions.json 的 1.12.2 片段
        const string json = """
        {
          "1.12.2": {
            "repo": { "stream": "SNAPSHOT", "type": "m2" },
            "snapshots": {
              "libraries": [
                { "name": "net.minecraft:launchwrapper:1.12" },
                { "name": "org.spongepowered:mixin:0.7.4-SNAPSHOT" }
              ],
              "com.mumfrey:liteloader": {
                "1420785ecbfed5aff4a586c5c9dd97eb": {
                  "tweakClass": "com.mumfrey.liteloader.launch.LiteLoaderTweaker",
                  "version": "1.12.2-SNAPSHOT"
                },
                "latest": {
                  "tweakClass": "com.mumfrey.liteloader.launch.LiteLoaderTweaker",
                  "libraries": [
                    { "name": "net.minecraft:launchwrapper:1.12" },
                    { "name": "org.ow2.asm:asm-all:5.2" }
                  ],
                  "version": "1.12.2-SNAPSHOT"
                }
              }
            }
          }
        }
        """;

        var versions = System.Text.Json.JsonSerializer.Deserialize<
            Dictionary<string, TestVersionEntry>>(json, Solar.Core.Json.SolarJson.Options)!;

        var entry = versions["1.12.2"].Snapshots!;
        Assert.Equal("1.12.2-SNAPSHOT", entry.LiteLoader!.Latest!.Version);
        Assert.Equal(2, entry.Libraries!.Count);

        // 哈希键也要能接住，否则反序列化会丢掉它们（这里只验证不抛异常）
        Assert.True(entry.LiteLoader.Builds!.ContainsKey("1420785ecbfed5aff4a586c5c9dd97eb"));
    }

    private sealed class TestVersionEntry
    {
        [System.Text.Json.Serialization.JsonPropertyName("snapshots")]
        public TestRepository? Snapshots { get; set; }
    }

    private sealed class TestRepository
    {
        [System.Text.Json.Serialization.JsonPropertyName("libraries")]
        public List<TestLibrary>? Libraries { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("com.mumfrey:liteloader")]
        public TestArtifacts? LiteLoader { get; set; }
    }

    private sealed class TestLibrary
    {
        [System.Text.Json.Serialization.JsonPropertyName("name")]
        public string Name { get; set; } = "";
    }

    private sealed class TestArtifacts
    {
        [System.Text.Json.Serialization.JsonPropertyName("latest")]
        public TestBuild? Latest { get; set; }

        [System.Text.Json.Serialization.JsonExtensionData]
        public Dictionary<string, System.Text.Json.JsonElement>? Builds { get; set; }
    }

    private sealed class TestBuild
    {
        [System.Text.Json.Serialization.JsonPropertyName("version")]
        public string? Version { get; set; }
    }
}
