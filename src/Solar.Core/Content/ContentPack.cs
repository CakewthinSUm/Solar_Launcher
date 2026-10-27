namespace Solar.Core.Content;

/// <summary>「管理」下三个内容 tab 对应的游戏目录子文件夹。</summary>
public enum ContentKind
{
    Mods,
    ResourcePacks,
    ShaderPacks,
}

public static class ContentFolders
{
    /// <summary>该类型内容在 .minecraft 下的文件夹名。</summary>
    public static string GetFolderName(ContentKind kind) => kind switch
    {
        ContentKind.Mods => "mods",
        ContentKind.ResourcePacks => "resourcepacks",
        ContentKind.ShaderPacks => "shaderpacks",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

/// <summary>
/// 游戏目录里的一个内容条目（模组 / 资源包 / 光影包）。
/// 资源包与光影包既可以是 zip，也可以是解压后的文件夹，所以两者都要能列出来。
/// </summary>
public sealed record ContentPack(
    string Name,
    string FullPath,
    bool IsDirectory,
    long SizeBytes,
    DateTimeOffset ModifiedAt)
{
    /// <summary>文件夹不统计体积（资源包展开后文件数很多，每次列表都遍历代价太大）。</summary>
    public string SizeText => IsDirectory ? "文件夹" : FormatSize(SizeBytes);

    public string MetaText => $"{SizeText} · {ModifiedAt.LocalDateTime:yyyy-MM-dd HH:mm}";

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };
}
