using System.IO.Compression;
using CmlLib.Core.Files;
using Solar.Core.Download;
using Solar.Core.Minecraft;
using Solar.Core.Models;
using Solar.Core.Paths;
using Solar.Core.Settings;
using Xunit;
using AssetObject = Solar.Core.Models.AssetObject;

namespace Solar.Core.Tests;

public class MirrorRewritingTests
{
    private static readonly Uri Mirror = new("https://bmclapi2.bangbang93.com");

    [Fact]
    public void LibraryUrlGoesToMaven()
    {
        var handler = new MirrorRewritingHandler(Mirror);
        var rewritten = handler.Rewrite(new Uri(
            "https://libraries.minecraft.net/org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3.jar"));

        Assert.Equal(
            "https://bmclapi2.bangbang93.com/maven/org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3.jar",
            rewritten.ToString());
    }

    [Fact]
    public void AssetUrlGoesToAssets()
    {
        var handler = new MirrorRewritingHandler(Mirror);
        var rewritten = handler.Rewrite(new Uri(
            "https://resources.download.minecraft.net/ab/abcdef0123"));

        Assert.Equal("https://bmclapi2.bangbang93.com/assets/ab/abcdef0123", rewritten.ToString());
    }

    [Fact]
    public void PistonDataIsNotRewritten()
    {
        // 镜像对 piston-data 支持不完整，重写会导致 404，必须保持原样
        var handler = new MirrorRewritingHandler(Mirror);
        var original = new Uri(
            "https://piston-data.mojang.com/v1/objects/abc/client.jar");

        Assert.Equal(original, handler.Rewrite(original));
    }

    [Fact]
    public void NoMirrorMeansNoRewrite()
    {
        var handler = new MirrorRewritingHandler(null);
        var original = new Uri("https://libraries.minecraft.net/a/b.jar");

        Assert.Equal(original, handler.Rewrite(original));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("ftp://example.com")]
    public void InvalidMirrorIsIgnored(string? value)
    {
        Assert.Null(MirrorRewritingHandler.ParseMirror(value));
    }

    [Fact]
    public void TrailingSlashIsTrimmed()
    {
        var parsed = MirrorRewritingHandler.ParseMirror("https://bmclapi2.bangbang93.com/");
        Assert.Equal(Mirror, parsed);
    }
}

public class GameFileBuilderTests
{
    private static MinecraftPaths NewPaths() =>
        new(
            new AppPaths(Path.Combine(Path.GetTempPath(), "solar-files-" + Guid.NewGuid().ToString("N"))),
            new TestSettings());

    private static readonly RuleContext Windows = new() { OsName = "windows", Arch = "x64" };

    [Fact]
    public void NewStyleNativesGetExtractTaskAndStayOutOfClassPath()
    {
        var paths = NewPaths();
        var builder = new GameFileBuilder(paths);

        var detail = new VersionDetail
        {
            Id = "1.21.4",
            Libraries =
            [
                new Library
                {
                    Name = "org.lwjgl:lwjgl:3.3.3:natives-windows",
                    Downloads = new LibraryDownloads
                    {
                        Artifact = new DownloadArtifact
                        {
                            Path = "org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3-natives-windows.jar",
                            Url = "https://libraries.minecraft.net/x.jar",
                        },
                    },
                },
            ],
        };

        var version = new ResolvedVersion { Detail = detail, Chain = [] };
        var files = builder.BuildLibraries(version, Windows, @"C:\inst\natives").ToList();

        var native = Assert.Single(files);
        Assert.Single(native.UpdateTask);
    }

    [Fact]
    public void OldStyleNativesUseClassifierMap()
    {
        var paths = NewPaths();
        var builder = new GameFileBuilder(paths);

        var detail = new VersionDetail
        {
            Id = "1.16.5",
            Libraries =
            [
                new Library
                {
                    Name = "org.lwjgl:lwjgl:3.2.1",
                    Natives = new Dictionary<string, string> { ["windows"] = "natives-windows-${arch}" },
                    Downloads = new LibraryDownloads
                    {
                        Artifact = new DownloadArtifact { Path = "org/lwjgl/lwjgl/3.2.1/lwjgl-3.2.1.jar" },
                        Classifiers = new Dictionary<string, DownloadArtifact>
                        {
                            ["natives-windows-64"] = new()
                            {
                                Path = "org/lwjgl/lwjgl/3.2.1/lwjgl-3.2.1-natives-windows.jar",
                                Url = "https://libraries.minecraft.net/n.jar",
                            },
                        },
                    },
                },
            ],
        };

        var version = new ResolvedVersion { Detail = detail, Chain = [] };
        var files = builder.BuildLibraries(version, Windows, @"C:\inst\natives").ToList();

        // 一个普通库 + 一个 natives 分类符
        Assert.Equal(2, files.Count);
        var native = Assert.Single(files, f => f.UpdateTask.Any());
        Assert.EndsWith("lwjgl-3.2.1-natives-windows.jar", native.Path);
    }

    [Fact]
    public void OldStyleNativesResolveArchPlaceholder()
    {
        var library = new Library
        {
            Name = "org.lwjgl:lwjgl:3.2.1",
            Natives = new Dictionary<string, string> { ["windows"] = "natives-windows-${arch}" },
        };

        Assert.Equal("natives-windows-64", GameFileBuilder.GetNativeClassifier(library, Windows));

        var x86 = new RuleContext { OsName = "windows", Arch = "x86" };
        Assert.Equal("natives-windows-32", GameFileBuilder.GetNativeClassifier(library, x86));
    }

    [Fact]
    public void MissingNativesMapReturnsNull()
    {
        var library = new Library { Name = "a:b:1.0" };
        Assert.Null(GameFileBuilder.GetNativeClassifier(library, Windows));
    }

    [Fact]
    public void AssetFilesUseHashedLayout()
    {
        var paths = NewPaths();
        var builder = new GameFileBuilder(paths);

        var index = new AssetIndex
        {
            Objects = new Dictionary<string, AssetObject>
            {
                ["minecraft/sounds/a.ogg"] = new() { Hash = "abcdef0123456789", Size = 100 },
            },
        };

        var file = Assert.Single(builder.BuildAssets(index));

        Assert.Equal("abcdef0123456789", file.Hash);
        Assert.Equal(100, file.Size);
        Assert.EndsWith(Path.Combine("objects", "ab", "abcdef0123456789"), file.Path);
        Assert.Equal(
            "https://resources.download.minecraft.net/ab/abcdef0123456789",
            file.Url);
    }
}

public class NativeExtractTaskTests
{
    [Fact]
    public async Task ExtractsEntriesAndSkipsMetaInf()
    {
        var root = Path.Combine(Path.GetTempPath(), "solar-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var jarPath = Path.Combine(root, "natives.jar");
        var nativesDir = Path.Combine(root, "natives");

        using (var zip = ZipFile.Open(jarPath, ZipArchiveMode.Create))
        {
            Write(zip, "lwjgl.dll", "binary");
            Write(zip, "META-INF/MANIFEST.MF", "manifest");
        }

        var task = new NativeExtractTask(nativesDir, null, new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
        var file = new GameFile("natives") { Path = jarPath };

        await task.Execute(file, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(nativesDir, "lwjgl.dll")));
        Assert.False(File.Exists(Path.Combine(nativesDir, "MANIFEST.MF")));

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task HonorsExtractExcludes()
    {
        var root = Path.Combine(Path.GetTempPath(), "solar-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var jarPath = Path.Combine(root, "natives.jar");
        var nativesDir = Path.Combine(root, "natives");

        using (var zip = ZipFile.Open(jarPath, ZipArchiveMode.Create))
        {
            Write(zip, "keep.dll", "a");
            Write(zip, "drop.dll", "b");
        }

        var task = new NativeExtractTask(nativesDir, ["drop.dll"], new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
        await task.Execute(new GameFile("natives") { Path = jarPath }, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(nativesDir, "keep.dll")));
        Assert.False(File.Exists(Path.Combine(nativesDir, "drop.dll")));

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task ConcurrentExtractionOfSameFileDoesNotThrow()
    {
        // 真实场景：lwjgl-glfw 与 lwjgl 的 natives jar 都含 glfw.dll，
        // 下载器会并行执行这两个解压任务。曾因此抛 IOException。
        var root = Path.Combine(Path.GetTempPath(), "solar-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var nativesDir = Path.Combine(root, "natives");

        var jars = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            var jarPath = Path.Combine(root, $"natives{i}.jar");
            using (var zip = ZipFile.Open(jarPath, ZipArchiveMode.Create))
            {
                Write(zip, "glfw.dll", "same-content");
            }

            jars.Add(jarPath);
        }

        var task = new NativeExtractTask(nativesDir, null, new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));

        await Task.WhenAll(jars.Select(jar =>
            Task.Run(() => task.Execute(new GameFile("natives") { Path = jar }, CancellationToken.None).AsTask())));

        Assert.True(File.Exists(Path.Combine(nativesDir, "glfw.dll")));

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task MissingFileIsNotAnError()
    {
        var task = new NativeExtractTask(Path.GetTempPath(), null, new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
        await task.Execute(new GameFile("natives") { Path = @"C:\does\not\exist.jar" }, CancellationToken.None);
    }

    private static void Write(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    private sealed class TestSettings : ISettingsService
    {
        public LauncherSettings Current { get; } = new();

        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}



internal sealed class TestSettings : ISettingsService
{
    public LauncherSettings Current { get; } = new();

    public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
}
