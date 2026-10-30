using Solar.Core.Instances;

namespace Solar.Core.Paths;

/// <summary>
/// 版本与库文件的查找位置。
/// <para>
/// Solar 自己下载的东西在 <c>SolarShared/</c> 下；接管官方启动器目录时，
/// 那份 <c>.minecraft</c> 里还有一整套 <c>versions</c> 与 <c>libraries</c>。
/// 两处都查，已有版本才能直接启动而不必重新下载。
/// </para>
/// <para>
/// 这不只是省流量：Forge/NeoForge 的安装器会在本地生成网络上下载不到的文件
/// （如 <c>forge-&lt;版本&gt;-client.jar</c>），只认 SolarShared 的话这些版本根本起不来。
/// </para>
/// </summary>
public sealed class GameFileLocator(AppPaths paths, InstanceGameDirectory gameDirectories)
{
    /// <summary>Solar 自己的版本目录，也是新下载的落点。</summary>
    public string PrimaryVersions => paths.Versions;

    /// <summary>Solar 自己的库目录，也是新下载的落点。</summary>
    public string PrimaryLibraries => paths.Libraries;

    /// <summary>游戏目录里的版本目录，即官方启动器那份。</summary>
    public string ExternalVersions => Path.Combine(gameDirectories.Shared, "versions");

    public string ExternalLibraries => Path.Combine(gameDirectories.Shared, "libraries");

    /// <summary>游戏目录里的资源目录，即官方启动器 / 其它启动器那份。</summary>
    public string ExternalAssets => Path.Combine(gameDirectories.Shared, "assets");

    /// <summary>版本目录的搜索顺序：先自己的，再游戏目录里的。</summary>
    public IEnumerable<string> VersionRoots => VersionFolders.Roots(paths, gameDirectories.Shared);

    public IEnumerable<string> LibraryRoots => VersionFolders.Normalize([PrimaryLibraries, ExternalLibraries]);

    /// <summary>找已存在的版本 JSON；都没有时返回 null。</summary>
    public string? FindVersionJson(string versionId)
    {
        foreach (var root in VersionRoots)
        {
            var candidate = Path.Combine(root, versionId, versionId + ".json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>找已存在的版本 jar（加载器版本通常没有自己的 jar）；都没有时返回 null。</summary>
    public string? FindVersionJar(string versionId)
    {
        foreach (var root in VersionRoots)
        {
            var candidate = Path.Combine(root, versionId, versionId + ".jar");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// 解析一个库的实际路径：哪个根目录里有就用哪个，都没有时返回自己的库目录下的路径
    /// （供下载器把文件补上）。
    /// </summary>
    public string ResolveLibrary(string relativePath)
    {
        foreach (var root in LibraryRoots)
        {
            var candidate = Path.Combine(root, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(PrimaryLibraries, relativePath);
    }
}
