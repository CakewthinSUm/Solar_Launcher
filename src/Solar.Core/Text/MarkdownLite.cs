namespace Solar.Core.Text;

/// <summary>Markdown 块的类型。只覆盖模组详情正文里实际会出现的几种。</summary>
public enum MarkdownBlockKind
{
    Heading,
    Paragraph,
    ListItem,
    Quote,
    Code,
}

/// <summary>行内片段的类型。</summary>
public enum MarkdownSpanKind
{
    Plain,
    Bold,
    Code,
    Link,
}

/// <summary>一段行内文本。<see cref="Url"/> 只在 <see cref="MarkdownSpanKind.Link"/> 时有值。</summary>
public sealed record MarkdownSpan(string Text, MarkdownSpanKind Kind, string? Url = null);

/// <summary>一个块级元素。标题与列表项的层级信息已经丢掉了，渲染时统一按正文处理。</summary>
public sealed record MarkdownBlock(
    MarkdownBlockKind Kind,
    string Text,
    IReadOnlyList<MarkdownSpan> Spans);

/// <summary>
/// 极简 Markdown 解析器，只给模组详情正文用。
/// <para>
/// 不追求规范完整：Modrinth 的正文是作者手写的 README，实际只用到标题、段落、列表、
/// 引用、行内代码、粗体和链接。图片、表格、HTML 一律丢弃——
/// 图片在详情页会从截图列表单独展示，正文里再嵌一份既慢又容易因为 WebP 解码失败留白。
/// </para>
/// <para>
/// 输出是纯数据，渲染交给 UI 层（<c>MarkdownBlockRenderer</c>），
/// 这样解析规则可以在 Core 里单测，不必启动 WinUI。
/// </para>
/// </summary>
public static class MarkdownLite
{
    /// <summary>块之间的空行数、单行长度都不可信，给个上限防止一篇超长 README 拖慢界面。</summary>
    private const int MaxBlocks = 400;

    public static IReadOnlyList<MarkdownBlock> Parse(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return [];
        }

        var blocks = new List<MarkdownBlock>();
        var paragraph = new List<string>();
        var code = new List<string>();
        var inCode = false;

        foreach (var rawLine in markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = rawLine.TrimEnd();

            // 围栏代码块：``` 或 ~~~ 开始与结束，内部原样保留
            if (IsFence(line))
            {
                if (inCode)
                {
                    AddBlock(blocks, MarkdownBlockKind.Code, string.Join("\n", code));
                    code.Clear();
                    inCode = false;
                }
                else
                {
                    FlushParagraph(blocks, paragraph);
                    inCode = true;
                }

                continue;
            }

            if (inCode)
            {
                code.Add(rawLine);
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph(blocks, paragraph);
                continue;
            }

            var trimmed = line.TrimStart();

            if (HeadingText(trimmed) is { } heading)
            {
                FlushParagraph(blocks, paragraph);
                AddBlock(blocks, MarkdownBlockKind.Heading, heading);
                continue;
            }

            if (trimmed.StartsWith('>'))
            {
                FlushParagraph(blocks, paragraph);
                AddBlock(blocks, MarkdownBlockKind.Quote, trimmed.TrimStart('>').Trim());
                continue;
            }

            if (ListItemText(trimmed) is { } item)
            {
                FlushParagraph(blocks, paragraph);
                AddBlock(blocks, MarkdownBlockKind.ListItem, item);
                continue;
            }

            // 水平线只是一条分隔，没有信息量
            if (trimmed is "---" or "***" or "___")
            {
                FlushParagraph(blocks, paragraph);
                continue;
            }

            paragraph.Add(trimmed);
        }

        // 没闭合的围栏也把已收集的内容当代码块输出，不要让整段正文消失
        if (inCode)
        {
            AddBlock(blocks, MarkdownBlockKind.Code, string.Join("\n", code));
        }

        FlushParagraph(blocks, paragraph);
        return blocks;
    }

    /// <summary>解析一行行内标记。图片在进入这里之前已经被 <see cref="StripImages"/> 去掉。</summary>
    public static IReadOnlyList<MarkdownSpan> ParseInline(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var spans = new List<MarkdownSpan>();
        var buffer = new System.Text.StringBuilder();

        void FlushPlain()
        {
            if (buffer.Length > 0)
            {
                spans.Add(new MarkdownSpan(buffer.ToString(), MarkdownSpanKind.Plain));
                buffer.Clear();
            }
        }

        for (var i = 0; i < text.Length;)
        {
            // 行内代码：反引号之间的内容原样保留，内部不再解析其它标记
            if (text[i] == '`')
            {
                var end = text.IndexOf('`', i + 1);
                if (end > i + 1)
                {
                    FlushPlain();
                    spans.Add(new MarkdownSpan(text[(i + 1)..end], MarkdownSpanKind.Code));
                    i = end + 1;
                    continue;
                }
            }

            // 粗体：**text**
            if (text[i] == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    FlushPlain();
                    spans.Add(new MarkdownSpan(text[(i + 2)..end], MarkdownSpanKind.Bold));
                    i = end + 2;
                    continue;
                }
            }

            // 链接：[text](url)
            if (text[i] == '[')
            {
                var close = text.IndexOf(']', i + 1);
                if (close > i + 1 && close + 1 < text.Length && text[close + 1] == '(')
                {
                    var paren = text.IndexOf(')', close + 2);
                    if (paren > close + 2)
                    {
                        var url = text[(close + 2)..paren].Trim();

                        // 相对链接与锚点在桌面端没有意义，退化成普通文本
                        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
                            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                        {
                            FlushPlain();
                            spans.Add(new MarkdownSpan(text[(i + 1)..close], MarkdownSpanKind.Link, uri.ToString()));
                            i = paren + 1;
                            continue;
                        }
                    }
                }
            }

            buffer.Append(text[i]);
            i++;
        }

        FlushPlain();
        return spans;
    }

    /// <summary>
    /// 去掉裸链接形态的徽章/图片：<c>[![alt](img)](href)</c> 与 <c>[![badge](img)][ref]</c>。
    /// </summary>
    private static string StripLinkedImages(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);

        for (var i = 0; i < text.Length;)
        {
            // [![ ... ]( ... )]( ... ) —— 嵌套一层链接包着图片，README 顶部几乎都是这种徽章
            if (text[i] == '[' && i + 2 < text.Length && text[i + 1] == '!' && text[i + 2] == '[')
            {
                var imageClose = text.IndexOf(']', i + 3);

                if (imageClose > 0 && imageClose + 1 < text.Length && text[imageClose + 1] == '(')
                {
                    var imageParen = text.IndexOf(')', imageClose + 2);

                    if (imageParen > 0 && imageParen + 1 < text.Length && text[imageParen + 1] == ']')
                    {
                        var outer = imageParen + 2;

                        // 外层链接的地址也要一起吞掉，否则会剩下一段孤零零的 (https://…)
                        if (outer < text.Length && text[outer] == '(')
                        {
                            var linkParen = text.IndexOf(')', outer + 1);
                            if (linkParen > 0)
                            {
                                i = linkParen + 1;
                                continue;
                            }
                        }

                        // 引用式链接 [![alt](img)][ref]：方括号部分整体丢掉
                        i = outer;
                        continue;
                    }
                }
            }

            builder.Append(text[i]);
            i++;
        }

        return builder.ToString();
    }

    /// <summary>
    /// 去掉 HTML 标签（<c>&lt;center&gt;</c>、<c>&lt;img …&gt;</c>、注释）。
    /// Modrinth 的正文允许内嵌 HTML，渲染器不解释它，留着就是一串尖括号。
    /// </summary>
    private static string StripHtml(string text)
    {
        if (!text.Contains('<'))
        {
            return text;
        }

        var builder = new System.Text.StringBuilder(text.Length);
        var inComment = false;

        for (var i = 0; i < text.Length;)
        {
            if (!inComment && i + 3 < text.Length && text.AsSpan(i, 4).SequenceEqual("<!--"))
            {
                inComment = true;
                i += 4;
                continue;
            }

            if (inComment)
            {
                if (i + 2 < text.Length && text.AsSpan(i, 3).SequenceEqual("-->"))
                {
                    inComment = false;
                    i += 3;
                    continue;
                }

                i++;
                continue;
            }

            if (text[i] == '<')
            {
                var close = text.IndexOf('>', i + 1);

                // 只有“看起来像标签”才吞掉：< 后面紧跟字母或 /，否则是数学比较之类的内容
                if (close > i + 1 && (char.IsLetter(text[i + 1]) || text[i + 1] == '/'))
                {
                    i = close + 1;
                    continue;
                }
            }

            builder.Append(text[i]);
            i++;
        }

        return builder.ToString();
    }

    /// <summary>
    /// 清掉删除后留下的空标记：<c>[]()</c>、<c>[]</c>、<c>()</c>、连续空格。
    /// 只处理「内容为空」的形态，正文里正常的链接与括号不受影响。
    /// </summary>
    private static string CollapseBlankets(string text)
    {
        // 删掉图片后最常见的残留就是两个连续空格，所以这里也要看进去
        if (!text.Contains('[') && !text.Contains("()") && !text.Contains("  ", StringComparison.Ordinal))
        {
            return text;
        }

        var result = text
            .Replace("[]()", "", StringComparison.Ordinal)
            .Replace("[]", "", StringComparison.Ordinal);

        // 括号里只剩空白也算空
        while (true)
        {
            var start = result.IndexOf("( ", StringComparison.Ordinal);
            if (start < 0)
            {
                break;
            }

            var end = result.IndexOf(')', start);
            if (end < 0 || result.AsSpan(start + 1, end - start - 1).Trim().Length > 0)
            {
                break;
            }

            result = result.Remove(start, end - start + 1);
        }

        // 删标记后常留下成串空格，压成一个
        while (result.Contains("  ", StringComparison.Ordinal))
        {
            result = result.Replace("  ", " ", StringComparison.Ordinal);
        }

        return result;
    }

    /// <summary>去掉 <c>![alt](url)</c>。图片单独走截图列表，正文里不渲染。</summary>
    internal static string StripImages(string text)
    {
        if (!text.Contains("!["))
        {
            return text;
        }

        var builder = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length;)
        {
            if (text[i] == '!' && i + 1 < text.Length && text[i + 1] == '[')
            {
                var close = text.IndexOf(']', i + 2);
                if (close > 0 && close + 1 < text.Length && text[close + 1] == '(')
                {
                    var paren = text.IndexOf(')', close + 2);
                    if (paren > 0)
                    {
                        i = paren + 1;
                        continue;
                    }
                }
            }

            builder.Append(text[i]);
            i++;
        }

        return builder.ToString();
    }

    private static void AddBlock(List<MarkdownBlock> blocks, MarkdownBlockKind kind, string text)
    {
        if (blocks.Count >= MaxBlocks)
        {
            return;
        }

        // 顺序有讲究：先拆掉「链接包图片」的徽章（否则内层图片被拿掉后会剩一对空方括号），
        // 再去图片、去 HTML，最后清掉因此留下的空括号与多余空白
        var cleaned = CollapseBlankets(StripHtml(StripImages(StripLinkedImages(text)))).Trim();

        // 图片去掉后可能只剩空白（整行只有一张图），这种块没有意义
        if (cleaned.Length == 0)
        {
            return;
        }

        blocks.Add(new MarkdownBlock(kind, cleaned, ParseInline(cleaned)));
    }

    private static void FlushParagraph(List<MarkdownBlock> blocks, List<string> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        // 软换行按空格拼回一行：README 里为了排版折行的句子要连起来读
        var text = string.Join(" ", lines.Select(l => l.Trim())).Trim();
        lines.Clear();
        AddBlock(blocks, MarkdownBlockKind.Paragraph, text);
    }

    private static bool IsFence(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("```", StringComparison.Ordinal)
               || trimmed.StartsWith("~~~", StringComparison.Ordinal);
    }

    /// <summary><c># 标题</c> 到 <c>###### 标题</c>。</summary>
    private static string? HeadingText(string trimmed)
    {
        var level = 0;
        while (level < trimmed.Length && trimmed[level] == '#')
        {
            level++;
        }

        if (level is 0 or > 6)
        {
            return null;
        }

        // #hashtag 不是标题，必须跟一个空格
        if (level >= trimmed.Length || trimmed[level] != ' ')
        {
            return null;
        }

        return trimmed[level..].Trim();
    }

    /// <summary><c>- item</c>、<c>* item</c>、<c>1. item</c>。</summary>
    private static string? ListItemText(string trimmed)
    {
        if (trimmed.Length >= 2 && (trimmed[0] is '-' or '*' or '+') && trimmed[1] == ' ')
        {
            return trimmed[2..].Trim();
        }

        var digits = 0;
        while (digits < trimmed.Length && char.IsAsciiDigit(trimmed[digits]))
        {
            digits++;
        }

        if (digits is > 0 and <= 3
            && digits + 1 < trimmed.Length
            && trimmed[digits] is '.' or ')'
            && trimmed[digits + 1] == ' ')
        {
            return trimmed[(digits + 2)..].Trim();
        }

        return null;
    }
}
