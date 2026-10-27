using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Solar.Core.Text;
using FontStyle = Windows.UI.Text.FontStyle;

namespace Solar.App.Services;

/// <summary>
/// 把 <see cref="MarkdownLite"/> 的解析结果铺进一个 <see cref="RichTextBlock"/>。
/// <para>
/// 用 RichTextBlock 而不是 WebView：正文只是排版好的文字，WebView 要起一个浏览器进程、
/// 还得处理外链与脚本，成本远大于收益。
/// </para>
/// <para>
/// 次要文字的颜色由调用方从 <c>ThemeResource</c> 取好再传进来——
/// 渲染器自己查不到主题字典，写死颜色又会在切换浅色 / 深色时失效。
/// </para>
/// </summary>
public static class MarkdownBlockRenderer
{
    public static void Render(
        RichTextBlock target,
        IEnumerable<MarkdownBlock> blocks,
        Brush? secondaryForeground = null)
    {
        target.Blocks.Clear();

        foreach (var block in blocks)
        {
            target.Blocks.Add(ToParagraph(block, secondaryForeground));
        }
    }

    private static Paragraph ToParagraph(MarkdownBlock block, Brush? secondary)
    {
        var paragraph = new Paragraph
        {
            // 标题后面多留一点空，正文段落之间留一行
            Margin = block.Kind == MarkdownBlockKind.Heading
                ? new Thickness(0, 16, 0, 4)
                : new Thickness(0, 0, 0, 8),
        };

        // RichTextBlock 没有列表结构，用行首圆点模拟
        if (block.Kind == MarkdownBlockKind.ListItem)
        {
            paragraph.Inlines.Add(new Run { Text = "•  " });
        }

        foreach (var span in block.Spans)
        {
            paragraph.Inlines.Add(ToInline(span, block.Kind, secondary));
        }

        return paragraph;
    }

    private static Inline ToInline(MarkdownSpan span, MarkdownBlockKind blockKind, Brush? secondary)
    {
        switch (span.Kind)
        {
            case MarkdownSpanKind.Link when Uri.TryCreate(span.Url, UriKind.Absolute, out var uri):
            {
                var link = new Hyperlink { NavigateUri = uri };
                link.Inlines.Add(new Run { Text = span.Text });
                return link;
            }

            case MarkdownSpanKind.Bold:
            {
                var bold = new Bold();
                bold.Inlines.Add(new Run { Text = span.Text });
                return bold;
            }

            case MarkdownSpanKind.Code:
                return new Run
                {
                    Text = span.Text,
                    FontFamily = new FontFamily("Consolas"),
                    Foreground = secondary,
                };

            default:
            {
                var run = new Run { Text = span.Text };

                // 引用整段用次要色 + 斜体，视觉上退到后面
                if (blockKind == MarkdownBlockKind.Quote)
                {
                    run.Foreground = secondary;
                    run.FontStyle = FontStyle.Italic;
                }

                // 标题块整段加粗：MarkdownLite 丢掉了标题层级，这里统一按小标题处理
                if (blockKind == MarkdownBlockKind.Heading)
                {
                    run.FontWeight = FontWeights.SemiBold;
                }

                return run;
            }
        }
    }
}
