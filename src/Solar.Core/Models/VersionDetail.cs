namespace Solar.Core.Models;

/// <summary>
/// 单个版本的 JSON（<c>versions/&lt;id&gt;/&lt;id&gt;.json</c>）。
/// 字段名与 Mojang 官方格式一一对应，缺失字段保持默认值。
/// </summary>
public sealed class VersionDetail
{
    public string Id { get; set; } = "";
    public string? Type { get; set; }
    public string? MainClass { get; set; }
    public string? Assets { get; set; }
    public AssetIndexRef? AssetIndex { get; set; }
    public int? ComplianceLevel { get; set; }

    /// <summary>
    /// 注意这是对象而不是数字：<c>{"component":"java-runtime-delta","majorVersion":21}</c>。
    /// 曾误声明为 int?，会导致所有版本 JSON 反序列化失败。
    /// </summary>
    public JavaVersionInfo? JavaVersion { get; set; }
    public string? InheritsFrom { get; set; }
    public string? MinecraftArguments { get; set; }

    public List<Library> Libraries { get; set; } = new();
    public Arguments? Arguments { get; set; }
    public Dictionary<string, DownloadArtifact>? Downloads { get; set; }

    /// <summary>版本自带的 log4j 配置，启动时必须下载并作为 JVM 参数传入。</summary>
    public VersionLogging? Logging { get; set; }

    public string? ReleaseTime { get; set; }
    public string? Time { get; set; }
}

public sealed class JavaVersionInfo
{
    /// <summary>Mojang 运行时组件名，如 <c>java-runtime-delta</c>；用于将来自动下载 Java。</summary>
    public string? Component { get; set; }

    public int MajorVersion { get; set; }
}

public sealed class VersionLogging
{
    public LoggingClient? Client { get; set; }
}

public sealed class LoggingClient
{
    /// <summary>参数模板，形如 <c>-Dlog4j.configurationFile=${path}</c>。</summary>
    public string? Argument { get; set; }

    public string? Type { get; set; }
    public DownloadArtifact? File { get; set; }
}

public sealed class Arguments
{
    public List<ArgumentEntry> Game { get; set; } = new();
    public List<ArgumentEntry> Jvm { get; set; } = new();
}

/// <summary>
/// 参数项既可能是纯字符串，也可能是带规则的对象，因此用自定义转换器承载。
/// </summary>
public sealed class ArgumentEntry
{
    public string? Value { get; set; }
    public List<string>? Values { get; set; }
    public List<Rule>? Rules { get; set; }

    public IEnumerable<string> Enumerate() => Values ?? (Value is null ? [] : [Value]);
}

public sealed class Library
{
    public string Name { get; set; } = "";
    public LibraryDownloads? Downloads { get; set; }
    public string? Url { get; set; }
    public string? ServerUrl { get; set; }
    public List<Rule>? Rules { get; set; }
    public LibraryExtract? Extract { get; set; }

    /// <summary>
    /// Fabric/Quilt 把校验信息平铺在库对象上，而不是放在 downloads.artifact 里。
    /// </summary>
    public string? Sha1 { get; set; }

    public long Size { get; set; }

    /// <summary>
    /// 老格式（1.18 及以前）的 natives 映射，如 <c>{"windows": "natives-windows"}</c>。
    /// 注意这里必须是对象而不是字符串，否则老版本 JSON 会反序列化失败。
    /// 1.19 起该字段不再出现，分类符直接写在 <see cref="Name"/> 里。
    /// </summary>
    public Dictionary<string, string>? Natives { get; set; }

    /// <summary>Forge/NeoForge 等会在这里放额外的下载项。</summary>
    public Dictionary<string, DownloadArtifact>? Classifiers { get; set; }

    /// <summary>Maven 坐标的 group:artifact:version[:classifier]。</summary>
    public (string Group, string Artifact, string Version, string? Classifier) ParseName()
    {
        var parts = Name.Split(':');
        var group = parts.Length > 0 ? parts[0] : "";
        var artifact = parts.Length > 1 ? parts[1] : "";
        var version = parts.Length > 2 ? parts[2] : "";
        var classifier = parts.Length > 3 ? parts[3] : null;
        return (group, artifact, version, classifier);
    }

    /// <summary>
    /// 解析这个库的下载地址与本地相对路径。
    /// <para>
    /// 两种格式并存：
    /// </para>
    /// <list type="bullet">
    /// <item>官方格式：<c>downloads.artifact</c> 直接给出 path 与完整 url。</item>
    /// <item>Fabric/Quilt 格式：只有 <c>url</c>（仓库根地址）与 maven 坐标，
    /// 路径要按 <c>group/artifact/version/artifact-version.jar</c> 推导。</item>
    /// </list>
    /// 第二种如果按第一种处理，库会被整个跳过——Fabric 实例因此无法启动。
    /// </summary>
    public (string RelativePath, string Url)? ResolveDownload()
    {
        if (Downloads?.Artifact is { } artifact && !string.IsNullOrEmpty(artifact.Path))
        {
            return (artifact.Path, artifact.Url ?? "");
        }

        if (string.IsNullOrEmpty(Url))
        {
            return null;
        }

        var (group, artifactName, version, classifier) = ParseName();
        if (string.IsNullOrEmpty(group) || string.IsNullOrEmpty(artifactName) || string.IsNullOrEmpty(version))
        {
            return null;
        }

        var fileName = $"{artifactName}-{version}";
        if (!string.IsNullOrEmpty(classifier))
        {
            fileName += "-" + classifier;
        }

        fileName += ".jar";

        var relative = Path.Combine(
            group.Replace('.', Path.DirectorySeparatorChar),
            artifactName,
            version,
            fileName);

        // url 是仓库根地址，拼上相对路径即可
        var url = Url.EndsWith('/') ? Url + relative.Replace('\\', '/') : Url + "/" + relative.Replace('\\', '/');

        return (relative, url);
    }

    /// <summary>
    /// 判断这个库的 natives 分类符是否适用于当前架构。
    /// <para>
    /// 1.19 起 natives 的架构后缀写在分类符里（<c>natives-windows</c>、
    /// <c>natives-windows-arm64</c>、<c>natives-windows-x86</c>），
    /// 而它们的 rules 通常都只写 <c>allow windows</c>，不做架构区分。
    /// 如果不按后缀再过滤一次，三个变体会同时被下载并解压，
    /// 最终哪个 DLL 落地取决于解压顺序——可能装入 32 位 DLL，
    /// 在 64 位 JVM 上表现为 "Can't load IA 32-bit .dll"。
    /// </para>
    /// </summary>
    public bool MatchesArchitecture(string arch)
    {
        var classifier = ParseName().Classifier;
        if (classifier is null || !classifier.StartsWith("natives", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var wantsArm64 = classifier.EndsWith("-arm64", StringComparison.OrdinalIgnoreCase);
        var wantsX86 = classifier.EndsWith("-x86", StringComparison.OrdinalIgnoreCase);

        return arch switch
        {
            "arm64" => wantsArm64,
            "x86" => wantsX86,
            _ => !wantsArm64 && !wantsX86,   // x64 用不带后缀的那个
        };
    }
}

public sealed class LibraryDownloads
{
    public DownloadArtifact? Artifact { get; set; }
    public Dictionary<string, DownloadArtifact>? Classifiers { get; set; }
}

public sealed class LibraryExtract
{
    public List<string>? Exclude { get; set; }
}

public sealed class DownloadArtifact
{
    /// <summary>log4j 配置文件用 id 命名，如 <c>client-1.21.2.xml</c>。</summary>
    public string? Id { get; set; }

    public string? Path { get; set; }
    public string? Sha1 { get; set; }
    public long Size { get; set; }
    public string? Url { get; set; }
}

public sealed class AssetIndexRef
{
    public string Id { get; set; } = "";
    public string? Sha1 { get; set; }
    public long Size { get; set; }
    public long TotalSize { get; set; }
    public string? Url { get; set; }
}
