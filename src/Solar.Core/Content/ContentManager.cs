using Solar.Core.Instances;

namespace Solar.Core.Content;

public interface IContentManager
{
    /// <summary>列出实例游戏目录里某一类内容的全部条目。</summary>
    IReadOnlyList<ContentPack> List(GameInstance instance, ContentKind kind);

    /// <summary>内容文件夹的实际路径；目录不存在时也会返回路径，调用方自行决定是否创建。</summary>
    string GetContentDirectory(GameInstance instance, ContentKind kind);

    /// <summary>
    /// 把文件或文件夹放进内容目录。同名条目已存在时直接报错，
    /// 由调用方先问过用户是否覆盖，再走 <paramref name="overwrite"/> 重试。
    /// </summary>
    Task ImportAsync(GameInstance instance, ContentKind kind, string sourcePath, bool overwrite, CancellationToken ct = default);

    /// <summary>删除条目。文件夹整体删除。</summary>
    void Delete(GameInstance instance, ContentKind kind, ContentPack pack);

    /// <summary>在资源管理器里定位该条目。</summary>
    void RevealInExplorer(ContentPack pack);

    /// <summary>
    /// 检查来源能否放进该类内容目录；不能时给出可直接展示给用户的原因。
    /// 页面在弹“是否覆盖”之前先问一次，避免为一个注定失败的导入询问用户。
    /// </summary>
    bool CanImport(ContentKind kind, string sourcePath, out string error);
}

/// <summary>
/// 内容管理：直接读写实例游戏目录下的 mods / resourcepacks / shaderpacks。
/// 不维护额外索引，文件系统就是唯一事实来源，用户手动往里放东西也能看到。
/// </summary>
public sealed class ContentManager(InstanceGameDirectory gameDirectories) : IContentManager
{
    public string GetContentDirectory(GameInstance instance, ContentKind kind) =>
        Path.Combine(gameDirectories.Resolve(instance), ContentFolders.GetFolderName(kind));

    public IReadOnlyList<ContentPack> List(GameInstance instance, ContentKind kind)
    {
        var directory = GetContentDirectory(instance, kind);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var result = new List<ContentPack>();

        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            var name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(name) || IsHidden(name))
            {
                continue;
            }

            // 资源包与光影包可以是解压后的文件夹，mods 则只认文件（加载器不会读子目录）
            var isDirectory = Directory.Exists(path);
            if (isDirectory ? kind == ContentKind.Mods : !IsPackFile(kind, path))
            {
                continue;
            }

            result.Add(new ContentPack(name, path, isDirectory, GetSize(path, isDirectory), GetModified(path)));
        }

        return result
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task ImportAsync(
        GameInstance instance,
        ContentKind kind,
        string sourcePath,
        bool overwrite,
        CancellationToken ct = default)
    {
        // 选错类型时当场报错，而不是复制进去后从列表里“消失”，让用户以为导入成功了
        if (!CanImport(kind, sourcePath, out var error))
        {
            throw new InvalidOperationException(error);
        }

        var isSourceDirectory = Directory.Exists(sourcePath);
        var directory = GetContentDirectory(instance, kind);
        Directory.CreateDirectory(directory);

        var target = Path.Combine(directory, Path.GetFileName(sourcePath));

        // 同名条目可能是文件也可能是文件夹（资源包两种形式都合法），
        // 类型不同时 File.Copy / Directory.Delete 都会失败，所以先按目标类型清掉。
        var targetIsDirectory = Directory.Exists(target);
        var targetIsFile = File.Exists(target);

        if (targetIsDirectory || targetIsFile)
        {
            if (!overwrite)
            {
                throw new IOException($"已存在同名条目：{Path.GetFileName(target)}");
            }

            if (targetIsDirectory)
            {
                Directory.Delete(target, recursive: true);
            }
            else
            {
                File.Delete(target);
            }
        }

        // 复制而非移动：来源可能是用户自己的下载目录，搬走会让原文件消失
        if (isSourceDirectory)
        {
            await Task.Run(() => CopyDirectory(sourcePath, target, ct), ct);
        }
        else
        {
            await Task.Run(() => File.Copy(sourcePath, target, overwrite: false), ct);
        }
    }

    public bool CanImport(ContentKind kind, string sourcePath, out string error)
    {
        error = "";

        if (Directory.Exists(sourcePath))
        {
            // mods 目录只放 jar，加载器不会读子目录
            if (kind == ContentKind.Mods)
            {
                error = "Mods 只接受 .jar 文件。请打开文件夹，把里面的 jar 逐个导入。";
                return false;
            }

            return true;
        }

        if (!File.Exists(sourcePath))
        {
            error = "找不到该文件。";
            return false;
        }

        if (!IsPackFile(kind, sourcePath))
        {
            error = $"“{Path.GetFileName(sourcePath)}”不是 {ContentFolders.GetFolderName(kind)} 能识别的类型。";
            return false;
        }

        return true;
    }

    public void Delete(GameInstance instance, ContentKind kind, ContentPack pack)
    {
        // 只删内容目录里的条目：pack 来自列表，但仍做一次包含性检查，
        // 避免误传外部路径把别处的文件删掉。
        var directory = GetContentDirectory(instance, kind);
        if (!IsInside(directory, pack.FullPath))
        {
            throw new InvalidOperationException($"{pack.FullPath} 不在 {directory} 内，已拒绝删除。");
        }

        if (pack.IsDirectory)
        {
            Directory.Delete(pack.FullPath, recursive: true);
        }
        else
        {
            File.Delete(pack.FullPath);
        }
    }

    public void RevealInExplorer(ContentPack pack)
    {
        if (!File.Exists(pack.FullPath) && !Directory.Exists(pack.FullPath))
        {
            return;
        }

        // /select, 让资源管理器打开父目录并选中该项
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{pack.FullPath}\"",
            UseShellExecute = true,
        });
    }

    /// <summary>
    /// 各类型认的文件后缀。mods 里放的是 jar；资源包与光影包是 zip。
    /// 允许列表之外的文件（例如 Forge 的 .txt 说明）不显示，避免列表被杂物塞满。
    /// </summary>
    private static bool IsPackFile(ContentKind kind, string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return kind switch
        {
            ContentKind.Mods => extension is ".jar" or ".zip" or ".disabled",
            _ => extension is ".zip" or ".disabled",
        };
    }

    private static bool IsHidden(string name) => name.StartsWith('.') || name.StartsWith('_');

    private static long GetSize(string path, bool isDirectory)
    {
        if (isDirectory)
        {
            return 0;
        }

        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static DateTimeOffset GetModified(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (IOException)
        {
            return DateTimeOffset.MinValue;
        }
    }

    private static bool IsInside(string directory, string path)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static void CopyDirectory(string source, string target, CancellationToken ct)
    {
        Directory.CreateDirectory(target);

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }
}
