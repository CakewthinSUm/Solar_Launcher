using Solar.Core.Accounts;
using Solar.Core.Minecraft;
using Solar.Core.Paths;
using Solar.Core.Settings;

namespace Solar.Core.Launch;

/// <summary>拼装启动上下文。把"该用什么 Java、目录在哪、用哪个账户"集中在一处。</summary>
public sealed class LaunchContextFactory(
    AppPaths paths,
    ISettingsService settings)
{
    public LaunchContext Create(
        Instances.GameInstance instance,
        ResolvedVersion version,
        string javaPath,
        AccountSession account,
        string? logConfigPath = null)
    {
        // 游戏目录是全局设置：所有实例共用同一份 .minecraft（存档、模组、配置）。
        // natives 仍按实例隔离，不同版本的原生库不能混用。
        var gameDirectory = paths.ResolveMinecraftDirectory(settings.Current.MinecraftDirectory);

        return new LaunchContext
        {
            Instance = instance,
            Version = version,
            Paths = paths,
            Settings = settings.Current,
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
