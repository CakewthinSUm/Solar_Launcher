using System.Text.Json;
using System.Text.Json.Serialization;
using Solar.Core.Instances;

namespace Solar.Core.Modpacks;

/// <summary>解析阶段读出来的 Modrinth 整合包信息。</summary>
internal sealed record ModrinthIndexData(
    string Name,
    string VersionId,
    string? Summary,
    string MinecraftVersion,
    ModLoader Loader,
    string? LoaderVersion,
    IReadOnlyList<ModpackFile> Files,
    IReadOnlyList<string> FailedFiles);

/// <summary>解析阶段读出来的 CurseForge 整合包信息。</summary>
internal sealed record CurseForgeManifestData(
    string Name,
    string Version,
    string MinecraftVersion,
    ModLoader Loader,
    string? LoaderVersion,
    string OverridesFolder,
    IReadOnlyList<CurseForgeManifestEntry> Files);

internal sealed record CurseForgeManifestEntry(long ProjectId, long FileId, bool Required);

/// <summary>
/// 整合包 manifest 的纯解析逻辑（不联网），单独拆出来便于单测。
/// <para>
/// 两种格式的坑：Modrinth 用下划线命名，且 <c>env.client = "unsupported"</c>
/// 的条目不能装；CurseForge 只给 projectID/fileID，真正的文件名和下载地址
/// 还要逐个查 API，那部分在 <see cref="ModpackService"/> 里。
/// </para>
/// </summary>
internal static class ModpackManifestReader
{
    /// <summary>
    /// Modrinth 的 index 是 camelCase（<c>formatVersion</c> / <c>fileSize</c>），
    /// 与它的 API（snake_case）不同；CurseForge 的 manifest 也是 camelCase，
    /// 但字段名里带大写的 <c>projectID</c> / <c>fileID</c>，靠大小写不敏感匹配。
    /// </summary>
    private static readonly JsonSerializerOptions ModrinthJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions CurseForgeJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static ModrinthIndexData ReadModrinthIndex(string json)
    {
        var dto = JsonSerializer.Deserialize<ModrinthIndexDto>(json, ModrinthJson)
                  ?? throw new InvalidOperationException("modrinth.index.json 是空的。");

        if (dto.FormatVersion != 1)
        {
            throw new InvalidOperationException($"不支持的整合包格式版本：{dto.FormatVersion}。");
        }

        var files = new List<ModpackFile>();
        var failed = new List<string>();
        var dependencies = dto.Dependencies ?? new Dictionary<string, string>();

        foreach (var file in dto.Files ?? [])
        {
            // 客户端不需要的条目（例如只装在服务端的 Mod）直接跳过
            if (string.Equals(file.Env?.Client, "unsupported", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var url = file.Downloads.FirstOrDefault(d => !string.IsNullOrWhiteSpace(d));
            if (url is null || !IsSafeRelativePath(file.Path))
            {
                failed.Add(string.IsNullOrWhiteSpace(file.Path) ? "（没有路径的条目）" : file.Path);
                continue;
            }

            var sha1 = file.Hashes is not null && file.Hashes.TryGetValue("sha1", out var value)
                ? value
                : null;

            files.Add(new ModpackFile(NormalizeRelativePath(file.Path), url, sha1, file.FileSize));
        }

        var (loader, loaderVersion) = ParseDependencies(dependencies);

        return new ModrinthIndexData(
            dto.Name,
            dto.VersionId,
            dto.Summary,
            dependencies.TryGetValue("minecraft", out var minecraft) ? minecraft : "",
            loader,
            loaderVersion,
            files,
            failed);
    }

    public static CurseForgeManifestData ReadCurseForgeManifest(string json)
    {
        var dto = JsonSerializer.Deserialize<CurseForgeManifestDto>(json, CurseForgeJson)
                  ?? throw new InvalidOperationException("manifest.json 是空的。");

        var minecraft = dto.Minecraft ?? new CurseForgeMinecraft();

        if (string.IsNullOrWhiteSpace(minecraft.Version))
        {
            throw new InvalidOperationException("manifest.json 里没有 Minecraft 版本。");
        }

        // 一个整合包可能列多个加载器，以 primary 为准，没有就取第一个
        var loaders = minecraft.ModLoaders ?? [];
        var primary = loaders.FirstOrDefault(m => m.Primary) ?? loaders.FirstOrDefault();

        var (loader, loaderVersion) = ParseLoaderId(primary?.Id);

        var entries = (dto.Files ?? [])
            .Where(f => f.ProjectId > 0 && f.FileId > 0)
            .Select(f => new CurseForgeManifestEntry(f.ProjectId, f.FileId, f.Required))
            .ToList();

        var overrides = string.IsNullOrWhiteSpace(dto.Overrides) ? "overrides" : dto.Overrides!.Trim();

        return new CurseForgeManifestData(
            dto.Name,
            dto.Version,
            minecraft.Version,
            loader,
            loaderVersion,
            overrides.Trim('/'),
            entries);
    }

    /// <summary>
    /// Modrinth 的依赖键：<c>minecraft</c>、<c>fabric-loader</c>、<c>quilt-loader</c>、
    /// <c>forge</c>、<c>neoforge</c>。一个整合包只会声明其中一个加载器。
    /// </summary>
    internal static (ModLoader Loader, string? Version) ParseDependencies(
        IReadOnlyDictionary<string, string> dependencies)
    {
        if (dependencies.TryGetValue("fabric-loader", out var fabric))
        {
            return (ModLoader.Fabric, fabric);
        }

        if (dependencies.TryGetValue("quilt-loader", out var quilt))
        {
            return (ModLoader.Quilt, quilt);
        }

        if (dependencies.TryGetValue("neoforge", out var neoForge))
        {
            return (ModLoader.NeoForge, neoForge);
        }

        if (dependencies.TryGetValue("forge", out var forge))
        {
            return (ModLoader.Forge, forge);
        }

        return (ModLoader.Vanilla, null);
    }

    /// <summary>CurseForge 的加载器 id 形如 <c>forge-14.23.5.2860</c>、<c>fabric-0.15.11</c>。</summary>
    internal static (ModLoader Loader, string? Version) ParseLoaderId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return (ModLoader.Vanilla, null);
        }

        var index = id.IndexOf('-');
        var name = index < 0 ? id : id[..index];
        var version = index < 0 ? null : id[(index + 1)..];

        var loader = name.ToLowerInvariant() switch
        {
            "forge" => ModLoader.Forge,
            "neoforge" => ModLoader.NeoForge,
            "fabric" => ModLoader.Fabric,
            "quilt" => ModLoader.Quilt,
            _ => ModLoader.Vanilla,
        };

        return (loader, string.IsNullOrWhiteSpace(version) ? null : version);
    }

    /// <summary>统一成 zip 的“/”分隔形式，去掉开头的斜杠与 <c>./</c> 前缀。</summary>
    internal static string NormalizeRelativePath(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');

        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized;
    }

    /// <summary>
    /// 拒绝绝对路径、盘符、<c>..</c> 与空段。整合包来自网络，不能让它写到游戏目录之外。
    /// </summary>
    internal static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalized = path.Replace('\\', '/');

        if (normalized.StartsWith('/') || normalized.Contains(':'))
        {
            return false;
        }

        foreach (var segment in normalized.Split('/'))
        {
            // 空段（mods//a.jar）与 .. 都可能越界；单个 "." 只是当前目录，无害
            if (segment.Length == 0 || segment == "..")
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>把相对路径接到根目录下；路径不安全或越界时返回 null。</summary>
    internal static string? CombineUnderRoot(string root, string relativePath)
    {
        if (!IsSafeRelativePath(relativePath))
        {
            return null;
        }

        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;
        var combined = Path.GetFullPath(Path.Combine(
            root,
            NormalizeRelativePath(relativePath).Replace('/', Path.DirectorySeparatorChar)));

        return combined.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) ? combined : null;
    }

    private sealed class ModrinthIndexDto
    {
        public int FormatVersion { get; set; }
        public string Name { get; set; } = "";
        public string VersionId { get; set; } = "";
        public string? Summary { get; set; }
        public List<ModrinthIndexFile> Files { get; set; } = [];
        public Dictionary<string, string> Dependencies { get; set; } = [];
    }

    private sealed class ModrinthIndexFile
    {
        public string Path { get; set; } = "";
        public Dictionary<string, string>? Hashes { get; set; }
        public long FileSize { get; set; }
        public List<string> Downloads { get; set; } = [];
        public ModrinthEnv? Env { get; set; }
    }

    private sealed class ModrinthEnv
    {
        public string? Client { get; set; }
        public string? Server { get; set; }
    }

    private sealed class CurseForgeManifestDto
    {
        public CurseForgeMinecraft Minecraft { get; set; } = new();
        public string ManifestType { get; set; } = "";
        public int ManifestVersion { get; set; }
        public string Name { get; set; } = "";
        public string Version { get; set; } = "";
        public string Author { get; set; } = "";
        public string? Overrides { get; set; }
        public List<CurseForgeFile> Files { get; set; } = [];
    }

    private sealed class CurseForgeMinecraft
    {
        public string Version { get; set; } = "";
        public List<CurseForgeLoader> ModLoaders { get; set; } = [];
    }

    private sealed class CurseForgeLoader
    {
        public string Id { get; set; } = "";
        public bool Primary { get; set; }
    }

    private sealed class CurseForgeFile
    {
        public long ProjectId { get; set; }
        public long FileId { get; set; }
        public bool Required { get; set; }
    }
}
