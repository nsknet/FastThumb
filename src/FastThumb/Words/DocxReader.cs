using System.Globalization;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using FastThumb.Layout;
using SkiaSharp;

namespace FastThumb.Words;

internal sealed class DocxReader : IPageSource, IDisposable
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace Wp = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";

    private readonly ZipArchive? _zip;
    private readonly Dictionary<string, string>? _flatText;      // Flat OPC: part name -> XML text
    private readonly Dictionary<string, byte[]>? _flatBinary;    // Flat OPC: part name -> bytes
    private readonly string _documentXml;
    private readonly Dictionary<string, StyleDef> _styles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (ParaProps P, RunProps R)> _resolved = new(StringComparer.Ordinal);
    private readonly ParaProps _defaultPara = new();
    private readonly RunProps _defaultRun = new();
    private string? _defaultParaStyle;
    private Dictionary<string, string>? _relationships;
    private (string Major, string Minor)? _theme;
    private string _part = "";                                   // prefix for relationship ids of the part being parsed
    private readonly Dictionary<string, Dictionary<string, string>> _partRelationships = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Block>> _stories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _storyParts = new(StringComparer.Ordinal);   // "header-default" → part path
    private bool _titlePage;
    private Dictionary<string, Dictionary<int, (string Format, string Text, float? Left, float? Hanging)>>? _numbering;
    private readonly Dictionary<(string, int), int> _counters = new();

    public float HeaderDistance { get; private set; } = 36;
    public float FooterDistance { get; private set; } = 36;

    public PageSetup Page { get; }

    private sealed record StyleDef(string? BasedOn, XElement? PPr, XElement? RPr, XElement? TblPr);

    public DocxReader(Stream docx) : this(new ZipArchive(docx, ZipArchiveMode.Read, leaveOpen: true), null, null) { }

    /// <summary>Flat OPC: the whole package as one XML file (pkg:package / pkg:part).</summary>
    public static DocxReader FromFlatOpc(string xml)
    {
        XNamespace pkg = "http://schemas.microsoft.com/office/2006/xmlPackage";
        var text = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var binary = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in Guard.ParseXml(xml).Root!.Elements(pkg + "part"))
        {
            var name = ((string?)part.Attribute(pkg + "name") ?? "").TrimStart('/');
            if (part.Element(pkg + "xmlData")?.Elements().FirstOrDefault() is { } content) text[name] = content.ToString(SaveOptions.DisableFormatting);
            else if (part.Element(pkg + "binaryData") is { } data) binary[name] = Convert.FromBase64String(data.Value);
        }
        return new DocxReader(null, text, binary);
    }

    /// <summary>
    /// Word 2003 XML (WordprocessingML 2003): one XML file whose elements mirror the later OOXML ones under an
    /// older namespace, with the body wrapped in wx:sect / wx:sub-section. Re-labelled and read as OOXML parts.
    /// </summary>
    public static DocxReader FromWordMl(string xml)
    {
        xml = xml.Replace("http://schemas.microsoft.com/office/word/2003/wordml", W.NamespaceName);
        var text = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["word/document.xml"] = xml };
        if (Guard.ParseXml(xml).Root?.Element(W + "styles") is { } styles) text["word/styles.xml"] = styles.ToString(SaveOptions.DisableFormatting);
        return new DocxReader(null, text, new Dictionary<string, byte[]>());
    }

    private DocxReader(ZipArchive? zip, Dictionary<string, string>? flatText, Dictionary<string, byte[]>? flatBinary)
    {
        _zip = zip;
        _flatText = flatText;
        _flatBinary = flatBinary;
        _documentXml = ReadText("word/document.xml") ?? throw new InvalidDataException("word/document.xml not found");
        Page = ReadPageSetup(_documentXml);
        LoadStyles();
        ReadStoryReferences();
    }

    public void Dispose() => _zip?.Dispose();

    private string? ReadText(string name)
    {
        if (_flatText != null) return _flatText.GetValueOrDefault(name);
        var entry = _zip!.GetEntry(name);
        if (entry == null) return null;
        using var reader = new StreamReader(Guard.Open(entry));
        return reader.ReadToEnd();
    }

    /// <summary>
    /// The first section's properties sit at the END of that section, so they are located with a cheap text
    /// scan instead of parsing the whole body (which would defeat early-stop pagination).
    /// </summary>
    private static PageSetup ReadPageSetup(string xml)
    {
        var at = xml.IndexOf("<w:sectPr", StringComparison.Ordinal);
        if (at < 0) return PageSetup.Default;
        var end = xml.IndexOf("</w:sectPr>", at, StringComparison.Ordinal);
        var slice = end > at ? xml.AsSpan(at, end - at) : xml.AsSpan(at, Math.Min(2000, xml.Length - at));

        float Attr(ReadOnlySpan<char> element, string name, float fallback)
        {
            var i = element.IndexOf((name + "=\"").AsSpan(), StringComparison.Ordinal);
            if (i < 0) return fallback;
            var value = element[(i + name.Length + 2)..];
            var q = value.IndexOf('"');
            return q > 0 && float.TryParse(value[..q], NumberStyles.Float, CultureInfo.InvariantCulture, out var twips) ? Math.Abs(twips) / 20f : fallback;
        }
        ReadOnlySpan<char> Element(ReadOnlySpan<char> source, string tag)
        {
            var i = source.IndexOf(tag.AsSpan(), StringComparison.Ordinal);
            if (i < 0) return default;
            var rest = source[i..];
            var close = rest.IndexOf('>');
            return close > 0 ? rest[..close] : default;
        }

        var size = Element(slice, "<w:pgSz");
        var margin = Element(slice, "<w:pgMar");
        var d = PageSetup.Default;
        return new PageSetup(
            Attr(size, "w:w", d.Width), Attr(size, "w:h", d.Height),
            Attr(margin, "w:top", d.Top), Attr(margin, "w:right", d.Right), Attr(margin, "w:bottom", d.Bottom), Attr(margin, "w:left", d.Left));
    }

    // ---- headers and footers ---------------------------------------------------------------------

    // linear time whatever the slice holds (a backtracking scan of [^>]* is quadratic on a crafted sectPr)
    private const System.Text.RegularExpressions.RegexOptions NonBacktracking = System.Text.RegularExpressions.RegexOptions.NonBacktracking;

    private void ReadStoryReferences()
    {
        var at = _documentXml.IndexOf("<w:sectPr", StringComparison.Ordinal);
        if (at < 0) return;
        var end = _documentXml.IndexOf("</w:sectPr>", at, StringComparison.Ordinal);
        var slice = end > at ? _documentXml[at..end] : "";
        _titlePage = slice.Contains("<w:titlePg", StringComparison.Ordinal);
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(slice, "<w:(header|footer)Reference ([^>]*)>", NonBacktracking))
        {
            var type = System.Text.RegularExpressions.Regex.Match(m.Groups[2].Value, "w:type=\"([^\"]+)\"", NonBacktracking).Groups[1].Value;
            var id = System.Text.RegularExpressions.Regex.Match(m.Groups[2].Value, "r:id=\"([^\"]+)\"", NonBacktracking).Groups[1].Value;
            if (Relationships("document.xml").TryGetValue(id, out var target)) _storyParts[m.Groups[1].Value + "-" + type] = target;
        }
        float Distance(string name, float fallback)
        {
            var m = System.Text.RegularExpressions.Regex.Match(slice, "<w:pgMar [^>]*w:" + name + "=\"(\\d+)\"", NonBacktracking);
            return m.Success ? float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) / 20f : fallback;
        }
        HeaderDistance = Distance("header", 36);
        FooterDistance = Distance("footer", 36);
    }

    private Dictionary<string, string> Relationships(string part)
    {
        if (_partRelationships.TryGetValue(part, out var hit)) return hit;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (ReadText("word/_rels/" + part + ".rels") is { } xml)
            foreach (var rel in Guard.ParseXml(xml).Root!.Elements(Rel + "Relationship"))
                if ((string?)rel.Attribute("Id") is { } id && (string?)rel.Attribute("Target") is { } target) result[id] = target;
        return _partRelationships[part] = result;
    }

    /// <summary>Blocks of the header or footer for a page; the first page has its own when the section says so.</summary>
    private List<Block> Story(string kind, bool firstPage)
    {
        var key = kind + "-" + (firstPage && _titlePage ? "first" : "default");
        if (_stories.TryGetValue(key, out var hit)) return hit;
        var blocks = new List<Block>();
        if (_storyParts.TryGetValue(key, out var target) && ReadText("word/" + target) is { } xml)
        {
            var previous = _part;
            _part = target + "|";
            try { foreach (var element in Guard.ParseXml(xml).Root!.Elements()) blocks.AddRange(ParseBlock(element)); }
            finally { _part = previous; }
        }
        return _stories[key] = blocks;
    }

    public IReadOnlyList<Block> Header(bool firstPage) => Story("header", firstPage);
    public IReadOnlyList<Block> Footer(bool firstPage) => Story("footer", firstPage);

    // ---- numbering ------------------------------------------------------------------------------

    private void ApplyNumbering(XElement? numPr, ParaProps props, bool hasDirectIndent)
    {
        var numId = (string?)numPr?.Element(W + "numId")?.Attribute(W + "val");
        if (numId is null or "0") return;
        var level = (int?)numPr!.Element(W + "ilvl")?.Attribute(W + "val") ?? 0;
        if (_numbering == null)
        {
            _numbering = new Dictionary<string, Dictionary<int, (string, string, float?, float?)>>(StringComparer.Ordinal);
            if (ReadText("word/numbering.xml") is { } xml)
            {
                var root = Guard.ParseXml(xml).Root!;
                var abstracts = root.Elements(W + "abstractNum").ToDictionary(a => (string?)a.Attribute(W + "abstractNumId") ?? "", a => a.Elements(W + "lvl").ToDictionary(
                    l => (int?)l.Attribute(W + "ilvl") ?? 0,
                    l => ((string?)l.Element(W + "numFmt")?.Attribute(W + "val") ?? "decimal", (string?)l.Element(W + "lvlText")?.Attribute(W + "val") ?? "",
                          l.Element(W + "pPr")?.Element(W + "ind")?.Attribute(W + "left") is { } left ? Twips(left) : (float?)null,
                          l.Element(W + "pPr")?.Element(W + "ind")?.Attribute(W + "hanging") is { } hanging ? Twips(hanging) : (float?)null)));
                foreach (var num in root.Elements(W + "num"))
                    if ((string?)num.Attribute(W + "numId") is { } id && abstracts.TryGetValue((string?)num.Element(W + "abstractNumId")?.Attribute(W + "val") ?? "", out var levels)) _numbering[id] = levels;
            }
        }
        if (!_numbering.TryGetValue(numId, out var definition) || !definition.TryGetValue(level, out var lvl)) { props.Bullet = "•"; return; }
        _counters[(numId, level)] = _counters.GetValueOrDefault((numId, level)) + 1;
        foreach (var key in _counters.Keys.Where(k => k.Item1 == numId && k.Item2 > level).ToList()) _counters.Remove(key); // deeper levels restart
        if (lvl.Format == "bullet") props.Bullet = "•";
        else if (lvl.Format == "none") return;
        else
        {
            var text = lvl.Text;
            for (var i = 0; i <= level; i++)
            {
                var n = _counters.GetValueOrDefault((numId, i), 1);
                var format = definition.TryGetValue(i, out var other) ? other.Format : "decimal";
                var value = format switch { "lowerLetter" => ((char)('a' + (n - 1) % 26)).ToString(), "upperLetter" => ((char)('A' + (n - 1) % 26)).ToString(), _ => n.ToString(CultureInfo.InvariantCulture) };
                text = text.Replace("%" + (i + 1), value);
            }
            props.Bullet = text;
        }
        if (!hasDirectIndent && lvl.Left != null) { props.IndentLeft = lvl.Left.Value; props.FirstLine = -(lvl.Hanging ?? 0); }
        else if (props.FirstLine >= 0 && props.IndentLeft < 1) { props.IndentLeft = 36; props.FirstLine = -18; }
    }

    // ---- styles ---------------------------------------------------------------------------------

    private void LoadStyles()
    {
        var xml = ReadText("word/styles.xml");
        if (xml == null) return;
        var root = Guard.ParseXml(xml).Root;
        if (root == null) return;

        var defaults = root.Element(W + "docDefaults");
        ApplyRun(_defaultRun, defaults?.Element(W + "rPrDefault")?.Element(W + "rPr"));
        ApplyPara(_defaultPara, defaults?.Element(W + "pPrDefault")?.Element(W + "pPr"));

        foreach (var style in root.Elements(W + "style"))
        {
            var id = (string?)style.Attribute(W + "styleId");
            if (id == null) continue;
            _styles[id] = new StyleDef((string?)style.Element(W + "basedOn")?.Attribute(W + "val"),
                style.Element(W + "pPr"), style.Element(W + "rPr"), style.Element(W + "tblPr"));
            if ((string?)style.Attribute(W + "type") == "paragraph" && IsOn(style.Attribute(W + "default")))
                _defaultParaStyle = id;
        }
    }

    private IEnumerable<StyleDef> Chain(string? id)
    {
        var chain = new List<StyleDef>();
        for (var guard = 0; id != null && guard < 20 && _styles.TryGetValue(id, out var def); guard++)
        {
            chain.Add(def);
            id = def.BasedOn;
        }
        chain.Reverse(); // base first, most derived last
        return chain;
    }

    private (ParaProps P, RunProps R) ResolveParagraphStyle(string? id)
    {
        id ??= _defaultParaStyle ?? "";
        if (_resolved.TryGetValue(id, out var hit)) return hit;
        var p = _defaultPara.Clone();
        var r = _defaultRun.Clone();
        foreach (var def in Chain(id.Length == 0 ? null : id))
        {
            ApplyPara(p, def.PPr);
            ApplyRun(r, def.RPr);
        }
        return _resolved[id] = (p, r);
    }

    private static bool IsOn(XAttribute? value) => value != null && value.Value is not ("0" or "false" or "off");
    private static bool Toggle(XElement element) => element.Attribute(W + "val") is not { } v || v.Value is not ("0" or "false" or "off" or "none");
    private static float Twips(XAttribute? value, float fallback = 0)
        => value != null && float.TryParse(value.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var twips) ? twips / 20f : fallback;

    private void ApplyRun(RunProps props, XElement? rPr)
    {
        if (rPr == null) return;
        foreach (var e in rPr.Elements())
        {
            switch (e.Name.LocalName)
            {
                case "rFonts":
                    var family = (string?)e.Attribute(W + "ascii") ?? (string?)e.Attribute(W + "hAnsi");
                    if (family == null && (string?)e.Attribute(W + "asciiTheme") is { } themed)
                        family = themed.StartsWith("major", StringComparison.Ordinal) ? Theme().Major : Theme().Minor;
                    if (!string.IsNullOrEmpty(family)) props.Font = family;
                    break;
                case "sz": props.Size = Twips(e.Attribute(W + "val"), props.Size / 10f) * 10f; break; // half-points
                case "b": props.Bold = Toggle(e); break;
                case "i": props.Italic = Toggle(e); break;
                case "u": props.Underline = Toggle(e); break;
                case "color":
                    var hex = (string?)e.Attribute(W + "val");
                    props.Color = hex is { Length: 6 } && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)
                        ? new SKColor(0xFF000000 | rgb) : SKColors.Black;
                    break;
            }
        }
    }

    private static void ApplyPara(ParaProps props, XElement? pPr)
    {
        if (pPr == null) return;
        foreach (var e in pPr.Elements())
        {
            switch (e.Name.LocalName)
            {
                case "jc":
                    props.Align = (string?)e.Attribute(W + "val") switch
                    {
                        "center" => Align.Center,
                        "right" or "end" => Align.Right,
                        "both" or "distribute" => Align.Justify,
                        _ => Align.Left,
                    };
                    break;
                case "ind":
                    props.IndentLeft = Twips(e.Attribute(W + "left") ?? e.Attribute(W + "start"), props.IndentLeft);
                    props.IndentRight = Twips(e.Attribute(W + "right") ?? e.Attribute(W + "end"), props.IndentRight);
                    if (e.Attribute(W + "hanging") is { } hanging) props.FirstLine = -Twips(hanging);
                    else if (e.Attribute(W + "firstLine") is { } first) props.FirstLine = Twips(first);
                    break;
                case "spacing":
                    props.SpaceBefore = Twips(e.Attribute(W + "before"), props.SpaceBefore);
                    props.SpaceAfter = Twips(e.Attribute(W + "after"), props.SpaceAfter);
                    if (e.Attribute(W + "beforeAutospacing") is { } autoBefore) props.AutoBefore = IsOn(autoBefore);
                    if (e.Attribute(W + "afterAutospacing") is { } autoAfter) props.AutoAfter = IsOn(autoAfter);
                    if (e.Attribute(W + "line") is { } line && float.TryParse(line.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    {
                        props.Line = value;
                        props.LineRule = (string?)e.Attribute(W + "lineRule") ?? "auto";
                    }
                    break;
                case "pageBreakBefore": props.PageBreakBefore = Toggle(e); break;
                case "tabs":
                    // a derived list: the inherited one is shared with the style it came from
                    var tabs = props.Tabs != null ? new List<(float Position, int Kind)>(props.Tabs) : [];
                    foreach (var tab in e.Elements(W + "tab"))
                    {
                        var position = Twips(tab.Attribute(W + "pos"));
                        var kind = (string?)tab.Attribute(W + "val");
                        tabs.RemoveAll(t => Math.Abs(t.Position - position) < 0.5f);
                        if (kind is not ("clear" or "bar")) tabs.Add((position, kind switch { "center" => 1, "right" or "end" or "decimal" => 2, _ => 0 }));
                    }
                    tabs.Sort((a, b) => a.Position.CompareTo(b.Position));
                    props.Tabs = tabs;
                    break;
                case "pBdr": props.BorderBottom = (string?)e.Element(W + "bottom")?.Attribute(W + "val") is not (null or "none" or "nil"); break;
                case "shd":
                    props.Shade = (string?)e.Attribute(W + "fill") is { Length: 6 } fill && uint.TryParse(fill, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var shade) ? new SKColor(0xFF000000 | shade) : null;
                    break;
            }
        }
    }

    private (string Major, string Minor) Theme()
    {
        if (_theme != null) return _theme.Value;
        string major = "Calibri Light", minor = "Calibri";
        if (ReadText("word/theme/theme1.xml") is { } xml)
        {
            var root = Guard.ParseXml(xml).Root;
            major = (string?)root?.Descendants(A + "majorFont").FirstOrDefault()?.Element(A + "latin")?.Attribute("typeface") ?? major;
            minor = (string?)root?.Descendants(A + "minorFont").FirstOrDefault()?.Element(A + "latin")?.Attribute("typeface") ?? minor;
        }
        return (_theme = (major, minor)).Value;
    }

    // ---- body -----------------------------------------------------------------------------------

    /// <summary>Lazily yields top-level blocks in document order; stop enumerating to stop parsing.</summary>
    public IEnumerable<Block> Blocks()
    {
        using var reader = Guard.Depth(XmlReader.Create(new StringReader(_documentXml), new XmlReaderSettings { IgnoreWhitespace = false, DtdProcessing = DtdProcessing.Prohibit }));
        if (!reader.ReadToFollowing("body", W.NamespaceName)) yield break;
        var depth = reader.Depth + 1;
        reader.Read();
        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Depth == depth)
            {
                var element = (XElement)XNode.ReadFrom(reader); // advances past the element
                foreach (var block in ParseBlock(element)) yield return block;
            }
            else if (reader.Depth < depth) yield break;
            else reader.Read();
        }
    }

    private IEnumerable<Block> ParseBlock(XElement element)
    {
        Guard.Deeper();
        switch (element.Name.LocalName)
        {
            case "p": yield return ParseParagraph(element); break;
            case "tbl": yield return ParseTable(element); break;
            case "sdt" or "customXml" or "smartTag" or "sect" or "sub-section":
                foreach (var child in (element.Element(W + "sdtContent") ?? element).Elements())
                    foreach (var block in ParseBlock(child)) yield return block;
                break;
        }
    }

    private Paragraph ParseParagraph(XElement p)
    {
        var pPr = p.Element(W + "pPr");
        var (styleP, styleR) = ResolveParagraphStyle((string?)pPr?.Element(W + "pStyle")?.Attribute(W + "val"));
        var props = styleP.Clone();
        ApplyPara(props, pPr);
        if (pPr?.Element(W + "sectPr") is { } section && (string?)section.Element(W + "type")?.Attribute(W + "val") != "continuous") props.BreakAfter = true;
        var numPr = pPr?.Element(W + "numPr") ?? Chain((string?)pPr?.Element(W + "pStyle")?.Attribute(W + "val") ?? _defaultParaStyle).Select(d => d.PPr?.Element(W + "numPr")).LastOrDefault(n => n != null);
        ApplyNumbering(numPr, props, pPr?.Element(W + "ind") != null);
        var mark = styleR.Clone();
        ApplyRun(mark, pPr?.Element(W + "rPr"));
        var paragraph = new Paragraph { Props = props, MarkProps = mark };
        ParseInlines(p, styleR, paragraph.Inlines);
        return paragraph;
    }

    private void ParseInlines(XElement container, RunProps baseRun, List<Inline> output)
    {
        Guard.Deeper();
        foreach (var child in container.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "r": ParseRun(child, baseRun, output); break;
                case "hyperlink" or "smartTag" or "ins" or "fldSimple" or "customXml" or "moveTo":
                    ParseInlines(child, baseRun, output); break;
                case "sdt":
                    if (child.Element(W + "sdtContent") is { } content) ParseInlines(content, baseRun, output);
                    break;
            }
        }
    }

    private void ParseRun(XElement run, RunProps baseRun, List<Inline> output)
    {
        var props = baseRun.Clone();
        var rPr = run.Element(W + "rPr");
        if ((string?)rPr?.Element(W + "rStyle")?.Attribute(W + "val") is { } charStyle)
            foreach (var def in Chain(charStyle)) ApplyRun(props, def.RPr);
        ApplyRun(props, rPr);
        if (rPr?.Element(W + "vanish") is { } vanish && Toggle(vanish)) return;

        foreach (var e in run.Elements())
        {
            switch (e.Name.LocalName)
            {
                case "t": if (e.Value.Length > 0) output.Add(new TextInline { Props = props, Text = e.Value }); break;
                case "tab": output.Add(new TabInline { Props = props }); break;
                case "noBreakHyphen": output.Add(new TextInline { Props = props, Text = "-" }); break;
                case "br" or "cr": output.Add(new BreakInline { Props = props, Page = (string?)e.Attribute(W + "type") == "page" }); break;
                case "lastRenderedPageBreak": output.Add(new BreakInline { Props = props, Page = true, Soft = true }); break;
                case "drawing": ParseDrawing(e, props, output); break;
                case "pict" or "object": ParsePict(e, props, output); break;
                case "AlternateContent":
                    // modern drawing in mc:Choice, legacy VML in mc:Fallback: only the first is read
                    if (e.Elements().FirstOrDefault(c => c.Name.LocalName == "Choice")?.Element(W + "drawing") is { } chosen) ParseDrawing(chosen, props, output);
                    else if (e.Elements().FirstOrDefault(c => c.Name.LocalName == "Fallback")?.Element(W + "pict") is { } legacy) ParsePict(legacy, props, output);
                    break;
            }
        }
    }

    private void ParseDrawing(XElement drawing, RunProps props, List<Inline> output)
    {
        var inline = drawing.Element(Wp + "inline");
        var anchor = drawing.Element(Wp + "anchor");
        var extent = (inline ?? anchor)?.Element(Wp + "extent");
        if (extent == null) return;
        float width = (float)((double?)extent.Attribute("cx") ?? 0) / 12700f, height = (float)((double?)extent.Attribute("cy") ?? 0) / 12700f;
        var blip = (string?)drawing.Descendants(A + "blip").FirstOrDefault()?.Attribute(R + "embed");
        if (inline != null)
        {
            if (blip != null) output.Add(new ImageInline { Props = props, RelationshipId = _part + blip, Width = width, Height = height });
            return;
        }
        var floating = new FloatInline { Props = props, Width = width, Height = height, RelationshipId = blip != null ? _part + blip : null };
        if (anchor!.Element(Wp + "positionH") is { } h)
        {
            floating.RelativeH = (string?)h.Attribute("relativeFrom") ?? "column";
            floating.X = (float)((double?)h.Element(Wp + "posOffset") ?? 0) / 12700f;
            floating.AlignH = (string?)h.Element(Wp + "align");
        }
        if (anchor.Element(Wp + "positionV") is { } v)
        {
            floating.RelativeV = (string?)v.Attribute("relativeFrom") ?? "paragraph";
            floating.Y = (float)((double?)v.Element(Wp + "posOffset") ?? 0) / 12700f;
            floating.AlignV = (string?)v.Element(Wp + "align");
        }
        if (drawing.Descendants(W + "txbxContent").FirstOrDefault() is { } textBox)
        {
            floating.Text = [];
            foreach (var child in textBox.Elements()) floating.Text.AddRange(ParseBlock(child));
        }
        var shape = drawing.Descendants().FirstOrDefault(x => x.Name.LocalName == "spPr");
        floating.Fill = DrawingColor(shape?.Element(A + "solidFill")) ?? DrawingColor(shape?.Element(A + "gradFill")?.Descendants(A + "gs").FirstOrDefault());
        if (floating.Fill == null && shape != null && shape.Element(A + "noFill") == null && blip == null &&
            drawing.Descendants().FirstOrDefault(x => x.Name.LocalName == "fillRef") is { } reference && (string?)reference.Attribute("idx") != "0")
            floating.Fill = DrawingColor(reference);
        if (floating.RelationshipId != null || floating.Text != null || floating.Fill != null) output.Add(floating);
    }

    /// <summary>Colour child of a DrawingML fill: literal RGB, or a theme slot looked up in the document theme.</summary>
    private SKColor? DrawingColor(XElement? container)
    {
        if (container == null) return null;
        if ((string?)container.Element(A + "srgbClr")?.Attribute("val") is { Length: 6 } hex && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            return new SKColor(0xFF000000 | rgb);
        if ((string?)container.Element(A + "schemeClr")?.Attribute("val") is not { } slot) return null;
        if (_themeColors == null)
        {
            _themeColors = new Dictionary<string, SKColor>(StringComparer.Ordinal);
            if (ReadText("word/theme/theme1.xml") is { } xml)
                foreach (var entry in Guard.ParseXml(xml).Root!.Descendants(A + "clrScheme").FirstOrDefault()?.Elements() ?? [])
                    if (((string?)entry.Element(A + "srgbClr")?.Attribute("val") ?? (string?)entry.Element(A + "sysClr")?.Attribute("lastClr")) is { Length: 6 } value &&
                        uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var themed)) _themeColors[entry.Name.LocalName] = new SKColor(0xFF000000 | themed);
        }
        slot = slot switch { "tx1" => "dk1", "bg1" => "lt1", "tx2" => "dk2", "bg2" => "lt2", _ => slot };
        return _themeColors.TryGetValue(slot, out var color) ? color : null;
    }

    private Dictionary<string, SKColor>? _themeColors;
    private static readonly XNamespace V = "urn:schemas-microsoft-com:vml";

    /// <summary>
    /// Legacy VML (w:pict / w:object): shapes carry their geometry in a CSS-like style attribute. Absolutely
    /// positioned shapes become floats (picture, filled box, text box); the others are inline pictures.
    /// </summary>
    private void ParsePict(XElement pict, RunProps props, List<Inline> output)
    {
        foreach (var shape in pict.Descendants().Where(x => x.Name.Namespace == V && x.Name.LocalName is "shape" or "rect" or "roundrect" or "oval"))
        {
            var style = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var declaration in ((string?)shape.Attribute("style") ?? "").Split(';'))
            {
                var colon = declaration.IndexOf(':');
                if (colon > 0) style[declaration[..colon].Trim()] = declaration[(colon + 1)..].Trim();
            }
            float Size(string name) => FormatDetector.Length(style.GetValueOrDefault(name)) ?? 0;
            float width = Size("width"), height = Size("height");
            if (width <= 0 || height <= 0) continue;
            var image = (string?)shape.Element(V + "imagedata")?.Attribute(R + "id");
            if (!string.Equals(style.GetValueOrDefault("position"), "absolute", StringComparison.OrdinalIgnoreCase))
            {
                if (image != null) output.Add(new ImageInline { Props = props, RelationshipId = _part + image, Width = width, Height = height });
                continue;
            }
            var floating = new FloatInline
            {
                Props = props,
                Width = width,
                Height = height,
                RelationshipId = image != null ? _part + image : null,
                X = Size(style.ContainsKey("margin-left") ? "margin-left" : "left"),
                Y = Size(style.ContainsKey("margin-top") ? "margin-top" : "top"),
                RelativeH = style.GetValueOrDefault("mso-position-horizontal-relative") switch { "page" => "page", "margin" => "margin", _ => "column" },
                RelativeV = style.GetValueOrDefault("mso-position-vertical-relative") switch { "page" => "page", "margin" => "margin", _ => "paragraph" },
                AlignH = style.GetValueOrDefault("mso-position-horizontal") is "left" or "center" or "right" ? style["mso-position-horizontal"] : null,
                AlignV = style.GetValueOrDefault("mso-position-vertical") is "top" or "center" or "bottom" ? style["mso-position-vertical"] : null,
            };
            if ((string?)shape.Attribute("filled") is not ("f" or "false") && (string?)shape.Attribute("fillcolor") is { } fill)
                floating.Fill = FormatDetector.Color(fill.Split(' ')[0]);
            if (shape.Descendants(W + "txbxContent").FirstOrDefault() is { } textBox)
            {
                floating.Text = [];
                foreach (var child in textBox.Elements()) floating.Text.AddRange(ParseBlock(child));
            }
            if (floating.RelationshipId != null || floating.Text != null || floating.Fill != null) output.Add(floating);
        }
    }

    private Table ParseTable(XElement tbl)
    {
        Guard.Deeper();
        var table = new Table();
        var tblPr = tbl.Element(W + "tblPr");
        if ((string?)tblPr?.Element(W + "tblStyle")?.Attribute(W + "val") is { } styleId)
            foreach (var def in Chain(styleId)) ApplyBorders(ref table.Borders, def.TblPr?.Element(W + "tblBorders"));
        ApplyBorders(ref table.Borders, tblPr?.Element(W + "tblBorders"));
        table.Indent = Twips(tblPr?.Element(W + "tblInd")?.Attribute(W + "w"));
        if (tblPr?.Element(W + "tblCellMar") is { } cellMargins)
        {
            float Side(string name, string alternative, float fallback) => (cellMargins.Element(W + name) ?? cellMargins.Element(W + alternative))?.Attribute(W + "w") is { } w ? Twips(w) : fallback;
            table.PadLeft = Side("left", "start", table.PadLeft); table.PadRight = Side("right", "end", table.PadRight);
            table.PadTop = Side("top", "top", 0); table.PadBottom = Side("bottom", "bottom", 0);
        }
        table.Align = (string?)tblPr?.Element(W + "jc")?.Attribute(W + "val") switch { "center" => Align.Center, "right" or "end" => Align.Right, _ => Align.Left };
        table.Grid = tbl.Element(W + "tblGrid")?.Elements(W + "gridCol").Select(c => Twips(c.Attribute(W + "w"))).ToArray() ?? [];

        foreach (var tr in tbl.Elements(W + "tr"))
        {
            var row = new Row { MinHeight = Twips(tr.Element(W + "trPr")?.Element(W + "trHeight")?.Attribute(W + "val")) };
            foreach (var tc in tr.Elements(W + "tc"))
            {
                var tcPr = tc.Element(W + "tcPr");
                var cell = new Cell { Span = Math.Clamp((int?)tcPr?.Element(W + "gridSpan")?.Attribute(W + "val") ?? 1, 1, Cell.MaxSpan) };
                ApplyBorders(ref cell.Borders, tcPr?.Element(W + "tcBorders"));
                if ((string?)tcPr?.Element(W + "shd")?.Attribute(W + "fill") is { Length: 6 } cellFill && uint.TryParse(cellFill, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var cellRgb))
                    cell.Fill = new SKColor(0xFF000000 | cellRgb);
                foreach (var child in tc.Elements()) cell.Blocks.AddRange(ParseBlock(child));
                row.Cells.Add(cell);
            }
            if (row.Cells.Count > 0) table.Rows.Add(row);
        }
        if (table.Grid.Length == 0 && table.Rows.Count > 0)
            table.Grid = Enumerable.Repeat(0f, Math.Min(table.Rows.Max(r => r.Cells.Sum(c => c.Span)), Cell.MaxSpan)).ToArray(); // widths decided at layout
        return table;
    }

    private static void ApplyBorders(ref Borders borders, XElement? source)
    {
        if (source == null) return;
        foreach (var e in source.Elements())
        {
            var visible = (string?)e.Attribute(W + "val") is not (null or "none" or "nil");
            switch (e.Name.LocalName)
            {
                case "top": borders.Top = visible; break;
                case "bottom": borders.Bottom = visible; break;
                case "left" or "start": borders.Left = visible; break;
                case "right" or "end": borders.Right = visible; break;
                case "insideH": borders.InsideH = visible; break;
                case "insideV": borders.InsideV = visible; break;
            }
        }
    }

    // ---- media (lazy: only parts that page 1 actually draws are opened) -----------------------------

    public SKImage? LoadImage(string relationshipId)
    {
        var bar = relationshipId.IndexOf('|');
        if (bar > 0)
        {
            // "<part>|<id>": a picture referenced from a header or footer part
            if (!Relationships(relationshipId[..bar]).TryGetValue(relationshipId[(bar + 1)..], out var partTarget)) return null;
            var name = partTarget.StartsWith('/') ? partTarget.TrimStart('/') : "word/" + partTarget;
            if (_flatBinary != null) return _flatBinary.TryGetValue(name, out var flat) ? Guard.Image(flat) : null;
            var partEntry = _zip!.GetEntry(name);
            return partEntry == null ? null : Guard.Image(Guard.ReadAll(partEntry));
        }
        if (_relationships == null)
        {
            _relationships = new Dictionary<string, string>(StringComparer.Ordinal);
            if (ReadText("word/_rels/document.xml.rels") is { } xml)
                foreach (var rel in Guard.ParseXml(xml).Root!.Elements(Rel + "Relationship"))
                    if ((string?)rel.Attribute("Id") is { } id && (string?)rel.Attribute("Target") is { } target)
                        _relationships[id] = target;
        }
        if (!_relationships.TryGetValue(relationshipId, out var path)) return null;
        var partName = path.StartsWith('/') ? path.TrimStart('/') : "word/" + path;
        if (_flatBinary != null) return _flatBinary.TryGetValue(partName, out var bytes) ? Guard.Image(bytes) : null;
        var entry = _zip!.GetEntry(partName);
        return entry == null ? null : Guard.Image(Guard.ReadAll(entry));
    }
}
