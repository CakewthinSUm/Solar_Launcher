using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Solar.Core.Download;
using Solar.Core.Instances;

namespace Solar.Core.Mods;

public interface IModInstaller
{
    /// <summary>目标实例的 <c>mods</c> 目录。</summary>
    string GetModsDirectory(GameInstance instance);

    /// <summary>某个文件是否已经装在这个实例里（按文件名判断）。</summary>
    bool IsInstalled(GameInstance instance, ModFile file);

    /// <summary>安装清单。已存在的文件按 <paramref name="overwrite"/> 决定覆盖还是跳过。</summary>
    Task<ModInstallResult> InstallAsync(
        GameInstance instance,
        ModInstallPlan plan,
        bool overwrite = false,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default);
}

/// <summary>
/// 把模组文件放进实例的 <c>mods</c> 目录。
/// <para>
/// 三个约束：
/// 一是必须写到实例实际使用的游戏目录（<see cref="InstanceGameDirectory.Resolve"/>），
/// 否则开了版本隔离的实例读不到；
/// 二是保留上游文件名，不做重命名——更新检测和社区排障都依赖原始文件名；
/// 三是先下到 <c>.part</c> 再改名，中断时不会留下一个看起来正常的半截 jar。
/// </para>
/// </summary>
public sealed class ModInstaller(
    InstanceGameDirectory gameDirectories,
    ModEndpoints endpoints,
    ILogger<ModInstaller> logger) : IModInstaller
{
    /// <summary>
    /// 下载用的 HttpClient。与 API 请求分开：模组文件动辄几十 MB，
    /// 超时放宽到 15 分钟，避免一个慢下载拖住整个服务。
    /// </summary>
    private static readonly HttpClient DownloadClient = new(new HttpClientHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        MaxConnectionsPerServer = 8,
    })
    {
        Timeout = TimeSpan.FromMinutes(15),
    };

    public string GetModsDirectory(GameInstance instance) =>
        Path.Combine(gameDirectories.Resolve(instance), "mods");

    public bool IsInstalled(GameInstance instance, ModFile file) =>
        File.Exists(Path.Combine(GetModsDirectory(instance), file.FileName));

    public async Task<ModInstallResult> InstallAsync(
        GameInstance instance,
        ModInstallPlan plan,
        bool overwrite = false,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var directory = GetModsDirectory(instance);
        Directory.CreateDirectory(directory);

        var pending = plan.Pending.ToList();
        var installed = 0;
        var skipped = 0;
        var files = new List<string>();

        for (var i = 0; i < pending.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var item = pending[i];
            var version = item.Version!;
            var target = Path.Combine(directory, version.File.FileName);

            if (File.Exists(target) && !overwrite)
            {
                skipped++;
                files.Add(version.File.FileName);
                logger.LogInformation("跳过已存在的模组文件: {File}", version.File.FileName);
                continue;
            }

            progress?.Report(new DownloadProgress(installed, pending.Count, 0, 0)
            {
                CurrentFile = version.File.FileName,
            });

            await DownloadAsync(version.File, target, ct).ConfigureAwait(false);

            installed++;
            files.Add(version.File.FileName);
            logger.LogInformation("已安装模组 {Name} → {Path}", item.Project.Title, target);
        }

        progress?.Report(new DownloadProgress(installed, pending.Count, 0, 0));
        return new ModInstallResult(installed, skipped, files);
    }

    private async Task DownloadAsync(ModFile file, string target, CancellationToken ct)
    {
        var official = new Uri(file.Url);
        var candidates = endpoints.Candidates(official);
        var temp = target + ".part";
        var errors = new List<Exception>();

        foreach (var candidate in candidates)
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
                File.Move(temp, target, overwrite: true);
                return;
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
    private static void VerifyOrThrow(string path, ModFile file)
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
