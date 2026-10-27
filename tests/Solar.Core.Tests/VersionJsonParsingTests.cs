using System.Text.Json;
using Solar.Core.Json;
using Solar.Core.Minecraft;
using Solar.Core.Models;
using Xunit;

namespace Solar.Core.Tests;

/// <summary>
/// 用真实 Mojang 版本 JSON 的片段做反序列化回归测试。
/// 这些字段的"形状"（对象 vs 数字 vs 字符串）曾导致启动器完全无法解析任何版本。
/// </summary>
public class VersionJsonParsingTests
{
    [Fact]
    public void ParsesJavaVersionObject()
    {
        const string json = """
        {
          "id": "1.21.4",
          "javaVersion": { "component": "java-runtime-delta", "majorVersion": 21 }
        }
        """;

        var detail = JsonSerializer.Deserialize<VersionDetail>(json, SolarJson.Options)!;

        Assert.NotNull(detail.JavaVersion);
        Assert.Equal(21, detail.JavaVersion.MajorVersion);
        Assert.Equal("java-runtime-delta", detail.JavaVersion.Component);
    }

    [Fact]
    public void ParsesOldStyleNativesMap()
    {
        const string json = """
        {
          "id": "1.16.5",
          "libraries": [
            {
              "name": "org.lwjgl:lwjgl:3.2.1",
              "natives": { "linux": "natives-linux", "osx": "natives-macos", "windows": "natives-windows" },
              "downloads": {
                "classifiers": {
                  "natives-windows": { "path": "x.jar", "sha1": "abc", "size": 1 }
                }
              }
            }
          ]
        }
        """;

        var detail = JsonSerializer.Deserialize<VersionDetail>(json, SolarJson.Options)!;
        var library = Assert.Single(detail.Libraries);

        Assert.NotNull(library.Natives);
        Assert.Equal(3, library.Natives.Count);
        Assert.Equal("natives-windows", library.Natives["windows"]);
        Assert.True(library.Downloads!.Classifiers!.ContainsKey("natives-windows"));
    }

    [Fact]
    public void ParsesLoggingConfig()
    {
        const string json = """
        {
          "id": "1.21.4",
          "logging": {
            "client": {
              "argument": "-Dlog4j.configurationFile=${path}",
              "type": "log4j2-xml",
              "file": { "id": "client-1.21.2.xml", "sha1": "39384bd1", "size": 1073, "url": "https://x/y" }
            }
          }
        }
        """;

        var detail = JsonSerializer.Deserialize<VersionDetail>(json, SolarJson.Options)!;

        Assert.NotNull(detail.Logging?.Client);
        Assert.Equal("-Dlog4j.configurationFile=${path}", detail.Logging.Client.Argument);
        Assert.Equal("client-1.21.2.xml", detail.Logging.Client.File!.Id);
    }

    [Fact]
    public void ParsesAssetIndexFlags()
    {
        // 老版本用字符串 "pre-1.6"，新版本该字段直接不出现
        const string legacy = """{ "objects": {}, "virtual": "pre-1.6", "map_to_resources": true }""";
        const string modern = """{ "objects": {} }""";

        var legacyIndex = JsonSerializer.Deserialize<AssetIndex>(legacy, SolarJson.Options)!;
        var modernIndex = JsonSerializer.Deserialize<AssetIndex>(modern, SolarJson.Options)!;

        Assert.True(legacyIndex.IsVirtual);
        Assert.True(legacyIndex.IsMapToResources);

        Assert.False(modernIndex.IsVirtual);
        Assert.False(modernIndex.IsMapToResources);
    }

    [Fact]
    public void MixedArgumentArrayIsParsed()
    {
        const string json = """
        {
          "id": "1.21.4",
          "arguments": {
            "game": [
              "--username",
              { "rules": [{ "action": "allow", "os": { "name": "windows" } }], "value": "--win" },
              { "rules": [{ "action": "allow", "features": { "is_demo_user": true } }], "value": ["--demo"] }
            ]
          }
        }
        """;

        var detail = JsonSerializer.Deserialize<VersionDetail>(json, SolarJson.Options)!;
        var game = detail.Arguments!.Game;

        Assert.Equal(3, game.Count);
        Assert.Equal("--username", game[0].Value);
        Assert.Equal("--win", game[1].Value);
        Assert.Equal(["--demo"], game[2].Values);
    }

    [Theory]
    [InlineData("natives-windows", "x64", true)]
    [InlineData("natives-windows", "x86", false)]
    [InlineData("natives-windows", "arm64", false)]
    [InlineData("natives-windows-x86", "x86", true)]
    [InlineData("natives-windows-x86", "x64", false)]
    [InlineData("natives-windows-arm64", "arm64", true)]
    [InlineData("natives-windows-arm64", "x64", false)]
    [InlineData("natives-linux", "x64", true)]
    public void NativeClassifierArchitectureMatching(string classifier, string arch, bool expected)
    {
        // 三个 natives 变体的 rules 都是 allow windows，只能靠后缀区分架构。
        // 若不过滤，32 位 DLL 可能覆盖 64 位版本。
        var library = new Library { Name = $"org.lwjgl:lwjgl:3.3.3:{classifier}" };
        Assert.Equal(expected, library.MatchesArchitecture(arch));
    }

    [Fact]
    public void NonNativeLibraryAlwaysMatchesArchitecture()
    {
        var library = new Library { Name = "com.mojang:brigadier:1.3.10" };
        Assert.True(library.MatchesArchitecture("x64"));
        Assert.True(library.MatchesArchitecture("arm64"));
    }

    [Fact]
    public void EffectiveLibrariesExcludeWrongArchitectureNatives()
    {
        var detail = new VersionDetail
        {
            Id = "1.21.4",
            Libraries =
            [
                new Library { Name = "org.lwjgl:lwjgl:3.3.3" },
                new Library
                {
                    Name = "org.lwjgl:lwjgl:3.3.3:natives-windows",
                    Rules = [new Rule { Action = "allow", Os = new OsRule { Name = "windows" } }],
                },
                new Library
                {
                    Name = "org.lwjgl:lwjgl:3.3.3:natives-windows-x86",
                    Rules = [new Rule { Action = "allow", Os = new OsRule { Name = "windows" } }],
                },
                new Library
                {
                    Name = "org.lwjgl:lwjgl:3.3.3:natives-windows-arm64",
                    Rules = [new Rule { Action = "allow", Os = new OsRule { Name = "windows" } }],
                },
            ],
        };

        var version = new ResolvedVersion { Detail = detail, Chain = [] };
        var x64 = version.GetEffectiveLibraries(new RuleContext { OsName = "windows", Arch = "x64" }).ToList();

        Assert.Equal(2, x64.Count);
        Assert.Contains(x64, l => l.Name == "org.lwjgl:lwjgl:3.3.3:natives-windows");
        Assert.DoesNotContain(x64, l => l.Name.Contains("x86"));
        Assert.DoesNotContain(x64, l => l.Name.Contains("arm64"));
    }

    [Fact]
    public void AssetObjectPathUsesFirstTwoHashChars()
    {
        var obj = new AssetObject { Hash = "b62ca8ec10d07e6bf5ac8dae0c8c1d2e6a1e3356" };
        Assert.Equal(Path.Combine("b6", "b62ca8ec10d07e6bf5ac8dae0c8c1d2e6a1e3356"), obj.RelativePath);
    }
}
