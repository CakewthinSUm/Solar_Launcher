using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Solar.Core.Mods;

/// <summary>下载一个内容文件。抽成接口是为了让安装流程能在测试里用假实现。</summary>
public interface IModFileDownloader
{
    Task DownloadAsync(ModFile file, string targetPath, CancellationToken ct = default);
}

/// <summary>
/// 下载一个通过镜像/官方候选列表描述的文件，并做 SHA1 校验。
/// <para>
/// 从 <see cref="ModInstaller"/> 抽出来，是因为整合包安装也要下文件
/// （整合包本身、以及 manifest 里列出的每个 Mod），两边必须用同一套
/// 「换源 → 校验 → 先写 .part 再改名」的规则。
/// </para>
/// </summary>
public sealed class ModFileDownloader(ModEndpoints endpoints, ILogger<ModFileDownloader> logger)
    : IModFileDownloader
{
    /// <summary>
    /// 下载用的 HttpClient。与 API 请求分开：资源文件动辄几十 MB，
    /// 超时放宽到 30 分钟，避免一个慢下载拖住整个服务。
    /// </summary>
    private static readonly HttpClient DownloadClient = new(new HttpClientHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        MaxConnectionsPerServer = 8,
    })
    {
        Timeout = TimeSpan.FromMinutes(30),
    };

    public async Task DownloadAsync(ModFile file, string targetPath, CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var official = new Uri(file.Url);
        var temp = targetPath + ".part";
        var errors = new List<Exception>();

        foreach (var candidate in endpoints.Candidates(official))
        {
            try
            {
                using (var response = await DownloadClient
                                 .GetAsync(candidate, HttpCompletionOption.ResponseHeadersRead, ct)
                                 .ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();

                    await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    await using var destination = File.Create(temp);
                    await source.CopyToAsync(destination, ct).ConfigureAwait(false);
                }

                VerifyOrThrow(temp, file);

                // 校验通过才改名：中断或校验失败时目标位置不会出现一个坏文件
                File.Move(temp, targetPath, overwrite: true);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 取消时也清掉 .part，避免在游戏目录里留下半截文件
                TryDelete(temp);
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogDebug(ex, "下载失败，尝试下一个源: {Uri}", candidate);
                errors.Add(ex);
                TryDelete(temp);
            }
        }

        throw new ModSourceException(official, errors);
    }

    /// <summary>
    /// 有 SHA1 就校验。CurseForge 镜像偶尔会返回一个 200 的 HTML 错误页，
    /// 只检查 HTTP 状态码发现不了，必须靠哈希兜住。
    /// </summary>
    public static void VerifyOrThrow(string path, ModFile file)
    {
        if (string.IsNullOrWhiteSpace(file.Sha1))
        {
            return;
        }

        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA1.HashData(stream)).ToLowerInvariant();

        if (!actual.Equals(file.Sha1, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"文件校验失败：{file.FileName} 的 SHA1 与预期不符（可能下到了错误页）。");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 临时文件删不掉不影响结果，下次下载会覆盖
        }
    }
}
