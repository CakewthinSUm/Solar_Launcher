using Solar.Core.Loaders;
using Xunit;

namespace Solar.Core.Tests;

public class ForgeCoordinateTests
{
    private const string Libs = @"C:\shared\libraries";

    [Theory]
    [InlineData("com.google.code.gson:gson:2.10.1",
        @"C:\shared\libraries\com\google\code\gson\gson\2.10.1\gson-2.10.1.jar")]
    [InlineData("net.minecraftforge:forge:1.21.4-54.1.22:client",
        @"C:\shared\libraries\net\minecraftforge\forge\1.21.4-54.1.22\forge-1.21.4-54.1.22-client.jar")]
    [InlineData("net.minecraft:client:1.21.4:mappings@tsrg",
        @"C:\shared\libraries\net\minecraft\client\1.21.4\client-1.21.4-mappings.tsrg")]
    public void CoordinateMapsToLibraryPath(string coordinate, string expected)
    {
        Assert.Equal(expected, ForgeDataResolver.GetLibraryPath(coordinate, Libs));
    }

    [Fact]
    public void ParseCoordinateHandlesClassifierAndExtension()
    {
        var (group, artifact, version, classifier, extension) =
            ForgeDataResolver.ParseCoordinate("net.minecraft:client:1.21.4:mappings@tsrg");

        Assert.Equal("net.minecraft", group);
        Assert.Equal("client", artifact);
        Assert.Equal("1.21.4", version);
        Assert.Equal("mappings", classifier);
        Assert.Equal("tsrg", extension);
    }

    [Fact]
    public void ParseCoordinateDefaultsToJar()
    {
        var (_, _, _, classifier, extension) =
            ForgeDataResolver.ParseCoordinate("com.google.code.gson:gson:2.10.1");

        Assert.Null(classifier);
        Assert.Equal("jar", extension);
    }
}

public class ForgeDataResolutionTests
{
    private const string Libs = @"C:\shared\libraries";
    private const string Extract = @"C:\tmp\extract";

    [Fact]
    public void MavenCoordinateResolvesToLocalPath()
    {
        var resolved = ForgeDataResolver.Resolve(
            "[net.minecraft:client:1.21.4:mappings@tsrg]", new Dictionary<string, string>(), Libs, Extract);

        Assert.EndsWith(@"client-1.21.4-mappings.tsrg", resolved);
    }

    [Fact]
    public void JarPathResolvesUnderExtractDirectory()
    {
        var resolved = ForgeDataResolver.Resolve(
            "/data/client.lzma", new Dictionary<string, string>(), Libs, Extract);

        Assert.Equal(Path.Combine(Extract, "data", "client.lzma"), resolved);
    }

    [Fact]
    public void PlaceholdersAreExpanded()
    {
        var data = new Dictionary<string, string>
        {
            ["MINECRAFT_JAR"] = @"C:\mc\1.21.4.jar",
            ["MOJMAPS"] = @"C:\libs\mappings.tsrg",
        };

        var resolved = ForgeDataResolver.ExpandPlaceholders(
            "--input {MINECRAFT_JAR} --names {MOJMAPS}", data, Libs, Extract);

        Assert.Equal(@"--input C:\mc\1.21.4.jar --names C:\libs\mappings.tsrg", resolved);
    }

    [Fact]
    public void UnknownPlaceholderIsLeftIntact()
    {
        var resolved = ForgeDataResolver.ExpandPlaceholders(
            "--unknown {NOT_A_KEY}", new Dictionary<string, string>(), Libs, Extract);

        Assert.Equal("--unknown {NOT_A_KEY}", resolved);
    }

    [Fact]
    public void LiteralValuePassesThrough()
    {
        var resolved = ForgeDataResolver.Resolve("1.21.4", new Dictionary<string, string>(), Libs, Extract);
        Assert.Equal("1.21.4", resolved);
    }

    [Fact]
    public void SingleQuotedValueIsUnwrapped()
    {
        // NeoForge 的 MCP_VERSION 写成 '1.21.4-20241203.161809'，
        // 引号是语法标记，不能进到实际参数里
        var resolved = ForgeDataResolver.Resolve(
            "'1.21.4-20241203.161809'", new Dictionary<string, string>(), Libs, Extract);

        Assert.Equal("1.21.4-20241203.161809", resolved);
    }

    [Fact]
    public void ZipExtensionIsPreserved()
    {
        // NeoForge 的 neoform 坐标以 @zip 结尾，不是 jar
        var resolved = ForgeDataResolver.Resolve(
            "[net.neoforged:neoform:1.21.4-20241203.161809@zip]",
            new Dictionary<string, string>(), Libs, Extract);

        Assert.EndsWith(".zip", resolved);
        Assert.Contains("neoform-1.21.4-20241203.161809", resolved);
    }
}

public class ForgeProcessorSideTests
{
    [Fact]
    public void NoSidesMeansBothEnds()
    {
        var processor = new ForgeProcessor { Jar = "x" };
        Assert.True(processor.AppliesToClient);
    }

    [Fact]
    public void EmptySidesMeansBothEnds()
    {
        var processor = new ForgeProcessor { Jar = "x", Sides = [] };
        Assert.True(processor.AppliesToClient);
    }

    [Fact]
    public void ServerOnlyIsSkippedOnClient()
    {
        // Forge 1.21.4 的多数 processor 只用于服务端，
        // 在客户端执行它们会白跑甚至失败
        var processor = new ForgeProcessor { Jar = "x", Sides = ["server"] };
        Assert.False(processor.AppliesToClient);
    }

    [Fact]
    public void ClientSideRuns()
    {
        var processor = new ForgeProcessor { Jar = "x", Sides = ["client"] };
        Assert.True(processor.AppliesToClient);
    }
}

public class NeoForgePrefixTests
{
    [Theory]
    [InlineData("1.21.4", "21.4")]
    [InlineData("1.21.1", "21.1")]
    [InlineData("1.20.4", "20.4")]
    [InlineData("1.21", "21.0")]
    public void ModernVersionsMapToPrefix(string minecraftVersion, string expected)
    {
        Assert.Equal(expected, LoaderVersionService.ToNeoForgePrefix(minecraftVersion));
    }

    [Theory]
    [InlineData("1.12.2")]
    [InlineData("1.7.10")]
    [InlineData("1.16.5")]
    public void LegacyVersionsHaveNoNeoForge(string minecraftVersion)
    {
        // NeoForge 从 1.20.1 开始，老版本不该出现在列表里
        Assert.Null(LoaderVersionService.ToNeoForgePrefix(minecraftVersion));
    }

    [Fact]
    public void MalformedVersionReturnsNull()
    {
        Assert.Null(LoaderVersionService.ToNeoForgePrefix("abc"));
        Assert.Null(LoaderVersionService.ToNeoForgePrefix("2.0"));
    }
}

public class ForgeInstallProfileParsingTests
{
    [Fact]
    public void ParsesRealProfileShape()
    {
        // 取自 Forge 1.21.4 的 install_profile.json 片段
        const string json = """
        {
          "spec": 1,
          "version": "1.21.4-54.1.22",
          "minecraft": "1.21.4",
          "data": {
            "MOJMAPS": { "client": "[net.minecraft:client:1.21.4:mappings@tsrg]", "server": "[net.minecraft:server:1.21.4:mappings@tsrg]" },
            "BINPATCH": { "client": "/data/client.lzma", "server": "/data/server.lzma" }
          },
          "processors": [
            {
              "jar": "net.minecraftforge:binarypatcher:1.2.0",
              "classpath": ["net.minecraftforge:srgutils:0.5.6"],
              "args": ["--clean", "{MC_OFF}", "--output", "{PATCHED}", "--apply", "{BINPATCH}"],
              "outputs": { "{PATCHED}": "{PATCHED_SHA}" }
            },
            {
              "jar": "net.minecraftforge:installertools:1.4.3",
              "args": ["--task", "DOWNLOAD_MOJMAPS"],
              "sides": ["server"]
            }
          ],
          "libraries": [
            { "name": "com.google.code.gson:gson:2.10.1",
              "downloads": { "artifact": { "path": "a/b.jar", "url": "https://x/y.jar", "sha1": "abc", "size": 1 } } }
          ]
        }
        """;

        var profile = System.Text.Json.JsonSerializer.Deserialize<ForgeInstallProfile>(
            json, Solar.Core.Json.SolarJson.Options)!;

        Assert.Equal(1, profile.Spec);
        Assert.Equal("1.21.4", profile.Minecraft);
        Assert.Equal(2, profile.Processors.Count);
        Assert.Equal("/data/client.lzma", profile.Data["BINPATCH"].Client);

        Assert.True(profile.Processors[0].AppliesToClient);
        Assert.False(profile.Processors[1].AppliesToClient);
        Assert.Single(profile.Libraries);
    }
}
