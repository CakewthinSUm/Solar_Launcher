using Solar.Core.Accounts;
using Solar.Core.Memory;
using Solar.Core.Minecraft;
using Solar.Core.Paths;
using Solar.Core.Settings;
using Solar.Core.Versions;

namespace Solar.Core.Launch;

/// <summary>拼装启动上下文。把"该用什么 Java、目录在哪、用哪个账户、给多少内存"集中在一处。</summary>
public sealed class LaunchContextFactory(
    MinecraftPaths paths,
    ISettingsService settings,
    GameFileLocator fileLocator,
    IMemoryAllocationService memory)
{
    public LaunchContext Create(
        GameVersion version,
        ResolvedVersion resolved,
        string javaPath,
        AccountSession account,
        string? logConfigPath = null,
        int javaMajorVersion = 8)
    {
        // 游戏目录取决于版本隔离开关：关闭时所有版本共用 MC 文件夹根，
        // 开启时用该版本自己的文件夹（存档、模组、配置互不影响）。
        // natives 始终按版本隔离，不同版本的原生库不能混用。
        var gameDirectory = paths.ResolveGameDirectory(version.Id, version.VersionIsolation);

        return new LaunchContext
        {
            Game = version,
            Version = resolved,
            Paths = paths,
            Settings = settings.Current,
            FileLocator = fileLocator,
            JavaPath = javaPath,
            JavaMajorVersion = javaMajorVersion,
            PlayerName = account.Username,
            Uuid = account.Uuid,
            AccessToken = account.AccessToken,
            Xuid = account.Xuid ?? "",
            UserType = account.UserType,
            GameDirectory = gameDirectory,
            NativesDirectory = paths.GetNativesDirectory(version.Id),
            AssetsDirectory = paths.Assets,
            LogConfigPath = logConfigPath ?? "",
            Resolution = (854, 480),
            MaxMemoryMb = memory.Resolve(version),
        };
    }
}
