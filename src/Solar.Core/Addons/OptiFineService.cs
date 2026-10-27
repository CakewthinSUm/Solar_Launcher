using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Solar.Core.Json;
using Solar.Core.Loaders;
using Solar.Core.Models;
using Solar.Core.Paths;

namespace Solar.Core.Addons;

public interface IOptiFineService
{
    /// <summary>列出某个 MC 版本可用的 OptiFine 构建，新的排前面。</summary>
    Task<IReadOnlyList<OptiFineBuild>> GetBuildsAsync(string minecraftVersion, CancellationToken ct = default);

    /// <summary>OptiFine 安装器（就是完整的 OptiFine jar）的下载地址。</summary>
    string GetInstallerUrl(OptiFineBuild build);

    /// <summary>
    /// 安装 OptiFine：跑官方安装器，把它产出的库与版本 JSON 收进 Solar 的共享目录。
    /// 返回可以启动的版本 Id。
    /// </summary>
    Task<string> InstallAsync(
        OptiFineBuild build,
        string minecraftVersion,
        string vanillaJarPath,
        string vanillaJsonPath,
        string javaPath,
        IProgress<LoaderInstallProgress>? progress = null,
        CancellationToken ct = default);
}

/// <summary>
/// OptiFine 的版本列表、下载与安装。
/// <para>
/// 列表走 BMCLAPI 的 <c>/optifine/versionList</c>（JSON），它同时给出文件名与所需的 Forge 版本。
/// 安装不能靠解压：安装器里只有 xdelta 差分补丁，必须跑它自己的 <c>optifine.Installer</c>
/// 把原版 jar 打补丁成 <c>OptiFine-&lt;版本&gt;.jar</c>。这与 Forge 的 processor 是同一类做法。
/// </para>
/// <para>
/// 安装器没有命令行参数，靠 <c>user.home</c> 定位游戏目录（<c>&lt;user.home&gt;/.minecraft</c>），
/// 所以这里搭一个临时的 home，装完把产出搬回 <see cref="AppPaths.Shared"/>。
/// </para>
/// </summary>
public sealed partial class OptiFineService(
    HttpClient http,
    AppPaths paths,
    ILogger<OptiFineService> logger) : IOptiFineService
{
    private const string VersionListUrl = "https://bmclapi2.bangbang93.com/optifine/versionList";
    private const string DownloadRoot = "https://bmclapi2.bangbang93.com/optifine";

    public async Task<IReadOnlyList<OptiFineBuild>> GetBuildsAsync(
        string minecraftVersion,
        CancellationToken ct = default)
    {
        try
        {
            var list = await http
                .GetFromJsonAsync<List<OptiFineEntry>>(VersionListUrl, ct)
                .ConfigureAwait(false);

            if (list is not { Count: > 0 })
            {
                return [];
            }

            return list
                .Where(e => !string.IsNullOrEmpty(e.McVersion)
                            && !string.IsNullOrEmpty(e.Type)
                            && !string.IsNullOrEmpty(e.Patch))
                .Where(e => string.Equals(e.McVersion, minecraftVersion, StringComparison.OrdinalIgnoreCase))
                .Select(e => new OptiFineBuild(
                    e.McVersion!,
                    e.Type!,
                    e.Patch!,
                    e.FileName ?? $"OptiFine_{e.McVersion}_{e.Type}_{e.Patch}.jar",
                    NormalizeForge(e.Forge)))
                // BMCLAPI 的原始顺序是按时间正序（I5_pre4…I5、I6_pre1…I6），
                // 直接拿来用界面默认会停在最老的预发布版上。这里按构建号从新到旧排，
                // 同一构建号的正式版排在它的预发布版前面。
                .OrderByDescending(b => b, Comparer<OptiFineBuild>.Create(CompareBuilds))
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "获取 OptiFine 版本列表失败（MC {Version}）", minecraftVersion);
            throw new InvalidOperationException("无法获取 OptiFine 版本列表，请检查网络连接。", ex);
        }
    }

    public string GetInstallerUrl(OptiFineBuild build) => GetInstallerUrlFor(build);

    /// <summary>
    /// 安装器 jar 的地址。它同时就是一个合法的 Mod jar：
    /// 1.13 起 OptiFine 以 Mod 形式提供，带主加载器时直接放进 mods 即可。
    /// </summary>
    public static string GetInstallerUrlFor(OptiFineBuild build) =>
        $"{DownloadRoot}/{build.MinecraftVersion}/{build.Type}/{build.Patch}";

    public async Task<string> InstallAsync(
        OptiFineBuild build,
        string minecraftVersion,
        string vanillaJarPath,
        string vanillaJsonPath,
        string javaPath,
        IProgress<LoaderInstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        var workDirectory = Path.Combine(paths.Data, "installer", "optifine-" + Guid.NewGuid().ToString("N")[..8]);
        var homeDirectory = Path.Combine(workDirectory, "home");
        var minecraftDirectory = Path.Combine(homeDirectory, ".minecraft");

        try
        {
            // 1. 安装器要一个「原版已安装」的目录：versions/<mc>/<mc>.jar + .json
            var versionDirectory = Path.Combine(minecraftDirectory, "versions", minecraftVersion);
            Directory.CreateDirectory(versionDirectory);
            File.Copy(vanillaJarPath, Path.Combine(versionDirectory, minecraftVersion + ".jar"), overwrite: true);

            // 版本 JSON 必须按 Mojang 原生的大小写写出去：安装器自带一个
            // 大小写敏感的解析器，读 PascalCase 的 "MainClass" 会拿到 null 然后崩掉。
            await WriteNativeVersionJsonAsync(vanillaJsonPath, versionDirectory, minecraftVersion, ct)
                .ConfigureAwait(false);

            Directory.CreateDirectory(Path.Combine(minecraftDirectory, "libraries"));

            // 安装器找不到它就会拒绝安装，内容本身不重要
            await File.WriteAllTextAsync(
                Path.Combine(minecraftDirectory, "launcher_profiles.json"),
                """{"profiles":{},"settings":{},"version":3}""",
                ct).ConfigureAwait(false);

            // 2. 下载安装器
            progress?.Report(new LoaderInstallProgress("正在下载 OptiFine 安装器", 0, 1));
            var installerPath = Path.Combine(workDirectory, "OptiFine-installer.jar");
            await DownloadFileAsync(GetInstallerUrl(build), installerPath, ct).ConfigureAwait(false);

            // 3. 跑安装器（无界面；失败时它会尝试弹窗，重定向输出后不会卡住）
            progress?.Report(new LoaderInstallProgress("正在安装 OptiFine", 0, 1));
            await RunInstallerAsync(installerPath, homeDirectory, javaPath, ct).ConfigureAwait(false);

            // 4. 收集产出：库 + 版本 JSON
            progress?.Report(new LoaderInstallProgress("正在整理 OptiFine 文件", 0, 1));
            var versionId = CollectOutput(minecraftDirectory, build);

            logger.LogInformation("OptiFine {Version} 安装完成: {Id}", build.Version, versionId);
            return versionId;
        }
        finally
        {
            TryDeleteDirectory(workDirectory);
        }
    }

    /// <summary>
    /// 读入 Solar 自己的版本 JSON，按 Mojang 原生 camelCase 写一份给安装器用。
    /// Solar 的缓存里是 PascalCase（沿用 C# 属性名），直接复制过去安装器读不到字段。
    /// </summary>
    private static async Task WriteNativeVersionJsonAsync(
        string sourceJsonPath,
        string targetDirectory,
        string minecraftVersion,
        CancellationToken ct)
    {
        var detail = await JsonFile.ReadAsync<VersionDetail>(sourceJsonPath, ct).ConfigureAwait(false)
                     ?? throw new InvalidOperationException($"版本 JSON 无法解析：{sourceJsonPath}");

        var target = Path.Combine(targetDirectory, minecraftVersion + ".json");
        await using var stream = File.Create(target);
        await JsonSerializer.SerializeAsync(stream, detail, SolarJson.MinecraftNative, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 把临时 home 里安装器写出的东西搬进 Solar 的共享目录，返回版本 Id。
    /// <para>
    /// 安装器把 OptiFine 的库写在 <c>libraries/optifine/</c> 下，版本 JSON 写在
    /// <c>versions/&lt;mc&gt;-OptiFine_&lt;edition&gt;/</c> 下，名字由安装器决定。
    /// </para>
    /// </summary>
    private string CollectOutput(string minecraftDirectory, OptiFineBuild build)
    {
        var producedLibraries = Path.Combine(minecraftDirectory, "libraries", "optifine");
        if (Directory.Exists(producedLibraries))
        {
            CopyDirectory(producedLibraries, Path.Combine(paths.Libraries, "optifine"));
        }

        var versionsDirectory = Path.Combine(minecraftDirectory, "versions");
        var expectedId = $"{build.MinecraftVersion}-OptiFine_{build.DisplayVersion}";

        // 安装器用的名字可能与我们的预期略有出入，按目录里新出现的那个来
        var produced = Directory.Exists(versionsDirectory)
            ? Directory.GetDirectories(versionsDirectory)
                .Select(Path.GetFileName)
                .Where(name => name is not null
                               && name.Contains("-OptiFine_", StringComparison.OrdinalIgnoreCase)
                               && !string.Equals(name, build.MinecraftVersion, StringComparison.OrdinalIgnoreCase))
                .ToList()
            : [];

        var versionId = produced.FirstOrDefault(name =>
                            string.Equals(name, expectedId, StringComparison.OrdinalIgnoreCase))
                        ?? produced.FirstOrDefault()
                        ?? expectedId;

        var source = Path.Combine(versionsDirectory, versionId);
        var target = Path.Combine(paths.Versions, versionId);
        Directory.CreateDirectory(target);

        var jsonPath = Path.Combine(source, versionId + ".json");
        if (!File.Exists(jsonPath))
        {
            throw new InvalidOperationException(
                $"OptiFine 安装器没有生成版本 JSON（期望 {jsonPath}）。");
        }

        // 安装器写出的 JSON 里的 id 与目录名一致；但 libraries 里的 url 指向
        // BMCLAPI 的 maven，Solar 的下载器能直接沿用。
        File.Copy(jsonPath, Path.Combine(target, versionId + ".json"), overwrite: true);

        // 版本 jar 由 patcher 产出，安装器把它放在版本目录里
        var jarPath = Path.Combine(source, versionId + ".jar");
        if (File.Exists(jarPath))
        {
            File.Copy(jarPath, Path.Combine(target, versionId + ".jar"), overwrite: true);
        }

        return versionId;
    }

    private async Task RunInstallerAsync(
        string installerPath,
        string homeDirectory,
        string javaPath,
        CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo(javaPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = homeDirectory,
        };

        // Windows 上安装器认 %APPDATA%\.minecraft 而不是 user.home，
        // 两个都指到临时 home，它才会装进我们准备的目录。
        startInfo.Environment["APPDATA"] = homeDirectory;
        startInfo.ArgumentList.Add($"-Duser.home={homeDirectory}");

        // 安装器出错时会弹 Swing 对话框；无人值守时那个窗口看不见，
        // 进程会一直挂着。headless 让弹窗直接抛异常，失败得快而明确。
        startInfo.ArgumentList.Add("-Djava.awt.headless=true");
        startInfo.ArgumentList.Add("-cp");
        startInfo.ArgumentList.Add(installerPath);
        startInfo.ArgumentList.Add("optifine.Installer");

        logger.LogDebug("运行 OptiFine 安装器: {Java} {Args}", javaPath, string.Join(' ', startInfo.ArgumentList));

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("无法启动 OptiFine 安装器。");

        var stdout = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        var stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            logger.LogError("OptiFine 安装器失败 ({Code})\n{Out}\n{Err}", process.ExitCode, stdout, stderr);
            throw new InvalidOperationException(
                $"OptiFine 安装失败（退出码 {process.ExitCode}）。{Environment.NewLine}{stderr}");
        }
    }

    /// <summary>OptiFine 的预发布版在 patch 里带 <c>pre</c>，如 <c>I5_pre4</c>。</summary>
    public static bool IsPreview(string patch) =>
        patch.Contains("pre", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 按构建号比较两个 OptiFine 构建。patch 形如 <c>I6</c> / <c>I5_pre4</c>：
    /// 字母段是主序号，<c>pre</c> 之后的数字是预发布序号；正式版大于同号的预发布版。
    /// </summary>
    public static int CompareBuilds(OptiFineBuild left, OptiFineBuild right)
    {
        var leftKey = ParsePatch(left.Patch);
        var rightKey = ParsePatch(right.Patch);

        var letter = leftKey.Letter.CompareTo(rightKey.Letter);
        if (letter != 0)
        {
            return letter;
        }

        var main = leftKey.Main.CompareTo(rightKey.Main);
        if (main != 0)
        {
            return main;
        }

        // 没有 pre 的是正式版，排在同号的预发布版之后
        return (leftKey.Preview ?? int.MaxValue).CompareTo(rightKey.Preview ?? int.MaxValue);
    }

    /// <summary>
    /// 把 <c>I6</c> / <c>I5_pre4</c> 拆成 (字母序号, 数字, 预发布序号)。
    /// 字母也参与比较：I 比 H 新，这样跨字母的版本才能排对。
    /// </summary>
    private static (int Letter, int Main, int? Preview) ParsePatch(string patch)
    {
        var match = PatchPattern().Match(patch);
        if (!match.Success)
        {
            return (0, 0, null);
        }

        var letter = char.ToUpperInvariant(match.Groups["letter"].Value[0]) - 'A';
        var main = int.Parse(match.Groups["main"].Value);
        var preview = match.Groups["pre"].Success ? int.Parse(match.Groups["pre"].Value) : (int?)null;
        return (letter, main, preview);
    }

    [GeneratedRegex(@"^(?<letter>[A-Za-z])(?<main>\d+)(?:_pre(?<pre>\d+))?$", RegexOptions.IgnoreCase)]
    private static partial Regex PatchPattern();

    /// <summary>
    /// BMCLAPI 的 forge 字段形如 <c>Forge 14.23.5.2859</c> 或 <c>Forge N/A</c>；
    /// 后者表示不限定，返回 null。
    /// </summary>
    public static string? NormalizeForge(string? forge)
    {
        if (string.IsNullOrWhiteSpace(forge))
        {
            return null;
        }

        var match = ForgeVersionPattern().Match(forge);
        return match.Success ? match.Value : null;
    }

    [GeneratedRegex(@"\d+(?:\.\d+)+")]
    private static partial Regex ForgeVersionPattern();

    private async Task DownloadFileAsync(string url, string targetPath, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var target = File.Create(targetPath);
        await source.CopyToAsync(target, ct).ConfigureAwait(false);
    }

    private static void CopyDirectory(string source, string target)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // 临时目录清理失败不影响安装结果
        }
    }

    /// <summary>BMCLAPI <c>versionList</c> 的条目形状。</summary>
    private sealed class OptiFineEntry
    {
        [System.Text.Json.Serialization.JsonPropertyName("mcversion")]
        public string? McVersion { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("type")]
        public string? Type { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("patch")]
        public string? Patch { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("filename")]
        public string? FileName { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("forge")]
        public string? Forge { get; set; }
    }
}
