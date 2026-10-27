using Solar.Core.Instances;
using Solar.Core.Loaders;

namespace Solar.Core.Addons;

/// <summary>创建实例时可以勾选的附加组件。</summary>
public enum ComponentKind
{
    Forge,
    NeoForge,
    Fabric,
    Quilt,
    FabricApi,
    OptiFine,
    OptiFabric,
    LiteLoader,
}

/// <summary>
/// 用户在创建页上选中的组件。加载器版本与附加组件的具体构建都放在这里，
/// 安装流程只读这个对象，不再回查列表。
/// </summary>
public sealed record ComponentSelection
{
    public ModLoader Loader { get; init; } = ModLoader.Vanilla;
    public string? LoaderVersion { get; init; }

    public OptiFineBuild? OptiFine { get; init; }
    public LiteLoaderBuild? LiteLoader { get; init; }

    public AddonModVersion? FabricApi { get; init; }
    public AddonModVersion? OptiFabric { get; init; }

    /// <summary>选了任何模组加载器时，OptiFine 作为 Mod 放进 mods，而不是生成独立版本。</summary>
    public bool OptiFineAsMod => OptiFine is not null && Loader != ModLoader.Vanilla;

    public bool HasAnyAddon =>
        OptiFine is not null || LiteLoader is not null || FabricApi is not null || OptiFabric is not null;

    public bool NeedsIsolation => Loader != ModLoader.Vanilla || HasAnyAddon;

    /// <summary>界面上的一句话摘要，用于默认实例名。</summary>
    public string BuildDefaultName(string minecraftVersion)
    {
        var parts = new List<string> { minecraftVersion };

        if (Loader != ModLoader.Vanilla && !string.IsNullOrEmpty(LoaderVersion))
        {
            parts.Add($"{Loader}_{LoaderVersion}");
        }

        if (OptiFine is { } optifine)
        {
            parts.Add($"OptiFine_{optifine.DisplayVersion}");
        }

        if (LiteLoader is not null)
        {
            parts.Add("LiteLoader");
        }

        return string.Join("-", parts);
    }
}

/// <summary>
/// 组件之间的兼容关系。全部是纯函数，页面只负责把结果画出来。
/// <para>
/// 规则对齐 PCL：四个主加载器互斥；LiteLoader 只到 1.12.2、不能与主加载器同用、
/// 且要求本机有 Java 8；Fabric API 依赖 Fabric/Quilt；OptiFabric 依赖 Fabric + OptiFine；
/// NeoForge 与 OptiFine 不兼容，Forge 1.13–1.14.3 与 OptiFine 不兼容。
/// </para>
/// </summary>
public static class ComponentRules
{
    /// <summary>该组件在当前选择下不可用的原因；可用时返回 null。</summary>
    /// <param name="hasJava8">
    /// 本机是否装了 Java 8。LiteLoader 依赖 launchwrapper，而 launchwrapper 在
    /// Java 9+ 上会因为 <c>AppClassLoader</c> 不是 <c>URLClassLoader</c> 直接崩，
    /// 所以没有 Java 8 时装了也启动不了。其它组件不受影响。
    /// </param>
    public static string? GetUnavailableReason(
        ComponentKind kind,
        ComponentSelection selection,
        string minecraftVersion,
        bool hasJava8 = true)
    {
        var loader = selection.Loader;
        var hasOptiFine = selection.OptiFine is not null;

        return kind switch
        {
            ComponentKind.Forge => loader switch
            {
                ModLoader.NeoForge => "与 NeoForge 互斥",
                ModLoader.Fabric => "与 Fabric 互斥",
                ModLoader.Quilt => "与 Quilt 互斥",
                _ => null,
            },

            ComponentKind.NeoForge => LoaderVersionService.ToNeoForgePrefix(minecraftVersion) is null
                ? "NeoForge 从 Minecraft 1.20.1 开始支持"
                : loader switch
                {
                    ModLoader.Forge => "与 Forge 互斥",
                    ModLoader.Fabric => "与 Fabric 互斥",
                    ModLoader.Quilt => "与 Quilt 互斥",
                    _ => hasOptiFine ? "与 OptiFine 不兼容" : null,
                },

            ComponentKind.Fabric => loader switch
            {
                ModLoader.Forge => "与 Forge 互斥",
                ModLoader.NeoForge => "与 NeoForge 互斥",
                ModLoader.Quilt => "与 Quilt 互斥",
                _ => null,
            },

            ComponentKind.Quilt => loader switch
            {
                ModLoader.Forge => "与 Forge 互斥",
                ModLoader.NeoForge => "与 NeoForge 互斥",
                ModLoader.Fabric => "与 Fabric 互斥",
                _ => null,
            },

            ComponentKind.FabricApi => loader is ModLoader.Fabric or ModLoader.Quilt
                ? null
                : "需要先选择 Fabric 或 Quilt",

            ComponentKind.OptiFine => GetOptiFineReason(selection, minecraftVersion),

            ComponentKind.OptiFabric => loader != ModLoader.Fabric
                ? "需要先选择 Fabric"
                : !hasOptiFine
                    ? "需要先选择 OptiFine"
                    : null,

            ComponentKind.LiteLoader => GetLiteLoaderReason(selection, minecraftVersion, hasJava8),

            _ => null,
        };
    }

    private static string? GetOptiFineReason(ComponentSelection selection, string minecraftVersion)
    {
        if (selection.Loader == ModLoader.NeoForge)
        {
            return "与 NeoForge 不兼容";
        }

        // Forge 1.13–1.14.3 的 launchwrapper 与 OptiFine 冲突，1.14.4 起 OptiFine 改成 Mod 形式
        if (selection.Loader == ModLoader.Forge && IsForgeOptiFineConflict(minecraftVersion))
        {
            return "Forge 1.13–1.14.3 与 OptiFine 不兼容";
        }

        // 1.20.5 起 OptiFine 不再支持 Fabric 端的 OptiFabric
        if (selection.Loader == ModLoader.Fabric && IsAfter(minecraftVersion, 1, 20, 4))
        {
            return "Fabric 1.20.5 及以上不支持 OptiFine";
        }

        return null;
    }

    private static string? GetLiteLoaderReason(
        ComponentSelection selection,
        string minecraftVersion,
        bool hasJava8)
    {
        if (IsAfter(minecraftVersion, 1, 12, 2))
        {
            return "LiteLoader 只支持 1.12.2 及更早的版本";
        }

        if (IsBefore(minecraftVersion, 1, 7, 2))
        {
            return "LiteLoader 从 Minecraft 1.7.2 开始支持";
        }

        // launchwrapper 在 Java 9+ 上会抛 ClassCastException，装上也启动不了
        if (!hasJava8)
        {
            return "需要 Java 8，本机未检测到";
        }

        if (selection.Loader != ModLoader.Vanilla)
        {
            return "不能与模组加载器同时使用";
        }

        // 两者都靠 launchwrapper 的 tweakClass 挂载，v1 不合并两份版本 JSON
        if (selection.OptiFine is not null)
        {
            return "暂不支持与 OptiFine 同时安装";
        }

        return null;
    }

    public static bool IsForgeOptiFineConflict(string minecraftVersion) =>
        !IsBefore(minecraftVersion, 1, 13, 0) && !IsAfter(minecraftVersion, 1, 14, 3);

    /// <summary>
    /// 自动清掉与当前选择冲突的组件。选择新的主加载器时调用，
    /// 避免界面上留下“看起来选了、实际装不上”的组合。
    /// </summary>
    public static ComponentSelection Normalize(
        ComponentSelection selection,
        string minecraftVersion,
        bool hasJava8 = true)
    {
        var result = selection;

        if (GetUnavailableReason(ComponentKind.OptiFine, result, minecraftVersion, hasJava8) is not null)
        {
            result = result with { OptiFine = null, OptiFabric = null };
        }

        if (GetUnavailableReason(ComponentKind.LiteLoader, result, minecraftVersion, hasJava8) is not null)
        {
            result = result with { LiteLoader = null };
        }

        if (GetUnavailableReason(ComponentKind.FabricApi, result, minecraftVersion, hasJava8) is not null)
        {
            result = result with { FabricApi = null };
        }

        if (GetUnavailableReason(ComponentKind.OptiFabric, result, minecraftVersion, hasJava8) is not null)
        {
            result = result with { OptiFabric = null };
        }

        return result;
    }

    /// <summary>
    /// 按点分数字比较版本；非数字段按字符串比。用于 1.12.2 / 1.20.4 这类判断。
    /// <para>
    /// 缺的段按 0 处理：<c>1.13</c> 与 <c>1.13.0</c> 相等，否则「1.13 是否早于 1.13.0」
    /// 会得出「早于」，把 Forge 与 OptiFine 的冲突区间算错。
    /// </para>
    /// </summary>
    public static int CompareVersions(string left, string right)
    {
        var a = left.Split('.', '-', '_');
        var b = right.Split('.', '-', '_');
        var length = Math.Max(a.Length, b.Length);

        for (var i = 0; i < length; i++)
        {
            var leftRaw = i < a.Length ? a[i] : null;
            var rightRaw = i < b.Length ? b[i] : null;

            var result = CompareSegment(leftRaw, rightRaw);
            if (result != 0)
            {
                return result;
            }
        }

        return 0;
    }

    /// <summary>
    /// 比较单段。缺段（null）按 0 与数字比（1.13 == 1.13.0），
    /// 但与 pre/rc 这类后缀比时缺段的一方更新（1.13 > 1.13-pre1）。
    /// </summary>
    private static int CompareSegment(string? left, string? right)
    {
        var x = left is not null && int.TryParse(left, out var xn) ? xn : (int?)null;
        var y = right is not null && int.TryParse(right, out var yn) ? yn : (int?)null;

        if (left is null && right is null)
        {
            return 0;
        }

        if (left is null)
        {
            return y is not null ? 0.CompareTo(y.Value) : 1;
        }

        if (right is null)
        {
            return x is not null ? x.Value.CompareTo(0) : -1;
        }

        return (x, y) switch
        {
            (not null, not null) => x.Value.CompareTo(y.Value),
            (not null, null) => 1,
            (null, not null) => -1,
            _ => string.Compare(left, right, StringComparison.OrdinalIgnoreCase),
        };
    }

    private static bool IsAfter(string version, int major, int minor, int patch) =>
        CompareVersions(version, $"{major}.{minor}.{patch}") > 0;

    private static bool IsBefore(string version, int major, int minor, int patch) =>
        CompareVersions(version, $"{major}.{minor}.{patch}") < 0;
}
