using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using FastThumb.Layout;

namespace FastThumb.Words;

/// <summary>OpenDocument Text: content.xml + styles.xml inside a ZIP.</summary>
internal static class OdtReader
{
    private static readonly XNamespace Office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private static readonly XNamespace Text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private static readonly XNamespace Table = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    private static readonly XNamespace Style = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
    private static readonly XNamespace Fo = "urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0";
    private static readonly XNamespace Svg = "urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0";

    internal sealed class Context
    {
        public Dictionary<string, XElement> Styles = new(StringComparer.Ordinal);
        public Dictionary<string, string> Fonts = new(StringComparer.Ordinal);
        public ParaProps DefaultPara = new();
        public RunProps DefaultRun = new() { Size = 12 };

        /// <summary>Same styles and fonts, private copies of the defaults (for one text container).</summary>
        public Context Scoped() => new() { Styles = Styles, Fonts = Fonts, DefaultPara = DefaultPara.Clone(), DefaultRun = DefaultRun.Clone() };
    }

    internal static Context CreateContext(params XElement?[] roots)
    {
        var context = new Context();
        foreach (var root in roots)
        {
            if (root == null) continue;
            foreach (var face in root.Descendants(Style + "font-face"))
                if ((string?)face.Attribute(Style + "name") is { } name && (string?)face.Attribute(Svg + "font-family") is { } family)
                    context.Fonts[name] = family.Trim('\'', '"');
            foreach (var style in root.Descendants(Style + "style"))
                if ((string?)style.Attribute(Style + "name") is { } name) context.Styles[name] = style;
            foreach (var style in root.Descendants(Style + "default-style"))
                if ((string?)style.Attribute(Style + "family") is "paragraph" or "graphic" or "table-cell") Apply(context, style, context.DefaultPara, context.DefaultRun);
        }
        return context;
    }

    public static IPageSource Read(byte[] data)
    {
        using var zip = new ZipArchive(new MemoryStream(data, false), ZipArchiveMode.Read);
        XElement? Load(string name)
        {
            var entry = zip.GetEntry(name);
            if (entry == null) return null;
            using var stream = Guard.Open(entry);
            return Guard.LoadXml(stream).Root;
        }
        var content = Load("content.xml") ?? throw new InvalidDataException("content.xml not found");
        var styles = Load("styles.xml");

        var context = new Context();
        foreach (var root in new[] { styles, content })
        {
            if (root == null) continue;
            foreach (var face in root.Descendants(Style + "font-face"))
                if ((string?)face.Attribute(Style + "name") is { } name && (string?)face.Attribute(Svg + "font-family") is { } family)
                    context.Fonts[name] = family.Trim('\'', '"');
            foreach (var style in root.Descendants(Style + "style"))
                if ((string?)style.Attribute(Style + "name") is { } name) context.Styles[name] = style;
            foreach (var style in root.Descendants(Style + "default-style"))
                if ((string?)style.Attribute(Style + "family") == "paragraph") Apply(context, style, context.DefaultPara, context.DefaultRun);
        }

        var page = PageSetup.Default;
        if (styles?.Descendants(Style + "page-layout-properties").FirstOrDefault() is { } layout)
        {
            float L(string name, float fallback) => FormatDetector.Length((string?)layout.Attribute(Fo + name)) ?? fallback;
            var margin = FormatDetector.Length((string?)layout.Attribute(Fo + "margin"));
            page = new PageSetup(L("page-width", 612), L("page-height", 792), L("margin-top", margin ?? 72), L("margin-right", margin ?? 72),
                L("margin-bottom", margin ?? 72), L("margin-left", margin ?? 72));
        }

        var blocks = new List<Block>();
        var body = content.Element(Office + "body")?.Element(Office + "text");
        if (body != null) ReadBlocks(context, body, blocks, null);
        return new StaticSource(page, blocks);
    }

    private static void Apply(Context context, XElement style, ParaProps? para, RunProps run)
    {
        if (para != null && style.Element(Style + "paragraph-properties") is { } pp)
        {
            para.Align = (string?)pp.Attribute(Fo + "text-align") switch { "center" => Align.Center, "end" or "right" => Align.Right, "justify" => Align.Justify, null => para.Align, _ => Align.Left };
            para.SpaceBefore = FormatDetector.Length((string?)pp.Attribute(Fo + "margin-top")) ?? para.SpaceBefore;
            para.SpaceAfter = FormatDetector.Length((string?)pp.Attribute(Fo + "margin-bottom")) ?? para.SpaceAfter;
            para.IndentLeft = FormatDetector.Length((string?)pp.Attribute(Fo + "margin-left")) ?? para.IndentLeft;
            para.IndentRight = FormatDetector.Length((string?)pp.Attribute(Fo + "margin-right")) ?? para.IndentRight;
            para.FirstLine = FormatDetector.Length((string?)pp.Attribute(Fo + "text-indent")) ?? para.FirstLine;
            if ((string?)pp.Attribute(Fo + "break-before") == "page") para.PageBreakBefore = true;
            if ((string?)pp.Attribute(Fo + "line-height") is { } height)
            {
                if (height.EndsWith('%') && float.TryParse(height.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)) { para.Line = 240 * percent / 100; para.LineRule = "auto"; }
                else if (FormatDetector.Length(height) is { } exact) { para.Line = exact * 20; para.LineRule = "exact"; }
            }
        }
        if (style.Element(Style + "text-properties") is { } tp)
        {
            run.Size = FormatDetector.Length((string?)tp.Attribute(Fo + "font-size")) ?? run.Size;
            if ((string?)tp.Attribute(Fo + "font-weight") is { } weight) run.Bold = weight is "bold" or "700" or "800" or "900";
            if ((string?)tp.Attribute(Fo + "font-style") is { } slant) run.Italic = slant is "italic" or "oblique";
            if ((string?)tp.Attribute(Style + "text-underline-style") is { } underline) run.Underline = underline != "none";
            run.Color = FormatDetector.Color((string?)tp.Attribute(Fo + "color")) ?? run.Color;
            if ((string?)tp.Attribute(Style + "font-name") is { } face) run.Font = context.Fonts.GetValueOrDefault(face, face);
            else if ((string?)tp.Attribute(Fo + "font-family") is { } family) run.Font = family.Trim('\'', '"');
        }
    }

    /// <summary>Applies a named style and its parents (parents first).</summary>
    internal static void Resolve(Context context, string? name, ParaProps? para, RunProps run, int depth = 0)
    {
        Guard.Deeper();
        if (name == null || depth > 20 || !context.Styles.TryGetValue(name, out var style)) return;
        Resolve(context, (string?)style.Attribute(Style + "parent-style-name"), para, run, depth + 1);
        Apply(context, style, para, run);
    }

    internal static void ReadBlocks(Context context, XElement container, List<Block> output, string? bullet)
    {
        Guard.Deeper();
        foreach (var element in container.Elements())
        {
            if (element.Name == Text + "p" || element.Name == Text + "h")
            {
                var para = context.DefaultPara.Clone();
                var run = context.DefaultRun.Clone();
                Resolve(context, (string?)element.Attribute(Text + "style-name"), para, run);
                var paragraph = new Paragraph { Props = para, MarkProps = run };
                if (bullet != null)
                {
                    para.Bullet = "•";
                    if (para.IndentLeft < 1) { para.IndentLeft = 18; para.FirstLine = -18; }
                    bullet = null;
                }
                ReadInlines(context, element, run, paragraph.Inlines);
                output.Add(paragraph);
            }
            else if (element.Name == Text + "list")
            {
                foreach (var item in element.Elements(Text + "list-item")) ReadBlocks(context, item, output, "•  ");
            }
            else if (element.Name == Text + "section") ReadBlocks(context, element, output, null);
            else if (element.Name == Table + "table")
            {
                var table = new Table();
                var widths = new List<float>();
                foreach (var column in element.Elements(Table + "table-column"))
                {
                    var repeat = (int?)column.Attribute(Table + "number-columns-repeated") ?? 1;
                    float width = 0;
                    if ((string?)column.Attribute(Table + "style-name") is { } name && context.Styles.TryGetValue(name, out var style))
                        width = FormatDetector.Length((string?)style.Element(Style + "table-column-properties")?.Attribute(Style + "column-width")) ?? 0;
                    for (var i = 0; i < Math.Min(repeat, 64); i++) widths.Add(width);
                }
                foreach (var tr in OwnRows(element))
                {
                    var row = new Row();
                    foreach (var tc in tr.Elements(Table + "table-cell"))
                    {
                        var cell = new Cell { Span = Math.Clamp((int?)tc.Attribute(Table + "number-columns-spanned") ?? 1, 1, Cell.MaxSpan) };
                        if ((string?)tc.Attribute(Table + "style-name") is { } cellStyle && context.Styles.TryGetValue(cellStyle, out var definition) &&
                            definition.Element(Style + "table-cell-properties") is { } cp)
                        {
                            bool? Side(string name) => ((string?)cp.Attribute(Fo + name) ?? (string?)cp.Attribute(Fo + "border")) is { } value && !value.StartsWith("none", StringComparison.Ordinal) ? true : null;
                            cell.Borders = new Borders { Top = Side("border-top"), Left = Side("border-left"), Bottom = Side("border-bottom"), Right = Side("border-right") };
                            cell.Fill = FormatDetector.Color((string?)cp.Attribute(Fo + "background-color"));
                        }
                        if (cell.Fill == null && (string?)tc.Attribute(Table + "style-name") is { } fillStyle && context.Styles.TryGetValue(fillStyle, out var fillDefinition) &&
                            fillDefinition.Element(Style + "graphic-properties") is { } gp && (string?)gp.Attribute(XName.Get("fill", "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0")) == "solid")
                            cell.Fill = FormatDetector.Color((string?)gp.Attribute(XName.Get("fill-color", "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0")));
                        ReadBlocks(context, tc, cell.Blocks, null);
                        row.Cells.Add(cell);
                    }
                    if (row.Cells.Count > 0) table.Rows.Add(row);
                }
                if (table.Rows.Count == 0) continue;
                var columns = Math.Min(table.Rows.Max(r => r.Cells.Sum(c => c.Span)), Cell.MaxSpan);   // the grid is sized from the file
                table.Grid = widths.Count >= columns ? widths.Take(columns).ToArray() : new float[columns];
                output.Add(table);
            }
        }
    }

    /// <summary>
    /// The rows whose nearest table is <paramref name="table"/>, in document order: rows of nested tables belong to them.
    /// One walk that stops at nested tables, not an ancestor check per row (quadratic in the nesting depth).
    /// </summary>
    internal static IEnumerable<XElement> OwnRows(XElement table)
    {
        var pending = new Stack<XElement>(table.Elements().Reverse());
        while (pending.Count > 0)
        {
            var element = pending.Pop();
            if (element.Name == Table + "table") continue;
            if (element.Name == Table + "table-row") yield return element;
            foreach (var child in element.Elements().Reverse()) pending.Push(child);
        }
    }

    private static void ReadInlines(Context context, XElement container, RunProps run, List<Inline> output)
    {
        Guard.Deeper();
        foreach (var node in container.Nodes())
        {
            if (node is XText text) { if (text.Value.Length > 0) output.Add(new TextInline { Props = run, Text = text.Value }); continue; }
            if (node is not XElement element) continue;
            if (element.Name == Text + "span")
            {
                var inner = run.Clone();
                Resolve(context, (string?)element.Attribute(Text + "style-name"), null, inner);
                ReadInlines(context, element, inner, output);
            }
            else if (element.Name == Text + "s") output.Add(new TextInline { Props = run, Text = new string(' ', Math.Clamp((int?)element.Attribute(Text + "c") ?? 1, 1, 200)) });
            else if (element.Name == Text + "tab") output.Add(new TabInline { Props = run });
            else if (element.Name == Text + "line-break") output.Add(new BreakInline { Props = run });
            else if (element.Name == Text + "a") ReadInlines(context, element, run, output);
        }
    }
}
