namespace Solar.Core.Paths;

/// <summary>
/// 版本文件夹的定位规则。
/// <para>
/// Solar 自己下载的版本在 <c>SolarShared/versions</c>；接管官方启动器目录时，
/// 那份 <c>.minecraft</c> 里还有一整套 <c>versions</c>。两处都查，顺序固定：
/// 先自己的，再共享游戏目录里的。
/// </para>
/// <para>
/// 版本隔离把「版本文件夹」直接当作游戏目录（对齐 PCL），所以这组规则同时被
/// <see cref="GameFileLocator"/>（找版本 JSON / jar）和实例目录解析使用，
/// 不能各写一份。
/// </para>
/// </summary>
internal static class VersionFolders
{
    /// <summary>版本目录的候选根目录。</summary>
    public static IEnumerable<string> Roots(AppPaths paths, string sharedGameDirectory) =>
        Normalize([paths.Versions, Path.Combine(sharedGameDirectory, "versions")]);

    /// <summary>
    /// 找到某个版本的版本文件夹，即包含 <c>&lt;id&gt;/&lt;id&gt;.json</c> 的那个目录；
    /// 两处都没有时返回 <see langword="null"/>。
    /// </summary>
    public static string? Find(AppPaths paths, string sharedGameDirectory, string versionId)
    {
        foreach (var root in Roots(paths, sharedGameDirectory))
        {
            var directory = Path.Combine(root, versionId);
            if (File.Exists(Path.Combine(directory, versionId + ".json")))
            {
                return directory;
            }
        }

        return null;
    }

    /// <summary>两个根目录可能被配成同一个（例如游戏目录指到程序目录下），去重后再用。</summary>
    public static IEnumerable<string> Normalize(IEnumerable<string> roots) =>
        roots.Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase);
}
