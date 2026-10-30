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

    /// <summary>
    /// 库与版本文件的查找器：SolarShared 与游戏目录两处都查。
    /// 为空时只认 <see cref="AppPaths.Libraries"/> 与 <see cref="AppPaths.Versions"/>。
    /// </summary>
    public GameFileLocator? FileLocator { get; init; }

    public required string JavaPath { get; init; }

    /// <summary>所选 Java 的大版本；GC、编码参数要按版本区分。</summary>
    public int JavaMajorVersion { get; init; } = 8;
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

    /// <summary>
    /// 实际使用的堆上限（MB），由 <see cref="LaunchContextFactory"/> 结合设置里的
    /// 分配方式与实例级覆盖算好。这里不再关心它从哪来。
    /// </summary>
    public int MaxMemoryMb { get; init; } = 4096;

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

        // 平台与路径参数放最前，版本 JSON 里的同名参数会覆盖它们
        result.Add($"-Djava.library.path={context.NativesDirectory}");
        result.Add("-Dminecraft.launcher.brand=SolarLauncher");
        result.Add("-Dminecraft.launcher.version=0.1.0");
        result.Add("-cp");
        result.Add(BuildClassPath(context));

        if (context.Version.Detail.Arguments is { } arguments)
        {
            result.AddRange(Expand(arguments.Jvm, context));
        }

        // 版本 JSON 自带的 -Xmx 不一定合适，去掉后统一用启动器算出来的值
        result.RemoveAll(IsMemoryArgument);
        result.Add($"-Xmx{context.MaxMemoryMb}M");

        AddGcArguments(result, context.JavaMajorVersion);
        AddEncodingArguments(result, context.JavaMajorVersion);

        // log4j 漏洞防御，与 PCL 一致
        result.Add("-Dlog4j2.formatMsgNoLookups=true");

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

        // 重复的 -cp / -Djava.library.path 会让 JVM 取最后一个，
        // 内容相同则去重，内容不同保留后者（与 PCL 的 JVM 参数策略一致）
        return Deduplicate(result, isJvm: true);
    }

    private static bool IsMemoryArgument(string argument) =>
        argument.StartsWith("-Xmx", StringComparison.Ordinal)
        || argument.StartsWith("-Xms", StringComparison.Ordinal);

    /// <summary>
    /// GC 参数按 Java 版本选择，与 PCL 的默认策略一致：
    /// Java 21+ 分代 ZGC，15–20 非分代 ZGC，更早用调优过的 G1GC。
    /// 用户或版本已经指定 GC 时不覆盖。
    /// </summary>
    private static void AddGcArguments(List<string> args, int javaMajor)
    {
        var specified = args.Any(a =>
            a.StartsWith("-XX:", StringComparison.Ordinal)
            && (a.Contains("GC", StringComparison.Ordinal) || a.Contains("ZGenerational", StringComparison.Ordinal)));

        if (specified)
        {
            return;
        }

        args.Add("-XX:+UnlockExperimentalVMOptions");

        if (javaMajor >= 24)
        {
            args.Add("-XX:+UseCompactObjectHeaders");
        }

        if (javaMajor >= 21)
        {
            args.Add("-XX:+UseZGC");
            if (javaMajor <= 22)
            {
                // Java 23 起分代 ZGC 默认开启，再加参数会报错
                args.Add("-XX:+ZGenerational");
            }
        }
        else if (javaMajor >= 15)
        {
            args.Add("-XX:+UseZGC");
        }
        else
        {
            args.Add("-XX:+UseG1GC");
            args.Add("-XX:G1NewSizePercent=20");
            args.Add("-XX:G1ReservePercent=20");
            args.Add("-XX:G1HeapRegionSize=32M");
            args.Add("-XX:MaxGCPauseMillis=50");
            if (javaMajor <= 7)
            {
                args.Add("-XX:MaxPermSize=512m");
            }
        }
    }

    /// <summary>
    /// 固定游戏标准输出的编码为 UTF-8：日志按 UTF-8 写盘，
    /// 否则中文路径、中文模组日志会变成乱码（PCL 也是这么处理的）。
    /// </summary>
    private static void AddEncodingArguments(List<string> args, int javaMajor)
    {
        var stdout = javaMajor >= 19 ? "stdout.encoding" : "sun.stdout.encoding";
        var stderr = javaMajor >= 19 ? "stderr.encoding" : "sun.stderr.encoding";

        if (!args.Any(a => a.StartsWith($"-D{stdout}=", StringComparison.Ordinal)))
        {
            args.Add($"-D{stdout}=UTF-8");
        }

        if (!args.Any(a => a.StartsWith($"-D{stderr}=", StringComparison.Ordinal)))
        {
            args.Add($"-D{stderr}=UTF-8");
        }
    }

    /// <summary>
    /// 参数去重，规则与 PCL 的 <c>DeduplicateJavaArguments</c> 一致：
    /// 单参数完全相同的只留一个；键值对在 JVM 参数里相同才去重，
    /// 游戏参数里后者覆盖前者（重复的 --width / --uuid 会让游戏整体失效），
    /// 但 --tweakClass 例外，OptiFine 需要保留多条。
    /// </summary>
    private static List<string> Deduplicate(List<string> args, bool isJvm)
    {
        var result = new List<string>();
        var i = 0;

        while (i < args.Count)
        {
            var key = args[i];

            if (!IsKey(key) || i + 1 >= args.Count || IsKey(args[i + 1]) && !IsNegativeNumber(args[i + 1]))
            {
                i++;
                if (!result.Contains(key))
                {
                    result.Add(key);
                }

                continue;
            }

            var value = args[i + 1];
            i += 2;

            var duplicate = false;
            for (var j = 0; j + 1 < result.Count; j++)
            {
                if (result[j] != key)
                {
                    continue;
                }

                if (!isJvm && key != "--tweakClass")
                {
                    // 游戏参数：后出现的值覆盖先出现的
                    result[j + 1] = value;
                    duplicate = true;
                    break;
                }

                // JVM 参数只在键和值都相同时去重；同名不同值保留两条，交给 JVM 取后者
                if (result[j + 1] == value)
                {
                    duplicate = true;
                    break;
                }
            }

            if (!duplicate)
            {
                result.Add(key);
                result.Add(value);
            }
        }

        return result;
    }

    private static bool IsKey(string argument) => argument.StartsWith('-');

    /// <summary><c>-50</c> 这类是参数值（如 <c>-xPos -50</c>），不是新的键。</summary>
    private static bool IsNegativeNumber(string argument) =>
        argument.Length > 1 && argument[1..].All(char.IsDigit);

    /// <summary>构建游戏参数（即 MainClass 之后的参数）。</summary>
    public IReadOnlyList<string> BuildGameArguments(LaunchContext context)
    {
        var result = new List<string>();

        // 1.13 以前没有 arguments 字段，用的是空格分隔的字符串模板。
        // 这段要单独处理而不能用 else 分支：OptiFine / LiteLoader 的 profile 是
        // 「旧版原版参数 + arguments.game 里的 tweakClass」这种混合形态，
        // 合并后两个字段会同时存在，漏掉任何一边都会导致启动参数不完整。
        if (!string.IsNullOrWhiteSpace(context.Version.Detail.MinecraftArguments))
        {
            result.AddRange(SplitArguments(context.Version.Detail.MinecraftArguments)
                .Select(a => Substitute(a, context)));
        }

        if (context.Version.Detail.Arguments is { } arguments)
        {
            result.AddRange(Expand(arguments.Game, context));
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

        // 游戏参数里重复的键会让两条都失效（例如两个 --uuid），后者覆盖前者
        return Deduplicate(result, isJvm: false);
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
                entries.Add(ResolveLibraryPath(context, download.RelativePath));
                continue;
            }

            // OptiFine 安装器写出的库只有坐标没有 url（文件由它自己放进 libraries），
            // 这类按坐标推路径，文件存在就进 classpath。
            if (library.GetMavenRelativePath() is { } relative)
            {
                var path = ResolveLibraryPath(context, relative);
                if (File.Exists(path))
                {
                    entries.Add(path);
                }
            }
        }

        // 加载器版本的 JSON 一般不指向自己的 jar，真正要进 classpath 的是父版本的 jar。
        // 解析链的最后一个就是根版本（原版）。
        var jarVersionId = context.Version.Chain.Count > 0
            ? context.Version.Chain[^1].Id
            : context.Version.Id;

        if (ResolveVersionJar(context, jarVersionId) is { } jarPath)
        {
            entries.Add(jarPath);
        }

        return string.Join(Path.PathSeparator, entries);
    }

    private static string ResolveLibraryPath(LaunchContext context, string relativePath) =>
        context.FileLocator?.ResolveLibrary(relativePath)
        ?? Path.Combine(context.Paths.Libraries, relativePath);

    /// <summary>版本 jar 的位置；没有查找器时按 Solar 自己的目录拼。</summary>
    private static string? ResolveVersionJar(LaunchContext context, string versionId) =>
        context.FileLocator is { } locator
            ? locator.FindVersionJar(versionId)
            : Path.Combine(context.Paths.Versions, versionId, versionId + ".jar");

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
        // 实例设置了自定义标题时覆盖 version_name：游戏的窗口标题取自 --version 参数的值
        "version_name" => string.IsNullOrWhiteSpace(context.Instance.CustomWindowTitle)
            ? context.Version.Id
            : context.Instance.CustomWindowTitle.Trim(),
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
