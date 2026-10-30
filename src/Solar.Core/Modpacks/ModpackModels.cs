using Solar.Core.Versions;
using Solar.Core.Mods;

namespace Solar.Core.Modpacks;

/// <summary>整合包归档格式。两种格式的 manifest 结构完全不同，但安装结果一样。</summary>
public enum ModpackFormat
{
    /// <summary><c>.mrpack</c>：zip 内含 <c>modrinth.index.json</c> 与 <c>overrides/</c>。</summary>
    Modrinth,

    /// <summary>CurseForge 整合包：zip 内含 <c>manifest.json</c>，文件按 projectID/fileID 解析。</summary>
    CurseForge,
}

/// <summary>整合包里列出的一个待下载文件。路径使用 zip 里的“/”分隔形式。</summary>
public sealed record ModpackFile(string RelativePath, string Url, string? Sha1, long Size);

/// <summary>
/// 解析完成、可以开始安装的整合包。
/// <para>
/// <see cref="ArchivePath"/> 是已经下到临时目录的整合包本体，解压 overrides 时还要用；
/// 对话框关闭时由 <see cref="IModpackService.Cleanup"/> 删掉。
/// </para>
/// </summary>
public sealed record ModpackPlan
{
    public required string ArchivePath { get; init; }
    public required string Name { get; init; }
    public required string MinecraftVersion { get; init; }

    public ModLoader Loader { get; init; } = ModLoader.Vanilla;
    public string? LoaderVersion { get; init; }

    /// <summary>整合包自身的版本号，用于展示，不参与安装。</summary>
    public string? Version { get; init; }

    public string? Summary { get; init; }
    public ModpackFormat Format { get; init; }

    /// <summary>CurseForge 的 overrides 目录名（默认 <c>overrides</c>）；Modrinth 固定。</summary>
    public string? OverridesFolder { get; init; }

    public IReadOnlyList<ModpackFile> Files { get; init; } = [];

    /// <summary>解析阶段就失败的条目（下载地址缺失、文件被作者禁用分发等）。</summary>
    public IReadOnlyList<string> FailedFiles { get; init; } = [];

    public long TotalBytes => Files.Sum(f => f.Size);
}

public enum ModpackInstallStage
{
    /// <summary>下载并解析整合包本体。</summary>
    Preparing,

    /// <summary>把加载器装进整合包的版本文件夹（与 PCL 一致，安装时一次装完）。</summary>
    InstallingLoader,

    DownloadingFiles,

    ExtractingOverrides,

    Saving,
}

/// <summary>安装进度。UI 用 <see cref="Completed"/>/<see cref="Total"/> 画进度条。</summary>
public sealed record ModpackInstallProgress(
    ModpackInstallStage Stage,
    string Message,
    int Completed = 0,
    int Total = 0,
    string? CurrentFile = null);

public sealed record ModpackInstallResult(
    GameVersion Version,
    int InstalledFiles,
    int SkippedFiles,
    int OverrideFiles,
    IReadOnlyList<string> FailedFiles);

public interface IModpackService
{
    /// <summary>
    /// 下载并解析整合包：拿到 MC 版本、加载器与全部文件清单。
    /// 会先下载整合包本体（通常是几 MB 的 manifest + overrides）。
    /// </summary>
    Task<ModpackPlan> CreatePlanAsync(
        ModVersion version,
        IProgress<ModpackInstallProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>创建版本文件夹并安装清单里的全部文件与 overrides。整合包版本始终隔离。</summary>
    Task<ModpackInstallResult> InstallAsync(
        ModpackPlan plan,
        string? versionName = null,
        IProgress<ModpackInstallProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>删除整合包临时文件。对话框关闭或安装结束后调用。</summary>
    void Cleanup(ModpackPlan plan);
}
