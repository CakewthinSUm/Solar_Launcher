using System.Text.Json.Serialization;
using Solar.Core.Models;

namespace Solar.Core.Loaders;

/// <summary>
/// Forge / NeoForge installer jar 里的 <c>install_profile.json</c>（spec=1）。
/// <para>
/// 客户端安装只需要跑与 client 相关的 processor：它们把原版 jar 重命名、
/// 打上二进制补丁，产出加载器实际使用的 <c>client</c> artifact。
/// 标记为 server-only 的 processor 必须跳过。
/// </para>
/// </summary>
public sealed class ForgeInstallProfile
{
    public int Spec { get; set; }

    public string? Version { get; set; }
    public string? Minecraft { get; set; }

    /// <summary>数据条目，值是字面量或 <c>[maven 坐标]</c> / <c>/jar 内路径</c>。</summary>
    public Dictionary<string, ForgeDataEntry> Data { get; set; } = new(StringComparer.Ordinal);

    public List<ForgeProcessor> Processors { get; set; } = new();

    public List<Library> Libraries { get; set; } = new();
}

public sealed class ForgeDataEntry
{
    public string? Client { get; set; }
    public string? Server { get; set; }
}

public sealed class ForgeProcessor
{
    /// <summary>执行入口的 maven 坐标。</summary>
    public string Jar { get; set; } = "";

    public List<string> Classpath { get; set; } = new();

    public List<string> Args { get; set; } = new();

    /// <summary>空表示两端都跑；否则只跑列出的那一端。</summary>
    public List<string>? Sides { get; set; }

    /// <summary>输出路径 → 期望的 SHA1，用于跳过已完成的步骤。</summary>
    public Dictionary<string, string>? Outputs { get; set; }

    /// <summary>该 processor 是否需要在客户端执行。</summary>
    public bool AppliesToClient =>
        Sides is null || Sides.Count == 0 || Sides.Contains("client", StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// installer jar 里 <c>version.json</c> 的内容。它就是一个标准的版本 JSON，
/// 带 <c>inheritsFrom</c>，可以直接落到 <c>versions/&lt;id&gt;/</c> 下。
/// </summary>
public sealed class ForgeInstallerVersion
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
}

/// <summary>解析 install_profile 中的占位符。</summary>
public static class ForgeDataResolver
{
    /// <summary>
    /// 把一个 data 值解析成实际字符串。
    /// <list type="bullet">
    /// <item><c>[group:artifact:version]</c> → 该 artifact 在 libraries 下的本地路径</item>
    /// <item><c>[group:artifact:version:classifier@ext]</c> → 带分类符与扩展名的路径</item>
    /// <item><c>/path/in/jar</c> → installer jar 内文件解压后的路径</item>
    /// <item>其他 → 字面量</item>
    /// </list>
    /// </summary>
    public static string Resolve(
        string value,
        IReadOnlyDictionary<string, string> data,
        string librariesDirectory,
        string installerExtractDirectory)
    {
        if (value.Length == 0)
        {
            return value;
        }

        // 单引号包裹表示字面量，例如 MCP_VERSION = '1.21.4-20241203.161809'
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
        {
            return value[1..^1];
        }

        if (value[0] == '[' && value[^1] == ']')
        {
            var coordinate = value[1..^1];
            return GetLibraryPath(coordinate, librariesDirectory);
        }

        if (value[0] == '/')
        {
            // installer jar 内路径：/data/client.lzma → 解压目录下的同名文件
            var relative = value.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            return Path.Combine(installerExtractDirectory, relative);
        }

        // 可能是 {PLACEHOLDER} 形式，递归替换
        return ExpandPlaceholders(value, data, librariesDirectory, installerExtractDirectory);
    }

    /// <summary>替换字符串里的 {KEY} 占位符。</summary>
    public static string ExpandPlaceholders(
        string template,
        IReadOnlyDictionary<string, string> data,
        string librariesDirectory,
        string installerExtractDirectory)
    {
        var result = template;
        foreach (var (key, replacement) in data)
        {
            var token = "{" + key + "}";
            if (result.Contains(token, StringComparison.Ordinal))
            {
                result = result.Replace(token, replacement, StringComparison.Ordinal);
            }
        }

        return result;
    }

    /// <summary>
    /// 把 maven 坐标转成 <c>libraries/</c> 下的相对路径。
    /// <c>group:artifact:version[:classifier][@ext]</c>
    /// </summary>
    public static string GetLibraryPath(string coordinate, string librariesDirectory)
    {
        var (group, artifact, version, classifier, extension) = ParseCoordinate(coordinate);
        var fileName = $"{artifact}-{version}";
        if (!string.IsNullOrEmpty(classifier))
        {
            fileName += "-" + classifier;
        }

        fileName += "." + extension;

        var relative = Path.Combine(
            group.Replace('.', Path.DirectorySeparatorChar),
            artifact,
            version,
            fileName);

        return Path.Combine(librariesDirectory, relative);
    }

    /// <summary>解析 maven 坐标，扩展名默认为 jar。</summary>
    public static (string Group, string Artifact, string Version, string? Classifier, string Extension)
        ParseCoordinate(string coordinate)
    {
        var extension = "jar";
        var at = coordinate.LastIndexOf('@');
        if (at > 0)
        {
            extension = coordinate[(at + 1)..];
            coordinate = coordinate[..at];
        }

        var parts = coordinate.Split(':');
        var group = parts.Length > 0 ? parts[0] : "";
        var artifact = parts.Length > 1 ? parts[1] : "";
        var version = parts.Length > 2 ? parts[2] : "";
        var classifier = parts.Length > 3 ? parts[3] : null;

        return (group, artifact, version, classifier, extension);
    }
}
