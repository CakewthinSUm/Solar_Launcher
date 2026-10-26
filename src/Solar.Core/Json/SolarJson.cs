using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Solar.Core.Json;

/// <summary>
/// 全项目统一的 JSON 选项：宽松读取（允许注释、尾逗号、大小写不敏感），
/// 输出时保留中文原文而不是 \uXXXX 转义。
/// </summary>
public static class SolarJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    /// <summary>仅用于下载清单等超大文件，输出更紧凑。</summary>
    public static JsonSerializerOptions Compact { get; } = Create();

    private static JsonSerializerOptions Create() => new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new ArgumentEntryConverter() },
    };
}
