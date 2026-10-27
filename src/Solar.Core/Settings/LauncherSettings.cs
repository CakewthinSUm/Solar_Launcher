using Solar.Core.Memory;

namespace Solar.Core.Settings;

/// <summary>全局启动器设置，持久化到 <c>SolarData/settings.json</c>。</summary>
public sealed class LauncherSettings
{
    /// <summary>内存分配方式：自动按内存与模组数量计算，或使用 <see cref="MaxMemoryMb"/>。</summary>
    public MemoryAllocationMode MemoryAllocationMode { get; set; } = MemoryAllocationMode.Automatic;

    /// <summary>手动模式下游戏内存上限（MB）；自动模式下不使用。</summary>
    public int MaxMemoryMb { get; set; } = 4096;

    /// <summary>
    /// 游戏目录（.minecraft）。所有实例共用其中的存档、模组与配置。
    /// 留空表示使用程序目录下的 .minecraft；空值才能保持便携性，
    /// 所以填成默认值时也按空值保存。
    /// </summary>
    public string? MinecraftDirectory { get; set; }

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

    /// <summary>
    /// 下载镜像根地址，留空表示直连 Mojang。
    /// 例如 BMCLAPI：<c>https://bmclapi2.bangbang93.com</c>。
    /// </summary>
    public string? DownloadMirrorUrl { get; set; }

    /// <summary>启动后是否自动关闭启动器。</summary>
    public bool CloseLauncherAfterLaunch { get; set; }

    /// <summary>
    /// 微软应用的客户端 ID，正版登录必需。
    /// 在 Azure 门户注册应用后填写，详见 <c>AuthOptions</c> 的说明。
    /// </summary>
    public string? MicrosoftClientId { get; set; }
}
