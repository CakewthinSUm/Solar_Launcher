using System.Text.RegularExpressions;

namespace Solar.Core.Versions;

/// <summary>
/// 版本隔离的建议值判定，对齐 PCL 的逻辑。
/// 扫描与创建页共用：给定全局档位和版本特征，回答"该不该隔离"。
/// </summary>
public static partial class VersionIsolationPolicy
{
    /// <summary>给定全局档位下，具备 <paramref name="modable"/> / <paramref name="unstable"/> 特征的版本是否建议隔离。</summary>
    public static bool ShouldIsolate(Settings.VersionIsolationScope scope, bool modable, bool unstable) => scope switch
    {
        Settings.VersionIsolationScope.Off => false,
        Settings.VersionIsolationScope.Modable => modable,
        Settings.VersionIsolationScope.Unstable => unstable,
        Settings.VersionIsolationScope.ModableOrUnstable => modable || unstable,
        Settings.VersionIsolationScope.All => true,
        _ => false,
    };

    /// <summary>
    /// 是否非正式版：快照、预发布（-pre/-rc，含老格式 "1.14 Pre-Release 1"）与远古版（old_beta/old_alpha）。
    /// 版本 JSON 里 type 缺失（第三方版本常见）时只按 id 正则判断。
    /// </summary>
    public static bool IsUnstable(string? type, string? id) => type switch
    {
        "release" => IsPrereleaseId(id),
        null or "" => IsPrereleaseId(id),
        _ => true, // snapshot / old_beta / old_alpha
    };

    public static bool IsPrereleaseId(string? id) =>
        !string.IsNullOrEmpty(id) && PrereleasePattern().IsMatch(id);

    [GeneratedRegex(@"-(?:pre|rc)\d+$| (?:Pre-Release|Release Candidate)\s*\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex PrereleasePattern();
}
