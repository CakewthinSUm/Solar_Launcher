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
public sealed class GameFileBuilder(AppPaths paths)
{
    private const string ResourcesHost = "https://resources.download.minecraft.net";

    /// <summary>游戏本体 jar，放在 <c>versions/&lt;id&gt;/&lt;id&gt;.jar</c>。</summary>
    public GameFile? BuildClientJar(ResolvedVersion version)
    {
        if (version.Detail.Downloads is not { } downloads
            || !downloads.TryGetValue("client", out var client)
            || string.IsNullOrEmpty(client.Url))
        {
            return null;
        }

        return new GameFile("client.jar")
        {
            Path = Path.Combine(paths.Versions, version.Id, version.Id + ".jar"),
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
    /// <param name="nativesDirectory">解压目标：实例的 <c>natives</c> 目录。</param>
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

            if (library.Downloads?.Artifact is { } artifact && !string.IsNullOrEmpty(artifact.Path))
            {
                yield return new GameFile(library.Name)
                {
                    Path = Path.Combine(paths.Libraries, artifact.Path),
                    Url = artifact.Url,
                    Hash = artifact.Sha1,
                    Size = artifact.Size,
                    UpdateTask = isNewStyleNative
                        ? [new NativeExtractTask(nativesDirectory, library.Extract?.Exclude)]
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
                        Path = Path.Combine(paths.Libraries, nativeArtifact.Path),
                        Url = nativeArtifact.Url,
                        Hash = nativeArtifact.Sha1,
                        Size = nativeArtifact.Size,
                        UpdateTask = [new NativeExtractTask(nativesDirectory, library.Extract?.Exclude)],
                    };
                }
            }
        }
    }

    /// <summary>把 assets 索引里的每个对象变成下载项。</summary>
    public IEnumerable<GameFile> BuildAssets(AssetIndex index)
    {
        foreach (var (name, obj) in index.Objects)
        {
            if (obj.Hash.Length < 2)
            {
                continue;
            }

            var prefix = obj.Hash[..2];
            yield return new GameFile(name)
            {
                Path = Path.Combine(paths.Assets, "objects", prefix, obj.Hash),
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
/// 下载完成后把原生库解压到实例的 <c>natives</c> 目录。
/// 由下载引擎在文件就绪后调用。
/// </summary>
public sealed class NativeExtractTask(string nativesDirectory, IReadOnlyList<string>? exclude) : IUpdateTask
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

        using var archive = ZipFile.OpenRead(file.Path);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 目录项没有内容，跳过
            if (string.IsNullOrEmpty(entry.Name))
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
                ExtractSafely(entry, destination);
            }
        }

        return ValueTask.CompletedTask;
    }

    private static void ExtractSafely(ZipArchiveEntry entry, string destination)
    {
        try
        {
            entry.ExtractToFile(destination, overwrite: true);
        }
        catch (IOException) when (File.Exists(destination))
        {
            // 另一个任务刚好写完同一个文件，内容一致，忽略即可
        }
    }

    /// <summary>按目标路径取一个进程内锁对象，保证同一文件不会被并发写入。</summary>
    private static object GetFileLock(string destination) =>
        Locks.GetOrAdd(destination, _ => new object());

    private static readonly ConcurrentDictionary<string, object> Locks =
        new(StringComparer.OrdinalIgnoreCase);
}
