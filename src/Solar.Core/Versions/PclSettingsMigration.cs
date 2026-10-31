using Solar.Core.Paths;

namespace Solar.Core.Versions;

/// <summary>
/// PCL 写在版本文件夹里的 <c>PCL/Setup.ini</c> 中，Solar 能对应上的设置。
/// </summary>
public sealed record PclVersionSettings(
    bool IsFavorite,
    bool? VersionIsolation,
    string? CustomIconPath)
{
    /// <summary>是否至少有一项可以迁移；全为默认值时调用方按“没有 PCL 设置”处理。</summary>
    public bool HasAnyValue => IsFavorite || VersionIsolation is not null || CustomIconPath is not null;
}

/// <summary>
/// 读取 PCL 的每版本设置文件，供“从 PCL 迁移设置”使用。
/// <para>
/// PCL 的 INI 是一行一个 <c>Key:Value</c>，按第一个冒号分割，没有分节与注释，键区分大小写。
/// 这里只取 Solar 有对应概念的几项：收藏、版本隔离与自定义图标；
/// <c>VersionFabric</c>、<c>State</c>、<c>Info</c> 这类识别缓存不迁移——Solar 会自己从版本 JSON 算。
/// </para>
/// </summary>
public static class PclSettingsMigration
{
    /// <summary>PCL 的每版本设置文件：<c>versions/&lt;id&gt;/PCL/Setup.ini</c>。</summary>
    public static string GetSetupPath(MinecraftPaths paths, string versionId) =>
        Path.Combine(paths.GetVersionDirectory(versionId), "PCL", "Setup.ini");

    /// <summary>
    /// 读取并转换 PCL 设置。文件不存在、读不出来，或没有可迁移项时返回 <c>null</c>。
    /// </summary>
    public static PclVersionSettings? TryRead(MinecraftPaths paths, string versionId)
    {
        var path = GetSetupPath(paths, versionId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var entries = Parse(File.ReadAllLines(path));
            var settings = FromEntries(entries, Path.GetDirectoryName(path)!);
            return settings.HasAnyValue ? settings : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>把 INI 行解析成键值表；第一个冒号之后的内容全部算值。</summary>
    public static IReadOnlyDictionary<string, string> Parse(IEnumerable<string> lines)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd('\r');
            var separator = trimmed.IndexOf(':');

            // PCL 只认 Index > 0：没有冒号或以冒号开头的行直接跳过
            if (separator <= 0)
            {
                continue;
            }

            result[trimmed[..separator]] = trimmed[(separator + 1)..];
        }

        return result;
    }

    private static PclVersionSettings FromEntries(IReadOnlyDictionary<string, string> entries, string pclDirectory)
    {
        // PCL 只在设过收藏时写 IsStar；没写就是没收藏
        var isFavorite = entries.TryGetValue("IsStar", out var star)
                         && bool.TryParse(star, out var starred)
                         && starred;

        // 新版用布尔 VersionArgumentIndieV2；旧版是 -1/0/1/2，只有 1/2 是明确值。
        // -1（未决定）和 0（跟随全局）在 Solar 里没有对应概念，保持 null 不覆盖现有值。
        bool? isolation = null;
        if (entries.TryGetValue("VersionArgumentIndieV2", out var indieV2)
            && bool.TryParse(indieV2, out var isolated))
        {
            isolation = isolated;
        }
        else if (entries.TryGetValue("VersionArgumentIndie", out var indie)
                 && int.TryParse(indie, out var code))
        {
            isolation = code switch
            {
                1 => true,
                2 => false,
                _ => null,
            };
        }

        // 自定义图标固定复制到 PCL\Logo.png，Logo 里存的是相对路径而不是原图路径
        string? iconPath = null;
        if (entries.TryGetValue("LogoCustom", out var custom)
            && bool.TryParse(custom, out var hasCustomLogo)
            && hasCustomLogo)
        {
            var logo = Path.Combine(pclDirectory, "Logo.png");
            if (File.Exists(logo))
            {
                iconPath = logo;
            }
        }

        return new PclVersionSettings(isFavorite, isolation, iconPath);
    }
}
