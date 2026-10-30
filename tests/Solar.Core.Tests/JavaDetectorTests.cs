using Solar.Core.Java;
using Xunit;

namespace Solar.Core.Tests;

public class JavaDetectorTests
{
    [Theory]
    [InlineData("1.8.0_402", 8)]
    [InlineData("17.0.9", 17)]
    [InlineData("21", 21)]
    [InlineData("21.0.1+12-LTS", 21)]
    [InlineData("1.7.0_80", 7)]
    public void ParseMajor_HandlesLegacyAndModernVersions(string version, int expected)
    {
        Assert.Equal(expected, JavaDetector.ParseMajor(version));
    }

    [Fact]
    public void ParseMajor_ReturnsZeroForGarbage()
    {
        Assert.Equal(0, JavaDetector.ParseMajor("not-a-version"));
    }

    private static JavaDetector CreateDetector() =>
        new(Microsoft.Extensions.Logging.Abstractions.NullLogger<JavaDetector>.Instance);

    private static JavaRuntime[] Runtimes() =>
    [
        new("j8.exe", "1.8.0_402", 8),
        new("j17.exe", "17.0.9", 17),
        new("j21.exe", "21.0.1", 21),
    ];

    [Fact]
    public void Select_PrefersExactMajor()
    {
        Assert.Equal("j17.exe", CreateDetector().Select(Runtimes(), new JavaRequirement(17))!.Path);
    }

    [Fact]
    public void Select_FallsBackToLowestHigherVersion()
    {
        // 要求 18+ 时选 21 而不是跳回 17
        Assert.Equal("j21.exe", CreateDetector().Select(Runtimes(), new JavaRequirement(18))!.Path);
    }

    [Fact]
    public void Select_ReturnsNullWhenNothingSatisfies()
    {
        // 26.2 这类需要 Java 25 的版本：没有合适的 Java 时不能拿 21 硬启动
        Assert.Null(CreateDetector().Select(Runtimes(), new JavaRequirement(25)));
    }

    [Fact]
    public void Select_RespectsMaxMajor()
    {
        Assert.Equal("j8.exe", CreateDetector().Select(Runtimes(), new JavaRequirement(8, 8))!.Path);
        Assert.Null(CreateDetector().Select([Runtimes()[2]], new JavaRequirement(8, 8)));
    }
}
