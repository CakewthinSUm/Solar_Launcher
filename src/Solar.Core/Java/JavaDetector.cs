using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Solar.Core.Paths;

namespace Solar.Core.Java;

public sealed record JavaRuntime(string Path, string Version, int MajorVersion)
{
    public override string ToString() => $"Java {MajorVersion} ({Version})";
}

public interface IJavaDetector
{
    /// <summary>探测本机可用的 Java，按大版本从高到低返回。</summary>
    Task<IReadOnlyList<JavaRuntime>> DetectAsync(CancellationToken ct = default);

    /// <summary>
    /// 在要求范围内选择最合适的 Java：优先恰好是下限的版本，
    /// 其次范围内最低的更高版本，最后才退到范围内最高的版本。
    /// 没有满足要求的返回 null，由调用方给出可操作的提示。
    /// </summary>
    JavaRuntime? Select(IReadOnlyList<JavaRuntime> runtimes, JavaRequirement requirement);
}

public sealed partial class JavaDetector(ILogger<JavaDetector> logger) : IJavaDetector
{
    [GeneratedRegex(@"version ""(?<version>[^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex VersionPattern();

    public async Task<IReadOnlyList<JavaRuntime>> DetectAsync(CancellationToken ct = default)
    {
        var candidates = new List<string>();

        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            candidates.Add(System.IO.Path.Combine(javaHome, "bin", "java.exe"));
        }

        foreach (var root in GetSearchRoots())
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            try
            {
                candidates.AddRange(Directory.EnumerateFiles(root, "java.exe", SearchOption.AllDirectories));
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "扫描 Java 目录失败: {Root}", root);
            }
        }

        var results = new List<JavaRuntime>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!seen.Add(candidate) || !File.Exists(candidate))
            {
                continue;
            }

            var runtime = await ProbeAsync(candidate, ct).ConfigureAwait(false);
            if (runtime is not null)
            {
                results.Add(runtime);
            }
        }

        return results.OrderByDescending(r => r.MajorVersion).ToList();
    }

    public JavaRuntime? Select(IReadOnlyList<JavaRuntime> runtimes, JavaRequirement requirement)
    {
        var candidates = runtimes.Where(requirement.IsSatisfiedBy).ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        // 恰好是下限最稳（模组生态通常只验证过这个版本）；
        // 其次范围内最低的更高版本，避免直接跳到最新的大版本。
        return candidates.FirstOrDefault(r => r.MajorVersion == requirement.MinMajor)
               ?? candidates.OrderBy(r => r.MajorVersion).First();
    }

    private static IEnumerable<string> GetSearchRoots()
    {
        yield return System.IO.Path.Combine(AppContext.BaseDirectory, "SolarShared", "runtime");

        foreach (var programFiles in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            if (string.IsNullOrEmpty(programFiles))
            {
                continue;
            }

            yield return System.IO.Path.Combine(programFiles, "Java");
            yield return System.IO.Path.Combine(programFiles, "Eclipse Adoptium");
            yield return System.IO.Path.Combine(programFiles, "Microsoft");
            yield return System.IO.Path.Combine(programFiles, "Zulu");
            yield return System.IO.Path.Combine(programFiles, "BellSoft");
        }
    }

    private async Task<JavaRuntime?> ProbeAsync(string javaPath, CancellationToken ct)
    {
        try
        {
            var startInfo = new ProcessStartInfo(javaPath, "-version")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            var match = VersionPattern().Match(stderr);
            if (!match.Success)
            {
                return null;
            }

            var version = match.Groups["version"].Value;
            var major = ParseMajor(version);
            return major <= 0 ? null : new JavaRuntime(javaPath, version, major);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "探测 Java 失败: {Path}", javaPath);
            return null;
        }
    }

    /// <summary>把 1.8.0_402 解析成 8，把 17.0.9 解析成 17。</summary>
    public static int ParseMajor(string version)
    {
        var parts = version.Split('.', '_', '-', '+');
        if (parts.Length == 0 || !int.TryParse(parts[0], out var first))
        {
            return 0;
        }

        if (first == 1 && parts.Length > 1 && int.TryParse(parts[1], out var legacy))
        {
            return legacy;
        }

        return first;
    }
}
