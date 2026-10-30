using Solar.Core.Versions;
using Solar.Core.Mods;

namespace Solar.App.Formatting;

/// <summary>
/// 模组页面的展示格式化。集中在一处，避免 XAML 里塞转换器或在各个视图模型里各写一遍。
/// </summary>
public static class ModDisplay
{
    /// <summary>下载量：按中文习惯用「万 / 亿」，小于一万直接显示原数。</summary>
    public static string Downloads(long value) => value switch
    {
        >= 100_000_000 => $"{value / 100_000_000.0:0.#} 亿",
        >= 10_000 => $"{value / 10_000.0:0.#} 万",
        _ => value.ToString("N0"),
    };

    /// <summary>文件大小，用二进制单位（与资源管理器一致）。</summary>
    public static string FileSize(long bytes) => bytes switch
    {
        <= 0 => "—",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.#} MB",
    };

    /// <summary>相对时间。超过一年就不再算相对值，直接给日期。</summary>
    public static string RelativeTime(DateTimeOffset? value)
    {
        if (value is not { } time)
        {
            return "—";
        }

        var span = DateTimeOffset.UtcNow - time;

        return span switch
        {
            { TotalMinutes: < 60 } => "刚刚",
            { TotalHours: < 24 } => $"{(int)span.TotalHours} 小时前",
            { TotalDays: < 30 } => $"{(int)span.TotalDays} 天前",
            { TotalDays: < 365 } => $"{(int)(span.TotalDays / 30)} 个月前",
            _ => time.ToLocalTime().ToString("yyyy-MM-dd"),
        };
    }

    /// <summary>加载器列表 → <c>Fabric · NeoForge</c>。空列表说明作者没标，显示「未标注」。</summary>
    public static string Loaders(IReadOnlyList<ModLoader> loaders)
    {
        if (loaders.Count == 0)
        {
            return "未标注加载器";
        }

        return string.Join(" · ", loaders.Distinct().OrderBy(DisplayLoaderName));
    }

    public static string LoaderName(ModLoader loader) => loader switch
    {
        ModLoader.Vanilla => "原版",
        ModLoader.Forge => "Forge",
        ModLoader.NeoForge => "NeoForge",
        ModLoader.Fabric => "Fabric",
        ModLoader.Quilt => "Quilt",
        _ => loader.ToString(),
    };

    /// <summary>来源徽章。跨源去重后 <see cref="ModProject.AlsoOn"/> 记录被合并掉的另一份。</summary>
    public static string SourceBadge(ModProject project)
    {
        var modrinth = project.Source == ModSource.Modrinth
                       || project.AlsoOn.Any(r => r.Source == ModSource.Modrinth);
        var curseForge = project.Source == ModSource.CurseForge
                         || project.AlsoOn.Any(r => r.Source == ModSource.CurseForge);

        return (modrinth, curseForge) switch
        {
            (true, true) => "MR+CF",
            (true, false) => "MR",
            (false, true) => "CF",
            _ => "",
        };
    }

    /// <summary>图标加载失败时的占位字母。</summary>
    public static string Initial(string? title)
    {
        var trimmed = title?.Trim();
        return string.IsNullOrEmpty(trimmed) ? "?" : char.ToUpperInvariant(trimmed[0]).ToString();
    }

    /// <summary>版本类型徽章。正式版不显示徽章，只给 Beta / Alpha 加提示。</summary>
    public static string ReleaseLabel(ModReleaseType type) => type switch
    {
        ModReleaseType.Beta => "Beta",
        ModReleaseType.Alpha => "Alpha",
        _ => "",
    };

    private static int DisplayLoaderName(ModLoader loader) => loader switch
    {
        ModLoader.Fabric => 0,
        ModLoader.Quilt => 1,
        ModLoader.NeoForge => 2,
        ModLoader.Forge => 3,
        _ => 4,
    };
}
