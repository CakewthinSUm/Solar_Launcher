namespace Solar.Core.Settings;

/// <summary>全局启动器设置，持久化到 <c>SolarData/settings.json</c>。</summary>
public sealed class LauncherSettings
{
    /// <summary>游戏内存上限（MB）。</summary>
    public int MaxMemoryMb { get; set; } = 4096;

    /// <summary>自定义 Java 路径；为空时自动探测。</summary>
    public string? JavaPath { get; set; }

    /// <summary>额外 JVM 参数，一行一个。</summary>
    public string ExtraJvmArguments { get; set; } = "";

    /// <summary>额外游戏参数。</summary>
    public string ExtraGameArguments { get; set; } = "";

    /// <summary>下载线程数。</summary>
    public int DownloadThreads { get; set; } = 8;

    /// <summary>版本清单下载源，可替换为国内镜像。</summary>
    public string VersionManifestUrl { get; set; } = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    /// <summary>资源/库下载源，可替换为 BMCLAPI 等镜像。</summary>
    public string ResourceBaseUrl { get; set; } = "https://resources.download.minecraft.net";

    /// <summary>启动后是否自动关闭启动器。</summary>
    public bool CloseLauncherAfterLaunch { get; set; }

    /// <summary>玩家昵称（离线模式）。</summary>
    public string PlayerName { get; set; } = "Steve";
}
