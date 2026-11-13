using System.Text;

namespace FastThumb.Scene;

/// <summary>
/// Plain text of a laid-out page, read back from its text runs. The layout already put every word on a baseline in reading
/// order, so this only has to decide what separates two runs: nothing, a space, a tab (far apart on one line), a line break,
/// or a blank line (a gap of more than a line).
/// </summary>
internal static class PageText
{
    public static string Read(PageScene page)
    {
        var text = new StringBuilder();
        float lastBaseline = float.NaN, lastEnd = 0, lastSize = 0;
        foreach (var op in page.Ops)
        {
            if (op is not PageScene.TextOp { Source: { Length: > 0 } source } run || run.Positions.Length == 0) continue;
            var start = run.Positions[0];
            var size = Math.Max(1f, run.Font.Size);
            if (!float.IsNaN(lastBaseline))
            {
                var drop = start.Y - lastBaseline;
                var gap = start.X - lastEnd;
                // same line: the baseline barely moves (superscripts, mixed sizes) and the run does not jump far back left
                // (a right-aligned number spilling over the cell before it overlaps a little and stays on the line)
                if (Math.Abs(drop) < 0.4f * Math.Max(size, lastSize) && gap > -4f * size)
                {
                    if (gap > 2.5f * size) Separate(text, '\t');
                    else if (gap > 0.15f * size || gap < -0.05f * size) Separate(text, ' ');
                }
                else
                {
                    TrimEnd(text);
                    text.Append('\n');
                    if (drop > 2.2f * Math.Max(size, lastSize)) text.Append('\n');
                }
            }
            text.Append(source);
            lastBaseline = start.Y;
            lastSize = size;
            lastEnd = run.Positions[^1].X + Advance(run);
        }
        // a run of spaces alone (an empty paragraph with a space in it) would leave a line of blanks
        var lines = text.ToString().Split('\n').Select(line => line.TrimEnd(' ', '\t', ' '));
        var joined = string.Join('\n', lines).Trim('\n');
        return joined.Length == 0 ? "" : joined + "\n";
    }

    /// <summary>Width of the last glyph of a run, so the gap to the next run is measured from where this one ends.</summary>
    private static float Advance(PageScene.TextOp run)
    {
        Span<ushort> last = [run.Glyphs[^1]];
        Span<float> width = stackalloc float[1];
        run.Font.GetGlyphWidths(last, width, Span<SkiaSharp.SKRect>.Empty);
        return width[0];
    }

    private static void Separate(StringBuilder text, char separator)
    {
        if (text.Length == 0 || text[^1] is ' ' or '\t' or '\n') return;
        text.Append(separator);
    }

    private static void TrimEnd(StringBuilder text)
    {
        while (text.Length > 0 && text[^1] is ' ' or '\t') text.Length--;
    }
}
