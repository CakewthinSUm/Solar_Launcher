using Solar.Core.Accounts;
using Solar.Core.Instances;
using Solar.Core.Minecraft;
using Solar.Core.Paths;
using Solar.Core.Settings;

namespace Solar.Core.Launch;

/// <summary>拼装启动上下文。把"该用什么 Java、目录在哪、用哪个账户"集中在一处。</summary>
public sealed class LaunchContextFactory(
    AppPaths paths,
    ISettingsService settings,
    InstanceGameDirectory gameDirectories,
    GameFileLocator fileLocator)
{
    public LaunchContext Create(
        Instances.GameInstance instance,
        ResolvedVersion version,
        string javaPath,
        AccountSession account,
        string? logConfigPath = null)
    {
        // 游戏目录取决于实例的版本隔离开关：关闭时所有实例共用设置里的 .minecraft，
        // 开启时用实例自己的目录（存档、模组、配置互不影响）。
        // natives 始终按实例隔离，不同版本的原生库不能混用。
        var gameDirectory = gameDirectories.Resolve(instance);

        return new LaunchContext
        {
            Instance = instance,
            Version = version,
            Paths = paths,
            Settings = settings.Current,
            FileLocator = fileLocator,
            JavaPath = javaPath,
            PlayerName = account.Username,
            Uuid = account.Uuid,
            AccessToken = account.AccessToken,
            Xuid = account.Xuid ?? "",
            UserType = account.UserType,
            GameDirectory = gameDirectory,
            NativesDirectory = Path.Combine(paths.GetInstanceDirectory(instance.Id), "natives"),
            AssetsDirectory = paths.Assets,
            LogConfigPath = logConfigPath ?? "",
            Resolution = (854, 480),
        };
    }
}
