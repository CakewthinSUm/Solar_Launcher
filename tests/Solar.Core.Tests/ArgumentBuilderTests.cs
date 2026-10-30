using Solar.Core.Versions;
using Solar.Core.Launch;
using Solar.Core.Memory;
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
        (int, int)? resolution = null,
        int javaMajorVersion = 8)
    {
        var appPaths = new AppPaths(Path.Combine(Path.GetTempPath(), "solar-test-" + Guid.NewGuid().ToString("N")));
        var paths = new MinecraftPaths(appPaths, new TestSettings());
        var version = new ResolvedVersion
        {
            Detail = detail ?? new VersionDetail { Id = "1.21.4", MainClass = "net.minecraft.client.main.Main" },
            Chain = [],
        };

        return new LaunchContext
        {
            Game = new GameVersion { Id = "inst1", Name = "测试" },
            Version = version,
            Paths = paths,
            Settings = new LauncherSettings { MaxMemoryMb = 4096 },
            JavaPath = "java.exe",
            JavaMajorVersion = javaMajorVersion,
            PlayerName = "Steve",
            Uuid = "8667ba71-b85a-4004-af54-457a9734eed7",
            AccessToken = "0",
            Xuid = "2535400000000000",
            UserType = "msa",
            GameDirectory = @"C:\games\inst1",
            NativesDirectory = @"C:\games\inst1\natives",
            AssetsDirectory = @"C:\shared\assets",
            Resolution = resolution,
            MaxMemoryMb = 4096,
        };
    }

    [Fact]
    public void JvmArgumentsIncludeGcEncodingAndLog4jDefense()
    {
        var context = CreateContext(javaMajorVersion: 21);
        var args = new ArgumentBuilder().BuildJvmArguments(context);

        Assert.Contains("-XX:+UseZGC", args);
        Assert.Contains("-XX:+ZGenerational", args);
        Assert.Contains("-Dstdout.encoding=UTF-8", args);
        Assert.Contains("-Dstderr.encoding=UTF-8", args);
        Assert.Contains("-Dlog4j2.formatMsgNoLookups=true", args);
        Assert.Contains("-Xmx4096M", args);
    }

    [Fact]
    public void Java8UsesG1AndLegacyEncodingProperty()
    {
        var context = CreateContext(javaMajorVersion: 8);
        var args = new ArgumentBuilder().BuildJvmArguments(context);

        Assert.Contains("-XX:+UseG1GC", args);
        Assert.DoesNotContain("-XX:+UseZGC", args);
        Assert.Contains("-Dsun.stdout.encoding=UTF-8", args);
    }

    [Fact]
    public void DuplicateClassPathFromVersionJsonIsCollapsed()
    {
        // 1.17+ 的版本 JSON 里自带 -cp ${classpath} 与 -Djava.library.path，
        // 与启动器加的那份内容相同，必须去重，否则 classpath 会被重复设置
        var detail = new VersionDetail
        {
            Id = "1.21",
            MainClass = "net.minecraft.client.main.Main",
            Arguments = new Arguments
            {
                Jvm =
                [
                    new ArgumentEntry { Value = "-cp" },
                    new ArgumentEntry { Value = "${classpath}" },
                    new ArgumentEntry { Value = "-Djava.library.path=${natives_directory}" },
                ],
            },
        };

        var args = new ArgumentBuilder().BuildJvmArguments(CreateContext(detail));

        Assert.Equal(1, args.Count(a => a == "-cp"));
        Assert.Equal(
            1,
            args.Count(a => a.StartsWith("-Djava.library.path=", StringComparison.Ordinal)));
    }

    [Fact]
    public void GameArgumentsLetLaterValueWin()
    {
        // 重复的 --width 会让游戏两个都失效，后出现的值必须覆盖先出现的
        var detail = new VersionDetail
        {
            Id = "1.21",
            MainClass = "net.minecraft.client.main.Main",
            Arguments = new Arguments
            {
                Game =
                [
                    new ArgumentEntry { Value = "--width" },
                    new ArgumentEntry { Value = "1280" },
                    new ArgumentEntry { Value = "--width" },
                    new ArgumentEntry { Value = "640" },
                ],
            },
        };

        var args = new ArgumentBuilder().BuildGameArguments(CreateContext(detail));

        var index = args.ToList().IndexOf("--width");
        Assert.Equal("640", args[index + 1]);
        Assert.Equal(1, args.Count(a => a == "--width"));
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
    public void HeapLimitComesFromContext()
    {
        // 用多少内存由 LaunchContextFactory 算好后写进上下文，参数构建器只负责拼参数
        var context = CreateContext();
        var larger = new LaunchContext
        {
            Game = context.Game,
            Version = context.Version,
            Paths = context.Paths,
            Settings = context.Settings,
            JavaPath = context.JavaPath,
            PlayerName = context.PlayerName,
            Uuid = context.Uuid,
            AccessToken = context.AccessToken,
            MaxMemoryMb = 8192,
        };

        var args = new ArgumentBuilder().BuildJvmArguments(larger);
        Assert.Contains("-Xmx8192M", args);
        Assert.DoesNotContain(args, a => a == "-Xmx4096M");
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
        context.Game.CustomWindowTitle = "我的世界";

        Assert.Equal("我的世界", ArgumentBuilder.Substitute("${version_name}", context));
    }

    [Fact]
    public void BlankCustomWindowTitleFallsBackToVersionId()
    {
        var context = CreateContext();
        context.Game.CustomWindowTitle = "   ";

        Assert.Equal("1.21.4", ArgumentBuilder.Substitute("${version_name}", context));
    }

    [Fact]
    public void CustomWindowTitleIsTrimmed()
    {
        var context = CreateContext();
        context.Game.CustomWindowTitle = "  存档服  ";

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
        context.Game.CustomWindowTitle = "整合包：星辰";

        var args = new ArgumentBuilder().BuildGameArguments(context);

        Assert.Equal(["--version", "整合包：星辰"], args);
    }

    private sealed class TestSettings : ISettingsService
    {
        public LauncherSettings Current { get; } = new();

        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
