using Solar.Core.Versions;

namespace Solar.Core.Mods;

/// <summary>模组来源站点。两个源的数据形状完全不同，靠这层枚举在上层统一。</summary>
public enum ModSource
{
    Modrinth = 1,
    CurseForge = 2,
}

/// <summary>搜索排序方式。两个源各有一套排序参数，由各自的目录实现翻译。</summary>
public enum ModSort
{
    Relevance,
    Downloads,
    Updated,
    Newest,
}

/// <summary>发布渠道。CurseForge 用 1/2/3 表示，Modrinth 用字符串，这里统一。</summary>
public enum ModReleaseType
{
    Release,
    Beta,
    Alpha,
}

/// <summary>
/// 在线项目类型。Modrinth 与 CurseForge 都按类型分目录，
/// 搜索、分类与安装落点都要跟着变。
/// </summary>
public enum ModProjectType
{
    Mod = 0,
    Modpack,
    ResourcePack,
    Shader,
    DataPack,
}

/// <summary>类型在两个源上的标识，以及安装落点。</summary>
public static class ModProjectTypes
{
    /// <summary>Modrinth 的 <c>project_type</c> facet 值。</summary>
    public static string ToModrinth(this ModProjectType type) => type switch
    {
        ModProjectType.Modpack => "modpack",
        ModProjectType.ResourcePack => "resourcepack",
        ModProjectType.Shader => "shader",
        ModProjectType.DataPack => "datapack",
        _ => "mod",
    };

    /// <summary>CurseForge 的 Minecraft <c>classId</c>。</summary>
    public static int ToCurseForgeClassId(this ModProjectType type) => type switch
    {
        ModProjectType.Modpack => 4471,
        ModProjectType.ResourcePack => 12,
        ModProjectType.Shader => 6552,
        ModProjectType.DataPack => 6945,
        _ => 6,
    };

    public static string DisplayName(this ModProjectType type) => type switch
    {
        ModProjectType.Modpack => "整合包",
        ModProjectType.ResourcePack => "资源包",
        ModProjectType.Shader => "光影",
        ModProjectType.DataPack => "数据包",
        _ => "Mod",
    };

    /// <summary>
    /// 内容目录名。整合包没有固定目录——它自己就是一个版本，返回 null。
    /// </summary>
    public static string? ContentFolder(this ModProjectType type) => type switch
    {
        ModProjectType.Mod => "mods",
        ModProjectType.ResourcePack => "resourcepacks",
        ModProjectType.Shader => "shaderpacks",
        ModProjectType.DataPack => "datapacks",
        _ => null,
    };

    /// <summary>只有 Mod 需要按加载器筛选；其余类型传了加载器反而会搜不到东西。</summary>
    public static bool UsesLoader(this ModProjectType type) => type == ModProjectType.Mod;

    /// <summary>整合包需要新建版本，其余类型装进已有版本。</summary>
    public static bool CreatesVersion(this ModProjectType type) => type == ModProjectType.Modpack;

    /// <summary>从 Modrinth 的 <c>project_type</c> 还原；未知值当作 Mod。</summary>
    public static ModProjectType FromModrinth(string? value) => value?.ToLowerInvariant() switch
    {
        "modpack" => ModProjectType.Modpack,
        "resourcepack" => ModProjectType.ResourcePack,
        "shader" => ModProjectType.Shader,
        "datapack" => ModProjectType.DataPack,
        _ => ModProjectType.Mod,
    };

    /// <summary>从 CurseForge 的 <c>classId</c> 还原；未知值当作 Mod。</summary>
    public static ModProjectType FromCurseForgeClassId(long? classId) => classId switch
    {
        4471 => ModProjectType.Modpack,
        12 => ModProjectType.ResourcePack,
        6552 => ModProjectType.Shader,
        6945 => ModProjectType.DataPack,
        _ => ModProjectType.Mod,
    };
}

/// <summary>
/// 依赖关系。只保留四种语义，CurseForge 的 EmbeddedLibrary / Tool / Include
/// 归到 <see cref="Other"/>，因为它们不影响“要不要一起装”。
/// </summary>
public enum ModDependencyKind
{
    Required,
    Optional,
    Incompatible,
    Embedded,
    Other,
}

/// <summary>指向另一个源上的同一个项目。跨源去重后保留这一份信息，避免丢失可选项。</summary>
public sealed record ModProjectRef(ModSource Source, string Id);

/// <summary>两个源统一后的项目信息。</summary>
public sealed record ModProject
{
    public required ModSource Source { get; init; }

    /// <summary>项目类型。旧缓存与测试数据没有这个字段时按 Mod 处理。</summary>
    public ModProjectType ProjectType { get; init; } = ModProjectType.Mod;

    /// <summary>Modrinth 是 base62 字符串，CurseForge 是数字字符串。</summary>
    public required string Id { get; init; }

    public string? Slug { get; init; }
    public required string Title { get; init; }

    /// <summary>列表里显示的一行简介。</summary>
    public string Summary { get; init; } = "";

    /// <summary>详情正文（Markdown）。只有 Modrinth 有；CurseForge 的正文接口在镜像上不存在。</summary>
    public string? Body { get; init; }

    /// <summary>
    /// 优先使用原始 PNG。Modrinth 的 <c>icon_url</c> 是 WebP，而 WinUI 的 Image
    /// 走 WIC 解码，WebP 依赖系统扩展包（Win10 不一定有），能拿到 PNG 就不要用 WebP。
    /// </summary>
    public string? IconUrl { get; init; }

    public string? WebsiteUrl { get; init; }
    public string? Author { get; init; }
    public long Downloads { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }

    public IReadOnlyList<ModLoader> Loaders { get; init; } = [];
    public IReadOnlyList<string> GameVersions { get; init; } = [];
    public IReadOnlyList<string> Categories { get; init; } = [];
    public IReadOnlyList<string> Gallery { get; init; } = [];

    /// <summary>去重时被合并掉的另一源条目。</summary>
    public IReadOnlyList<ModProjectRef> AlsoOn { get; init; } = [];
}

/// <summary>一个可下载的文件。一个版本可能带多个文件，只取主文件。</summary>
public sealed record ModFile(string FileName, string Url, string? Sha1, long Size);

/// <summary>版本对另一个项目的依赖。</summary>
public sealed record ModDependency(string ProjectId, string? VersionId, ModDependencyKind Kind);

/// <summary>两个源统一后的版本（CurseForge 叫 file，Modrinth 叫 version）。</summary>
public sealed record ModVersion
{
    public required ModSource Source { get; init; }
    public required string Id { get; init; }
    public required string ProjectId { get; init; }
    public required string Name { get; init; }
    public string VersionNumber { get; init; } = "";
    public required ModFile File { get; init; }

    public IReadOnlyList<string> GameVersions { get; init; } = [];
    public IReadOnlyList<ModLoader> Loaders { get; init; } = [];
    public IReadOnlyList<ModDependency> Dependencies { get; init; } = [];
    public ModReleaseType ReleaseType { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
    public long Downloads { get; init; }
    public string? Changelog { get; init; }
}

/// <summary>搜索条件。两个源共用一个查询对象，各自挑自己能用的字段。</summary>
public sealed record ModSearchQuery
{
    public string Text { get; init; } = "";

    /// <summary>要搜索的项目类型；默认 Mod。</summary>
    public ModProjectType Type { get; init; } = ModProjectType.Mod;

    /// <summary>游戏版本，如 <c>1.21.4</c>；为空表示不限。</summary>
    public string? GameVersion { get; init; }

    /// <summary>加载器；<see cref="ModLoader.Vanilla"/> 与 null 都表示不限。</summary>
    public ModLoader? Loader { get; init; }

    /// <summary>
    /// 分类。分类键在两个源上不通用（CurseForge 是数字 id，Modrinth 是 slug），
    /// 所以每个源只应用属于自己那一份。
    /// </summary>
    public ModCategory? Category { get; init; }

    public ModSort Sort { get; init; } = ModSort.Relevance;
    public int Offset { get; init; }
    public int Limit { get; init; } = 40;

    /// <summary>限定来源；null 表示两个源都查。</summary>
    public ModSource? Source { get; init; }
}

/// <summary>一页搜索结果。</summary>
public sealed record ModSearchPage
{
    public IReadOnlyList<ModProject> Items { get; init; } = [];
    public int TotalHits { get; init; }
    public int Offset { get; init; }
    public int Limit { get; init; }

    /// <summary>本次查询中失败、已被降级跳过的源。</summary>
    public IReadOnlyList<ModSource> FailedSources { get; init; } = [];
}

/// <summary>版本列表的过滤条件。</summary>
public sealed record ModVersionQuery
{
    /// <summary>项目类型。只有 Mod 会把加载器带进请求。</summary>
    public ModProjectType Type { get; init; } = ModProjectType.Mod;

    public string? GameVersion { get; init; }
    public ModLoader? Loader { get; init; }

    /// <summary>true 时不做版本/加载器过滤，列出全部历史版本。</summary>
    public bool IncludeIncompatible { get; init; }

    public int Limit { get; init; } = 100;
}

/// <summary>分类。<see cref="Key"/> 只在同一个源内有效。</summary>
public sealed record ModCategory(string Key, string Name, ModSource Source);

/// <summary>安装清单里的一项。<see cref="Version"/> 为 null 表示找不到兼容版本。</summary>
public sealed record ModInstallItem(
    ModProject Project,
    ModVersion? Version,
    ModDependencyKind Kind,
    bool AlreadyInstalled,
    bool IsAvailable);

/// <summary>一次安装的完整清单：目标模组本身 + 递归展开的前置。</summary>
public sealed record ModInstallPlan(IReadOnlyList<ModInstallItem> Items)
{
    /// <summary>需要实际下载的项。</summary>
    public IEnumerable<ModInstallItem> Pending =>
        Items.Where(i => i.IsAvailable && !i.AlreadyInstalled && i.Version is not null);

    public bool HasUnavailable => Items.Any(i => !i.IsAvailable);
}

/// <summary>安装结果。</summary>
public sealed record ModInstallResult(int Installed, int Skipped, IReadOnlyList<string> Files);
