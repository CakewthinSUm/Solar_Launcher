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

    /// <summary>进程已拉起，等游戏窗口真正出现才算启动成功。</summary>
    WaitingForWindow,
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
    InstanceGameDirectory gameDirectories,
    ILogger<GameLaunchService> logger) : IGameLaunchService
{
    /// <summary>
    /// 等窗口的上限。大型整合包初始化几十秒很常见，给足余量；
    /// 超过这个时间基本可以判定卡死，继续等只会让用户以为没反应。
    /// </summary>
    private static readonly TimeSpan GameWindowTimeout = TimeSpan.FromMinutes(5);

    public async Task<Process> LaunchAsync(
        GameInstance instance,
        IProgress<DownloadProgress>? downloadProgress = null,
        IProgress<LaunchStageChanged>? stageProgress = null,
        CancellationToken ct = default)
    {
        // 预检与 PCL 一致：路径里的特殊字符会让参数解析出错，必须在启动前拦住
        Precheck(instance);

        stageProgress?.Report(new LaunchStageChanged(LaunchStage.Authenticating, "正在验证账户…"));
        var account = await AcquireAccountAsync(ct).ConfigureAwait(false);

        var (version, java) = await PrepareCoreAsync(instance, downloadProgress, stageProgress, ct)
            .ConfigureAwait(false);

        stageProgress?.Report(new LaunchStageChanged(LaunchStage.Starting, "正在启动游戏…"));

        var logConfig = GetLogConfigPath(version);
        var context = contextFactory.Create(
            instance, version, java.Path, account, logConfig, java.MajorVersion);

        // 首次启动预处理 options.txt（语言 + 无障碍引导），规则与 PCL 一致
        EnsureFirstRunOptions(instance, context.GameDirectory, settings.Current.GameLanguage);

        var launched = launcher.Start(context);

        // 进程起来不等于启动成功：Java 参数错误、缺库、加载器崩溃都会让进程很快退出。
        // 等到窗口真的出现再报成功，失败时把日志尾部带出来，用户不用自己翻日志。
        stageProgress?.Report(new LaunchStageChanged(LaunchStage.WaitingForWindow, "正在等待游戏窗口…"));
        await WaitForGameWindowAsync(launched, ct).ConfigureAwait(false);

        instance.LastPlayedAt = DateTimeOffset.Now;
        logger.LogInformation("实例 {Instance} 已启动，pid={Pid}", instance.Name, launched.Process.Id);

        return launched.Process;
    }

    /// <summary>
    /// 首次启动时预处理 options.txt，对齐 PCL 的 <c>McLaunchPrerun</c>：
    /// <list type="bullet">
    /// <item>游戏把 options.txt 放在 <c>config/yosbr/</c> 时（Yosbr 模组）改那份；</item>
    /// <item>只在还没有语言设置、或游戏目录还没有 <c>saves</c> 时改写语言，
    /// 避免覆盖整合包自带 options.txt 里的选择；</item>
    /// <item>1.1–1.10 只认大写结尾的 <c>zh_CN</c> / <c>zh_TW</c>，小写会被游戏当成英文。</item>
    /// </list>
    /// 另外，新建 options.txt 时顺手关掉无障碍引导——否则每个新实例第一次启动
    /// 都会停在「Welcome to Minecraft!」那个界面上。
    /// </summary>
    internal static void EnsureFirstRunOptions(
        GameInstance instance,
        string gameDirectory,
        GameLanguage language)
    {
        var optionsPath = Path.Combine(gameDirectory, "options.txt");
        var createdNew = !File.Exists(optionsPath);

        if (createdNew)
        {
            var yosbrPath = Path.Combine(gameDirectory, "config", "yosbr", "options.txt");
            if (File.Exists(yosbrPath))
            {
                optionsPath = yosbrPath;
            }
        }

        var mc = JavaRequirements.ParseMinecraftVersion(instance.MinecraftVersion);
        if (mc is { Major: 1, Minor: 0 })
        {
            // 1.0 及以前没有语言选项
            return;
        }

        try
        {
            var current = OptionsIni.Read(optionsPath, "lang") ?? "none";
            var hasSaves = Directory.Exists(Path.Combine(gameDirectory, "saves"));

            // 玩家已经在这里玩过（有存档）就尊重他改过的语言
            var required = current == "none" || !hasSaves
                ? LanguageCode(language)
                : current.ToLowerInvariant();

            // 1.1–1.10 必须大写结尾
            if (mc is { Major: 1, Minor: <= 10 })
            {
                required = required[..^2] + required[^2..].ToUpperInvariant();
            }

            if (!string.Equals(current, required, StringComparison.Ordinal))
            {
                // 与 PCL 一致：先写一个占位值再写目标值，避免游戏读到的还是旧缓存
                OptionsIni.Write(optionsPath, "lang", "-");
                OptionsIni.Write(optionsPath, "lang", required);
            }

            if (createdNew && OptionsIni.Read(optionsPath, "onboardAccessibility") is null)
            {
                OptionsIni.Write(optionsPath, "onboardAccessibility", "false");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 写不了就退回游戏自己的首次引导，不影响启动
        }
    }

    private static string LanguageCode(GameLanguage language) => language switch
    {
        GameLanguage.ChineseTraditional => "zh_tw",
        GameLanguage.English => "en_us",
        _ => "zh_cn",
    };

    /// <summary>
    /// 启动前预检，对齐 PCL 的 <c>McLaunchPrecheck</c>：
    /// 路径里的 <c>!</c> 和 <c>;</c> 会在命令行解析时被吞掉，必须提前拦住。
    /// </summary>
    private void Precheck(GameInstance instance)
    {
        if (string.IsNullOrWhiteSpace(instance.MinecraftVersion))
        {
            throw new InvalidOperationException($"实例「{instance.Name}」没有设置 Minecraft 版本。");
        }

        var gameDirectory = gameDirectories.Resolve(instance);
        var versionDirectory = Path.Combine(paths.Versions, instance.EffectiveVersionId);

        foreach (var path in new[] { gameDirectory, versionDirectory })
        {
            if (path.Contains('!') || path.Contains(';'))
            {
                throw new InvalidOperationException(
                    $"游戏路径中不能包含 ! 或 ;（{path}）。请把游戏目录移到别的路径后重试。");
            }
        }
    }

    /// <summary>
    /// 等游戏窗口出现。判定顺序参照 PCL 的 Watcher：
    /// 日志里的崩溃关键字优先，其次是进程退出、窗口出现、日志里程碑。
    /// </summary>
    private async Task WaitForGameWindowAsync(LaunchedGame launched, CancellationToken ct)
    {
        var process = launched.Process;
        var monitor = new GameLogMonitor(launched.LogPath);
        var deadline = DateTimeOffset.UtcNow + GameWindowTimeout;
        var loaderWindowLogged = false;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            monitor.Poll();

            if (monitor.CrashDetected)
            {
                throw new InvalidOperationException(
                    $"游戏崩溃：{monitor.CrashLine}\n{monitor.Tail}"
                    + $"\n完整日志：{launched.LogPath}");
            }

            if (monitor.Ended)
            {
                throw new InvalidOperationException(
                    $"游戏在加载完成前退出。\n{monitor.Tail}"
                    + $"\n完整日志：{launched.LogPath}");
            }

            bool exited;
            try
            {
                exited = process.HasExited;
            }
            catch (InvalidOperationException)
            {
                // 刚好在这一刻退出，与下面的 HasExited 同理
                throw GameExited(launched);
            }

            if (exited)
            {
                throw GameExited(launched);
            }

            switch (GameWindowFinder.Probe(process.Id, out var title))
            {
                case GameWindowFinder.WindowKind.Game:
                    logger.LogInformation("游戏窗口已出现: {Title}", title);
                    return;

                case GameWindowFinder.WindowKind.Loader when !loaderWindowLogged:
                    // 加载器窗口不是游戏本体，继续等；只记一次日志
                    loaderWindowLogged = true;
                    logger.LogInformation("模组加载器窗口已出现，继续等待游戏窗口: {Title}", title);
                    break;

                case GameWindowFinder.WindowKind.JavaError:
                    // Java 自己弹了报错框（例如版本要求的 JVM 参数当前 Java 不支持），
                    // 这不是游戏窗口，继续等只会白等
                    throw new InvalidOperationException(
                        $"Java 启动失败：{title}\n{ReadLogTail(launched.LogPath, 10)}"
                        + $"\n完整日志：{launched.LogPath}");
            }

            // 反作弊或安全软件可能挡住窗口枚举；日志已经走到"材质已加载"就说明游戏起来了
            if (monitor.Milestones >= 5)
            {
                logger.LogInformation("日志显示游戏已加载完成，按启动成功处理（未枚举到窗口）");
                return;
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new InvalidOperationException(
                    $"等待游戏窗口超时（{GameWindowTimeout.TotalMinutes:0} 分钟），游戏可能卡在启动阶段。"
                    + (string.IsNullOrWhiteSpace(monitor.Tail) ? "" : $"\n{monitor.Tail}")
                    + $"\n完整日志：{launched.LogPath}");
            }

            await Task.Delay(250, ct).ConfigureAwait(false);
        }
    }

    private static InvalidOperationException GameExited(LaunchedGame launched)
    {
        var code = launched.Process.HasExited ? launched.Process.ExitCode : -1;
        var tail = ReadLogTail(launched.LogPath, 10);

        return new InvalidOperationException(
            $"游戏进程已退出（退出码 {code}），窗口没有出现。"
            + (string.IsNullOrWhiteSpace(tail) ? "" : $"\n{tail}")
            + $"\n完整日志：{launched.LogPath}");
    }

    /// <summary>读游戏日志的最后几行；日志正被写入，必须允许共享读取。</summary>
    private static string ReadLogTail(string path, int lines)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var all = reader.ReadToEnd()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimEnd('\r'))
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToArray();

            return string.Join('\n', all.TakeLast(lines));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
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
        var java = await SelectJavaAsync(instance, requirement: null, ct).ConfigureAwait(false);

        await EnsureLoaderAsync(instance, java, stageProgress, ct).ConfigureAwait(false);

        stageProgress?.Report(new LaunchStageChanged(LaunchStage.ResolvingVersion, "正在解析版本…"));
        var version = await resolver.ResolveAsync(instance.EffectiveVersionId, ct).ConfigureAwait(false);

        // 版本解析出来后才能知道它要求的 Java 范围（含模组加载器与 OptiFine 的约束）
        var requirement = JavaRequirements.For(instance, version);
        java = await SelectJavaAsync(instance, requirement, ct).ConfigureAwait(false);

        stageProgress?.Report(new LaunchStageChanged(LaunchStage.Downloading, "正在检查并补齐文件…"));
        await downloader.DownloadAsync(instance, version, downloadProgress, ct).ConfigureAwait(false);

        return (version, java);
    }

    /// <param name="requirement">
    /// 版本要求的 Java 范围；为 null 时（版本尚未解析）先取最新的 Java 用于安装加载器。
    /// </param>
    private async Task<JavaRuntime> SelectJavaAsync(
        GameInstance instance,
        JavaRequirement? requirement,
        CancellationToken ct)
    {
        // 实例级覆盖优先，其次全局设置
        var explicitPath = instance.JavaPath ?? settings.Current.JavaPath;
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
        {
            var explicitJava = await ProbeExplicitJavaAsync(explicitPath, requirement?.MinMajor, ct)
                .ConfigureAwait(false);

            // 用户指定的 Java 优先，但不合适时要提醒——这类问题最后都会以难懂的崩溃出现
            if (requirement is not null && !requirement.IsSatisfiedBy(explicitJava))
            {
                logger.LogWarning(
                    "指定的 Java {Java} 不满足要求（{Requirement}），仍按用户设置使用",
                    explicitJava,
                    requirement.Describe());
            }

            return explicitJava;
        }

        var runtimes = await javaDetector.DetectAsync(ct).ConfigureAwait(false);
        if (runtimes.Count == 0)
        {
            throw new InvalidOperationException(
                "没有找到可用的 Java。请先安装 Java，或在设置中手动指定 java.exe 路径。");
        }

        if (requirement is null)
        {
            // 版本未解析时用最新的 Java：安装器的 processor 都是现代工具，版本越新越稳
            return runtimes[0];
        }

        var selected = javaDetector.Select(runtimes, requirement);
        if (selected is null)
        {
            throw new InvalidOperationException(
                $"该版本需要 {requirement.Describe()}，但只检测到 {string.Join("、", runtimes)}。"
                + "请安装对应版本的 Java，或在设置里指定 java.exe 路径。");
        }

        logger.LogInformation("需要 {Requirement}，选用 {Selected}", requirement.Describe(), selected);
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
