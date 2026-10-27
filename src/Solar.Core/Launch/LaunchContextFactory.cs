using System.Security.Cryptography;
using System.Text;
using Solar.Core.Minecraft;
using Solar.Core.Models;
using Solar.Core.Paths;
using Solar.Core.Settings;

namespace Solar.Core.Launch;

/// <summary>
/// 离线账户的 UUID 约定：对 <c>OfflinePlayer:&lt;name&gt;</c> 取 MD5，
/// 并把版本位与变体位改成 RFC 4122 的 v3 格式。所有主流启动器都遵循这个规则，
/// 这样同一个昵称在不同启动器里得到的存档归属是一致的。
/// </summary>
public static class OfflineUuid
{
    public static string FromName(string name)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));

        // version = 3
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
        // variant = RFC 4122
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);

        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }
}

/// <summary>拼装启动上下文。把"该用什么 Java、目录在哪"这些决定集中在一处。</summary>
public sealed class LaunchContextFactory(
    AppPaths paths,
    ISettingsService settings)
{
    public LaunchContext Create(
        Instances.GameInstance instance,
        ResolvedVersion version,
        string javaPath,
        string? logConfigPath = null)
    {
        var current = settings.Current;
        var playerName = string.IsNullOrWhiteSpace(current.PlayerName) ? "Steve" : current.PlayerName.Trim();

        return new LaunchContext
        {
            Instance = instance,
            Version = version,
            Paths = paths,
            Settings = current,
            JavaPath = javaPath,
            PlayerName = playerName,
            Uuid = OfflineUuid.FromName(playerName),
            AccessToken = "0",
            UserType = "legacy",
            GameDirectory = paths.GetInstanceMinecraftDirectory(instance.Id),
            NativesDirectory = Path.Combine(paths.GetInstanceDirectory(instance.Id), "natives"),
            AssetsDirectory = paths.Assets,
            LogConfigPath = logConfigPath ?? "",
            Resolution = (854, 480),
        };
    }
}
