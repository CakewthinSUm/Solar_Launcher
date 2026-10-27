using Solar.Core.Instances;
using Solar.Core.Launch;
using Solar.Core.Minecraft;
using Solar.Core.Models;
using Solar.Core.Paths;
using Solar.Core.Settings;
using Xunit;

namespace Solar.Core.Tests;

public class ArgumentBuilderTests
{
    private static LaunchContext CreateContext(
        VersionDetail? detail = null,
        (int, int)? resolution = null)
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "solar-test-" + Guid.NewGuid().ToString("N")));
        var version = new ResolvedVersion
        {
            Detail = detail ?? new VersionDetail { Id = "1.21.4", MainClass = "net.minecraft.client.main.Main" },
            Chain = [],
        };

        return new LaunchContext
        {
            Instance = new GameInstance { Id = "inst1", Name = "测试" },
            Version = version,
            Paths = paths,
            Settings = new LauncherSettings { MaxMemoryMb = 4096 },
            JavaPath = "java.exe",
            PlayerName = "Steve",
            Uuid = "8667ba71-b85a-4004-af54-457a9734eed7",
            AccessToken = "0",
            Xuid = "2535400000000000",
            UserType = "msa",
            GameDirectory = @"C:\games\inst1",
            NativesDirectory = @"C:\games\inst1\natives",
            AssetsDirectory = @"C:\shared\assets",
            Resolution = resolution,
        };
    }

    [Fact]
    public void SubstitutesPlayerPlaceholders()
    {
        var context = CreateContext();
        var result = ArgumentBuilder.Substitute("--username ${auth_player_name} --uuid ${auth_uuid}", context);

        Assert.Equal("--username Steve --uuid 8667ba71-b85a-4004-af54-457a9734eed7", result);
    }

    [Fact]
    public void ModernAuthPlaceholdersResolve()
    {
        // 1.21.4 的 game 参数里有 ${clientid} 和 ${auth_xuid}。
        // 若原样保留，其后的参数会错位，游戏无法启动。
        var context = CreateContext();

        Assert.Equal("", ArgumentBuilder.Substitute("${clientid}", context));
        Assert.Equal("2535400000000000", ArgumentBuilder.Substitute("${auth_xuid}", context));
    }

    [Fact]
    public void NoUnresolvedPlaceholdersRemainInModernVersion()
    {
        // 用 1.21.4 的真实参数集合验证：所有占位符都必须能解析
        var context = CreateContext();
        // resolution_width/height 只有在 has_custom_resolution 规则命中时才展开，
        // 单独替换时保持原样是正确行为，因此不在此列。
        var templates = new[]
        {
            "${auth_player_name}", "${version_name}", "${game_directory}", "${assets_root}",
            "${assets_index_name}", "${auth_uuid}", "${auth_access_token}", "${clientid}",
            "${auth_xuid}", "${user_type}", "${version_type}",
        };

        foreach (var template in templates)
        {
            var result = ArgumentBuilder.Substitute(template, context);
            Assert.DoesNotContain("${", result);
        }
    }

    [Fact]
    public void UnknownPlaceholderIsLeftIntact()
    {
        var context = CreateContext();
        Assert.Equal("${unknown_thing}", ArgumentBuilder.Substitute("${unknown_thing}", context));
    }

    [Fact]
    public void GameDirectoryWithSpacesIsASingleArgument()
    {
        // 路径带空格时，必须作为单个参数传给 ArgumentList，
        // 而不是自己拼命令行加引号。
        var context = CreateContext();
        var builder = new ArgumentBuilder();

        var detail = new VersionDetail
        {
            Id = "1.21.4",
            MainClass = "net.minecraft.client.main.Main",
            Arguments = new Arguments
            {
                Game = [new ArgumentEntry { Value = "--gameDir" }, new ArgumentEntry { Value = "${game_directory}" }],
            },
        };

        var withSpaces = CreateContext(detail);
        var args = builder.BuildGameArguments(withSpaces);

        Assert.Contains(@"C:\games\inst1", args);
        Assert.DoesNotContain(args, a => a.StartsWith('"'));
    }

    [Fact]
    public void JvmArgumentsIncludeMemoryAndMainClass()
    {
        var context = CreateContext();
        var args = new ArgumentBuilder().BuildJvmArguments(context);

        Assert.Contains("-Xmx4096M", args);
        Assert.Equal("net.minecraft.client.main.Main", args[^1]);
    }

    [Fact]
    public void InstanceMemoryOverrideWins()
    {
        var context = CreateContext();
        var instance = new GameInstance { Id = "inst1", MaxMemoryMb = 8192 };
        var overridden = new LaunchContext
        {
            Instance = instance,
            Version = context.Version,
            Paths = context.Paths,
            Settings = context.Settings,
            JavaPath = context.JavaPath,
            PlayerName = context.PlayerName,
            Uuid = context.Uuid,
            AccessToken = context.AccessToken,
        };

        var args = new ArgumentBuilder().BuildJvmArguments(overridden);
        Assert.Contains("-Xmx8192M", args);
    }

    [Fact]
    public void RuleGatedArgumentsAreFiltered()
    {
        var detail = new VersionDetail
        {
            Id = "1.21.4",
            MainClass = "Main",
            Arguments = new Arguments
            {
                Game =
                [
                    new ArgumentEntry { Value = "--always" },
                    new ArgumentEntry
                    {
                        Value = "--mac-only",
                        Rules = [new Rule { Action = "allow", Os = new OsRule { Name = "osx" } }],
                    },
                ],
            },
        };

        var context = CreateContext(detail);
        var args = new ArgumentBuilder().BuildGameArguments(context);

        Assert.Contains("--always", args);
        Assert.DoesNotContain("--mac-only", args);
    }

    [Fact]
    public void ClassPathExcludesNativeLibraries()
    {
        var detail = new VersionDetail
        {
            Id = "1.21.4",
            MainClass = "Main",
            Libraries =
            [
                new Library
                {
                    Name = "org.lwjgl:lwjgl:3.3.3",
                    Downloads = new LibraryDownloads
                    {
                        Artifact = new DownloadArtifact { Path = "org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3.jar" },
                    },
                },
                new Library
                {
                    Name = "org.lwjgl:lwjgl:3.3.3:natives-windows",
                    Downloads = new LibraryDownloads
                    {
                        Artifact = new DownloadArtifact { Path = "org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3-natives-windows.jar" },
                    },
                },
            ],
        };

        var context = CreateContext(detail);
        var classPath = ArgumentBuilder.BuildClassPath(context);
        var entries = classPath.Split(Path.PathSeparator);

        Assert.Contains(entries, e => e.EndsWith("lwjgl-3.3.3.jar"));
        Assert.DoesNotContain(entries, e => e.Contains("natives-windows"));
    }

    [Fact]
    public void LegacyMinecraftArgumentsAreSplitAndSubstituted()
    {
        var detail = new VersionDetail
        {
            Id = "1.7.10",
            MainClass = "Main",
            MinecraftArguments = "--username ${auth_player_name} --gameDir ${game_directory}",
        };

        var context = CreateContext(detail);
        var args = new ArgumentBuilder().BuildGameArguments(context);

        Assert.Equal(["--username", "Steve", "--gameDir", @"C:\games\inst1"], args);
    }

    [Fact]
    public void QuotedArgumentsStayTogether()
    {
        var parts = ArgumentBuilder.SplitArguments("--a \"hello world\" --b");
        Assert.Equal(["--a", "hello world", "--b"], parts);
    }

    [Fact]
    public void ResolutionIsAppendedWhenMissing()
    {
        var context = CreateContext(resolution: (1280, 720));
        var args = new ArgumentBuilder().BuildGameArguments(context);

        Assert.Contains("--width", args);
        Assert.Contains("1280", args);
        Assert.Contains("720", args);
    }

    [Fact]
    public void CustomWindowTitleOverridesVersionName()
    {
        var context = CreateContext();
        context.Instance.CustomWindowTitle = "我的世界";

        Assert.Equal("我的世界", ArgumentBuilder.Substitute("${version_name}", context));
    }

    [Fact]
    public void BlankCustomWindowTitleFallsBackToVersionId()
    {
        var context = CreateContext();
        context.Instance.CustomWindowTitle = "   ";

        Assert.Equal("1.21.4", ArgumentBuilder.Substitute("${version_name}", context));
    }

    [Fact]
    public void CustomWindowTitleIsTrimmed()
    {
        var context = CreateContext();
        context.Instance.CustomWindowTitle = "  存档服  ";

        Assert.Equal("存档服", ArgumentBuilder.Substitute("${version_name}", context));
    }

    [Fact]
    public void CustomWindowTitleChangesGameArguments()
    {
        // 真实场景：版本 JSON 里 --version ${version_name}，启动后的窗口标题用它
        var detail = new VersionDetail
        {
            Id = "1.21.4",
            MainClass = "net.minecraft.client.main.Main",
            Arguments = new Arguments
            {
                Game = [new ArgumentEntry { Value = "--version" }, new ArgumentEntry { Value = "${version_name}" }],
            },
        };

        var context = CreateContext(detail);
        context.Instance.CustomWindowTitle = "整合包：星辰";

        var args = new ArgumentBuilder().BuildGameArguments(context);

        Assert.Equal(["--version", "整合包：星辰"], args);
    }
}
