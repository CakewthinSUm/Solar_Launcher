using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Solar.Core.Mods;

/// <summary>
/// 带“换源重试”的 JSON 请求。
/// <para>
/// 模组源有两个特性决定了不能直接用 <see cref="HttpClient.GetFromJsonAsync{TValue}(string, CancellationToken)"/>：
/// 一是国内直连官方经常超时，需要自动降级到镜像；二是镜像缺字段/缺接口时返回的是
/// 404 或格式不对的内容，必须能被识别成“这个源不行”而不是当成业务错误。
/// </para>
/// </summary>
public sealed class ModHttp(HttpClient http, ModEndpoints endpoints, ILogger<ModHttp> logger)
{
    /// <summary>
    /// 单个候选地址的超时。搜索是交互式的，不能等太久；
    /// 但国内连官方 API 经常是「能连上、迟迟不回」，10 秒会经常把本来能出结果的源判死，
    /// 所以给 15 秒。两个源是并行请求的，最坏等待时间就是这一个超时，不会翻倍。
    /// </summary>
    private static readonly TimeSpan PerAttemptTimeout = TimeSpan.FromSeconds(15);

    public async Task<T> GetJsonAsync<T>(
        Uri official,
        JsonSerializerOptions options,
        CancellationToken ct) where T : class
    {
        var errors = new List<Exception>();

        foreach (var candidate in endpoints.Candidates(official))
        {
            try
            {
                return await GetJsonFromAsync<T>(candidate, options, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogDebug(ex, "请求失败，尝试下一个源: {Uri}", candidate);
                errors.Add(new InvalidOperationException($"{candidate.Host}: {ex.Message}", ex));
            }
        }

        throw new ModSourceException(official, errors);
    }

    /// <summary>
    /// 带 CurseForge API Key 的请求。Key 只加在官方地址上——镜像自己持有 Key，
    /// 把用户的 Key 发给第三方镜像既没必要也不安全。
    /// </summary>
    public async Task<T> GetCurseForgeJsonAsync<T>(Uri official, JsonSerializerOptions options, CancellationToken ct)
        where T : class
    {
        var errors = new List<Exception>();

        foreach (var candidate in endpoints.RequestCandidates(official, ModSource.CurseForge))
        {
            try
            {
                return await GetJsonFromAsync<T>(candidate, options, ct, CurseForgeKeyFor(candidate))
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogDebug(ex, "CurseForge 请求失败，尝试下一个源: {Uri}", candidate);
                errors.Add(new InvalidOperationException($"{candidate.Host}: {ex.Message}", ex));
            }
        }

        throw new ModSourceException(official, errors);
    }

    /// <summary>POST JSON 并读回 JSON。CurseForge 的批量接口是 POST。</summary>
    public async Task<T> PostCurseForgeJsonAsync<T>(
        Uri official,
        object body,
        JsonSerializerOptions options,
        CancellationToken ct) where T : class
    {
        var errors = new List<Exception>();

        foreach (var candidate in endpoints.RequestCandidates(official, ModSource.CurseForge))
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(PerAttemptTimeout);

                using var request = new HttpRequestMessage(HttpMethod.Post, candidate)
                {
                    Content = JsonContent.Create(body, options: options),
                };
                AddCurseForgeKey(request, candidate);

                using var response = await http.SendAsync(request, cts.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var result = await response.Content
                    .ReadFromJsonAsync<T>(options, cts.Token)
                    .ConfigureAwait(false);

                return result ?? throw new InvalidOperationException("响应内容为空。");
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogDebug(ex, "CurseForge POST 失败，尝试下一个源: {Uri}", candidate);
                errors.Add(new InvalidOperationException($"{candidate.Host}: {ex.Message}", ex));
            }
        }

        throw new ModSourceException(official, errors);
    }

    /// <summary>只探测一个地址是否可下载，用于安装前的连通性判断。返回第一个可用的地址。</summary>
    public Uri? PickReachableDownloadUrl(Uri official)
    {
        // 下载走单独的 HttpClient，这里只决定顺序，不做真实探测——
        // 真实探测要多一次往返，而下载失败本来就会重试。
        var candidates = endpoints.Candidates(official);
        return candidates.Count > 0 ? candidates[0] : null;
    }

    private async Task<T> GetJsonFromAsync<T>(
        Uri uri,
        JsonSerializerOptions options,
        CancellationToken ct,
        string? apiKey = null) where T : class
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(PerAttemptTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (apiKey is not null)
        {
            request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        }

        using var response = await http.SendAsync(request, cts.Token).ConfigureAwait(false);

        // 403/404 在镜像上通常意味着“这个接口没实现”，换个源可能就好了
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}", null, response.StatusCode);
        }

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<T>(options, cts.Token).ConfigureAwait(false);
        return result ?? throw new InvalidOperationException("响应内容为空。");
    }

    /// <summary>只给官方地址加 Key；镜像地址不加。</summary>
    private string? CurseForgeKeyFor(Uri candidate) =>
        candidate.Host.Equals("api.curseforge.com", StringComparison.OrdinalIgnoreCase)
            ? endpoints.CurseForgeApiKey
            : null;

    private void AddCurseForgeKey(HttpRequestMessage request, Uri candidate)
    {
        if (CurseForgeKeyFor(candidate) is { } key)
        {
            request.Headers.TryAddWithoutValidation("x-api-key", key);
        }
    }
}

/// <summary>所有候选地址都失败时抛出。消息里带上每个源的原因，便于排查。</summary>
public sealed class ModSourceException(Uri official, IReadOnlyList<Exception> errors)
    : Exception(BuildMessage(official, errors))
{
    public IReadOnlyList<Exception> Errors { get; } = errors;

    private static string BuildMessage(Uri official, IReadOnlyList<Exception> errors)
    {
        var detail = errors.Count == 0
            ? "没有可用的地址"
            : string.Join("；", errors.Select(e => e.Message));

        return $"无法访问 {official.Host}（{detail}）。请检查网络，或在设置里换一个 Mod 镜像。";
    }
}
