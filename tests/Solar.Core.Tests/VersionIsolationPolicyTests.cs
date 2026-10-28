using Solar.Core.Instances;
using Solar.Core.Settings;
using Xunit;

namespace Solar.Core.Tests;

/// <summary>版本隔离建议值的判定规则，对齐 PCL 的 5 档逻辑。</summary>
public sealed class VersionIsolationPolicyTests
{
    [Theory]
    [InlineData(VersionIsolationScope.Off, false, false, false)]
    [InlineData(VersionIsolationScope.Off, true, false, false)]
    [InlineData(VersionIsolationScope.Off, false, true, false)]
    [InlineData(VersionIsolationScope.Off, true, true, false)]
    [InlineData(VersionIsolationScope.Modable, false, false, false)]
    [InlineData(VersionIsolationScope.Modable, true, false, true)]
    [InlineData(VersionIsolationScope.Modable, false, true, false)]
    [InlineData(VersionIsolationScope.Modable, true, true, true)]
    [InlineData(VersionIsolationScope.Unstable, false, false, false)]
    [InlineData(VersionIsolationScope.Unstable, true, false, false)]
    [InlineData(VersionIsolationScope.Unstable, false, true, true)]
    [InlineData(VersionIsolationScope.Unstable, true, true, true)]
    [InlineData(VersionIsolationScope.ModableOrUnstable, false, false, false)]
    [InlineData(VersionIsolationScope.ModableOrUnstable, true, false, true)]
    [InlineData(VersionIsolationScope.ModableOrUnstable, false, true, true)]
    [InlineData(VersionIsolationScope.ModableOrUnstable, true, true, true)]
    [InlineData(VersionIsolationScope.All, false, false, true)]
    [InlineData(VersionIsolationScope.All, true, false, true)]
    [InlineData(VersionIsolationScope.All, false, true, true)]
    [InlineData(VersionIsolationScope.All, true, true, true)]
    public void ShouldIsolateFollowsScope(
        VersionIsolationScope scope, bool modable, bool unstable, bool expected)
    {
        Assert.Equal(expected, VersionIsolationPolicy.ShouldIsolate(scope, modable, unstable));
    }

    [Theory]
    [InlineData("release", "1.21.4", false)]
    [InlineData("snapshot", "24w40a", true)]
    [InlineData("old_beta", "b1.7.3", true)]
    [InlineData("old_alpha", "a1.2.6", true)]
    public void IsUnstableFollowsType(string type, string id, bool expected)
    {
        Assert.Equal(expected, VersionIsolationPolicy.IsUnstable(type, id));
    }

    [Theory]
    [InlineData("1.21.4", false)]
    [InlineData("1.21.4-pre1", true)]
    [InlineData("1.21.4-rc2", true)]
    [InlineData("1.14 Pre-Release 1", true)]
    [InlineData("1.20.1 Release Candidate 1", true)]
    [InlineData("1.21.4-patch1", false)]
    public void IsUnstableFallsBackToIdWhenTypeMissing(string id, bool expected)
    {
        // 第三方版本 JSON 常缺 type；release 也要防一手 pre/rc 后缀
        Assert.Equal(expected, VersionIsolationPolicy.IsUnstable("release", id));
        Assert.Equal(expected, VersionIsolationPolicy.IsUnstable(null, id));
    }
}
