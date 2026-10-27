using System.Text;
using Solar.Core.Instances;
using Solar.Core.Minecraft;
using Solar.Core.Models;
using Solar.Core.Paths;
using Solar.Core.Settings;

namespace Solar.Core.Launch;

/// <summary>启动一个实例所需的全部输入，由启动器服务组装后交给参数构建器。</summary>
public sealed class LaunchContext
{
    public required GameInstance Instance { get; init; }
    public required ResolvedVersion Version { get; init; }
    public required AppPaths Paths { get; init; }
    public required LauncherSettings Settings { get; init; }

    public required string JavaPath { get; init; }
    public required string PlayerName { get; init; }
    public required string Uuid { get; init; }
    public required string AccessToken { get; init; }

    /// <summary>Xbox 用户 ID，1.21+ 的 <c>${auth_xuid}</c> 需要它。正版登录才有。</summary>
    public string Xuid { get; init; } = "";

    /// <summary>微软登录固定为 <c>msa</c>。</summary>
    public string UserType { get; init; } = "msa";

    public string GameDirectory { get; init; } = "";
    public string NativesDirectory { get; init; } = "";
    public string AssetsDirectory { get; init; } = "";
    public string LogConfigPath { get; init; } = "";

    /// <summary>要传给游戏的分辨率；为空时由游戏自己决定。</summary>
    public (int Width, int Height)? Resolution { get; init; }

    public string VersionType => Version.Detail.Type ?? "release";

    public RuleContext RuleContext { get; init; } = new();
}

/// <summary>
/// 把版本 JSON 里的参数模板展开成最终命令行。
/// 规则求值、占位符替换、带空格参数的引号处理都在这里，与进程启动无关，便于单测。
/// </summary>
public sealed class ArgumentBuilder
{
    /// <summary>构建 JVM 参数（不含 java.exe 本身）。</summary>
    public IReadOnlyList<string> BuildJvmArguments(LaunchContext context)
    {
        var result = new List<string>();

        // 内存与平台相关参数放在最前面，用户/版本参数可以覆盖它们
        result.Add($"-Xmx{context.Instance.MaxMemoryMb ?? context.Settings.MaxMemoryMb}M");
        result.Add($"-Djava.library.path={context.NativesDirectory}");
        result.Add("-Dminecraft.launcher.brand=SolarLauncher");
        result.Add("-Dminecraft.launcher.version=0.1.0");
        result.Add("-cp");
        result.Add(BuildClassPath(context));

        if (context.Version.Detail.Arguments is { } arguments)
        {
            result.AddRange(Expand(arguments.Jvm, context));
        }

        if (!string.IsNullOrWhiteSpace(context.Settings.ExtraJvmArguments))
        {
            result.AddRange(SplitArguments(context.Settings.ExtraJvmArguments));
        }

        // log4j 配置必须排在版本参数之后，否则会被覆盖
        if (!string.IsNullOrEmpty(context.LogConfigPath) && File.Exists(context.LogConfigPath))
        {
            result.Add($"-Dlog4j.configurationFile={context.LogConfigPath}");
        }

        result.Add(context.Version.Detail.MainClass
                   ?? throw new InvalidOperationException($"版本 {context.Version.Id} 缺少 MainClass。"));

        return result;
    }

    /// <summary>构建游戏参数（即 MainClass 之后的参数）。</summary>
    public IReadOnlyList<string> BuildGameArguments(LaunchContext context)
    {
        var result = new List<string>();

        if (context.Version.Detail.Arguments is { } arguments)
        {
            result.AddRange(Expand(arguments.Game, context));
        }
        else if (!string.IsNullOrWhiteSpace(context.Version.Detail.MinecraftArguments))
        {
            // 1.13 以前没有 arguments 字段，用的是空格分隔的字符串模板
            result.AddRange(SplitArguments(context.Version.Detail.MinecraftArguments));
            result = result.Select(a => Substitute(a, context)).ToList();
        }

        // 老版本模板里没有分辨率参数，需要补上
        if (context.Resolution is { } resolution && !result.Contains("--width"))
        {
            result.AddRange(["--width", resolution.Width.ToString(), "--height", resolution.Height.ToString()]);
        }

        if (!string.IsNullOrWhiteSpace(context.Settings.ExtraGameArguments))
        {
            result.AddRange(SplitArguments(context.Settings.ExtraGameArguments));
        }

        return result;
    }

    /// <summary>把库列表拼成 classpath，只包含有实际文件的库。</summary>
    public static string BuildClassPath(LaunchContext context)
    {
        var entries = new List<string>();

        foreach (var library in context.Version.GetEffectiveLibraries(context.RuleContext))
        {
            // natives 是解压到 java.library.path 的，不能进 classpath
            var coordinate = library.ParseName();
            if (coordinate.Classifier?.StartsWith("natives", StringComparison.OrdinalIgnoreCase) == true)
            {
                continue;
            }

            // 与下载端共用同一套路径推导，避免两边不一致
            if (library.ResolveDownload() is { } download)
            {
                entries.Add(Path.Combine(context.Paths.Libraries, download.RelativePath));
            }
        }

        entries.Add(Path.Combine(context.Paths.Versions, context.Version.Id, context.Version.Id + ".jar"));

        return string.Join(Path.PathSeparator, entries);
    }

    private static IEnumerable<string> Expand(IEnumerable<ArgumentEntry> entries, LaunchContext context)
    {
        foreach (var entry in entries)
        {
            if (!RuleEvaluator.IsAllowed(entry.Rules, context.RuleContext))
            {
                continue;
            }

            foreach (var value in entry.Enumerate())
            {
                yield return Substitute(value, context);
            }
        }
    }

    /// <summary>替换 ${...} 占位符。未知占位符原样保留，便于排查。</summary>
    public static string Substitute(string value, LaunchContext context)
    {
        if (!value.Contains("${", StringComparison.Ordinal))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        var index = 0;

        while (index < value.Length)
        {
            var start = value.IndexOf("${", index, StringComparison.Ordinal);
            if (start < 0)
            {
                builder.Append(value, index, value.Length - index);
                break;
            }

            var end = value.IndexOf('}', start);
            if (end < 0)
            {
                builder.Append(value, index, value.Length - index);
                break;
            }

            builder.Append(value, index, start - index);
            var key = value[(start + 2)..end];
            builder.Append(Resolve(key, context) ?? value[start..(end + 1)]);
            index = end + 1;
        }

        return builder.ToString();
    }

    private static string? Resolve(string key, LaunchContext context) => key switch
    {
        "auth_player_name" => context.PlayerName,
        "auth_uuid" => context.Uuid,
        "auth_access_token" => context.AccessToken,
        "auth_session" => context.AccessToken,

        // 1.21+ 新增。必须始终能解析：若原样保留 ${...}，
        // 其后的参数会整体错位，游戏启动时解析失败。
        "clientid" => "",
        "auth_xuid" => context.Xuid,

        "user_type" => context.UserType,
        "user_properties" => "{}",
        "version_name" => context.Version.Id,
        "version_type" => context.VersionType,
        "game_directory" => context.GameDirectory,
        "assets_root" => context.AssetsDirectory,
        "game_assets" => context.AssetsDirectory,
        "assets_index_name" => context.Version.Detail.AssetIndex?.Id
                               ?? context.Version.Detail.Assets
                               ?? "legacy",
        "natives_directory" => context.NativesDirectory,
        "launcher_name" => "SolarLauncher",
        "launcher_version" => "0.1.0",
        "classpath" => BuildClassPath(context),
        "resolution_width" => context.Resolution?.Width.ToString(),
        "resolution_height" => context.Resolution?.Height.ToString(),
        "library_directory" => context.Paths.Libraries,
        "classpath_separator" => Path.PathSeparator.ToString(),

        // 快速加入功能（1.20+）。我们暂不支持，替换为空串；
        // 这些参数只有在对应 feature 规则命中时才会出现，正常不会走到这里。
        "quickPlayPath" or "quickPlaySingleplayer" or "quickPlayMultiplayer" or "quickPlayRealms" => "",

        _ => null,
    };

    /// <summary>
    /// 按空格切分参数字符串，支持双引号包裹。
    /// 用于 <c>minecraftArguments</c>（1.13 前）和用户自定义的额外参数。
    /// </summary>
    public static IReadOnlyList<string> SplitArguments(string input)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(input))
        {
            return result;
        }

        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var ch in input)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(ch);
            }
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result;
    }
}
