namespace Solar.Core.Launch;

/// <summary>
/// 读写 Minecraft 的 <c>options.txt</c>（<c>key:value</c> 格式）。
/// 只做最小实现：读一行、改一行、保留其余内容，避免把玩家的其它设置弄丢。
/// </summary>
internal static class OptionsIni
{
    public static string? Read(string path, string key)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        foreach (var line in File.ReadLines(path))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            if (line[..separator].Trim().Equals(key, StringComparison.Ordinal))
            {
                return line[(separator + 1)..].Trim();
            }
        }

        return null;
    }

    public static void Write(string path, string key, string value)
    {
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];

        var replaced = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var separator = lines[i].IndexOf(':');
            if (separator > 0 && lines[i][..separator].Trim().Equals(key, StringComparison.Ordinal))
            {
                lines[i] = $"{key}:{value}";
                replaced = true;
                break;
            }
        }

        if (!replaced)
        {
            lines.Add($"{key}:{value}");
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllLines(path, lines);
    }
}
