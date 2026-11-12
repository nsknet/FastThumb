using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Xml.Linq;
using FastThumb.Layout;

namespace FastThumb.Words;

/// <summary>
/// Tolerant HTML → blocks, at thumbnail fidelity: block structure, headings, inline formatting from tags and
/// inline style attributes, lists, tables (with colspan and width hints). No CSS cascade, no scripts, no images.
/// Also the back end for MHTML (first text/html part) and EPUB (spine chapters in order).
/// </summary>
internal static class HtmlReader
{
    public static List<Block> ParseBlocks(string html) => new Parser().Parse(html);

    public static IPageSource Read(string html) => new StaticSource(PageSetup.Default, new Parser().Parse(html));

    public static IPageSource ReadEpub(byte[] data)
    {
        using var zip = new ZipArchive(new MemoryStream(data, false), ZipArchiveMode.Read);
        Dictionary<string, ZipArchiveEntry>? ignoringCase = null;   // built once: a scan per missing name is quadratic
        byte[]? Bytes(string name)
        {
            if (zip.GetEntry(name) is not { } entry)
            {
                if (ignoringCase == null)
                {
                    ignoringCase = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
                    foreach (var e in zip.Entries) ignoringCase.TryAdd(e.FullName, e);
                }
                if (!ignoringCase.TryGetValue(name, out entry)) return null;
            }
            return Guard.ReadAll(entry);
        }

        var container = Bytes("META-INF/container.xml") ?? throw new InvalidDataException("EPUB container not found");
        var opfPath = (string?)Guard.ParseXml(FormatDetector.Decode(container, null)).Descendants().FirstOrDefault(e => e.Name.LocalName == "rootfile")?.Attribute("full-path")
                      ?? throw new InvalidDataException("EPUB package document not found");
        var opf = Guard.ParseXml(FormatDetector.Decode(Bytes(opfPath) ?? throw new InvalidDataException("EPUB package document missing"), null)).Root!;
        var folder = opfPath.Contains('/') ? opfPath[..(opfPath.LastIndexOf('/') + 1)] : "";
        var manifest = opf.Descendants().Where(e => e.Name.LocalName == "item")
            .Where(e => e.Attribute("id") != null && e.Attribute("href") != null)
            .ToDictionary(e => (string)e.Attribute("id")!, e => (string)e.Attribute("href")!);

        var blocks = new List<Block>();
        var included = new HashSet<string>(StringComparer.Ordinal);   // a spine may name one chapter any number of times: read once
        foreach (var reference in opf.Descendants().Where(e => e.Name.LocalName == "itemref"))
        {
            if ((string?)reference.Attribute("idref") is not { } id || !manifest.TryGetValue(id, out var href) || !included.Add(href)) continue;
            if (Bytes(folder + Uri.UnescapeDataString(href)) is { } chapter) blocks.AddRange(new Parser().Parse(FormatDetector.DecodeHtml(chapter)));
        }
        return new StaticSource(PageSetup.Default, blocks);
    }

    public static IPageSource ReadMhtml(byte[] data)
    {
        var text = Encoding.Latin1.GetString(data); // byte-transparent: parts are re-encoded from it below
        var marker = text.IndexOf("boundary=", StringComparison.OrdinalIgnoreCase);
        string[] parts;
        if (marker < 0) parts = [text]; // single-part message: the headers are followed directly by the HTML body
        else
        {
            var rest = text[(marker + 9)..];
            var boundary = rest.StartsWith('"') ? rest[1..rest.IndexOf('"', 1)] : new string(rest.TakeWhile(c => c is not ('\r' or '\n' or ';' or ' ')).ToArray());
            parts = text.Split("--" + boundary);
        }

        foreach (var part in parts)
        {
            var split = part.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            var gap = 4;
            if (split < 0) { split = part.IndexOf("\n\n", StringComparison.Ordinal); gap = 2; }
            if (split < 0) continue;
            var headers = part[..split];
            if (!headers.Contains("text/html", StringComparison.OrdinalIgnoreCase)) continue;
            var body = part[(split + gap)..];

            byte[] bytes;
            if (headers.Contains("quoted-printable", StringComparison.OrdinalIgnoreCase)) bytes = QuotedPrintable(body);
            else if (headers.Contains("base64", StringComparison.OrdinalIgnoreCase)) bytes = Convert.FromBase64String(new string(body.Where(c => !char.IsWhiteSpace(c)).ToArray()));
            else bytes = Encoding.Latin1.GetBytes(body);

            Encoding? encoding = null;
            var charset = headers.IndexOf("charset=", StringComparison.OrdinalIgnoreCase);
            if (charset >= 0)
            {
                var name = new string(headers[(charset + 8)..].TrimStart('"').TakeWhile(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
                try { encoding = Encoding.GetEncoding(name); } catch (ArgumentException) { }
            }
            var html = encoding != null ? FormatDetector.Decode(bytes, encoding) : FormatDetector.DecodeHtml(bytes);
            return new StaticSource(PageSetup.Default, new Parser().Parse(html));
        }
        throw new InvalidDataException("MHTML has no text/html part");
    }

    private static byte[] QuotedPrintable(string text)
    {
        var output = new MemoryStream();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '=') { output.WriteByte((byte)c); continue; }
            if (i + 1 < text.Length && text[i + 1] == '\r') { i += i + 2 < text.Length && text[i + 2] == '\n' ? 2 : 1; continue; } // soft line break
            if (i + 1 < text.Length && text[i + 1] == '\n') { i++; continue; }
            if (i + 2 < text.Length && byte.TryParse(text.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)) { output.WriteByte(value); i += 2; }
            else output.WriteByte((byte)c);
        }
        return output.ToArray();
    }

    private sealed class Frame
    {
        public string Tag = "";
        public RunProps Run = new() { Size = 12 };
        public Align Align;
        public float Indent;
        public bool Spaced, Pre, ListItem;
        public string? Bullet;
        public int Counter;          // for <ol>
        public bool Ordered;
    }

    private sealed class TableBuilder { public Table Table = new(); public Row? Row; public Cell? Cell; public List<float> Hints = []; public bool HintsDone; }

    private sealed class Parser
    {
        private static readonly HashSet<string> BlockTags = new(StringComparer.Ordinal)
        {
            "p", "div", "h1", "h2", "h3", "h4", "h5", "h6", "li", "ul", "ol", "table", "tr", "td", "th", "blockquote", "center", "pre", "hr",
            "body", "section", "article", "header", "footer", "dl", "dt", "dd", "tbody", "thead", "tfoot", "form", "fieldset", "address", "main", "nav", "aside", "figure",
        };
        private static readonly HashSet<string> VoidTags = new(StringComparer.Ordinal) { "br", "hr", "img", "meta", "link", "input", "col", "area", "base", "wbr" };
        private static readonly HashSet<string> SkippedTags = new(StringComparer.Ordinal) { "script", "style", "head", "title", "noscript", "svg", "template", "select", "textarea" };

        // open elements; past this depth further start tags are ignored (their text goes to the element that is open)
        private const int MaxDepth = 512;
        private readonly List<Frame> _frames = [new Frame { Tag = "#root" }];
        private readonly Stack<List<Block>> _targets = new();
        private readonly Stack<TableBuilder> _tables = new();
        private Paragraph? _paragraph;
        private bool _lastWasSpace = true;

        private Frame Top => _frames[^1];

        public List<Block> Parse(string html)
        {
            var root = new List<Block>();
            _targets.Push(root);
            var at = 0;
            while (at < html.Length)
            {
                var open = html.IndexOf('<', at);
                if (open < 0) { Text(html[at..]); break; }
                if (open > at) Text(html[at..open]);
                if (html.AsSpan(open).StartsWith("<!--")) { var end = html.IndexOf("-->", open + 4, StringComparison.Ordinal); at = end < 0 ? html.Length : end + 3; continue; }
                var close = html.IndexOf('>', open + 1);
                if (close < 0) break;
                var tag = html[(open + 1)..close];
                at = close + 1;
                if (tag.Length == 0 || tag[0] is '!' or '?') continue;

                if (tag[0] == '/') { CloseTag(tag[1..].Trim().ToLowerInvariant()); continue; }
                var nameEnd = 0;
                while (nameEnd < tag.Length && !char.IsWhiteSpace(tag[nameEnd]) && tag[nameEnd] != '/') nameEnd++;
                var name = tag[..nameEnd].ToLowerInvariant();
                if (SkippedTags.Contains(name))
                {
                    var end = html.IndexOf("</" + name, at, StringComparison.OrdinalIgnoreCase);
                    if (end < 0) break;
                    var endClose = html.IndexOf('>', end);
                    at = endClose < 0 ? html.Length : endClose + 1;
                    continue;
                }
                OpenTag(name, Attributes(tag.AsSpan(nameEnd)), tag.EndsWith('/'));
            }
            EndParagraph();
            while (_tables.Count > 0) CloseTag("table");
            return root;
        }

        private static Dictionary<string, string> Attributes(ReadOnlySpan<char> source)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var i = 0;
            while (i < source.Length)
            {
                while (i < source.Length && (char.IsWhiteSpace(source[i]) || source[i] == '/')) i++;
                var start = i;
                while (i < source.Length && !char.IsWhiteSpace(source[i]) && source[i] != '=' && source[i] != '/') i++;
                if (i == start) break;
                var key = source[start..i].ToString();
                while (i < source.Length && char.IsWhiteSpace(source[i])) i++;
                var value = "";
                if (i < source.Length && source[i] == '=')
                {
                    i++;
                    while (i < source.Length && char.IsWhiteSpace(source[i])) i++;
                    if (i < source.Length && source[i] is '"' or '\'')
                    {
                        var quote = source[i++];
                        start = i;
                        while (i < source.Length && source[i] != quote) i++;
                        value = source[start..i].ToString();
                        i++;
                    }
                    else
                    {
                        start = i;
                        while (i < source.Length && !char.IsWhiteSpace(source[i])) i++;
                        value = source[start..i].ToString();
                    }
                }
                result[key] = WebUtility.HtmlDecode(value);
            }
            return result;
        }

        private void OpenTag(string name, Dictionary<string, string> attributes, bool selfClosed)
        {
            if (name == "br") { Paragraph().Inlines.Add(new BreakInline { Props = Top.Run }); _lastWasSpace = true; return; }

            // implied end tags: a new <p> ends an open <p>, a new <li> the previous one, and so on
            switch (name)
            {
                case "p": ClosePending("p", stopAt: ["td", "th", "table", "li", "div", "body"]); break;
                case "li": ClosePending("li", stopAt: ["ul", "ol"]); break;
                case "tr": ClosePending("tr", stopAt: ["table"]); break;
                case "td" or "th": ClosePending("td", stopAt: ["tr", "table"]); ClosePending("th", stopAt: ["tr", "table"]); break;
            }
            if (BlockTags.Contains(name)) EndParagraph();
            if (name == "hr" || (VoidTags.Contains(name) && name != "br")) return;
            if (_frames.Count >= MaxDepth) return;

            var parent = Top;
            var frame = new Frame { Tag = name, Run = parent.Run.Clone(), Align = parent.Align, Indent = parent.Indent, Pre = parent.Pre, Ordered = parent.Ordered };
            switch (name)
            {
                case "b" or "strong": frame.Run.Bold = true; break;
                case "i" or "em" or "cite": frame.Run.Italic = true; break;
                case "u" or "ins": frame.Run.Underline = true; break;
                case "a": if (attributes.ContainsKey("href")) { frame.Run.Underline = true; frame.Run.Color = new SkiaSharp.SKColor(0xFF0000EE); } break;
                case "center": frame.Align = Align.Center; break;
                case "p" or "blockquote": frame.Spaced = true; if (name == "blockquote") frame.Indent += 30; break;
                case "pre": frame.Spaced = true; frame.Pre = true; frame.Run.Font = "Courier New"; frame.Run.Size = 10; break;
                case "code" or "tt" or "kbd": frame.Run.Font = "Courier New"; break;
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                    frame.Spaced = true;
                    frame.Run.Bold = true;
                    frame.Run.Size = name[1] switch { '1' => 24, '2' => 18, '3' => 14, '4' => 12, '5' => 10, _ => 8 };
                    break;
                case "ul" or "ol": frame.Indent += 30; frame.Ordered = name == "ol"; frame.Counter = 0; break;
                case "li":
                    var list = _frames.LastOrDefault(f => f.Tag is "ul" or "ol");
                    frame.ListItem = true;
                    frame.Bullet = list is { Ordered: true } ? $"{++list.Counter}. " : "•  ";
                    break;
                case "th": frame.Run.Bold = true; frame.Align = Align.Center; break;
                case "font":
                    if (attributes.TryGetValue("face", out var face) && face.Length > 0) frame.Run.Font = face.Split(',')[0].Trim(' ', '\'', '"');
                    if (attributes.TryGetValue("color", out var fontColor) && FormatDetector.Color(fontColor) is { } parsed) frame.Run.Color = parsed;
                    if (attributes.TryGetValue("size", out var fontSize) && int.TryParse(fontSize.TrimStart('+'), out var step))
                        frame.Run.Size = Math.Clamp(fontSize.StartsWith('+') || fontSize.StartsWith('-') ? 3 + step : step, 1, 7) switch { 1 => 7.5f, 2 => 10, 3 => 12, 4 => 13.5f, 5 => 18, 6 => 24, _ => 36 };
                    break;
            }
            if (attributes.TryGetValue("align", out var align)) frame.Align = ParseAlign(align) ?? frame.Align;
            if (attributes.TryGetValue("style", out var style)) ApplyStyle(frame, style, parent.Run.Size);

            switch (name)
            {
                case "table":
                    var builder = new TableBuilder();
                    var bordered = attributes.TryGetValue("border", out var border) && border is not ("0" or "") || HasBorderStyle(attributes.GetValueOrDefault("style"));
                    if (bordered) builder.Table.Borders = new Borders { Top = true, Left = true, Bottom = true, Right = true, InsideH = true, InsideV = true };
                    builder.Table.Align = frame.Align == Align.Center ? Align.Center : Align.Left;
                    frame.Align = Align.Left; // a centred table does not centre its cells' text
                    _tables.Push(builder);
                    break;
                case "tr" when _tables.Count > 0:
                    _tables.Peek().Row = new Row();
                    break;
                case "td" or "th" when _tables.Count > 0:
                    var table = _tables.Peek();
                    table.Row ??= new Row();
                    table.Cell = new Cell { Span = attributes.TryGetValue("colspan", out var span) && int.TryParse(span, out var count) ? Math.Clamp(count, 1, 64) : 1 };
                    if (HasBorderStyle(attributes.GetValueOrDefault("style")))
                        table.Cell.Borders = new Borders { Top = true, Left = true, Bottom = true, Right = true };
                    if (!table.HintsDone)
                    {
                        // column width hints from the first row: "width" attribute or style, in px or %
                        var hint = attributes.TryGetValue("width", out var width) ? width : StyleValue(attributes.GetValueOrDefault("style"), "width");
                        float value = 0;
                        if (hint != null && float.TryParse(hint.Trim().TrimEnd('%', 'p', 'x', 't'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) value = number;
                        for (var i = 0; i < table.Cell.Span; i++) table.Hints.Add(value / table.Cell.Span);
                    }
                    _targets.Push(table.Cell.Blocks);
                    break;
            }
            _frames.Add(frame);
            if (selfClosed) CloseTag(name);
        }

        private void ClosePending(string tag, string[] stopAt)
        {
            for (var i = _frames.Count - 1; i > 0; i--)
            {
                if (_frames[i].Tag == tag) { CloseTag(tag); return; }
                if (Array.IndexOf(stopAt, _frames[i].Tag) >= 0) return;
            }
        }

        private void CloseTag(string name)
        {
            var index = _frames.FindLastIndex(f => f.Tag == name);
            if (index <= 0) { if (BlockTags.Contains(name)) EndParagraph(); return; }
            for (var i = _frames.Count - 1; i >= index; i--)
            {
                var tag = _frames[i].Tag;
                if (BlockTags.Contains(tag)) EndParagraph();
                _frames.RemoveAt(i);
                if (_tables.Count == 0) continue;
                var table = _tables.Peek();
                switch (tag)
                {
                    case "td" or "th" when table.Cell != null:
                        _targets.Pop();
                        (table.Row ??= new Row()).Cells.Add(table.Cell);
                        table.Cell = null;
                        break;
                    case "tr" when table.Row != null:
                        if (table.Row.Cells.Count > 0) { table.Table.Rows.Add(table.Row); table.HintsDone = true; }
                        table.Row = null;
                        break;
                    case "table":
                        _tables.Pop();
                        if (table.Cell != null) { _targets.Pop(); (table.Row ??= new Row()).Cells.Add(table.Cell); }
                        if (table.Row is { Cells.Count: > 0 }) table.Table.Rows.Add(table.Row);
                        if (table.Table.Rows.Count == 0) break;
                        var columns = Math.Min(table.Table.Rows.Max(r => r.Cells.Sum(c => c.Span)), Cell.MaxSpan);
                        table.Table.Grid = new float[columns];
                        var total = table.Hints.Sum();
                        if (table.Hints.Count == columns && table.Hints.All(h => h > 0))
                            for (var c = 0; c < columns; c++) table.Table.Grid[c] = table.Hints[c] / total; // fractions of the available width
                        _targets.Peek().Add(table.Table);
                        break;
                }
            }
        }

        private static bool HasBorderStyle(string? style) => style != null && style.Contains("border", StringComparison.OrdinalIgnoreCase) &&
            (style.Contains("solid", StringComparison.OrdinalIgnoreCase) || style.Contains("double", StringComparison.OrdinalIgnoreCase) ||
             style.Contains("dashed", StringComparison.OrdinalIgnoreCase) || style.Contains("dotted", StringComparison.OrdinalIgnoreCase));

        private static Align? ParseAlign(string value) => value.Trim().ToLowerInvariant() switch
        {
            "center" => Align.Center,
            "right" or "end" => Align.Right,
            "justify" => Align.Justify,
            "left" or "start" => Align.Left,
            _ => null,
        };

        private static string? StyleValue(string? style, string key)
        {
            if (style == null) return null;
            foreach (var declaration in style.Split(';'))
            {
                var colon = declaration.IndexOf(':');
                if (colon > 0 && declaration[..colon].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) return declaration[(colon + 1)..].Trim();
            }
            return null;
        }

        private static void ApplyStyle(Frame frame, string style, float parentSize)
        {
            foreach (var declaration in style.Split(';'))
            {
                var colon = declaration.IndexOf(':');
                if (colon <= 0) continue;
                var value = declaration[(colon + 1)..].Trim();
                var lower = value.ToLowerInvariant();
                switch (declaration[..colon].Trim().ToLowerInvariant())
                {
                    case "font-weight": frame.Run.Bold = lower is "bold" or "bolder" || (int.TryParse(lower, out var weight) && weight >= 600); break;
                    case "font-style": frame.Run.Italic = lower is "italic" or "oblique"; break;
                    case "text-decoration" or "text-decoration-line": frame.Run.Underline = lower.Contains("underline"); break;
                    case "color": frame.Run.Color = FormatDetector.Color(value) ?? frame.Run.Color; break;
                    case "text-align": frame.Align = ParseAlign(value) ?? frame.Align; break;
                    case "font-family": if (value.Length > 0) frame.Run.Font = value.Split(',')[0].Trim(' ', '\'', '"'); break;
                    case "margin-left" or "padding-left": frame.Indent += FormatDetector.Length(value) ?? 0; break;
                    case "font-size":
                        if (lower.EndsWith("em") && float.TryParse(lower[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var em)) frame.Run.Size = parentSize * em;
                        else if (lower.EndsWith('%') && float.TryParse(lower[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)) frame.Run.Size = parentSize * percent / 100;
                        else frame.Run.Size = FormatDetector.Length(value) ?? frame.Run.Size;
                        break;
                }
            }
        }

        private Paragraph Paragraph()
        {
            if (_paragraph != null) return _paragraph;
            var block = _frames.LastOrDefault(f => BlockTags.Contains(f.Tag));
            var spaced = block is { Spaced: true };
            var props = new ParaProps { Align = Top.Align, IndentLeft = Top.Indent, AutoBefore = spaced, AutoAfter = spaced };
            _paragraph = new Paragraph { Props = props, MarkProps = Top.Run };
            _lastWasSpace = true;
            var item = _frames.LastOrDefault(f => f.ListItem);
            if (item is { Bullet: not null })
            {
                props.FirstLine = -14;
                _paragraph.Inlines.Add(new TextInline { Props = Top.Run, Text = item.Bullet });
                item.Bullet = null;
            }
            return _paragraph;
        }

        private void EndParagraph()
        {
            if (_paragraph == null) return;
            if (_paragraph.Inlines.Count > 0 && _paragraph.Inlines[^1] is TextInline { Text: " " }) _paragraph.Inlines.RemoveAt(_paragraph.Inlines.Count - 1);
            if (_paragraph.Inlines.Count > 0) _targets.Peek().Add(_paragraph);
            _paragraph = null;
            _lastWasSpace = true;
        }

        private void Text(string raw)
        {
            if (raw.Length == 0) return;
            // text directly inside table structure (between rows/cells) is not rendered
            if (Top.Tag is "table" or "tr" or "tbody" or "thead" or "tfoot" && string.IsNullOrWhiteSpace(raw)) return;
            var text = WebUtility.HtmlDecode(raw);
            if (Top.Pre)
            {
                var lines = text.Split('\n');
                for (var i = 0; i < lines.Length; i++)
                {
                    if (i > 0) Paragraph().Inlines.Add(new BreakInline { Props = Top.Run });
                    var line = lines[i].TrimEnd('\r');
                    if (line.Length > 0) Paragraph().Inlines.Add(new TextInline { Props = Top.Run, Text = line });
                }
                return;
            }

            var collapsed = new StringBuilder(text.Length);
            foreach (var c in text)
            {
                if (c is ' ' or '\t' or '\r' or '\n' or '\f')
                {
                    if (!_lastWasSpace) collapsed.Append(' ');
                    _lastWasSpace = true;
                }
                else
                {
                    if (_paragraph == null && collapsed.Length == 0) Paragraph(); // sets _lastWasSpace, so start it before appending
                    collapsed.Append(c);
                    _lastWasSpace = false;
                }
            }
            if (collapsed.Length == 0 || (collapsed.Length == 1 && collapsed[0] == ' ' && _paragraph == null)) return;
            Paragraph().Inlines.Add(new TextInline { Props = Top.Run, Text = collapsed.ToString() });
            _lastWasSpace = collapsed[^1] == ' ';
        }
    }
}
