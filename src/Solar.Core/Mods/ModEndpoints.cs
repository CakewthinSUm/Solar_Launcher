using Solar.Core.Settings;

namespace Solar.Core.Mods;

/// <summary>模组源的访问偏好。</summary>
public enum ModSourcePreference
{
    /// <summary>先直连官方，失败再走镜像。网络通畅时延迟最低。</summary>
    Auto = 0,

    /// <summary>先走镜像，失败再直连官方。</summary>
    MirrorFirst = 1,

    /// <summary>只走镜像。官方 API 不可达（例如 CurseForge 没有 Key）时最稳。</summary>
    MirrorOnly = 2,
}

/// <summary>
/// 把官方地址换算成候选地址列表，并按设置排出尝试顺序。
/// <para>
/// 和 <c>MirrorRewritingHandler</c> 分开：那个只认 Mojang 的两个主机，
/// 而且是在 HTTP 管道里静默改写、失败无法回退。模组源需要“这个地址不通就换下一个”
/// 的显式降级，所以在这里返回候选列表，由 <see cref="ModHttp"/> 逐个尝试。
/// </para>
/// <para>
/// 镜像路径规则（与 PCL 一致，已实测可用）：
/// <c>api.modrinth.com</c> → <c>{mirror}/modrinth</c>；
/// <c>api.curseforge.com</c> → <c>{mirror}/curseforge</c>；
/// <c>cdn.modrinth.com</c>、<c>*.forgecdn.net</c> → <c>{mirror}</c>（直接顶掉主机名）。
/// </para>
/// </summary>
public sealed class ModEndpoints(ISettingsService settings)
{
    private static readonly Uri ModrinthApi = new("https://api.modrinth.com");
    private static readonly Uri CurseForgeApi = new("https://api.curseforge.com");

    /// <summary>镜像根地址；未配置或格式非法时为 null。</summary>
    public Uri? Mirror => ParseMirror(settings.Current.ModMirrorUrl);

    public ModSourcePreference Preference => settings.Current.ModSourcePreference;

    /// <summary>用户自备的 CurseForge API Key；为空时官方 API 会 403。</summary>
    public string? CurseForgeApiKey =>
        string.IsNullOrWhiteSpace(settings.Current.CurseForgeApiKey)
            ? null
            : settings.Current.CurseForgeApiKey!.Trim();

    /// <summary>官方 CurseForge API 是否可用（需要 Key）。没有 Key 时不必白跑一趟。</summary>
    public bool CanUseCurseForgeOfficial => CurseForgeApiKey is not null;

    /// <summary>某个源的 API 根地址的候选列表，按偏好排序。第一个是首选。</summary>
    public IReadOnlyList<Uri> ApiCandidates(ModSource source)
    {
        var official = source == ModSource.Modrinth ? ModrinthApi : CurseForgeApi;

        // CurseForge 没有 Key 时官方必然 403，直接跳过
        var allowOfficial = source == ModSource.Modrinth || CanUseCurseForgeOfficial;

        return Order(official, allowOfficial);
    }

    /// <summary>
    /// 针对一条具体的官方请求 URL，给出可依次尝试的完整 URL。
    /// <para>
    /// 注意这里返回的是<b>整条 URL</b>（含 path 与 query），不是 API 根地址——
    /// 曾经把根地址当成请求地址发出去，结果打到了 <c>/curseforge/</c> 根路径上，
    /// 拿回一个 HTML 页面。
    /// </para>
    /// </summary>
    public IReadOnlyList<Uri> RequestCandidates(Uri official, ModSource source)
    {
        var allowOfficial = source == ModSource.Modrinth || CanUseCurseForgeOfficial;
        return Order(official, allowOfficial);
    }

    /// <summary>任意官方地址（API、CDN、图标）的候选列表。文件与图片不需要 API Key。</summary>
    public IReadOnlyList<Uri> Candidates(Uri official) => Order(official, allowOfficial: true);

    /// <summary>把单个地址换成镜像地址；镜像未配置时返回原地址。</summary>
    public Uri RewriteToMirror(Uri official)
    {
        if (Mirror is not { } mirror)
        {
            return official;
        }

        // 查询串必须一起带过去：搜索参数全在 query 里，丢了就等于每次都搜空
        var pathAndQuery = official.PathAndQuery;

        if (IsHost(official, "api.modrinth.com"))
        {
            return new Uri(mirror, "/modrinth" + pathAndQuery);
        }

        if (IsHost(official, "api.curseforge.com"))
        {
            return new Uri(mirror, "/curseforge" + pathAndQuery);
        }

        // cdn.modrinth.com、edge/media/mediafilez.forgecdn.net：
        // 镜像把文件直接挂在根路径下，原样保留 path 即可。
        if (IsHost(official, "cdn.modrinth.com") || official.Host.EndsWith(".forgecdn.net", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri(mirror, pathAndQuery);
        }

        return official;
    }

    private IReadOnlyList<Uri> Order(Uri official, bool allowOfficial)
    {
        var mirrored = Mirror is null ? official : RewriteToMirror(official);
        var hasMirror = mirrored != official;

        if (!hasMirror)
        {
            return [official];
        }

        return (Preference, allowOfficial) switch
        {
            (ModSourcePreference.MirrorOnly, _) => [mirrored],
            (ModSourcePreference.MirrorFirst, true) => [mirrored, official],
            (ModSourcePreference.MirrorFirst, false) => [mirrored],
            (_, true) => [official, mirrored],
            _ => [mirrored],
        };
    }

    private static bool IsHost(Uri uri, string host) =>
        uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase);

    /// <summary>把设置里的镜像地址解析成根地址；留空或格式非法时返回 null。</summary>
    public static Uri? ParseMirror(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return null;
        }

        return Uri.TryCreate(baseUrl.Trim().TrimEnd('/'), UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri
            : null;
    }
}
