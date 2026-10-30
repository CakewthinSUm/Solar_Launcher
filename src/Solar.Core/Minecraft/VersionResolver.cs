using System.Text.Json;
using Microsoft.Extensions.Logging;
using Solar.Core.Json;
using Solar.Core.Models;
using Solar.Core.Paths;

namespace Solar.Core.Minecraft;

/// <summary>已解析（含 inheritsFrom 合并）的完整版本信息。</summary>
public sealed class ResolvedVersion
{
    public required VersionDetail Detail { get; init; }
    public required IReadOnlyList<VersionDetail> Chain { get; init; }

    public string Id => Detail.Id;

    /// <summary>合并后的库列表（父版本在前）。</summary>
    public IReadOnlyList<Library> Libraries => Detail.Libraries;

    /// <summary>
    /// 按平台与架构过滤后、需要使用的库。
    /// 架构过滤是必须的：natives 的三个变体（windows / -arm64 / -x86）
    /// 在 rules 里都只写 <c>allow windows</c>，只能靠分类符后缀区分。
    /// </summary>
    public IEnumerable<Library> GetEffectiveLibraries(RuleContext context) =>
        Detail.Libraries
            .Where(l => RuleEvaluator.IsAllowed(l.Rules, context))
            .Where(l => l.MatchesArchitecture(context.Arch));
}

public interface IVersionResolver
{
    Task<ResolvedVersion> ResolveAsync(string versionId, CancellationToken ct = default);
    Task<VersionDetail> DownloadVersionJsonAsync(VersionSummary summary, CancellationToken ct = default);
}

public sealed class VersionResolver(
    HttpClient http,
    AppPaths paths,
    GameFileLocator locator,
    IVersionManifestService manifestService,
    ILogger<VersionResolver> logger) : IVersionResolver
{
    /// <summary>新下载的落点：Solar 自己的 versions 目录。</summary>
    public string GetVersionDirectory(string versionId) => Path.Combine(paths.Versions, versionId);
    public string GetVersionJsonPath(string versionId) => Path.Combine(GetVersionDirectory(versionId), versionId + ".json");

    /// <summary>
    /// 已存在的版本 jar。加载器版本的 JSON 通常没有自己的 jar，
    /// 真正要进 classpath 的是父版本（原版）的 jar，所以找不到时返回 null。
    /// </summary>
    public string? GetVersionJarPath(string versionId) => locator.FindVersionJar(versionId);

    public async Task<ResolvedVersion> ResolveAsync(string versionId, CancellationToken ct = default)
    {
        var chain = new List<VersionDetail>();
        var current = await LoadOrDownloadAsync(versionId, ct).ConfigureAwait(false);
        chain.Add(current);

        var guard = 0;
        while (!string.IsNullOrWhiteSpace(current.InheritsFrom) && guard++ < 16)
        {
            current = await LoadOrDownloadAsync(current.InheritsFrom!, ct).ConfigureAwait(false);
            chain.Add(current);
        }

        var merged = Merge(chain);
        return new ResolvedVersion { Detail = merged, Chain = chain };
    }

    /// <summary>
    /// 子版本覆盖父版本的同名字段，库列表按顺序拼接（父在前），
    /// 与官方启动器及主流第三方启动器一致。
    /// </summary>
    public static VersionDetail Merge(IReadOnlyList<VersionDetail> chain)
    {
        // chain[0] 是最具体的版本（子），最后一个是根版本。
        var result = new VersionDetail
        {
            Id = chain[0].Id,
            Type = chain[0].Type,
            MainClass = FirstNonNull(chain, v => v.MainClass),
            Assets = FirstNonNull(chain, v => v.Assets),
            AssetIndex = FirstNonNull(chain, v => v.AssetIndex),
            ComplianceLevel = FirstNonNull(chain, v => v.ComplianceLevel),
            JavaVersion = FirstNonNull(chain, v => v.JavaVersion),
            ReleaseTime = FirstNonNull(chain, v => v.ReleaseTime),
            Time = FirstNonNull(chain, v => v.Time),
        };

        // 去重键是 group:artifact[:classifier]，不含版本号。
        // 同一个 artifact 的不同版本（如原版要 asm 9.6、Fabric 要 asm 9.10.1）
        // 必须只保留一个，否则 classpath 上会出现重复类，
        // Fabric 的 Knot 会直接报 "duplicate ASM classes found on classpath" 拒绝启动。
        //
        // 按"父在前"的原始顺序输出，但子版本（chain[0]）声明的库要覆盖父版本的同名项，
        // 因此先收集父版本，遇到子版本时原地替换。
        //
        // 注意：只跨版本去重，同一版本里同名的库必须都保留。
        // 1.12.2 的 JSON 同时列了 org.lwjgl.lwjgl:lwjgl 的 Windows 版（2.9.4）
        // 和 macOS 版（2.9.2），后一条会覆盖前一条，Windows 版被规则过滤掉后
        // classpath 里就没有 LWJGL 2 了，游戏直接 NoClassDefFoundError。
        var libraries = new List<Library>();
        var levelOf = new List<int>();
        var indexByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var i = chain.Count - 1; i >= 0; i--)
        {
            foreach (var library in chain[i].Libraries)
            {
                var key = GetArtifactKey(library.Name);

                if (indexByKey.TryGetValue(key, out var existing) && levelOf[existing] != i)
                {
                    // 已有条目来自父版本：子版本覆盖它
                    libraries[existing] = library;
                    levelOf[existing] = i;
                    continue;
                }

                if (indexByKey.ContainsKey(key))
                {
                    // 同一版本内的同名库是不同平台的变体，全部保留，交给规则筛选
                    libraries.Add(library);
                    levelOf.Add(i);
                    continue;
                }

                indexByKey[key] = libraries.Count;
                libraries.Add(library);
                levelOf.Add(i);
            }
        }

        result.Libraries = libraries;

        var gameArgs = new List<ArgumentEntry>();
        var jvmArgs = new List<ArgumentEntry>();
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            if (chain[i].Arguments is { } args)
            {
                gameArgs.AddRange(args.Game);
                jvmArgs.AddRange(args.Jvm);
            }
        }

        result.Arguments = new Arguments { Game = gameArgs, Jvm = jvmArgs };
        result.Downloads = chain.Select(v => v.Downloads).FirstOrDefault(d => d is { Count: > 0 });
        result.MinecraftArguments = FirstNonNull(chain, v => v.MinecraftArguments);
        return result;
    }

    /// <summary>
    /// 库的去重键：<c>group:artifact[:classifier]</c>，不含版本号。
    /// 保留分类符是必要的——<c>lwjgl</c> 与 <c>lwjgl:natives-windows</c> 是两个不同的库。
    /// </summary>
    public static string GetArtifactKey(string mavenName)
    {
        var parts = mavenName.Split(':');
        if (parts.Length < 2)
        {
            return mavenName;
        }

        return parts.Length >= 4
            ? $"{parts[0]}:{parts[1]}:{parts[3]}"
            : $"{parts[0]}:{parts[1]}";
    }

    private static T? FirstNonNull<T>(IReadOnlyList<VersionDetail> chain, Func<VersionDetail, T?> selector)
    {
        foreach (var item in chain)
        {
            if (selector(item) is { } value)
            {
                return value;
            }
        }

        return default;
    }

    private async Task<VersionDetail> LoadOrDownloadAsync(string versionId, CancellationToken ct)
    {
        // 先找已存在的 JSON：Solar 自己下过的那份，或接管过来的游戏目录里的那份
        var existing = locator.FindVersionJson(versionId);
        if (existing is not null)
        {
            var cached = await JsonFile.ReadAsync<VersionDetail>(existing, ct).ConfigureAwait(false);
            if (cached is not null)
            {
                return cached;
            }
        }

        // 通过清单服务获取，它会先看缓存再联网，避免"没打开过版本页就无法启动"
        var manifest = await manifestService.GetManifestAsync(ct: ct).ConfigureAwait(false);

        var summary = manifest.Versions.FirstOrDefault(v => v.Id == versionId)
                      ?? throw new InvalidOperationException($"版本清单中找不到 {versionId}。");

        return await DownloadVersionJsonAsync(summary, ct).ConfigureAwait(false);
    }

    public async Task<VersionDetail> DownloadVersionJsonAsync(VersionSummary summary, CancellationToken ct = default)
    {
        logger.LogInformation("下载版本 JSON: {Id}", summary.Id);
        var json = await http.GetStringAsync(summary.Url, ct).ConfigureAwait(false);
        var detail = JsonSerializer.Deserialize<VersionDetail>(json, SolarJson.Options)
                     ?? throw new InvalidOperationException($"版本 {summary.Id} 的 JSON 无效。");

        // 原样保存：Forge/OptiFine 安装器等外部工具按 camelCase 读这份文件
        await JsonFile.WriteRawAsync(GetVersionJsonPath(summary.Id), json, ct).ConfigureAwait(false);
        return detail;
    }
}
