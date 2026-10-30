using Solar.Core.Paths;
using Xunit;

namespace Solar.Core.Tests;

public sealed class AppPathsTests
{
    private static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), "solar-paths-" + Guid.NewGuid().ToString("N")[..8]);

    [Fact]
    public void ExplicitRoot_KeepsDataInsideRoot()
    {
        // 显式根目录 = 便携布局，数据留在根目录下的 SolarData
        var root = NewRoot();
        var paths = new AppPaths(root);

        Assert.Equal(Path.Combine(root, "SolarData"), paths.Data);
        Assert.Equal(root, paths.Root);
    }

    [Fact]
    public void ExplicitDataRoot_OverridesLayout()
    {
        var root = NewRoot();
        var dataRoot = NewRoot();

        var paths = new AppPaths(root, dataRoot);

        Assert.Equal(Path.GetFullPath(dataRoot), paths.Data);
        Assert.Equal(Path.GetFullPath(root), paths.Root);
    }

    [Fact]
    public void ExplicitRoot_LeavesAppDataUntouched()
    {
        // 便携/测试布局不应读写 %APPDATA%，否则测试会污染真实账户
        var paths = new AppPaths(NewRoot());

        Assert.DoesNotContain(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            paths.Data,
            StringComparison.OrdinalIgnoreCase);
    }
}
