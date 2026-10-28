using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Solar.Core.Accounts;
using Solar.Core.Download;
using Solar.Core.Instances;
using Solar.Core.Java;
using Solar.Core.Loaders;
using Solar.Core.Minecraft;
using Solar.Core.Paths;
using Solar.Core.Settings;

namespace Solar.Core.Launch;

/// <summary>启动流程的阶段，供 UI 显示进度文案。</summary>
public enum LaunchStage
{
    Authenticating,
    DetectingJava,
    InstallingLoader,
    ResolvingVersion,
    Downloading,
    Starting,
}

/// <summary>
/// 阶段变化。带进度类下载的阶段（如安装加载器）会在 <see cref="Ratio"/> 里给出 0~1 的比例，
/// 其余阶段为 0——0 表示这一步没有可细分的进度，UI 应显示不确定状态而不是 0%。
/// </summary>
public sealed record LaunchStageChanged(LaunchStage Stage, string Message, double Ratio = 0);

public interface IGameLaunchService
{
    /// <summary>完整流程：解析版本 → 选 Java → 补齐文件 → 启动进程。</summary>
    Task<Process> LaunchAsync(
        GameInstance instance,
        IProgress<DownloadProgress>? downloadProgress = null,
        IProgress<LaunchStageChanged>? stageProgress = null,
        CancellationToken ct = default);

    /// <summary>只补齐文件，不启动（用于"预下载"）。</summary>
    Task PrepareAsync(
        GameInstance instance,
        IProgress<DownloadProgress>? downloadProgress = null,
        IProgress<LaunchStageChanged>? stageProgress = null,
        CancellationToken ct = default);
}

public sealed class GameLaunchService(
    AppPaths paths,
    ISettingsService settings,
    IVersionResolver resolver,
    IJavaDetector javaDetector,
    IGameDownloader downloader,
    IGameLauncher launcher,
    LaunchContextFactory contextFactory,
    IAccountStore accountStore,
    IMicrosoftAuthService authService,
    ILoaderInstaller loaderInstaller,
    IInstanceRepository instanceRepository,
    ILogger<GameLaunchService> logger) : IGameLaunchService
{
    public async Task<Process> LaunchAsync(
        GameInstance instance,
        IProgress<DownloadProgress>? downloadProgress = null,
        IProgress<LaunchStageChanged>? stageProgress = null,
        CancellationToken ct = default)
    {
        stageProgress?.Report(new LaunchStageChanged(LaunchStage.Authenticating, "正在验证账户…"));
        var account = await AcquireAccountAsync(ct).ConfigureAwait(false);

        var (version, java) = await PrepareCoreAsync(instance, downloadProgress, stageProgress, ct)
            .ConfigureAwait(false);

        stageProgress?.Report(new LaunchStageChanged(LaunchStage.Starting, "正在启动游戏…"));

        var logConfig = GetLogConfigPath(version);
        var context = contextFactory.Create(instance, version, java.Path, account, logConfig);

        var process = launcher.Start(context);

        instance.LastPlayedAt = DateTimeOffset.Now;
        logger.LogInformation("实例 {Instance} 已启动，pid={Pid}", instance.Name, process.Id);

        return process;
    }

    /// <summary>启动前取一份新的账户凭据；令牌有时效，不能缓存。</summary>
    private async Task<AccountSession> AcquireAccountAsync(CancellationToken ct)
    {
        var accountId = accountStore.ActiveAccountId;
        if (string.IsNullOrEmpty(accountId))
        {
            throw new AccountException(
                AccountErrorKind.NotSignedIn,
                "还没有登录微软账户，请先到「账户」页登录。");
        }

        return await authService.AcquireSessionAsync(accountId, ct).ConfigureAwait(false);
    }

    /// <summary>只补齐文件，不要求登录（未登录也能预下载）。</summary>
    public async Task PrepareAsync(
        GameInstance instance,
        IProgress<DownloadProgress>? downloadProgress = null,
        IProgress<LaunchStageChanged>? stageProgress = null,
        CancellationToken ct = default)
    {
        await PrepareCoreAsync(instance, downloadProgress, stageProgress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 首次启动模组实例时按需安装加载器。
    /// 放在启动流程里而不是创建实例时，是因为安装要下载几百 MB，
    /// 建实例时同步等待体验很差。
    /// </summary>
    private async Task EnsureLoaderAsync(
        GameInstance instance,
        JavaRuntime java,
        IProgress<LaunchStageChanged>? stageProgress,
        CancellationToken ct)
    {
        if (instance.Loader == ModLoader.Vanilla || instance.LoaderInstalled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(instance.LoaderVersion))
        {
            throw new InvalidOperationException(
                $"实例「{instance.Name}」的 {instance.Loader} 版本未设置，无法安装加载器。");
        }

        stageProgress?.Report(new LaunchStageChanged(
            LaunchStage.InstallingLoader,
            $"正在安装 {instance.Loader} {instance.LoaderVersion}…"));

        var installer = loaderInstaller;
        var progress = stageProgress is null
            ? null
            : new Progress<LoaderInstallProgress>(p =>
                stageProgress.Report(new LaunchStageChanged(
                    LaunchStage.InstallingLoader,
                    p.Total > 0 ? $"{p.Stage} ({p.Current}/{p.Total})" : p.Stage,
                    // 只有安装步骤（processors）会给真实计数；其余子阶段是 (0, 1)，按比例算出来是 0，
                    // 让 UI 显示不确定进度即可
                    p.Total > 0 ? (double)p.Current / p.Total : 0)));

        var versionId = await installer.InstallAsync(
            instance.Loader,
            instance.MinecraftVersion,
            instance.LoaderVersion,
            java.Path,
            progress,
            ct).ConfigureAwait(false);

        instance.VersionId = versionId;
        instance.LoaderInstalled = true;
        await instanceRepository.SaveAsync(instance, ct).ConfigureAwait(false);

        logger.LogInformation("加载器安装完成: {VersionId}", versionId);
    }

    private async Task<(ResolvedVersion Version, JavaRuntime Java)> PrepareCoreAsync(
        GameInstance instance,
        IProgress<DownloadProgress>? downloadProgress,
        IProgress<LaunchStageChanged>? stageProgress,
        CancellationToken ct)
    {
        stageProgress?.Report(new LaunchStageChanged(LaunchStage.DetectingJava, "正在选择 Java…"));

        // 加载器安装需要 Java，所以先确定 Java；此时版本可能还不存在
        var java = await SelectJavaAsync(instance, requiredMajor: null, ct).ConfigureAwait(false);

        await EnsureLoaderAsync(instance, java, stageProgress, ct).ConfigureAwait(false);

        stageProgress?.Report(new LaunchStageChanged(LaunchStage.ResolvingVersion, "正在解析版本…"));
        var version = await resolver.ResolveAsync(instance.EffectiveVersionId, ct).ConfigureAwait(false);

        // 版本解析出来后才能知道它要求的 Java 大版本，必要时重新选择
        java = await SelectJavaAsync(instance, version.Detail.JavaVersion?.MajorVersion, ct)
            .ConfigureAwait(false);

        stageProgress?.Report(new LaunchStageChanged(LaunchStage.Downloading, "正在检查并补齐文件…"));
        await downloader.DownloadAsync(instance, version, downloadProgress, ct).ConfigureAwait(false);

        return (version, java);
    }

    /// <param name="requiredMajor">
    /// 版本要求的大版本；为 null 时（版本尚未解析）先取最新的 Java 用于安装加载器。
    /// </param>
    private async Task<JavaRuntime> SelectJavaAsync(
        GameInstance instance,
        int? requiredMajor,
        CancellationToken ct)
    {
        // 实例级覆盖优先，其次全局设置
        var explicitPath = instance.JavaPath ?? settings.Current.JavaPath;
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
        {
            return await ProbeExplicitJavaAsync(explicitPath, requiredMajor, ct).ConfigureAwait(false);
        }

        var runtimes = await javaDetector.DetectAsync(ct).ConfigureAwait(false);
        if (runtimes.Count == 0)
        {
            throw new InvalidOperationException(
                "没有找到可用的 Java。请先安装 Java，或在设置中手动指定 java.exe 路径。");
        }

        if (requiredMajor is not { } required)
        {
            // 版本未解析时用最新的 Java：安装器的 processor 都是现代工具，版本越新越稳
            return runtimes[0];
        }

        var selected = javaDetector.Select(runtimes, required)
                       ?? throw new InvalidOperationException($"找不到适合 Java {required} 的运行时。");

        logger.LogInformation("需要 Java {Required}，选用 {Selected}", required, selected);
        return selected;
    }

    private async Task<JavaRuntime> ProbeExplicitJavaAsync(
        string javaPath,
        int? requiredMajor,
        CancellationToken ct)
    {
        var runtimes = await javaDetector.DetectAsync(ct).ConfigureAwait(false);
        var match = runtimes.FirstOrDefault(r =>
            string.Equals(r.Path, javaPath, StringComparison.OrdinalIgnoreCase));

        if (match is not null)
        {
            return match;
        }

        // 探测不到（例如目录不在搜索路径里）也照用，只是不知道版本号
        logger.LogWarning("无法探测指定 Java 的版本，直接使用: {Path}", javaPath);
        return new JavaRuntime(javaPath, "unknown", requiredMajor ?? 8);
    }

    /// <summary>log4j 配置文件路径；版本没有该字段时返回 null。</summary>
    public string? GetLogConfigPath(ResolvedVersion version)
    {
        if (version.Detail.Logging?.Client?.File is not { } file)
        {
            return null;
        }

        var name = file.Id ?? file.Path ?? "client-log4j.xml";
        return Path.Combine(paths.Assets, "log_configs", name);
    }
}
