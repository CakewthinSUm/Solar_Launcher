using Solar.Core.Models;
using Xunit;

namespace Solar.Core.Tests;

public class RuleEvaluatorTests
{
    private static readonly RuleContext Windows = new() { OsName = "windows", Arch = "x64" };

    [Fact]
    public void NoRules_IsAllowed()
    {
        Assert.True(RuleEvaluator.IsAllowed(null, Windows));
        Assert.True(RuleEvaluator.IsAllowed([], Windows));
    }

    [Fact]
    public void AllowWindows_MatchesOnWindows()
    {
        var rules = new List<Rule> { new() { Action = "allow", Os = new OsRule { Name = "windows" } } };
        Assert.True(RuleEvaluator.IsAllowed(rules, Windows));
    }

    [Fact]
    public void AllowWindows_DoesNotMatchOnLinux()
    {
        var rules = new List<Rule> { new() { Action = "allow", Os = new OsRule { Name = "windows" } } };
        var linux = new RuleContext { OsName = "linux", Arch = "x64" };
        Assert.False(RuleEvaluator.IsAllowed(rules, linux));
    }

    [Fact]
    public void AllowAllThenDisallowWindows_IsDisallowed()
    {
        // 典型的 natives 写法：允许所有平台，再排除 Windows
        var rules = new List<Rule>
        {
            new() { Action = "allow" },
            new() { Action = "disallow", Os = new OsRule { Name = "windows" } },
        };

        Assert.False(RuleEvaluator.IsAllowed(rules, Windows));
    }

    [Fact]
    public void FeatureRule_RequiresMatchingFeature()
    {
        var rules = new List<Rule>
        {
            new() { Action = "allow", Features = new Dictionary<string, bool> { ["has_custom_resolution"] = true } },
        };

        Assert.False(RuleEvaluator.IsAllowed(rules, Windows));

        var withFeature = new RuleContext
        {
            OsName = "windows",
            Arch = "x64",
            Features = new Dictionary<string, bool> { ["has_custom_resolution"] = true },
        };

        Assert.True(RuleEvaluator.IsAllowed(rules, withFeature));
    }
}
