using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Solar.Core.Paths;

namespace Solar.Core.Launch;

/// <summary>一次启动的结果：游戏进程与它正在写的日志文件。</summary>
public sealed record LaunchedGame(Process Process, string LogPath);

public interface IGameLauncher
{
    /// <summary>启动游戏进程。调用方负责等待窗口出现与退出。</summary>
    LaunchedGame Start(LaunchContext context);
}

public sealed class GameLauncher(AppPaths paths, ILogger<GameLauncher> logger) : IGameLauncher
{
    public LaunchedGame Start(LaunchContext context)
    {
        var builder = new ArgumentBuilder();
        var jvmArguments = builder.BuildJvmArguments(context);
        var gameArguments = builder.BuildGameArguments(context);

        Directory.CreateDirectory(context.GameDirectory);
        Directory.CreateDirectory(context.NativesDirectory);

        var startInfo = new ProcessStartInfo(context.JavaPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = context.GameDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,

            // 启动参数里给游戏固定了 UTF-8 输出（stdout.encoding），
            // 这里必须按同一种编码解码，否则中文日志会变成乱码
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };

        foreach (var argument in jvmArguments.Concat(gameArguments))
        {
            startInfo.ArgumentList.Add(argument);
        }

        var logPath = Path.Combine(paths.Logs, $"game-{context.Game.Id}-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.log");
        logger.LogInformation("启动游戏，日志: {LogPath}", logPath);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        // 游戏输出可能很多，必须异步写盘，否则管道写满会让进程卡住
        var logWriter = new StreamWriter(logPath, append: false) { AutoFlush = true };
        process.OutputDataReceived += (_, e) => WriteLine(logWriter, e.Data);
        process.ErrorDataReceived += (_, e) => WriteLine(logWriter, e.Data);
        process.Exited += (_, _) =>
        {
            try
            {
                logWriter.Flush();
                logWriter.Dispose();
            }
            catch
            {
                // 进程已退出，写日志失败不影响后续处理
            }
        };

        if (!process.Start())
        {
            logWriter.Dispose();
            throw new InvalidOperationException("无法启动游戏进程。");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        logger.LogInformation(
            "游戏进程已启动: pid={Pid}, java={Java}",
            process.Id,
            context.JavaPath);

        return new LaunchedGame(process, logPath);
    }

    private static void WriteLine(TextWriter writer, string? line)
    {
        if (line is null)
        {
            return;
        }

        try
        {
            writer.WriteLine(line);
        }
        catch (ObjectDisposedException)
        {
            // 进程退出时日志流可能已关闭
        }
    }
}
