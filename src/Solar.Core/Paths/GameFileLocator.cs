namespace Solar.Core.Paths;

/// <summary>
/// 版本与库文件的查找位置。
/// <para>
/// 与 PCL 一致，只有一份 MC 文件夹：<c>versions/</c>、<c>libraries/</c>、<c>assets/</c>
/// 都在它下面，接管官方启动器目录时直接指向那份 <c>.minecraft</c> 即可。
/// </para>
/// </summary>
public sealed class GameFileLocator(MinecraftPaths paths)
{
    /// <summary>库目录，也是新下载的落点。</summary>
    public string PrimaryLibraries => paths.Libraries;

    /// <summary>找已存在的版本 JSON；没有时返回 null。</summary>
    public string? FindVersionJson(string versionId)
    {
        var candidate = paths.GetVersionJsonPath(versionId);
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>找已存在的版本 jar（加载器版本通常没有自己的 jar）；没有时返回 null。</summary>
    public string? FindVersionJar(string versionId)
    {
        var candidate = paths.GetVersionJarPath(versionId);
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>
    /// 解析一个库的实际路径：存在时用它，否则返回库目录下的路径（供下载器把文件补上）。
    /// </summary>
    public string ResolveLibrary(string relativePath)
    {
        var candidate = Path.Combine(PrimaryLibraries, relativePath);
        return candidate;
    }
}
