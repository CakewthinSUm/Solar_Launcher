using System.Text;

namespace Solar.Core.Launch;

/// <summary>
/// 增量读取游戏日志，识别加载里程碑与崩溃关键字。
/// <para>
/// 参照 PCL 的 <c>Watcher</c>：窗口迟迟不出现时，日志能说明游戏走到哪一步；
/// 有些环境（反作弊、安全软件）会挡住窗口枚举，这时"材质已加载"就足以判定启动成功。
/// </para>
/// </summary>
internal sealed class GameLogMonitor(string logPath)
{
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _pending = new();
    private readonly List<string> _tail = [];

    private long _position;

    /// <summary>加载里程碑：1 有输出、2 用户已设置、3 LWJGL 就绪、4 声音引擎、5 材质已加载。</summary>
    public int Milestones { get; private set; }

    public bool CrashDetected { get; private set; }

    public string? CrashLine { get; private set; }

    /// <summary>游戏开始退出（有人在关我 / 重启命令）。</summary>
    public bool Ended { get; private set; }

    /// <summary>最近若干行，失败时随错误一起展示。</summary>
    public string Tail => string.Join('\n', _tail);

    /// <summary>读取新增的日志；日志被替换或截断时从头再来。</summary>
    public void Poll()
    {
        try
        {
            using var stream = new FileStream(
                logPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            if (stream.Length < _position)
            {
                _position = 0;
                _pending.Clear();
            }

            if (stream.Length == _position)
            {
                return;
            }

            stream.Seek(_position, SeekOrigin.Begin);
            var buffer = new byte[8192];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                _position += read;
                var chars = new char[_decoder.GetCharCount(buffer, 0, read)];
                var count = _decoder.GetChars(buffer, 0, read, chars, 0);
                _pending.Append(chars, 0, count);
            }

            FlushCompleteLines();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 日志正被写入或还没创建，下一次再试
        }
    }

    private void FlushCompleteLines()
    {
        var text = _pending.ToString();
        var lastNewline = text.LastIndexOf('\n');
        if (lastNewline < 0)
        {
            return;
        }

        var complete = text[..lastNewline];
        _pending.Clear();
        _pending.Append(text[(lastNewline + 1)..]);

        foreach (var line in complete.Split('\n'))
        {
            HandleLine(line.TrimEnd('\r'));
        }
    }

    private void HandleLine(string line)
    {
        if (line.Length == 0)
        {
            return;
        }

        _tail.Add(line);
        if (_tail.Count > 30)
        {
            _tail.RemoveAt(0);
        }

        TrackMilestones(line);

        // 聊天栏里的文字不算：玩家可能正好在聊天里发这些词
        if (line.Contains("[CHAT]", StringComparison.Ordinal))
        {
            return;
        }

        if (line.Contains("Crash report saved to", StringComparison.Ordinal)
            || line.Contains("This crash report has been saved to:", StringComparison.Ordinal)
            || line.Contains("Could not save crash report to", StringComparison.Ordinal)
            || line.Contains("/ERROR]: Unable to launch", StringComparison.Ordinal)
            || line.Contains("An exception was thrown, the game will display an error screen and halt.", StringComparison.Ordinal))
        {
            CrashDetected = true;
            CrashLine = line;
        }
        else if (line.Contains("Someone is closing me!", StringComparison.Ordinal)
                 || line.Contains("Restarting Minecraft with command", StringComparison.Ordinal))
        {
            Ended = true;
        }
    }

    private void TrackMilestones(string line)
    {
        if (Milestones < 1)
        {
            Milestones = 1;
        }

        if (Milestones < 2 && line.Contains("Setting user:", StringComparison.Ordinal))
        {
            Milestones = 2;
        }
        else if (Milestones < 3 && line.Contains("lwjgl version", StringComparison.OrdinalIgnoreCase))
        {
            Milestones = 3;
        }
        else if (Milestones < 4
                 && (line.Contains("OpenAL initialized", StringComparison.Ordinal)
                     || line.Contains("Starting up SoundSystem", StringComparison.Ordinal)))
        {
            Milestones = 4;
        }
        else if (Milestones < 5
                 && ((line.Contains("Created", StringComparison.Ordinal)
                      && line.Contains("textures", StringComparison.Ordinal)
                      && line.Contains("-atlas", StringComparison.Ordinal))
                     || line.Contains("Found animation info", StringComparison.Ordinal)))
        {
            Milestones = 5;
        }
    }
}
