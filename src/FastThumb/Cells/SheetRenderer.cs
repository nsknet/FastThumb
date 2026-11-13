using FastThumb.Layout;
using FastThumb.Scene;
using FastThumb.Slides;
using SkiaSharp;

namespace FastThumb.Cells;

internal static class SheetRenderer
{
    private const float Padding = 2f;
    private const int MaxRow = (1 << 20) - 1, MaxColumn = (1 << 14) - 1;   // Excel's grid, 0-based
    private const long MaxMergedCells = 4 << 20;

    /// <summary>Built-in limit of printed pages per workbook.</summary>
    public const int MaxPages = 200;

    /// <summary>Plans and draws printed pages, sheet after sheet, stopping at <paramref name="maxPages"/>.</summary>
    public static PagedDocument Render(IReadOnlyList<SheetSnapshot> sheets, int maxPages = MaxPages)
    {
        var document = new PagedDocument();
        var text = Paginator.ForBoxes(document);
        foreach (var sheet in sheets)
        {
            if (!sheet.Visible || document.Pages.Count >= maxPages) continue;
            RenderSheet(document, text, sheet, maxPages);
        }
        if (document.Pages.Count == 0) document.Pages.Add(new PageScene(612, 792));
        return document;
    }

    private static void RenderSheet(PagedDocument document, Paginator text, SheetSnapshot sheet, int maxPages)
    {
        // printed range: the print area if one is set, else everything that has content or formatting
        int lastRow = -1, lastColumn = -1, firstRow = 0, firstColumn = 0;
        foreach (var (row, cells) in sheet.Rows)
            foreach (var (column, cell) in cells)
            {
                if (cell.Text.Length == 0 && cell.Style.Fill == null && !cell.Style.BorderLeft && !cell.Style.BorderTop && !cell.Style.BorderRight && !cell.Style.BorderBottom) continue;
                lastRow = Math.Max(lastRow, row);
                lastColumn = Math.Max(lastColumn, column);
            }
        foreach (var merge in sheet.Merges)
            if (sheet.Rows.TryGetValue(merge.Row1, out var mergeRow) && mergeRow.ContainsKey(merge.Column1)) { lastRow = Math.Max(lastRow, merge.Row2); lastColumn = Math.Max(lastColumn, merge.Column2); }
        if (sheet.PrintArea is { } area) { firstRow = area.Row1; firstColumn = area.Column1; lastRow = area.Row2; lastColumn = area.Column2; }
        // indices come from the file: kept inside Excel's grid so no loop below can overflow
        firstRow = Math.Clamp(firstRow, 0, MaxRow); lastRow = Math.Min(lastRow, MaxRow);
        firstColumn = Math.Clamp(firstColumn, 0, MaxColumn); lastColumn = Math.Min(lastColumn, MaxColumn);
        if (lastRow < firstRow || lastColumn < firstColumn) return;
        lastRow = Math.Min(lastRow, firstRow + 20000);
        lastColumn = Math.Min(lastColumn, firstColumn + 500);

        float pageWidth = sheet.Landscape ? sheet.PaperHeight : sheet.PaperWidth, pageHeight = sheet.Landscape ? sheet.PaperWidth : sheet.PaperHeight;
        float availableWidth = pageWidth - sheet.MarginLeft - sheet.MarginRight, availableHeight = pageHeight - sheet.MarginTop - sheet.MarginBottom;

        float Width(int column) => sheet.HiddenColumns.Contains(column) ? 0 : sheet.ColumnWidths.GetValueOrDefault(column, sheet.DefaultColumnWidth);
        float Height(int row) => sheet.HiddenRows.Contains(row) ? 0 : sheet.RowHeights.GetValueOrDefault(row, sheet.DefaultRowHeight);

        var scale = sheet.Scale;
        if (sheet.FitToPage)
        {
            float totalWidth = 0, totalHeight = 0;
            for (var c = firstColumn; c <= lastColumn; c++) totalWidth += Width(c);
            for (var r = firstRow; r <= lastRow; r++) totalHeight += Height(r);
            scale = 1;
            if (sheet.FitWidth > 0 && totalWidth > 0) scale = Math.Min(scale, availableWidth * sheet.FitWidth / totalWidth);
            if (sheet.FitHeight > 0 && totalHeight > 0) scale = Math.Min(scale, availableHeight * sheet.FitHeight / totalHeight);
        }
        scale = float.IsNaN(scale) ? 1 : Math.Clamp(scale, 0.1f, 4f);

        // strips: runs of columns / rows that fit one page, cut early at manual page breaks
        static List<(int First, int Last)> Strips(int first, int last, float limit, Func<int, float> size, HashSet<int> breaks)
        {
            var strips = new List<(int, int)>();
            var start = first;
            float used = 0;
            for (var i = first; i <= last; i++)
            {
                var extent = size(i);
                if (i > start && (used + extent > limit + 0.5f || breaks.Contains(i))) { strips.Add((start, i - 1)); start = i; used = 0; }
                used += extent;
            }
            strips.Add((start, last));
            return strips;
        }
        var columnStrips = Strips(firstColumn, lastColumn, availableWidth, c => Width(c) * scale, sheet.ColumnBreaks);
        var rowStrips = Strips(firstRow, lastRow, availableHeight, r => Height(r) * scale, sheet.RowBreaks);

        var merges = new Dictionary<(int, int), (int Row2, int Column2)>();
        var covered = new HashSet<(int, int)>();
        // only the printed part of a merge is ever looked up; past the budget further merges are drawn as single cells
        var budget = MaxMergedCells;
        foreach (var m in sheet.Merges)
        {
            if (m.Row1 > lastRow || m.Column1 > lastColumn || m.Row2 < firstRow || m.Column2 < firstColumn) continue;
            int rowEnd = (int)Math.Min(Math.Min(m.Row2, m.Row1 + 2000L), lastRow), columnEnd = (int)Math.Min(Math.Min(m.Column2, m.Column1 + 200L), lastColumn);
            int rowStart = Math.Max(m.Row1, firstRow), columnStart = Math.Max(m.Column1, firstColumn);
            if (rowEnd >= rowStart && columnEnd >= columnStart && (budget -= (long)(rowEnd - rowStart + 1) * (columnEnd - columnStart + 1)) < 0) break;
            merges[(m.Row1, m.Column1)] = (m.Row2, m.Column2);
            for (var r = rowStart; r <= rowEnd; r++)
                for (var c = columnStart; c <= columnEnd; c++)
                    if (r != m.Row1 || c != m.Column1) covered.Add((r, c));
        }
        var scaled = new Dictionary<RunProps, RunProps>(ReferenceEqualityComparer.Instance);
        RunProps Font(RunProps font)
        {
            if (scale == 1) return font;
            if (scaled.TryGetValue(font, out var hit)) return hit;
            var copy = font.Clone();
            copy.Size *= scale;
            return scaled[font] = copy;
        }

        // Excel's default order: down the rows first, then over to the next block of columns
        foreach (var (column1, column2) in columnStrips)
            foreach (var (row1, row2) in rowStrips)
            {
                if (document.Pages.Count >= maxPages) return;
                var scene = new PageScene(pageWidth, pageHeight);
                var xs = new float[column2 - column1 + 2];
                var ys = new float[row2 - row1 + 2];
                xs[0] = sheet.MarginLeft; ys[0] = sheet.MarginTop;
                for (var c = column1; c <= column2; c++) xs[c - column1 + 1] = xs[c - column1] + Width(c) * scale;
                for (var r = row1; r <= row2; r++) ys[r - row1 + 1] = ys[r - row1] + Height(r) * scale;

                var any = false;
                var lines = new List<(float, float, float, float)>();
                for (var r = row1; r <= row2; r++)
                {
                    if (!sheet.Rows.TryGetValue(r, out var cells) || ys[r - row1 + 1] - ys[r - row1] <= 0) continue;
                    foreach (var (c, cell) in cells)
                    {
                        if (c < column1 || c > column2 || covered.Contains((r, c)) || xs[c - column1 + 1] - xs[c - column1] <= 0) continue;
                        int endRow = r, endColumn = c;
                        if (merges.TryGetValue((r, c), out var merge)) { endRow = Math.Min(merge.Row2, row2); endColumn = Math.Min(merge.Column2, column2); }
                        var box = new SKRect(xs[c - column1], ys[r - row1], xs[endColumn - column1 + 1], ys[endRow - row1 + 1]);
                        var style = cell.Style;
                        if (style.Fill is { } fill) { scene.Shape(ShapeKind.Rect, box, fill, null, 0); any = true; }
                        if (style.BorderTop) lines.Add((box.Left, box.Top, box.Right, box.Top));
                        if (style.BorderBottom) lines.Add((box.Left, box.Bottom, box.Right, box.Bottom));
                        if (style.BorderLeft) lines.Add((box.Left, box.Top, box.Left, box.Bottom));
                        if (style.BorderRight) lines.Add((box.Right, box.Top, box.Right, box.Bottom));
                        if (cell.Text.Length == 0) continue;
                        any = true;

                        var align = style.Align != HAlign.General ? style.Align : cell.Centered ? HAlign.Center : cell.Numeric ? HAlign.Right : HAlign.Left;
                        var textBox = box;
                        if (!style.Wrap && align == HAlign.Left && endColumn == c)
                        {
                            // unwrapped text spills over empty neighbours to its right, as on screen
                            var next = endColumn + 1;
                            while (next <= column2 && !(cells.TryGetValue(next, out var neighbour) && neighbour.Text.Length > 0) && !covered.Contains((r, next))) next++;
                            textBox.Right = xs[next - column1];
                        }
                        var font = Font(style.Font);
                        var paragraph = new Paragraph
                        {
                            Props = new ParaProps { Align = align switch { HAlign.Center => Align.Center, HAlign.Right => Align.Right, _ => Align.Left } },
                            MarkProps = font,
                        };
                        paragraph.Inlines.Add(new TextInline { Props = font, Text = cell.Text });
                        textBox.Left += Padding * scale; textBox.Right -= Padding * scale;
                        // content never leaves its cell vertically: wrapped text keeps only the lines that fit the row
                        var fitting = Math.Max(1, (int)((box.Height + 1) / (font.Size * 1.15f)));
                        if (textBox.Width > 1) text.LayoutBox(scene, [paragraph], textBox, style.VAlign, maxLines: style.Wrap ? fitting : 1);
                    }
                }
                if (sheet.PrintGridlines)
                {
                    for (var c = 0; c < xs.Length; c++) lines.Add((xs[c], ys[0], xs[c], ys[^1]));
                    for (var r = 0; r < ys.Length; r++) lines.Add((xs[0], ys[r], xs[^1], ys[r]));
                }
                foreach (var (x1, y1, x2, y2) in lines.Distinct()) scene.Line(x1, y1, x2, y2, 0.5f, SKColors.Black);
                Guard.Check();
                if (any || lines.Count > 0 || document.Pages.Count == 0) document.Pages.Add(scene); // blank pages are not printed
            }
    }
}
