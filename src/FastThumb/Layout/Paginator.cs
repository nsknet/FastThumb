using FastThumb.Scene;
using SkiaSharp;

namespace FastThumb.Layout;

/// <summary>
/// Pulls blocks one at a time and lays out lines / table rows page after page. With maxPages = 1 it stops the
/// moment page 1 is full and the reader is never asked for more content.
/// </summary>
internal sealed class Paginator
{
    private const float CellPadding = 5.4f;   // Word's default 0.075" left/right cell margin
    private const float TabWidth = 36f;
    private const float AutoSpacing = 14f;    // Word's "auto" paragraph spacing
    private static readonly SKColor BorderColor = SKColors.Black;

    private readonly IPageSource _source;
    private readonly PagedDocument _document;
    private readonly int _maxPages;
    private float _top;
    private readonly float _bottom;
    private int _floats;                      // floating objects already placed at the bottom of the current page's z-order
    private readonly Dictionary<RunProps, (SKFont Font, LineMetrics Metrics)> _fonts = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(SKTypeface Face, float Size), (SKFont Font, LineMetrics Metrics)> _fontsByFace = new();
    // per-document shaping cache: the same word in the same font is measured once (documents repeat words heavily)
    private readonly Dictionary<(SKFont Font, string Text), (ushort[] Glyphs, float[] Advances, float Width)> _words = new();
    private PageScene _scene = null!;
    private int _cellDepth;
    private int _lineBudget = int.MaxValue;   // box mode: lines still allowed in the current box

    private Paginator(IPageSource source, int maxPages)
    {
        _source = source;
        _document = new PagedDocument();
        _maxPages = maxPages;
        _top = source.Page.Top;
        _bottom = source.Page.Height - source.Page.Bottom;
        NewPage();
    }

    private Paginator(PagedDocument document)
    {
        _source = new StaticSource(PageSetup.Default, []);
        _document = document;
        _maxPages = 1;
        _bottom = float.MaxValue;
        _cellDepth = 1; // box mode behaves like a table cell: text never starts a new page
    }

    /// <summary>A text-box layouter that shares fonts and the word cache across all boxes of one document.</summary>
    public static Paginator ForBoxes(PagedDocument document) => new(document);

    /// <summary>Lays blocks out inside <paramref name="box"/> on an existing scene. anchor: 0 top, 1 middle, 2 bottom.</summary>
    public void LayoutBox(PageScene scene, IReadOnlyList<Block> blocks, SKRect box, int anchor, int maxLines = int.MaxValue)
    {
        _scene = scene;
        _lineBudget = maxLines;
        var mark = scene.Count;
        var y = box.Top;
        Layout(blocks, box.Left, box.Width, ref y);
        if (anchor == 0) return;
        var offset = (box.Height - (y - box.Top)) * (anchor == 1 ? 0.5f : 1f);
        if (Math.Abs(offset) < 0.5f) return;
        scene.Truncate(mark); // second pass at the anchored position (the word cache makes it cheap)
        _lineBudget = maxLines;
        y = box.Top + offset;
        Layout(blocks, box.Left, box.Width, ref y);
    }

    public static PagedDocument Build(IPageSource source, int maxPages = 3000)
    {
        var paginator = new Paginator(source, Math.Max(1, maxPages));
        var page = source.Page;
        var y = paginator._top;
        paginator.Layout(source.Blocks(), page.Left, page.Width - page.Left - page.Right, ref y);
        return paginator._document;
    }

    /// <returns>false when the page limit is reached: layout must stop</returns>
    private bool NewPage()
    {
        if (_document.Pages.Count >= _maxPages) return false;
        _scene = new PageScene(_source.Page.Width, _source.Page.Height);
        Guard.Check();
        _document.Pages.Add(_scene);
        _floats = 0;

        // header and footer of this page; a header taller than the top margin pushes the body down
        var page = _source.Page;
        var first = _document.Pages.Count == 1;
        float left = page.Left, width = page.Width - page.Left - page.Right;
        _top = page.Top;
        _cellDepth++;
        try
        {
            if (_source.Header(first) is { Count: > 0 } header)
            {
                var y = _source.HeaderDistance;
                Layout(header, left, width, ref y);
                _top = Math.Max(page.Top, Math.Min(y, page.Height * 0.5f));
            }
            if (_source.Footer(first) is { Count: > 0 } footer)
            {
                var mark = _scene.Count;
                var y = 0f;
                Layout(footer, left, width, ref y);       // measuring pass
                _scene.Truncate(mark);
                y = Math.Max(page.Height - _source.FooterDistance - y, page.Height * 0.5f);
                Layout(footer, left, width, ref y);
            }
        }
        finally { _cellDepth--; }
        return true;
    }

    private (SKFont Font, LineMetrics Metrics) FontOf(RunProps props)
    {
        if (_fonts.TryGetValue(props, out var hit)) return hit;
        var key = (FontCache.Get(props), props.Size);
        if (!_fontsByFace.TryGetValue(key, out hit))
        {
            var font = _document.Own(new SKFont(key.Item1, props.Size)
            { Subpixel = true, Edging = SKFontEdging.Antialias, Hinting = SKFontHinting.None, LinearMetrics = true });
            var metrics = font.Metrics;
            _fontsByFace[key] = hit = (font, new LineMetrics(metrics.Ascent, metrics.Descent, metrics.Leading));
        }
        // a stand-in face keeps the requested family's line spacing (Segoe UI set in Liberation Sans still spaces like Segoe UI)
        return _fonts[props] = (hit.Font, FontCache.LineSpacing(props) is { } original ? original.Scale(props.Size) : hit.Metrics);
    }

    private (ushort[] Glyphs, float[] Advances, float Width) Shape(SKFont font, string text)
    {
        if (_words.TryGetValue((font, text), out var hit)) return hit;
        var glyphs = font.GetGlyphs(text);
        var advances = font.GetGlyphWidths(glyphs);
        float width = 0;
        foreach (var advance in advances) width += advance;
        return _words[(font, text)] = (glyphs, advances, width);
    }

    /// <returns>true when layout must stop (page limit reached)</returns>
    private bool Layout(IEnumerable<Block> blocks, float x, float width, ref float y)
    {
        Guard.Deeper();
        // Spacing between blocks is resolved here, not inside the paragraph, because "auto" spacing collapses
        // with its neighbour (the larger of the two wins) and is dropped at the edges of a table cell.
        float pendingAfter = 0;
        var pendingAuto = false;
        var first = true;
        foreach (var block in blocks)
        {
            if (block is Paragraph p)
            {
                var before = p.Props.AutoBefore ? (first && _cellDepth > 0 ? 0 : AutoSpacing) : p.Props.SpaceBefore;
                y += pendingAuto || p.Props.AutoBefore ? Math.Max(pendingAfter, before) : pendingAfter + before;
                var scene = _scene;
                var mark = _scene.Count;
                var startY = y;
                if (LayoutParagraph(p, x, width, ref y)) return true;
                if (scene == _scene)
                {
                    if (p.Props.Shade is { } shade) _scene.InsertFill(mark, new SKRect(x + p.Props.IndentLeft, startY, x + width - p.Props.IndentRight, y), shade);
                    if (p.Props.BorderBottom) _scene.Line(x + p.Props.IndentLeft, y + 1.5f, x + width - p.Props.IndentRight, y + 1.5f, 0.75f, new SKColor(0xFF4472C4));
                }
                if (p.Props.BreakAfter && _cellDepth == 0)
                {
                    if (!NewPage()) return true;
                    y = _top;
                    pendingAfter = 0; pendingAuto = false; first = false;
                    continue;
                }
                pendingAuto = p.Props.AutoAfter;
                pendingAfter = pendingAuto ? AutoSpacing : p.Props.SpaceAfter;
            }
            else if (block is Table t)
            {
                y += pendingAfter;
                pendingAfter = 0;
                pendingAuto = false;
                if (LayoutTable(t, x, width, ref y)) return true;
            }
            first = false;
        }
        if (!(pendingAuto && _cellDepth > 0)) y += pendingAfter; // auto spacing is not added after the last paragraph of a cell
        return false;
    }

    // ---- paragraph ------------------------------------------------------------------------------

    private sealed class Token
    {
        public SKFont? Font; public float Ascent, Descent, Leading; public RunProps Props = null!;
        public ushort[]? Glyphs; public float[]? Advances; public string? Text; public float Width; public bool Space; public ImageInline? Image;
        public Token With(LineMetrics m) { Ascent = -m.Ascent; Descent = m.Descent; Leading = m.Leading; return this; }
    }

    private bool LayoutParagraph(Paragraph p, float x, float width, ref float y)
    {
        var props = p.Props;
        if (props.PageBreakBefore && y > _top + 1 && _cellDepth == 0)
        {
            if (!NewPage()) return true;
            y = _top;
        }

        var left = x + props.IndentLeft;
        var right = x + width - props.IndentRight;
        var line = new List<Token>();
        float lineWidth = 0;
        var first = true;

        float Start() => left + (first ? props.FirstLine : 0);
        float Available() => Math.Max(20, right - Start());

        // a centre / right tab waits for the text after it: its width is settled at the next tab or at the line end
        Token? pendingTab = null;
        float pendingStop = 0, pendingStart = 0;
        var pendingKind = 0;
        void ResolveTab()
        {
            if (pendingTab == null) return;
            var following = Start() - x + lineWidth - pendingStart;
            var advance = Math.Max(0, pendingStop - pendingStart - (pendingKind == 2 ? following : following / 2));
            pendingTab.Width = advance;
            lineWidth += advance;
            pendingTab = null;
        }

        if (p.Inlines.Count == 0) return Flush(ref y, last: true);

        if (props.Bullet != null)
        {
            // the bullet hangs in the first-line indent; its token is padded so the text starts at IndentLeft
            var bulletRun = props.BulletProps ?? p.MarkProps;
            var (bulletFont, bulletMetrics) = FontOf(bulletRun);
            var bullet = Shape(bulletFont, props.Bullet);
            var bulletToken = new Token
            {
                Font = bulletFont,
                Props = bulletRun,
                Glyphs = bullet.Glyphs,
                Advances = bullet.Advances,
                Text = props.Bullet,
                Width = Math.Max(bullet.Width + bulletRun.Size * 0.3f, -props.FirstLine)
            }.With(bulletMetrics);
            line.Add(bulletToken);
            lineWidth += bulletToken.Width;
        }

        foreach (var inline in p.Inlines)
        {
            switch (inline)
            {
                case TextInline text:
                    var (font, fontMetrics) = FontOf(text.Props);
                    foreach (var (word, isSpace) in Split(text.Text))
                    {
                        var shaped = Shape(font, word);
                        var token = new Token { Font = font, Props = text.Props, Glyphs = shaped.Glyphs, Advances = shaped.Advances, Text = word, Width = shaped.Width, Space = isSpace }.With(fontMetrics);
                        if (!isSpace && line.Count > 0 && lineWidth + token.Width > Available() && Flush(ref y, last: false)) return true;
                        if (isSpace && line.Count == 0 && !first) continue; // no leading space on wrapped lines
                        line.Add(token);
                        lineWidth += token.Width;
                    }
                    break;

                case TabInline tab:
                    var (tabFont, tabMetrics) = FontOf(tab.Props);
                    ResolveTab();
                    var position = Start() - x + lineWidth;
                    var tabToken = new Token { Font = tabFont, Props = tab.Props }.With(tabMetrics);
                    var stop = props.Tabs?.FirstOrDefault(t => t.Position > position + 0.5f) ?? default;
                    if (stop.Position > 0 && stop.Kind == 0) tabToken.Width = stop.Position - position;
                    else if (stop.Position > 0) { pendingTab = tabToken; pendingStop = stop.Position; pendingStart = position; pendingKind = stop.Kind; }
                    else if (props.FirstLine < 0 && first && position < props.IndentLeft - 0.5f) tabToken.Width = props.IndentLeft - position; // hanging indent acts as a stop
                    else tabToken.Width = TabWidth - position % TabWidth;
                    line.Add(tabToken);
                    lineWidth += tabToken.Width;
                    break;

                case ImageInline image:
                    var scale = image.Width > Available() ? Available() / image.Width : 1f;
                    var picture = new Token { Props = image.Props, Image = image, Width = image.Width * scale, Ascent = image.Height * scale };
                    if (line.Count > 0 && lineWidth + picture.Width > Available() && Flush(ref y, last: false)) return true;
                    line.Add(picture);
                    lineWidth += picture.Width;
                    break;

                case FloatInline floating:
                    PlaceFloat(floating, x, width, y);
                    break;

                case BreakInline br:
                    if (br.Soft) break; // Word's cached page boundary from its last render: our own pagination decides
                    if (line.Count == 0)
                    {
                        var (breakFont, breakMetrics) = FontOf(br.Props);
                        line.Add(new Token { Font = breakFont, Props = br.Props }.With(breakMetrics));
                    }
                    if (Flush(ref y, last: true)) return true;
                    if (br.Page && _cellDepth == 0)
                    {
                        if (!NewPage()) return true;
                        y = _top;
                    }
                    break;
            }
        }

        return line.Count > 0 && Flush(ref y, last: true);

        bool Flush(ref float top, bool last)
        {
            if (_lineBudget <= 0) { line.Clear(); lineWidth = 0; return true; }
            if (_lineBudget != int.MaxValue) _lineBudget--;
            while (line.Count > 0 && line[^1].Space) { lineWidth -= line[^1].Width; line.RemoveAt(line.Count - 1); }
            ResolveTab();
            float ascent = 0, descent = 0, leading = 0;
            foreach (var token in line)
            {
                ascent = Math.Max(ascent, token.Ascent);
                descent = Math.Max(descent, token.Descent);
                leading = Math.Max(leading, token.Leading);
            }
            if (line.Count == 0)
            {
                var (_, metrics) = FontOf(p.MarkProps);
                ascent = -metrics.Ascent; descent = metrics.Descent; leading = metrics.Leading;
            }
            var natural = ascent + descent + leading;
            var height = props.LineRule switch
            {
                "exact" => props.Line / 20f,
                "atLeast" => Math.Max(natural, props.Line / 20f),
                _ => natural * props.Line / 240f,
            };

            // the line does not fit: continue on a fresh page (never inside a table cell — rows move as a whole)
            if (top + height > _bottom + 0.5f && _cellDepth == 0 && top > _top + 0.5f)
            {
                if (!NewPage()) { line.Clear(); lineWidth = 0; return true; }
                top = _top;
            }

            var extra = Available() - lineWidth;
            var cursor = Start();
            float gap = 0;
            switch (props.Align)
            {
                case Align.Center: cursor += extra / 2; break;
                case Align.Right: cursor += extra; break;
                case Align.Justify when !last && extra > 0:
                    var spaces = line.Count(t => t.Space);
                    if (spaces > 0) gap = extra / spaces;
                    break;
            }
            var baseline = top + (height - natural) / 2 + ascent;

            foreach (var token in line)
            {
                if (token.Image != null)
                {
                    if (_source.LoadImage(token.Image.RelationshipId) is { } image)
                        _scene.Image(_document.Own(image), new SKRect(cursor, baseline - token.Ascent, cursor + token.Width, baseline));
                }
                else if (token.Glyphs is { Length: > 0 } && !token.Space)
                {
                    // positions from the cached advances: no second trip into the text engine
                    var positions = new SKPoint[token.Glyphs.Length];
                    var pen = cursor;
                    for (var g = 0; g < positions.Length; g++) { positions[g] = new SKPoint(pen, baseline); pen += token.Advances![g]; }
                    _scene.Text(token.Font!, token.Glyphs, positions, token.Props.Color, token.Text);
                }
                if (token.Props.Underline && token.Width > 0 && token.Image == null)
                {
                    var offset = Math.Max(1f, token.Props.Size * 0.12f);
                    _scene.Line(cursor, baseline + offset, cursor + token.Width + (token.Space ? gap : 0), baseline + offset, Math.Max(0.5f, token.Props.Size / 18f), token.Props.Color);
                }
                cursor += token.Width + (token.Space ? gap : 0);
            }

            top += height;
            line.Clear();
            lineWidth = 0;
            first = false;
            return false;
        }
    }

    /// <summary>Floating picture / text box: placed from its anchor description, underneath the page's text.</summary>
    private void PlaceFloat(FloatInline f, float x, float width, float y)
    {
        Guard.Deeper();
        var page = _source.Page;
        (float Start, float Extent) horizontal = f.RelativeH switch
        {
            "page" => (0, page.Width),
            "leftMargin" => (0, page.Left),
            "rightMargin" => (page.Width - page.Right, page.Right),
            "margin" => (page.Left, page.Width - page.Left - page.Right),
            _ => (x, width),
        };
        (float Start, float Extent) vertical = f.RelativeV switch
        {
            "page" => (0, page.Height),
            "topMargin" => (0, page.Top),
            "bottomMargin" => (page.Height - page.Bottom, page.Bottom),
            "margin" => (page.Top, page.Height - page.Top - page.Bottom),
            _ => (y, 0),
        };
        var left = f.AlignH switch { "center" => horizontal.Start + (horizontal.Extent - f.Width) / 2, "right" => horizontal.Start + horizontal.Extent - f.Width, "left" => horizontal.Start, _ => horizontal.Start + f.X };
        var top = f.AlignV switch { "center" => vertical.Start + (vertical.Extent - f.Height) / 2, "bottom" => vertical.Start + vertical.Extent - f.Height, "top" => vertical.Start, _ => vertical.Start + f.Y };
        var box = SKRect.Create(left, top, f.Width, f.Height);
        if (f.Fill is { } fill) _scene.InsertFill(_floats++, box, fill);
        if (f.RelationshipId != null && _source.LoadImage(f.RelationshipId) is { } image) _scene.InsertImage(_floats++, _document.Own(image), box);
        if (f.Text is { Count: > 0 })
        {
            // text box content: laid out inside the box like a table cell (never breaks the page)
            _cellDepth++;
            var cursor = box.Top + 3.6f;
            try { Layout(f.Text, box.Left + 7.2f, Math.Max(10, box.Width - 14.4f), ref cursor); }
            finally { _cellDepth--; }
        }
    }

    /// <summary>Splits into word / single-space pieces; a line can break at a space or after a hyphen.</summary>
    private static IEnumerable<(string Text, bool Space)> Split(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '-' && i > start && i + 1 < text.Length && text[i + 1] != ' ')
            {
                yield return (text[start..(i + 1)], false); // a line may also break after a hyphen
                start = i + 1;
                continue;
            }
            if (text[i] != ' ') continue;
            if (i > start) yield return (text[start..i], false);
            yield return (" ", true);
            start = i + 1;
        }
        if (start < text.Length) yield return (text[start..], false);
    }

    // ---- table ----------------------------------------------------------------------------------

    private bool LayoutTable(Table table, float x, float width, ref float y)
    {
        Guard.Deeper();
        var grid = (float[])table.Grid.Clone();
        var total = grid.Sum();
        if (total <= 0)
        {
            Array.Fill(grid, width / grid.Length);
            total = width;
        }
        else if (total <= 1.5f)
        {
            for (var i = 0; i < grid.Length; i++) grid[i] *= width / total; // fractions of the available width
            total = width;
        }
        else if (total > width * 1.15f)
        {
            var shrink = width / total;
            for (var i = 0; i < grid.Length; i++) grid[i] *= shrink;
            total = width;
        }
        var origin = table.Align switch
        {
            Align.Center => x + (width - total) / 2,
            Align.Right => x + width - total,
            _ => x + table.Indent,
        };

        var edges = new float[grid.Length + 1];
        for (var i = 0; i < grid.Length; i++) edges[i + 1] = edges[i] + grid[i];

        for (var r = 0; r < table.Rows.Count; r++)
        {
            var row = table.Rows[r];
            var mark = _scene.Count;
            var rowTop = y;
            var rowBottom = LayoutRow(table, row, edges, total, origin, rowTop, out var stop);
            if (stop) return true;

            // row does not fit on what is left of the page: redo it at the top of the next one
            if (rowBottom > _bottom + 0.5f && rowTop > _top + 0.5f && _cellDepth == 0)
            {
                _scene.Truncate(mark);
                if (!NewPage()) return true;
                rowTop = _top;
                rowBottom = LayoutRow(table, row, edges, total, origin, rowTop, out stop);
                if (stop) return true;
            }
            if (_cellDepth == 0) rowBottom = Math.Min(rowBottom, _bottom); // a row taller than a whole page is cut at the bottom margin

            var column = 0;
            foreach (var cell in row.Cells)
            {
                var (cellLeft, cellWidth) = CellBox(edges, column, cell.Span, total, origin, row.Cells.Count);
                var cellRight = cellLeft + cellWidth;
                if (cell.Fill is { } cellFill) _scene.InsertFill(mark, new SKRect(cellLeft, rowTop, cellRight, rowBottom), cellFill);
                var b = cell.Borders;
                var t = table.Borders;
                if (b.Top ?? (r == 0 ? t.Top : t.InsideH) ?? false) _scene.Line(cellLeft, rowTop, cellRight, rowTop, 0.5f, BorderColor);
                if (b.Bottom ?? (r == table.Rows.Count - 1 ? t.Bottom : t.InsideH) ?? false) _scene.Line(cellLeft, rowBottom, cellRight, rowBottom, 0.5f, BorderColor);
                if (b.Left ?? (column == 0 ? t.Left : t.InsideV) ?? false) _scene.Line(cellLeft, rowTop, cellLeft, rowBottom, 0.5f, BorderColor);
                if (b.Right ?? (column + cell.Span >= edges.Length - 1 ? t.Right : t.InsideV) ?? false) _scene.Line(cellRight, rowTop, cellRight, rowBottom, 0.5f, BorderColor);
                column += cell.Span;
            }
            y = rowBottom;
        }
        return false;
    }

    private static (float Left, float Width) CellBox(float[] edges, int column, int span, float total, float origin, int cells)
    {
        var last = edges.Length - 1;
        int from = Math.Min(column, last), to = Math.Min(column + span, last);
        var width = edges[to] - edges[from];
        return (origin + edges[from], width > 0 ? width : total / Math.Max(1, cells));
    }

    private float LayoutRow(Table table, Row row, float[] edges, float total, float origin, float rowTop, out bool stop)
    {
        Guard.Deeper();
        stop = false;
        var rowBottom = rowTop + row.MinHeight;
        var column = 0;
        _cellDepth++;
        foreach (var cell in row.Cells)
        {
            var (cellLeft, cellWidth) = CellBox(edges, column, cell.Span, total, origin, row.Cells.Count);
            var cursor = rowTop + table.PadTop;
            stop |= Layout(cell.Blocks, cellLeft + table.PadLeft, Math.Max(10, cellWidth - table.PadLeft - table.PadRight), ref cursor);
            rowBottom = Math.Max(rowBottom, cursor + table.PadBottom);
            column += cell.Span;
        }
        _cellDepth--;
        return rowBottom;
    }
}
