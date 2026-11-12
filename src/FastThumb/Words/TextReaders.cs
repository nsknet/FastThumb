using System.Text;
using FastThumb.Layout;

namespace FastThumb.Words;

/// <summary>Plain text and Markdown.</summary>
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

    public static IPageSource ReadMarkdown(string text)
    {
        var body = new RunProps { Size = 12 };
        var blocks = new List<Block>();
        var pending = new StringBuilder();
        var inCode = false;

        Table? table = null;
        void FlushTable()
        {
            if (table == null) return;
            table.Grid = new float[table.Rows.Max(r => r.Cells.Count)];
            blocks.Add(table);
            table = null;
        }

        void Flush()
        {
            if (pending.Length == 0) return;
            var paragraph = new Paragraph { Props = new ParaProps { SpaceAfter = 8 }, MarkProps = body };
            Emphasis(pending.ToString(), body, paragraph.Inlines);
            blocks.Add(paragraph);
            pending.Clear();
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal)) { Flush(); inCode = !inCode; continue; }
            if (inCode)
            {
                var code = new RunProps { Font = "Courier New", Size = 10 };
                var paragraph = new Paragraph { Props = new ParaProps(), MarkProps = code };
                if (line.Length > 0) paragraph.Inlines.Add(new TextInline { Props = code, Text = line });
                blocks.Add(paragraph);
                continue;
            }
            if (trimmed.Length == 0) { Flush(); FlushTable(); continue; }

            if (trimmed.StartsWith('|'))
            {
                Flush();
                var columns = trimmed.Trim('|').Split('|');
                if (columns.All(c => c.Trim().Trim(':', '-', ' ').Length == 0)) continue; // header separator row
                var row = new Row();
                foreach (var column in columns)
                {
                    var cell = new Cell();
                    foreach (var piece in column.Split("<br>", StringSplitOptions.None))
                    {
                        var paragraph = new Paragraph { Props = new ParaProps(), MarkProps = body };
                        Emphasis(piece.Trim(), body, paragraph.Inlines);
                        cell.Blocks.Add(paragraph);
                    }
                    row.Cells.Add(cell);
                }
                (table ??= new Table()).Rows.Add(row);
                continue;
            }
            FlushTable();

            var level = trimmed.TakeWhile(c => c == '#').Count();
            if (level is > 0 and <= 6 && trimmed.Length > level && trimmed[level] == ' ')
            {
                Flush();
                var heading = new RunProps { Size = level switch { 1 => 24, 2 => 18, 3 => 14, 4 => 12, 5 => 10, _ => 9 }, Bold = true };
                var paragraph = new Paragraph { Props = new ParaProps { SpaceBefore = 12, SpaceAfter = 6 }, MarkProps = heading };
                Emphasis(trimmed[(level + 1)..].TrimEnd('#', ' '), heading, paragraph.Inlines);
                blocks.Add(paragraph);
                continue;
            }

            var digits = trimmed.TakeWhile(char.IsDigit).Count();
            var bullet = trimmed.Length > 1 && trimmed[0] is '-' or '*' or '+' && trimmed[1] == ' ';
            var numbered = digits > 0 && trimmed.Length > digits + 1 && trimmed[digits] == '.' && trimmed[digits + 1] == ' ';
            if (bullet || numbered)
            {
                Flush();
                var indent = 18 + (line.Length - trimmed.Length) * 6;
                var paragraph = new Paragraph { Props = new ParaProps { IndentLeft = indent + 14, FirstLine = -14, SpaceAfter = 2 }, MarkProps = body };
                paragraph.Inlines.Add(new TextInline { Props = body, Text = bullet ? "•  " : trimmed[..(digits + 1)] + " " });
                Emphasis(trimmed[(bullet ? 2 : digits + 2)..], body, paragraph.Inlines);
                blocks.Add(paragraph);
                continue;
            }
            if (pending.Length > 0) pending.Append(' ');
            pending.Append(trimmed);
        }
        Flush();
        FlushTable();
        return new StaticSource(PageSetup.Default, blocks);
    }

    /// <summary>**bold**, *italic*, `code`; everything else is literal text.</summary>
    private static void Emphasis(string text, RunProps baseRun, List<Inline> output)
    {
        text = System.Text.RegularExpressions.Regex.Replace(text.Replace("<br>", " "), "</?[a-zA-Z][^>]*>", "",
            System.Text.RegularExpressions.RegexOptions.NonBacktracking); // inline HTML left by converters; linear time on unclosed tags
        var current = baseRun;
        var buffer = new StringBuilder();
        void Emit() { if (buffer.Length > 0) output.Add(new TextInline { Props = current, Text = buffer.ToString() }); buffer.Clear(); }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '*' or '_' && i + 1 < text.Length && text[i + 1] == c)
            {
                Emit();
                current = current.Clone();
                current.Bold = !current.Bold;
                i++;
            }
            else if (c == '*' || (c == '_' && (i == 0 || text[i - 1] == ' ' || i + 1 == text.Length || text[i + 1] == ' ')))
            {
                Emit();
                current = current.Clone();
                current.Italic = !current.Italic;
            }
            else if (c == '`')
            {
                Emit();
                current = current.Clone();
                current.Font = current.Font == "Courier New" ? baseRun.Font : "Courier New";
            }
            else buffer.Append(c);
        }
        Emit();
    }
}
