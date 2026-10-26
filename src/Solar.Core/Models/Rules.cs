using System.Runtime.InteropServices;

namespace Solar.Core.Models;

/// <summary>版本 JSON 里的 rules 条目，用来决定某个库/参数在当前平台是否启用。</summary>
public sealed class Rule
{
    public string Action { get; set; } = "allow";
    public OsRule? Os { get; set; }
    public Dictionary<string, bool>? Features { get; set; }
}

public sealed class OsRule
{
    public string? Name { get; set; }
    public string? Version { get; set; }
    public string? Arch { get; set; }
}

/// <summary>规则求值时的环境信息。</summary>
public sealed class RuleContext
{
    public string OsName { get; init; } = "windows";
    public string Arch { get; init; } = DetectArch();
    public IReadOnlyDictionary<string, bool> Features { get; init; } = new Dictionary<string, bool>();

    public static string DetectArch()
    {
        if (!Environment.Is64BitOperatingSystem)
        {
            return "x86";
        }

        return RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
    }
}

public static class RuleEvaluator
{
    /// <summary>
    /// 与官方启动器一致的求值逻辑：没有规则则允许；否则从“不允许”开始，
    /// 逐条匹配，最后一条匹配的规则决定结果。
    /// </summary>
    public static bool IsAllowed(IReadOnlyList<Rule>? rules, RuleContext context)
    {
        if (rules is null || rules.Count == 0)
        {
            return true;
        }

        var allowed = false;
        foreach (var rule in rules)
        {
            if (Matches(rule, context))
            {
                allowed = string.Equals(rule.Action, "allow", StringComparison.OrdinalIgnoreCase);
            }
        }

        return allowed;
    }

    private static bool Matches(Rule rule, RuleContext context)
    {
        if (rule.Os is { } os)
        {
            if (os.Name is not null && !string.Equals(os.Name, context.OsName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (os.Arch is not null && !string.Equals(os.Arch, context.Arch, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (rule.Features is { Count: > 0 })
        {
            foreach (var (key, expected) in rule.Features)
            {
                if (!context.Features.TryGetValue(key, out var actual) || actual != expected)
                {
                    return false;
                }
            }
        }

        return true;
    }
}
