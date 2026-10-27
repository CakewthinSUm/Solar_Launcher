using Solar.Core.Content;
using Solar.Core.Instances;
using Solar.Core.Paths;
using Solar.Core.Settings;
using Xunit;

namespace Solar.Core.Tests;

/// <summary>Mods / 资源包 / 光影 的列表、导入与删除。</summary>
public sealed class ContentManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "solar-test-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly AppPaths _paths;
    private readonly ContentManager _content;
    private readonly GameInstance _instance = new() { Id = "inst" };

    public ContentManagerTests()
    {
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();
        _content = new ContentManager(new InstanceGameDirectory(_paths, new FakeSettings()));
    }

    [Fact]
    public void MissingDirectoryListsNothing()
    {
        Assert.Empty(_content.List(_instance, ContentKind.Mods));
    }

    [Fact]
    public void ListReturnsJarFilesForMods()
    {
        var mods = _content.GetContentDirectory(_instance, ContentKind.Mods);
        WriteFile(Path.Combine(mods, "sodium.jar"), "jar");
        WriteFile(Path.Combine(mods, "readme.txt"), "text");
        WriteFile(Path.Combine(mods, "config", "a.toml"), "toml");

        var packs = _content.List(_instance, ContentKind.Mods);

        // 只认 jar；说明文件和子目录都不显示，避免列表被杂物塞满
        Assert.Equal(["sodium.jar"], packs.Select(p => p.Name));
    }

    [Fact]
    public void ListAcceptsZipFilesAndFoldersForResourcePacks()
    {
        var packsDir = _content.GetContentDirectory(_instance, ContentKind.ResourcePacks);
        WriteFile(Path.Combine(packsDir, "faithful.zip"), "zip");
        WriteFile(Path.Combine(packsDir, "unzipped", "pack.mcmeta"), "meta");
        WriteFile(Path.Combine(packsDir, "notes.txt"), "text");

        var packs = _content.List(_instance, ContentKind.ResourcePacks);

        Assert.Equal(["faithful.zip", "unzipped"], packs.Select(p => p.Name).OrderBy(n => n));
        Assert.Contains(packs, p => p.Name == "unzipped" && p.IsDirectory);
    }

    [Fact]
    public void FoldersUseSeparateMetaText()
    {
        var dir = _content.GetContentDirectory(_instance, ContentKind.ShaderPacks);
        WriteFile(Path.Combine(dir, "bsl", "shaders", "block.properties"), "x");

        var pack = Assert.Single(_content.List(_instance, ContentKind.ShaderPacks));

        Assert.Equal("文件夹", pack.SizeText);
        Assert.StartsWith("文件夹 · ", pack.MetaText);
    }

    [Fact]
    public async Task ImportCopiesFileWithoutRemovingSource()
    {
        var source = Path.Combine(_root, "download", "sodium.jar");
        WriteFile(source, "jar");

        await _content.ImportAsync(_instance, ContentKind.Mods, source, overwrite: false);

        Assert.Equal("jar", ReadFile(Path.Combine(_content.GetContentDirectory(_instance, ContentKind.Mods), "sodium.jar")));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task ImportWithoutOverwriteRejectsExistingName()
    {
        var target = Path.Combine(_content.GetContentDirectory(_instance, ContentKind.Mods), "a.jar");
        WriteFile(target, "old");
        var source = Path.Combine(_root, "download", "a.jar");
        WriteFile(source, "new");

        await Assert.ThrowsAsync<IOException>(
            () => _content.ImportAsync(_instance, ContentKind.Mods, source, overwrite: false));

        Assert.Equal("old", ReadFile(target));
    }

    [Fact]
    public async Task ImportWithOverwriteReplacesExistingFile()
    {
        var target = Path.Combine(_content.GetContentDirectory(_instance, ContentKind.Mods), "a.jar");
        WriteFile(target, "old");
        var source = Path.Combine(_root, "download", "a.jar");
        WriteFile(source, "new");

        await _content.ImportAsync(_instance, ContentKind.Mods, source, overwrite: true);

        Assert.Equal("new", ReadFile(target));
    }

    [Fact]
    public async Task ImportFolderCopiesWholeTree()
    {
        var source = Path.Combine(_root, "download", "bsl");
        WriteFile(Path.Combine(source, "shaders", "composite.fsh"), "shader");

        await _content.ImportAsync(_instance, ContentKind.ShaderPacks, source, overwrite: false);

        var copied = Path.Combine(
            _content.GetContentDirectory(_instance, ContentKind.ShaderPacks),
            "bsl",
            "shaders",
            "composite.fsh");
        Assert.Equal("shader", ReadFile(copied));
    }

    [Fact]
    public async Task ImportingAFolderIntoModsIsRejectedUpFront()
    {
        var source = Path.Combine(_root, "download", "old-mod");
        WriteFile(Path.Combine(source, "a.jar"), "jar");

        Assert.False(_content.CanImport(ContentKind.Mods, source, out var error));
        Assert.Contains("jar", error);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _content.ImportAsync(_instance, ContentKind.Mods, source, overwrite: false));
    }

    [Fact]
    public void UnsupportedExtensionIsRejected()
    {
        var source = Path.Combine(_root, "download", "notes.txt");
        WriteFile(source, "text");

        Assert.False(_content.CanImport(ContentKind.ResourcePacks, source, out var error));
        Assert.Contains("notes.txt", error);
    }

    [Fact]
    public void DeleteRemovesFile()
    {
        var target = Path.Combine(_content.GetContentDirectory(_instance, ContentKind.Mods), "a.jar");
        WriteFile(target, "jar");
        var pack = Assert.Single(_content.List(_instance, ContentKind.Mods));

        _content.Delete(_instance, ContentKind.Mods, pack);

        Assert.False(File.Exists(target));
    }

    [Fact]
    public void DeleteRefusesPathOutsideContentDirectory()
    {
        var outside = Path.Combine(_root, "important.txt");
        WriteFile(outside, "keep");

        var pack = new ContentPack("important.txt", outside, false, 4, DateTimeOffset.Now);

        Assert.Throws<InvalidOperationException>(
            () => _content.Delete(_instance, ContentKind.Mods, pack));
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public void IsolatedInstanceUsesItsOwnContentDirectory()
    {
        _instance.VersionIsolation = true;

        var directory = _content.GetContentDirectory(_instance, ContentKind.Mods);

        Assert.Equal(
            Path.Combine(_paths.GetInstanceMinecraftDirectory("inst"), "mods"),
            directory);
    }

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string ReadFile(string path) => File.ReadAllText(path);

    private sealed class FakeSettings : ISettingsService
    {
        public LauncherSettings Current { get; } = new();

        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
