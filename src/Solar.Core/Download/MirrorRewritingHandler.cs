using System.Net;

namespace Solar.Core.Download;

/// <summary>
/// 把 Mojang 官方下载地址重写到国内镜像。
/// <para>
/// 只重写实测可用的两个主机：库文件走 <c>/maven/</c>，资源走 <c>/assets/</c>。
/// 其余主机（如 piston-data 的版本 JSON、client.jar、log4j 配置）保持原样，
/// 因为镜像对这些路径的支持不完整，盲目重写反而会 404。
/// </para>
/// <para>
/// 镜像的 maven 端点会拦截没有 User-Agent 的请求（返回 403），
/// 所以这里统一补上 UA，而不是依赖调用方。
/// </para>
/// </summary>
public sealed class MirrorRewritingHandler(Uri? mirrorBase) : DelegatingHandler
{
    private const string LibrariesHost = "libraries.minecraft.net";
    private const string ResourcesHost = "resources.download.minecraft.net";

    /// <summary>镜像根地址，如 <c>https://bmclapi2.bangbang93.com</c>；为空时不重写。</summary>
    public Uri? MirrorBase { get; } = mirrorBase;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.RequestUri is { } uri)
        {
            request.RequestUri = Rewrite(uri);
        }

        if (!request.Headers.UserAgent.Any())
        {
            request.Headers.UserAgent.ParseAdd("SolarLauncher/0.1");
        }

        return base.SendAsync(request, ct);
    }

    /// <summary>按主机名重写单个地址；不需要重写时原样返回。</summary>
    public Uri Rewrite(Uri uri)
    {
        if (MirrorBase is null)
        {
            return uri;
        }

        var path = uri.AbsolutePath;

        if (uri.Host.Equals(LibrariesHost, StringComparison.OrdinalIgnoreCase))
        {
            // libraries.minecraft.net/a/b/c.jar → <mirror>/maven/a/b/c.jar
            return new Uri(MirrorBase, "/maven" + path);
        }

        if (uri.Host.Equals(ResourcesHost, StringComparison.OrdinalIgnoreCase))
        {
            // resources.download.minecraft.net/ab/hash → <mirror>/assets/ab/hash
            return new Uri(MirrorBase, "/assets" + path);
        }

        return uri;
    }

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

    /// <summary>新建一个带镜像重写的 HttpClient，供下载引擎使用。</summary>
    public static HttpClient CreateClient(Uri? mirrorBase)
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            MaxConnectionsPerServer = 64,
        };

        return new HttpClient(new MirrorRewritingHandler(mirrorBase) { InnerHandler = handler })
        {
            Timeout = TimeSpan.FromMinutes(5),
        };
    }
}
