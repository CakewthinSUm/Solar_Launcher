namespace Solar.Core.Versions;

/// <summary>复制共享游戏目录内容的结果。</summary>
public sealed record CopySummary(int CopiedFiles, int SkippedFiles);

/// <summary>
/// 开启版本隔离时把共享 MC 文件夹里属于玩家的内容复制到版本文件夹，
/// 给用户一个可用的起点。目标已存在的文件不覆盖（共享目录里那份可能更旧），源目录只读不动。
/// </summary>
public static class VersionContent
{
    /// <summary>开启隔离时值得带走的内容。其余（logs、crash-reports、缓存）留一份旧的没有意义。</summary>
    private static readonly string[] CopyableEntries =
    [
        "mods",
        "config",
        "defaultconfigs",
        "saves",
        "resourcepacks",
        "shaderpacks",
        "options.txt",
        "servers.dat",
    ];

    /// <summary>
    /// 复制可能涉及上千个文件，调用方应放到后台线程执行。
    /// </summary>
    public static CopySummary CopySharedContent(string source, string target, CancellationToken ct = default)
    {
        if (!Directory.Exists(source))
        {
            return new CopySummary(0, 0);
        }

        Directory.CreateDirectory(target);

        var copied = 0;
        var skipped = 0;

        foreach (var entry in CopyableEntries)
        {
            ct.ThrowIfCancellationRequested();

            var sourcePath = Path.Combine(source, entry);
            var targetPath = Path.Combine(target, entry);

            if (Directory.Exists(sourcePath))
            {
                CopyDirectory(sourcePath, targetPath, ct, ref copied, ref skipped);
            }
            else if (File.Exists(sourcePath))
            {
                CopyFile(sourcePath, targetPath, ref copied, ref skipped);
            }
        }

        return new CopySummary(copied, skipped);
    }

    private static void CopyDirectory(string source, string target, CancellationToken ct, ref int copied, ref int skipped)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(source, file);
            CopyFile(file, Path.Combine(target, relative), ref copied, ref skipped);
        }
    }

    private static void CopyFile(string source, string target, ref int copied, ref int skipped)
    {
        if (File.Exists(target))
        {
            skipped++;
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        try
        {
            // overwrite: false —— 与上面的存在性检查配合，避免并发下悄悄覆盖
            File.Copy(source, target, overwrite: false);
            copied++;
        }
        catch (IOException)
        {
            // 检查与复制之间被创建（或文件正被占用），按已存在处理
            skipped++;
        }
    }
}
