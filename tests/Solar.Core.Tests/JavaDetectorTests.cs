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

    [Fact]
    public void Select_PrefersExactMajor()
    {
        var detector = new JavaDetector(Microsoft.Extensions.Logging.Abstractions.NullLogger<JavaDetector>.Instance);
        var runtimes = new[]
        {
            new JavaRuntime("j8.exe", "1.8.0_402", 8),
            new JavaRuntime("j17.exe", "17.0.9", 17),
            new JavaRuntime("j21.exe", "21.0.1", 21),
        };

        Assert.Equal("j17.exe", detector.Select(runtimes, 17)!.Path);
        Assert.Equal("j21.exe", detector.Select(runtimes, 22)!.Path);
        Assert.Equal("j8.exe", detector.Select(runtimes, 6)!.Path);
    }
}
