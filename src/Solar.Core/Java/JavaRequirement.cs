using Solar.Core.Instances;
using Solar.Core.Minecraft;

namespace Solar.Core.Java;

/// <summary>
/// 启动某个版本对 Java 大版本的要求。
/// <para>
/// 参照 PCL 的 <c>GetJavaRequirement</c>：原版按版本号给出下限，
/// 模组加载器与 OptiFine 再叠加各自的上下限。超出范围不会直接崩溃，
/// 但会出现难以定位的问题（Forge 的反射、Fabric 的 Mixin、老版本 OptiFine），
/// 所以这里宁可提前拦住，也不要拿不合适的 Java 硬启动。
/// </para>
/// </summary>
public sealed record JavaRequirement(int MinMajor, int? MaxMajor = null, string? RecommendedComponent = null)
{
    public bool IsSatisfiedBy(JavaRuntime runtime) =>
        runtime.MajorVersion >= MinMajor && (MaxMajor is not { } max || runtime.MajorVersion <= max);

    public string Describe() => MaxMajor switch
    {
        null => $"Java {MinMajor} 或更高",
        var max when max == MinMajor => $"Java {MinMajor}",
        var max => $"Java {MinMajor}–{max}",
    };
}

/// <summary>从实例与已解析的版本推导 Java 要求。</summary>
public static class JavaRequirements
{
    public static JavaRequirement For(GameInstance instance, ResolvedVersion version)
    {
        var min = 8;
        int? max = null;

        var mc = ParseMinecraftVersion(instance.MinecraftVersion) ?? ParseMinecraftVersion(version.Id);
        var vanillaMinor = mc is { Major: 1 } ? mc.Value.Minor : 0;
        var vanillaPatch = mc is { Major: 1 } ? mc.Value.Patch : 0;

        // 原版：按版本号给下限
        if (mc is { Major: 1 })
        {
            if (vanillaMinor > 20 || (vanillaMinor == 20 && vanillaPatch >= 5))
            {
                min = Math.Max(min, 21); // 1.20.5+（24w14a+）
            }
            else if (vanillaMinor >= 18)
            {
                min = Math.Max(min, 17);
            }
            else if (vanillaMinor == 17)
            {
                min = Math.Max(min, 16);
            }
            else if (vanillaMinor >= 1 && vanillaMinor <= 5)
            {
                max = 8; // 1.5.2- 最高 Java 8
            }
        }

        // Mojang 在版本 JSON 里明确要求的最低版本（26.x 是 25）
        if (version.Detail.JavaVersion?.MajorVersion is { } required && required >= 22)
        {
            min = Math.Max(min, required);
        }

        // OptiFine：老版本的反射实现与新 Java 不兼容
        if (!string.IsNullOrEmpty(instance.OptiFineVersion) && mc is { Major: 1 })
        {
            if (vanillaMinor >= 8 && vanillaMinor <= 11)
            {
                min = Math.Max(min, 8);
                max = 8; // 1.8–1.11 必须恰好 Java 8
            }
            else if (vanillaMinor == 12)
            {
                max = 8;
            }
            else if (vanillaMinor < 7)
            {
                max = 8;
            }
        }

        // LiteLoader 只支持 Java 8
        if (!string.IsNullOrEmpty(instance.LiteLoaderVersion))
        {
            max = 8;
        }

        switch (instance.Loader)
        {
            case ModLoader.Forge:
                ApplyForge(instance, mc, ref min, ref max);
                break;

            case ModLoader.NeoForge:
                // 1.20.1 与 20.2.62-beta 之前的 1.20.2 最高 Java 21
                if (mc is { Major: 1, Minor: 20 } && vanillaPatch <= 1)
                {
                    max = Math.Min(max ?? int.MaxValue, 21);
                }

                break;

            case ModLoader.Fabric:
            case ModLoader.Quilt:
                // 1.18+ 至少 Java 17
                if (mc is { Major: 1 } && vanillaMinor >= 18)
                {
                    min = Math.Max(min, 17);
                }

                // Loader 0.16.x 及更早的 Mixin/ASM 不兼容 Java 25
                if (ParseVersion(instance.LoaderVersion) is { } fabric && fabric < new Version(0, 17, 0))
                {
                    max = Math.Min(max ?? int.MaxValue, 24);
                }

                break;
        }

        return new JavaRequirement(min, max, version.Detail.JavaVersion?.Component);
    }

    private static void ApplyForge(GameInstance instance, MinecraftVersion? mc, ref int min, ref int? max)
    {
        if (mc is not { Major: 1 })
        {
            return;
        }

        var forge = ParseVersion(instance.LoaderVersion);
        var minor = mc.Value.Minor;

        if (minor <= 12)
        {
            max = 8; // ≤1.12：Java 8
            return;
        }

        if (minor <= 14)
        {
            min = Math.Max(min, 8);
            max = Math.Min(max ?? int.MaxValue, 10);
            return;
        }

        if (minor == 15)
        {
            min = Math.Max(min, 8);
            max = Math.Min(max ?? int.MaxValue, 15);
            return;
        }

        if (forge is null)
        {
            return;
        }

        if (minor == 16 && forge >= new Version(36, 2, 26) && forge < new Version(37, 0, 0))
        {
            max = Math.Min(max ?? int.MaxValue, 23); // 1.16.5，Forge 36.2.26+
        }
        else if (minor == 17 && forge >= new Version(37, 0, 0) && forge <= new Version(37, 0, 79))
        {
            max = Math.Min(max ?? int.MaxValue, 16); // 1.17.1，Forge 37.0.0–37.0.79
        }
        else if ((minor == 20 || (minor == 19 && mc.Value.Patch >= 4))
                 && forge >= new Version(45, 0, 66) && forge <= new Version(47, 4, 8))
        {
            max = Math.Min(max ?? int.MaxValue, 21); // 1.19.4–1.20.1
        }
    }

    /// <summary>解析 <c>1.20.1</c>、<c>26.2</c> 这类版本号；快照（23w31a）返回 null。</summary>
    public static MinecraftVersion? ParseMinecraftVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var parts = text.Trim().Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor))
        {
            return null;
        }

        var patch = parts.Length >= 3 && int.TryParse(parts[2], out var p) ? p : 0;
        return new MinecraftVersion(major, minor, patch);
    }

    /// <summary>解析加载器版本号（<c>0.16.9</c>、<c>47.2.0</c>）；失败返回 null。</summary>
    private static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var numeric = new string(text.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        return Version.TryParse(numeric.TrimEnd('.'), out var version) ? version : null;
    }
}

public readonly record struct MinecraftVersion(int Major, int Minor, int Patch);
