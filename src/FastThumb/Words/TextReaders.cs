using FastThumb.Layout;

namespace FastThumb.Words;

/// <summary>Plain text (Markdown is <see cref="MarkdownReader"/>).</summary>
internal static class TextReaders
{
    public static IPageSource ReadText(string text)
    {
        var run = new RunProps { Font = "Courier New", Size = 10.5f };
        var para = new ParaProps();
        var blocks = new List<Block>();
        foreach (var line in text.Split('\n'))
        {
            var paragraph = new Paragraph { Props = para, MarkProps = run };
            var value = line.TrimEnd('\r').Replace("\t", "    ");
            if (value.Length > 0) paragraph.Inlines.Add(new TextInline { Props = run, Text = value });
            blocks.Add(paragraph);
        }
        return new StaticSource(PageSetup.Default, blocks);
    }
}
