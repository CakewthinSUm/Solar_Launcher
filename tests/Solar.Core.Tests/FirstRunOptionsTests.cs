using Solar.Core.Instances;
using Solar.Core.Launch;
using Solar.Core.Settings;
using Xunit;

namespace Solar.Core.Tests;

public class FirstRunOptionsTests
{
    private static string NewDirectory() =>
        Path.Combine(Path.GetTempPath(), "solar-options-" + Guid.NewGuid().ToString("N"));

    private static GameInstance Instance(string mcVersion = "1.20.1") =>
        new() { MinecraftVersion = mcVersion };

    private static string ReadLang(string optionsPath) =>
        File.ReadLines(optionsPath)
            .First(line => line.StartsWith("lang:", StringComparison.Ordinal))
            .Split(':', 2)[1];

    [Fact]
    public void CreatesOptionsWithLanguageAndOnboardingFlag()
    {
        var directory = NewDirectory();

        GameLaunchService.EnsureFirstRunOptions(
            Instance(), directory, GameLanguage.ChineseSimplified);

        var path = Path.Combine(directory, "options.txt");
        Assert.Equal("zh_cn", ReadLang(path));
        Assert.Contains(
            "onboardAccessibility:false",
            File.ReadAllLines(path));

        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void EnglishSettingWritesEnUs()
    {
        var directory = NewDirectory();

        GameLaunchService.EnsureFirstRunOptions(Instance(), directory, GameLanguage.English);

        Assert.Equal("en_us", ReadLang(Path.Combine(directory, "options.txt")));
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void ExistingLanguageIsKeptWhenPlayerAlreadyHasSaves()
    {
        // 与 PCL 一致：有存档说明玩家已经玩过，尊重他自己改过的语言
        var directory = NewDirectory();
        Directory.CreateDirectory(Path.Combine(directory, "saves"));
        var path = Path.Combine(directory, "options.txt");
        File.WriteAllText(path, "lang:en_us\nfov:0.5\n");

        GameLaunchService.EnsureFirstRunOptions(
            Instance(), directory, GameLanguage.ChineseSimplified);

        Assert.Equal("en_us", ReadLang(path));
        Assert.Contains("fov:0.5", File.ReadAllLines(path));

        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void LanguageIsOverriddenWhenThereAreNoSaves()
    {
        // 整合包自带的 options.txt 可能锁死了语言；还没存档时按设置改写
        var directory = NewDirectory();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "options.txt");
        File.WriteAllText(path, "lang:en_us\nfov:0.5\n");

        GameLaunchService.EnsureFirstRunOptions(
            Instance(), directory, GameLanguage.ChineseSimplified);

        Assert.Equal("zh_cn", ReadLang(path));
        Assert.Contains("fov:0.5", File.ReadAllLines(path));

        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void OldVersionsUseUppercaseSuffix()
    {
        // 1.1–1.10 只认 zh_CN，小写会被游戏当成英文
        var directory = NewDirectory();

        GameLaunchService.EnsureFirstRunOptions(
            Instance("1.8.9"), directory, GameLanguage.ChineseSimplified);

        Assert.Equal("zh_CN", ReadLang(Path.Combine(directory, "options.txt")));
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void YosbrOptionsFileIsUsedWhenPresent()
    {
        var directory = NewDirectory();
        var yosbrPath = Path.Combine(directory, "config", "yosbr", "options.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(yosbrPath)!);
        File.WriteAllText(yosbrPath, "lang:en_us\n");

        GameLaunchService.EnsureFirstRunOptions(
            Instance(), directory, GameLanguage.ChineseSimplified);

        Assert.Equal("zh_cn", ReadLang(yosbrPath));
        Assert.False(File.Exists(Path.Combine(directory, "options.txt")));

        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void AncientVersionsAreLeftAlone()
    {
        // 1.0 及以前没有语言选项，写进去反而可能出问题
        var directory = NewDirectory();

        GameLaunchService.EnsureFirstRunOptions(
            Instance("1.0"), directory, GameLanguage.ChineseSimplified);

        Assert.False(Directory.Exists(directory));
    }
}
