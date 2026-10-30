using System.Text.Json.Serialization;
using Solar.Core.Memory;
using Solar.Core.Json;

namespace Solar.Core.Settings;

/// <summary>全局启动器设置，持久化到 <c>SolarData/settings.json</c>。</summary>
public sealed class LauncherSettings : IJsonOnDeserialized
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

    /// <summary>
    /// Mod 来源（Modrinth / CurseForge）的访问偏好。
    /// 默认「官方优先」：直连能通时延迟最低，超时后自动降级到镜像。
    /// </summary>
    public Mods.ModSourcePreference ModSourcePreference { get; set; } = Mods.ModSourcePreference.Auto;

    /// <summary>
    /// Mod 源镜像根地址。默认 MCIM，它同时代理 Modrinth 与 CurseForge
    /// （后者不需要 API Key，这是国内可用 CurseForge 的唯一途径）。
    /// </summary>
    public string? ModMirrorUrl { get; set; } = "https://mod.mcimirror.top";

    /// <summary>
    /// 自备的 CurseForge API Key。留空时官方 API 一律 403，只能走镜像。
    /// 启动器不分发 Key——那违反 CurseForge 的使用条款。
    /// </summary>
    public string? CurseForgeApiKey { get; set; }

    /// <summary>游戏窗口出现后对启动器窗口的处理方式。默认保持不动。</summary>
    public AfterLaunchAction AfterLaunchAction { get; set; } = AfterLaunchAction.Nothing;

    /// <summary>
    /// 新建实例时版本隔离的默认档位（对齐 PCL 的 5 档）。默认隔离所有版本。
    /// </summary>
    public VersionIsolationScope NewInstanceIsolationScope { get; set; } = VersionIsolationScope.All;

    /// <summary>
    /// 首次启动时写进 options.txt 的游戏语言（对齐 PCL 的「帮助中文」）。
    /// 只在游戏目录还没有语言设置、或还没有存档时生效，不会覆盖玩家自己改过的语言。
    /// </summary>
    public GameLanguage GameLanguage { get; set; } = GameLanguage.ChineseSimplified;

    /// <summary>
    /// [已废弃] 旧的三档设置（询问/总是/从不）。只在读取旧 settings.json 时用于一次性迁移：
    /// OnDeserialized 里换算成 <see cref="NewInstanceIsolationScope"/> 后置 null，
    /// 下次保存时因 WhenWritingNull 不再写出。
    /// </summary>
    public VersionIsolationDefault? NewInstanceIsolation { get; set; }

    public void OnDeserialized()
    {
        if (NewInstanceIsolation is { } legacy)
        {
            // 注意数字错位：旧枚举 Ask=0/Always=1/Never=2 与新枚举 Off=0/Modable=1 不同，
            // 必须按名字换算，不能按数值。
#pragma warning disable CS0618 // 旧枚举仅在此迁移处使用
            NewInstanceIsolationScope = legacy switch
            {
                VersionIsolationDefault.Always => VersionIsolationScope.All,
                VersionIsolationDefault.Never => VersionIsolationScope.Off,
                // 旧默认「每次询问」的用户没有表达过偏好，落到对齐 PCL 的默认行为
                _ => VersionIsolationScope.All,
            };
#pragma warning restore CS0618
            NewInstanceIsolation = null;
        }
    }
}

/// <summary>游戏启动成功后启动器窗口的处理方式。</summary>
public enum AfterLaunchAction
{
    /// <summary>什么都不做，启动器留在原地。</summary>
    Nothing = 0,

    /// <summary>把启动器最小化到任务栏。</summary>
    Minimize = 1,

    /// <summary>关闭启动器。游戏是独立进程，不受影响。</summary>
    Close = 2,
}

/// <summary>首次启动时写进 options.txt 的游戏语言。</summary>
public enum GameLanguage
{
    /// <summary>简体中文（PCL 的默认行为）。</summary>
    ChineseSimplified = 0,

    /// <summary>繁體中文。</summary>
    ChineseTraditional = 1,

    /// <summary>English (US)。</summary>
    English = 2,
}

/// <summary>新建实例时版本隔离的处理方式。</summary>
[Obsolete("已被 VersionIsolationScope 取代，仅在读取旧 settings.json 时使用。")]
public enum VersionIsolationDefault
{
    /// <summary>创建实例时问一次，可记住选择。</summary>
    Ask = 0,

    /// <summary>总是开启，不再询问。</summary>
    Always = 1,

    /// <summary>总是关闭，不再询问。</summary>
    Never = 2,
}

/// <summary>版本隔离的全局默认档位，对齐 PCL 的 5 档。</summary>
public enum VersionIsolationScope
{
    /// <summary>不隔离，全部共用游戏目录。</summary>
    Off = 0,

    /// <summary>只隔离可安装 Mod 的版本（带加载器或附加组件）。</summary>
    Modable = 1,

    /// <summary>只隔离非正式版（快照、预发布、远古 Beta/Alpha）。</summary>
    Unstable = 2,

    /// <summary>隔离可安装 Mod 的版本与非正式版。</summary>
    ModableOrUnstable = 3,

    /// <summary>隔离所有版本（PCL 的默认行为）。</summary>
    All = 4,
}
