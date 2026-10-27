using Solar.Core.Launch;
using Xunit;

namespace Solar.Core.Tests;

public class OfflineUuidTests
{
    [Fact]
    public void MatchesKnownOfflineUuid()
    {
        // Notch 的离线 UUID，被大量启动器文档引用，可作为交叉验证的基准值
        Assert.Equal("b50ad385-829d-3141-a216-7e7d7539ba7f", OfflineUuid.FromName("Notch"));
    }

    [Fact]
    public void IsStableForSameName()
    {
        Assert.Equal(OfflineUuid.FromName("Steve"), OfflineUuid.FromName("Steve"));
    }

    [Fact]
    public void DiffersForDifferentNames()
    {
        Assert.NotEqual(OfflineUuid.FromName("Steve"), OfflineUuid.FromName("Alex"));
    }

    [Fact]
    public void HasRfc4122Version3Layout()
    {
        var uuid = OfflineUuid.FromName("Steve");
        var groups = uuid.Split('-');

        Assert.Equal(5, groups.Length);
        Assert.Equal(8, groups[0].Length);
        Assert.Equal(4, groups[1].Length);
        Assert.Equal(4, groups[2].Length);
        Assert.Equal(4, groups[3].Length);
        Assert.Equal(12, groups[4].Length);

        // 版本位固定为 3
        Assert.StartsWith("3", groups[2]);
        // 变体位属于 8/9/a/b
        Assert.Contains(groups[3][0], "89ab");
    }
}
