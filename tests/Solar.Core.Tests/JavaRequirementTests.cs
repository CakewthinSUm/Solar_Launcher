using Solar.Core.Instances;
using Solar.Core.Java;
using Solar.Core.Minecraft;
using Solar.Core.Models;
using Xunit;

namespace Solar.Core.Tests;

public class JavaRequirementTests
{
    private static JavaRequirement For(
        string minecraftVersion,
        ModLoader loader = ModLoader.Vanilla,
        string? loaderVersion = null,
        string? optifine = null,
        string? liteloader = null,
        int? javaMajor = null)
    {
        var instance = new GameInstance
        {
            MinecraftVersion = minecraftVersion,
            Loader = loader,
            LoaderVersion = loaderVersion,
            OptiFineVersion = optifine,
            LiteLoaderVersion = liteloader,
        };

        var version = new ResolvedVersion
        {
            Detail = new VersionDetail
            {
                Id = minecraftVersion,
                JavaVersion = javaMajor is { } major ? new JavaVersionInfo { MajorVersion = major } : null,
            },
            Chain = [],
        };

        return JavaRequirements.For(instance, version);
    }

    [Theory]
    [InlineData("1.20.1", 17)]
    [InlineData("1.20.5", 21)]
    [InlineData("1.21.4", 21)]
    [InlineData("1.12.2", 8)]
    public void VanillaUsesVersionBasedMinimum(string mcVersion, int expectedMin)
    {
        Assert.Equal(expectedMin, For(mcVersion).MinMajor);
    }

    [Fact]
    public void NewVersionSchemeUsesMojangRequirement()
    {
        // 26.2 的版本 JSON 明确要求 Java 25，不能退到 21 硬启动
        var requirement = For("26.2", javaMajor: 25);

        Assert.Equal(25, requirement.MinMajor);
        Assert.Null(requirement.MaxMajor);
    }

    [Fact]
    public void ForgeOnLegacyVersionCapsAtJava8()
    {
        var requirement = For("1.12.2", ModLoader.Forge, "14.23.5.2854");

        Assert.Equal(8, requirement.MaxMajor);
    }

    [Fact]
    public void ForgeOn1_20_1CapsAtJava21()
    {
        var requirement = For("1.20.1", ModLoader.Forge, "47.2.0");

        Assert.Equal(21, requirement.MaxMajor);
    }

    [Fact]
    public void OptiFineOn1_12CapsAtJava8()
    {
        var requirement = For("1.12.2", optifine: "1.12.2_HD_U_G5");

        Assert.Equal(8, requirement.MaxMajor);
    }

    [Fact]
    public void LiteLoaderCapsAtJava8()
    {
        Assert.Equal(8, For("1.7.10", liteloader: "1.7.10_04").MaxMajor);
    }

    [Fact]
    public void OldFabricLoaderExcludesJava25()
    {
        var requirement = For("1.21.4", ModLoader.Fabric, "0.16.9");

        // 1.20.5+ 的下限（21）与 Fabric 的下限（17）取高者
        Assert.Equal(21, requirement.MinMajor);
        Assert.Equal(24, requirement.MaxMajor);
    }

    [Fact]
    public void DescribeReadsNaturally()
    {
        Assert.Equal("Java 21 或更高", new JavaRequirement(21).Describe());
        Assert.Equal("Java 8", new JavaRequirement(8, 8).Describe());
        Assert.Equal("Java 17–24", new JavaRequirement(17, 24).Describe());
    }

    [Fact]
    public void RuntimeMustBeWithinRange()
    {
        var requirement = new JavaRequirement(17, 24);

        Assert.True(requirement.IsSatisfiedBy(new JavaRuntime("a", "21", 21)));
        Assert.False(requirement.IsSatisfiedBy(new JavaRuntime("b", "25", 25)));
        Assert.False(requirement.IsSatisfiedBy(new JavaRuntime("c", "8", 8)));
    }
}
