using System.Collections.Concurrent;
using System.IO.Compression;
using CmlLib.Core.Files;
using CmlLib.Core.Tasks;
using Solar.Core.Minecraft;
using Solar.Core.Models;
using Solar.Core.Paths;

namespace Solar.Core.Download;

/// <summary>
/// 把已解析的版本转换成下载引擎认识的 <see cref="GameFile"/> 列表。
/// 这里只负责"要下什么、下到哪"，并发与校验交给 CmlLib 的安装器。
/// </summary>
public sealed class GameFileBuilder(MinecraftPaths paths, GameFileLocator? locator = null)
{
    private const string ResourcesHost = "https://resources.download.minecraft.net";

    /// <summary>
    /// 本次启动会解压出来的 natives 文件名。
    /// 下载完成后用它清掉 natives 目录里不属于当前版本的旧文件。
    /// <para>
    /// 必须用并发容器：解压任务在下载器的多个线程上并行执行，
    /// 普通 HashSet 并发写入会丢条目，清理时就会把还在用的 dll 当垃圾删掉。
    /// </para>
    /// </summary>
    public ConcurrentDictionary<string, byte> ExtractedNatives { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 游戏本体 jar，放在 <c>versions/&lt;id&gt;/&lt;id&gt;.jar</c>。
    /// <para>
    /// 接管过来的游戏目录里已经有这个 jar 时，直接把落点指向那份：
    /// 下载引擎会按哈希判定"已就绪"并跳过，不浪费一次复制。
    /// </para>
    /// </summary>
    public GameFile? BuildClientJar(ResolvedVersion version)
    {
        if (version.Detail.Downloads is not { } downloads
            || !downloads.TryGetValue("client", out var client)
            || string.IsNullOrEmpty(client.Url))
        {
            return null;
        }

        var target = locator?.FindVersionJar(version.Id)
                     ?? paths.GetVersionJarPath(version.Id);

        return new GameFile("client.jar")
        {
            Path = target,
            Url = client.Url,
            Hash = client.Sha1,
            Size = client.Size,
        };
    }

    /// <summary>log4j 配置；没有该字段的版本返回 null。</summary>
    public GameFile? BuildLogConfig(ResolvedVersion version)
    {
        if (version.Detail.Logging?.Client?.File is not { } file || string.IsNullOrEmpty(file.Url))
        {
            return null;
        }

        var name = file.Id ?? file.Path ?? "client-log4j.xml";
        return new GameFile(name)
        {
            Path = Path.Combine(paths.Assets, "log_configs", name),
            Url = file.Url,
            Hash = file.Sha1,
            Size = file.Size,
        };
    }

    /// <summary>
    /// 构建库文件下载项。natives 会额外生成"下载后解压"的任务。
    /// </summary>
    /// <param name="nativesDirectory">解压目标：版本的 <c>natives</c> 目录。</param>
    public IEnumerable<GameFile> BuildLibraries(
        ResolvedVersion version,
        RuleContext context,
        string nativesDirectory)
    {
        foreach (var library in version.GetEffectiveLibraries(context))
        {
            var coordinate = library.ParseName();

            // 新格式（1.19+）：natives 的分类符直接写在 name 里，
            // 它的 artifact 就是需要解压的原生库，不应该进 classpath。
            var isNewStyleNative =
                coordinate.Classifier?.StartsWith("natives", StringComparison.OrdinalIgnoreCase) == true;

            // 同时支持官方格式（downloads.artifact）与 Fabric/Quilt 的
            // 仓库根地址格式（url + maven 坐标推导路径）
            if (library.ResolveDownload() is { } download)
            {
                var (relativePath, url) = download;

                var target = locator?.ResolveLibrary(relativePath)
                             ?? Path.Combine(paths.Libraries, relativePath);

                yield return new GameFile(library.Name)
                {
                    Path = target,
                    Url = url,
                    Hash = library.Downloads?.Artifact?.Sha1 ?? library.Sha1,
                    Size = library.Downloads?.Artifact?.Size ?? library.Size,
                    UpdateTask = isNewStyleNative
                        ? [new NativeExtractTask(nativesDirectory, library.Extract?.Exclude, ExtractedNatives)]
                        : [],
                };
            }

            // 老格式（1.18 及以前）：natives 是 {os: classifier} 映射，
            // 真正的原生库在 downloads.classifiers 里，单独的 jar 需要解压。
            if (library.Natives is { Count: > 0 } && library.Downloads?.Classifiers is { } classifiers)
            {
                var classifierKey = GetNativeClassifier(library, context);
                if (classifierKey is not null
                    && classifiers.TryGetValue(classifierKey, out var nativeArtifact)
                    && !string.IsNullOrEmpty(nativeArtifact.Path))
                {
                    yield return new GameFile(library.Name + ":" + classifierKey)
                    {
                        Path = locator?.ResolveLibrary(nativeArtifact.Path)
                               ?? Path.Combine(paths.Libraries, nativeArtifact.Path),
                        Url = nativeArtifact.Url,
                        Hash = nativeArtifact.Sha1,
                        Size = nativeArtifact.Size,
                        UpdateTask = [new NativeExtractTask(nativesDirectory, library.Extract?.Exclude, ExtractedNatives)],
                    };
                }
            }
        }
    }

    /// <summary>把 assets 索引里的每个对象变成下载项。</summary>
    public IEnumerable<GameFile> BuildAssets(AssetIndex index)
    {
        var objectsRoot = Path.Combine(paths.Assets, "objects");

        foreach (var (name, obj) in index.Objects)
        {
            if (obj.Hash.Length < 2)
            {
                continue;
            }

            var prefix = obj.Hash[..2];
            var target = Path.Combine(objectsRoot, prefix, obj.Hash);

            // 资源不跟着版本走：游戏的 --assetsDir 只有一个目录，
            // 这里必须落在 Solar 自己的 assets 下，否则游戏会去空目录里找资源。
            yield return new GameFile(name)
            {
                Path = target,
                Url = $"{ResourcesHost}/{prefix}/{obj.Hash}",
                Hash = obj.Hash,
                Size = obj.Size,
            };
        }
    }

    /// <summary>解析老格式的 natives 分类符，例如 <c>natives-windows-${arch}</c>。</summary>
    public static string? GetNativeClassifier(Library library, RuleContext context)
    {
        if (library.Natives is not { Count: > 0 } natives)
        {
            return null;
        }

        var osName = context.OsName;
        if (!natives.TryGetValue(osName, out var classifier) || string.IsNullOrEmpty(classifier))
        {
            // 老版本用 "osx" 表示 macOS，个别版本写 "macos"，两种都试。
            var alias = osName switch
            {
                "osx" => "macos",
                "macos" => "osx",
                _ => null,
            };

            if (alias is null || !natives.TryGetValue(alias, out classifier) || string.IsNullOrEmpty(classifier))
            {
                return null;
            }
        }

        return classifier.Replace("${arch}", context.Arch == "x86" ? "32" : "64");
    }
}

/// <summary>
/// 下载完成后把原生库解压到版本的 <c>natives</c> 目录。
/// 由下载引擎在文件就绪后调用。
/// </summary>
public sealed class NativeExtractTask(
    string nativesDirectory,
    IReadOnlyList<string>? exclude,
    ConcurrentDictionary<string, byte> extracted) : IUpdateTask
{
    /// <summary>
    /// META-INF 是签名信息，对运行无用；老版本 JSON 的 extract.exclude 通常只列它，
    /// 但个别版本会漏写，所以始终排除。
    /// </summary>
    private static readonly string[] AlwaysExcluded = ["META-INF/"];

    public ValueTask Execute(GameFile file, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(file.Path) || !File.Exists(file.Path))
        {
            return ValueTask.CompletedTask;
        }

        Directory.CreateDirectory(nativesDirectory);

        var excluded = AlwaysExcluded
            .Concat(exclude ?? [])
            .ToArray();

        try
        {
            using var archive = ZipFile.OpenRead(file.Path);
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // 与 PCL 一致：只解压 dll。Windows 上真正要加载的原生库就是它们，
                // 其余（签名、说明文件）解压出来只会拖慢启动。
                if (string.IsNullOrEmpty(entry.Name)
                    || !entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (excluded.Any(prefix => entry.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var destination = Path.Combine(nativesDirectory, entry.Name);

                // 多个 natives jar 可能包含同名文件（glfw.dll 就同时出现在
                // lwjgl-glfw 和 lwjgl 里），而解压任务在下载器里是并行执行的。
                // 按目标路径加锁，避免两个线程同时写同一个文件。
                lock (GetFileLock(destination))
                {
                    extracted.TryAdd(entry.Name, 0);

                    // 已经是同一份文件就不再解压
                    var info = new FileInfo(destination);
                    if (info.Exists && info.Length == entry.Length)
                    {
                        continue;
                    }

                    if (!ExtractSafely(entry, destination))
                    {
                        return ValueTask.CompletedTask;
                    }
                }
            }
        }
        catch (InvalidDataException)
        {
            // 压缩包损坏：删掉它，下次启动会重新下载
            TryDelete(file.Path);
        }

        return ValueTask.CompletedTask;
    }

    private static bool ExtractSafely(ZipArchiveEntry entry, string destination)
    {
        try
        {
            entry.ExtractToFile(destination, overwrite: true);
            return true;
        }
        catch (IOException) when (File.Exists(destination))
        {
            // 另一个任务刚好写完同一个文件，内容一致，忽略即可
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // 文件被占用（通常是有游戏正在运行）：跳过解压，不影响本次启动
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删不掉就留着，下次启动还会再试
        }
    }

    /// <summary>按目标路径取一个进程内锁对象，保证同一文件不会被并发写入。</summary>
    private static object GetFileLock(string destination) =>
        Locks.GetOrAdd(destination, _ => new object());

    private static readonly ConcurrentDictionary<string, object> Locks =
        new(StringComparer.OrdinalIgnoreCase);
}
