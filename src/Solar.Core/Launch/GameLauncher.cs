using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Solar.Core.Paths;

namespace Solar.Core.Launch;

public interface IGameLauncher
{
    /// <summary>启动游戏进程，返回运行中的进程。调用方负责等待退出。</summary>
    Process Start(LaunchContext context);
}

public sealed class GameLauncher(AppPaths paths, ILogger<GameLauncher> logger) : IGameLauncher
{
    public Process Start(LaunchContext context)
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
        };

        foreach (var argument in jvmArguments.Concat(gameArguments))
        {
            startInfo.ArgumentList.Add(argument);
        }

        var logPath = Path.Combine(paths.Logs, $"game-{context.Instance.Id}-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.log");
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

        return process;
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
