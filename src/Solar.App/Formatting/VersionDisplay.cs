using System.Text.RegularExpressions;
using Solar.Core.Models;

namespace Solar.App.Formatting;

/// <summary>下载页展示用的版本分类。Mojang 清单只给出四种 <c>Type</c>，快照再按 Id 拆出预发布版。</summary>
public enum VersionCategory
{
    Release,
    Snapshot,
    Prerelease,
    OldBeta,
    OldAlpha,
}

/// <summary>
/// Minecraft 版本清单的展示格式化。分类只在这里判定一次，
/// 筛选和行内标签共用，避免两处规则走样。
/// </summary>
public static class VersionDisplay
{
    // 新格式：1.21.5-pre1 / 1.21.5-rc1；
    // 1.14 前后用的是带空格的老格式：1.14 Pre-Release 1、1.16 Release Candidate 1。
    private static readonly Regex PrereleasePattern = new(
        @"-(?:pre|rc)\d+$| (?:Pre-Release|Release Candidate)\s*\d+$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>快照里属于预发布/候选版的那些（Mojang 一律标成 <c>snapshot</c>，只能看 Id）。</summary>
    public static bool IsPrerelease(string? id) =>
        !string.IsNullOrEmpty(id) && PrereleasePattern.IsMatch(id);

    /// <summary>清单条目 → 展示分类；未知类型返回 null，不参与任何筛选。</summary>
    public static VersionCategory? Categorize(string? type, string? id) => type switch
    {
        "release" => VersionCategory.Release,
        "snapshot" => IsPrerelease(id) ? VersionCategory.Prerelease : VersionCategory.Snapshot,
        "old_beta" => VersionCategory.OldBeta,
        "old_alpha" => VersionCategory.OldAlpha,
        _ => null,
    };

    /// <summary>该条目是否属于某个筛选类别。</summary>
    public static bool Matches(VersionSummary version, VersionCategory category) =>
        Categorize(version.Type, version.Id) == category;

    /// <summary>版本行里显示的类型名。不要加重载：x:Bind 的函数绑定只按参数个数匹配。</summary>
    public static string TypeLabel(string? type, string? id) => Categorize(type, id) switch
    {
        VersionCategory.Release => "正式版",
        VersionCategory.Snapshot => "快照",
        VersionCategory.Prerelease => "预发布版",
        VersionCategory.OldBeta => "远古 Beta",
        VersionCategory.OldAlpha => "远古 Alpha",
        _ => type ?? "",
    };
}
