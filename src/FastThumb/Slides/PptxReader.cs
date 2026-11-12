using System.IO.Compression;
using System.Xml.Linq;
using FastThumb.Layout;
using SkiaSharp;

namespace FastThumb.Slides;

/// <summary>
/// PresentationML package reader (PPTX / PPSX / POTX / PPTM / PPSM / POTM share one structure).
/// Follows the relationship graph slide → layout → master → theme and resolves, for every shape, geometry,
/// fill, outline, picture and text with placeholder / list-style / theme inheritance.
/// Not handled: charts, SmartArt, OLE, effects, rotation, gradients (first stop is used), picture cropping.
/// </summary>
internal static class PptxReader
{
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const float Emu = 12700f;

    private sealed class Package(ZipArchive zip)
    {
        private readonly Dictionary<string, XElement?> _xml = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<string, string>> _rels = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, byte[]?> _bytes = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>A part's bytes, read once: every shape that references the same picture shares the array.</summary>
        public byte[]? Bytes(string path)
        {
            if (_bytes.TryGetValue(path, out var hit)) return hit;
            var entry = zip.GetEntry(path);
            return _bytes[path] = entry == null ? null : Guard.ReadAll(entry);
        }

        public XElement? Xml(string path)
        {
            if (_xml.TryGetValue(path, out var hit)) return hit;
            var entry = zip.GetEntry(path);
            if (entry == null) return _xml[path] = null;
            using var stream = Guard.Open(entry);
            return _xml[path] = Guard.LoadXml(stream).Root;
        }

        /// <summary>Relationship id → absolute part path, for the part at <paramref name="path"/>.</summary>
        public Dictionary<string, string> Rels(string path)
        {
            if (_rels.TryGetValue(path, out var hit)) return hit;
            var folder = path[..(path.LastIndexOf('/') + 1)];
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (Xml(folder + "_rels/" + path[folder.Length..] + ".rels") is { } root)
                foreach (var rel in root.Elements(Rel + "Relationship"))
                    if ((string?)rel.Attribute("Id") is { } id && (string?)rel.Attribute("Target") is { } target && (string?)rel.Attribute("TargetMode") != "External")
                        result[id] = Resolve(folder, target);
            return _rels[path] = result;
        }

        public string? Related(string path, string typeSuffix)
        {
            var folder = path[..(path.LastIndexOf('/') + 1)];
            return Xml(folder + "_rels/" + path[folder.Length..] + ".rels")?.Elements(Rel + "Relationship")
                .Where(r => ((string?)r.Attribute("Type") ?? "").EndsWith(typeSuffix, StringComparison.Ordinal))
                .Select(r => Resolve(folder, (string)r.Attribute("Target")!)).FirstOrDefault();
        }

        private static string Resolve(string folder, string target)
        {
            if (target.StartsWith('/')) return target.TrimStart('/');
            var parts = new List<string>(folder.Split('/', StringSplitOptions.RemoveEmptyEntries));
            foreach (var piece in target.Split('/'))
            {
                if (piece == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
                else if (piece != ".") parts.Add(piece);
            }
            return string.Join('/', parts);
        }
    }

    private sealed class Theme
    {
        public readonly Dictionary<string, SKColor> Colors = new(StringComparer.Ordinal);
        public string Major = "Calibri Light", Minor = "Calibri";
    }

    private sealed class Part
    {
        public string Path = "";
        public XElement Root = null!;
        public Part? Parent;                                   // slide → layout → master
        public Theme Theme = new();
        public Dictionary<string, string> ColorMap = new(StringComparer.Ordinal);
    }

    private sealed class Context
    {
        public Package Package = null!;
        public XElement? DefaultTextStyle;
        public Dictionary<string, Part> Parts = new(StringComparer.OrdinalIgnoreCase);
    }

    public static SlideDeck Read(byte[] data)
    {
        using var zip = new ZipArchive(new MemoryStream(data, false), ZipArchiveMode.Read);
        var context = new Context { Package = new Package(zip) };
        const string main = "ppt/presentation.xml";
        var presentation = context.Package.Xml(main) ?? throw new InvalidDataException("ppt/presentation.xml not found");
        context.DefaultTextStyle = presentation.Element(P + "defaultTextStyle");

        var deck = new SlideDeck();
        if (presentation.Element(P + "sldSz") is { } size)
        {
            deck.Width = ((float?)size.Attribute("cx") ?? 9144000) / Emu;
            deck.Height = ((float?)size.Attribute("cy") ?? 6858000) / Emu;
        }
        var rels = context.Package.Rels(main);
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // one slide part listed again: drawn once
        foreach (var id in presentation.Element(P + "sldIdLst")?.Elements(P + "sldId") ?? [])
        {
            if (deck.Slides.Count >= SlideDeck.MaxSlides) break;
            if ((string?)id.Attribute(R + "id") is not { } rid || !rels.TryGetValue(rid, out var path) || !listed.Add(path)) continue;
            var slide = LoadPart(context, path, "/slideLayout");
            if (slide != null) deck.Slides.Add(BuildSlide(context, slide));
        }
        return deck;
    }

    private static Part? LoadPart(Context context, string path, string? parentType)
    {
        if (context.Parts.TryGetValue(path, out var hit)) return hit;
        var root = context.Package.Xml(path);
        if (root == null) return null;
        var part = new Part { Path = path, Root = root };
        context.Parts[path] = part;

        if (parentType != null && context.Package.Related(path, parentType) is { } parent)
            part.Parent = LoadPart(context, parent, parentType == "/slideLayout" ? "/slideMaster" : null);

        if (part.Parent != null) { part.Theme = part.Parent.Theme; part.ColorMap = part.Parent.ColorMap; }
        else
        {
            // a master: its theme and colour map apply to everything that inherits from it
            if (context.Package.Related(path, "/theme") is { } themePath && context.Package.Xml(themePath) is { } theme)
            {
                foreach (var entry in theme.Descendants(A + "clrScheme").FirstOrDefault()?.Elements() ?? [])
                {
                    var value = (string?)entry.Element(A + "srgbClr")?.Attribute("val") ?? (string?)entry.Element(A + "sysClr")?.Attribute("lastClr");
                    if (FormatDetector.Color("#" + value) is { } color) part.Theme.Colors[entry.Name.LocalName] = color;
                }
                var fonts = theme.Descendants(A + "fontScheme").FirstOrDefault();
                part.Theme.Major = (string?)fonts?.Element(A + "majorFont")?.Element(A + "latin")?.Attribute("typeface") is { Length: > 0 } major ? major : part.Theme.Major;
                part.Theme.Minor = (string?)fonts?.Element(A + "minorFont")?.Element(A + "latin")?.Attribute("typeface") is { Length: > 0 } minor ? minor : part.Theme.Minor;
            }
            foreach (var attribute in root.Element(P + "clrMap")?.Attributes() ?? []) part.ColorMap[attribute.Name.LocalName] = attribute.Value;
        }
        return part;
    }

    // ---- colours, fills, lines ------------------------------------------------------------------

    private static SKColor? Color(Part part, XElement? container, SKColor? placeholderColor = null)
    {
        if (container == null) return null;
        foreach (var e in container.Elements())
        {
            SKColor? color = e.Name.LocalName switch
            {
                "srgbClr" => FormatDetector.Color("#" + (string?)e.Attribute("val")),
                "sysClr" => FormatDetector.Color("#" + (string?)e.Attribute("lastClr")),
                "prstClr" => FormatDetector.Color((string?)e.Attribute("val")) ?? SKColors.Black,
                "schemeClr" => SchemeColor(part, (string?)e.Attribute("val") ?? "", placeholderColor),
                _ => null,
            };
            if (color == null) continue;
            float Mod(string name, float fallback) => e.Element(A + name) is { } m ? ((float?)m.Attribute("val") ?? 100000) / 100000f : fallback;
            return SlideRenderer.Adjust(color.Value, Mod("lumMod", 1), e.Element(A + "lumOff") != null ? Mod("lumOff", 0) : 0, Mod("tint", -1), Mod("shade", -1), Mod("alpha", 1));
        }
        return null;
    }

    private static SKColor? SchemeColor(Part part, string name, SKColor? placeholderColor)
    {
        if (name == "phClr") return placeholderColor;
        var mapped = part.ColorMap.TryGetValue(name, out var target) ? target : name switch { "tx1" => "dk1", "bg1" => "lt1", "tx2" => "dk2", "bg2" => "lt2", _ => name };
        return part.Theme.Colors.TryGetValue(mapped, out var color) ? color : null;
    }

    /// <summary>Fill of a shape-properties element: colour, or picture bytes for a blip fill. Returns false for "no fill declared".</summary>
    private static bool Fill(Context context, Part part, XElement? properties, out SKColor? color, out byte[]? image)
    {
        color = null; image = null;
        if (properties == null) return false;
        if (properties.Element(A + "noFill") != null) return true;
        if (properties.Element(A + "solidFill") is { } solid) { color = Color(part, solid); return true; }
        if (properties.Element(A + "gradFill") is { } gradient) { color = Color(part, gradient.Descendants(A + "gs").FirstOrDefault()); return true; }
        if (properties.Element(A + "pattFill") is { } pattern) { color = Color(part, pattern.Element(A + "fgClr")); return true; }
        if (properties.Element(A + "blipFill") is { } blip) { image = Picture(context, part, blip); return true; }
        return false;
    }

    private static byte[]? Picture(Context context, Part part, XElement? blipFill)
    {
        var id = (string?)blipFill?.Descendants(A + "blip").FirstOrDefault()?.Attribute(R + "embed");
        return id != null && context.Package.Rels(part.Path).TryGetValue(id, out var path) ? context.Package.Bytes(path) : null;
    }

    // ---- slide ----------------------------------------------------------------------------------

    private static SlideData BuildSlide(Context context, Part slide)
    {
        var data = new SlideData();
        for (var part = slide; part != null; part = part.Parent)
        {
            var bg = part.Root.Element(P + "cSld")?.Element(P + "bg");
            if (bg == null) continue;
            if (Fill(context, part, bg.Element(P + "bgPr"), out var color, out var image)) { data.Background = color ?? data.Background; data.BackgroundImage = image; }
            else if (Color(part, bg.Element(P + "bgRef")) is { } referenced) data.Background = referenced;
            break;
        }

        // inherited decoration: non-placeholder shapes of the master, then of the layout
        var layout = slide.Parent;
        var master = layout?.Parent;
        var showMaster = (string?)slide.Root.Attribute("showMasterSp") != "0" && (string?)layout?.Root.Attribute("showMasterSp") != "0";
        if (master != null && showMaster) AddShapes(context, master, master.Root.Element(P + "cSld")?.Element(P + "spTree"), data, Transform.Identity, inherited: true);
        if (layout != null) AddShapes(context, layout, layout.Root.Element(P + "cSld")?.Element(P + "spTree"), data, Transform.Identity, inherited: true);
        AddShapes(context, slide, slide.Root.Element(P + "cSld")?.Element(P + "spTree"), data, Transform.Identity, inherited: false);
        return data;
    }

    private readonly record struct Transform(float ScaleX, float ScaleY, float OffsetX, float OffsetY)
    {
        public static readonly Transform Identity = new(1, 1, 0, 0);
        public SKRect Apply(float x, float y, float width, float height) => SKRect.Create(x * ScaleX + OffsetX, y * ScaleY + OffsetY, width * ScaleX, height * ScaleY);
    }

    private static (float X, float Y, float W, float H)? Xfrm(XElement? xfrm)
    {
        if (xfrm?.Element(A + "off") is not { } off || xfrm.Element(A + "ext") is not { } ext) return null;
        return (((float?)off.Attribute("x") ?? 0) / Emu, ((float?)off.Attribute("y") ?? 0) / Emu, ((float?)ext.Attribute("cx") ?? 0) / Emu, ((float?)ext.Attribute("cy") ?? 0) / Emu);
    }

    private static void AddShapes(Context context, Part part, XElement? tree, SlideData data, Transform transform, bool inherited)
    {
        Guard.Deeper();
        foreach (var element in tree?.Elements() ?? [])
        {
            switch (element.Name.LocalName)
            {
                case "sp" or "cxnSp":
                    var placeholder = element.Descendants(P + "ph").FirstOrDefault();
                    if (inherited && placeholder != null) break; // layout/master placeholders are templates, not content
                    AddShape(context, part, element, placeholder, data, transform);
                    break;
                case "pic":
                    if (Xfrm(element.Element(P + "spPr")?.Element(A + "xfrm")) is { } box && Picture(context, part, element.Element(P + "blipFill")) is { } bytes)
                        data.Shapes.Add(new SlideShape { Bounds = transform.Apply(box.X, box.Y, box.W, box.H), Image = bytes });
                    break;
                case "grpSp":
                    var group = element.Element(P + "grpSpPr")?.Element(A + "xfrm");
                    var inner = transform;
                    if (Xfrm(group) is { } outer && group!.Element(A + "chOff") is { } chOff && group.Element(A + "chExt") is { } chExt)
                    {
                        float cx = ((float?)chExt.Attribute("cx") ?? 0) / Emu, cy = ((float?)chExt.Attribute("cy") ?? 0) / Emu;
                        float ox = ((float?)chOff.Attribute("x") ?? 0) / Emu, oy = ((float?)chOff.Attribute("y") ?? 0) / Emu;
                        float sx = cx > 0 ? outer.W / cx : 1, sy = cy > 0 ? outer.H / cy : 1;
                        inner = new Transform(transform.ScaleX * sx, transform.ScaleY * sy,
                            transform.OffsetX + (outer.X - ox * sx) * transform.ScaleX, transform.OffsetY + (outer.Y - oy * sy) * transform.ScaleY);
                    }
                    AddShapes(context, part, element, data, inner, inherited);
                    break;
                case "graphicFrame":
                    if (Xfrm(element.Element(P + "xfrm")) is { } frame && element.Descendants(A + "tbl").FirstOrDefault() is { } tbl)
                        data.Shapes.Add(new SlideShape
                        {
                            Bounds = transform.Apply(frame.X, frame.Y, frame.W, frame.H),
                            Text = [BuildTable(context, part, tbl)],
                            InsetLeft = 0,
                            InsetTop = 0,
                            InsetRight = 0,
                            InsetBottom = 0,
                        });
                    break;
            }
        }
    }

    /// <summary>The same placeholder one level up (layout, then master): matched by idx, else by type family.</summary>
    private static XElement? FindPlaceholder(Part? part, string? type, string? index)
    {
        var shapes = part?.Root.Element(P + "cSld")?.Element(P + "spTree")?.Elements(P + "sp").ToList();
        if (shapes == null) return null;
        static string Family(string? t) => t switch { "ctrTitle" or "title" => "title", null or "body" or "subTitle" or "obj" => "body", _ => t };
        XElement? byType = null;
        foreach (var shape in shapes)
        {
            var ph = shape.Descendants(P + "ph").FirstOrDefault();
            if (ph == null) continue;
            if (index != null && (string?)ph.Attribute("idx") == index) return shape;
            if (byType == null && Family((string?)ph.Attribute("type")) == Family(type) && (part!.Parent == null || index == null)) byType = shape;
        }
        return byType;
    }

    private static void AddShape(Context context, Part part, XElement element, XElement? placeholder, SlideData data, Transform transform)
    {
        var type = (string?)placeholder?.Attribute("type");
        var index = (string?)placeholder?.Attribute("idx");
        // inheritance chain for a placeholder: this shape, the layout's, the master's
        var chain = new List<(Part Part, XElement Shape)> { (part, element) };
        if (placeholder != null)
        {
            if (FindPlaceholder(part.Parent, type, index) is { } inLayout) chain.Add((part.Parent!, inLayout));
            var master = part.Parent?.Parent ?? (part.Parent is { Parent: null } ? part.Parent : null);
            if (master != null && master != part && FindPlaceholder(master, type ?? (string?)chain[^1].Shape.Descendants(P + "ph").FirstOrDefault()?.Attribute("type"), null) is { } inMaster) chain.Add((master, inMaster));
        }

        (float X, float Y, float W, float H)? box = null;
        foreach (var (_, shape) in chain) if ((box = Xfrm(shape.Element(P + "spPr")?.Element(A + "xfrm"))) != null) break;
        if (box == null) return;

        var properties = element.Element(P + "spPr");
        var style = element.Element(P + "style");
        var result = new SlideShape { Bounds = transform.Apply(box.Value.X, box.Value.Y, box.Value.W, box.Value.H) };
        var geometry = (string?)properties?.Element(A + "prstGeom")?.Attribute("prst");
        result.Kind = element.Name.LocalName == "cxnSp" || geometry is "line" or "straightConnector1" ? ShapeKind.Line
            : geometry is "ellipse" ? ShapeKind.Ellipse : geometry is "roundRect" or "round2SameRect" or "flowChartAlternateProcess" ? ShapeKind.RoundRect : ShapeKind.Rect;

        if (!Fill(context, part, properties, out result.Fill, out result.Image) && style?.Element(A + "fillRef") is { } fillRef && (string?)fillRef.Attribute("idx") != "0")
            result.Fill = Color(part, fillRef);
        var line = properties?.Element(A + "ln");
        if (line?.Element(A + "noFill") == null)
        {
            if (line?.Element(A + "solidFill") is { } stroke) result.Stroke = Color(part, stroke);
            else if (style?.Element(A + "lnRef") is { } lnRef && (string?)lnRef.Attribute("idx") != "0") result.Stroke = Color(part, lnRef);
            if ((float?)line?.Attribute("w") is { } width) result.StrokeWidth = Math.Max(0.25f, width / Emu);
        }
        if (result.Kind == ShapeKind.Line) result.Stroke ??= SKColors.Black;

        if (element.Element(P + "txBody") is { } body)
        {
            // body properties (anchor, insets, autofit) and list styles also inherit along the placeholder chain
            XElement? Property(string name) => chain.Select(c => c.Shape.Element(P + "txBody")?.Element(A + "bodyPr")).FirstOrDefault(b => b?.Attribute(name) != null);
            result.Anchor = (string?)Property("anchor")?.Attribute("anchor") switch { "ctr" => TextAnchor.Middle, "b" => TextAnchor.Bottom, _ => TextAnchor.Top };
            float Inset(string name, float fallback) => (float?)Property(name)?.Attribute(name) is { } emu ? emu / Emu : fallback;
            result.InsetLeft = Inset("lIns", 7.2f); result.InsetRight = Inset("rIns", 7.2f); result.InsetTop = Inset("tIns", 3.6f); result.InsetBottom = Inset("bIns", 3.6f);

            var master = chain[^1].Part.Parent == null ? chain[^1].Part : part.Parent?.Parent ?? part.Parent ?? part;
            var category = placeholder == null ? "otherStyle" : type is "title" or "ctrTitle" ? "titleStyle" : type is null or "body" or "subTitle" or "obj" ? "bodyStyle" : "otherStyle";
            var sources = new List<XElement?> { context.DefaultTextStyle, master.Root.Element(P + "txStyles")?.Element(P + category) };
            for (var i = chain.Count - 1; i >= 0; i--) sources.Add(chain[i].Shape.Element(P + "txBody")?.Element(A + "lstStyle"));
            var fontColor = Color(part, style?.Element(A + "fontRef"));
            var scale = (float?)body.Element(A + "bodyPr")?.Element(A + "normAutofit")?.Attribute("fontScale") is { } fontScale ? fontScale / 100000f : 1f;
            result.Text = BuildText(part, body, sources, category == "titleStyle", fontColor, scale);
        }
        if (result.Fill != null || result.Stroke != null || result.Image != null || result.Text.Count > 0) data.Shapes.Add(result);
    }

    // ---- text -----------------------------------------------------------------------------------

    private static List<Block> BuildText(Part part, XElement body, List<XElement?> sources, bool title, SKColor? fontColor, float scale, bool forceBold = false)
    {
        var blocks = new List<Block>();
        var number = 0;
        foreach (var p in body.Elements(A + "p"))
        {
            var pPr = p.Element(A + "pPr");
            var level = Math.Clamp((int?)pPr?.Attribute("lvl") ?? 0, 0, 8);
            var para = new ParaProps();
            var run = new RunProps { Size = 18, Font = title ? part.Theme.Major : part.Theme.Minor, Color = SchemeColor(part, "tx1", null) ?? SKColors.Black };
            string? bullet = null;
            // inherited styles first, then the shape style's font colour, then the shape's own list style and the paragraph
            for (var i = 0; i < sources.Count - 1; i++) ApplyLevel(part, sources[i]?.Element(A + $"lvl{level + 1}pPr"), para, run, ref bullet);
            if (fontColor != null) run.Color = fontColor.Value;
            if (forceBold) run.Bold = true;
            if (sources.Count > 0) ApplyLevel(part, sources[^1]?.Element(A + $"lvl{level + 1}pPr"), para, run, ref bullet);
            ApplyLevel(part, pPr, para, run, ref bullet);
            if (bullet == "#") bullet = $"{++number}."; else if (bullet == null) number = 0;

            var paragraph = new Paragraph { Props = para, MarkProps = Scaled(run, scale) };
            foreach (var e in p.Elements())
            {
                if (e.Name == A + "r" || e.Name == A + "fld")
                {
                    var props = run.Clone();
                    ApplyRun(part, e.Element(A + "rPr"), props);
                    var text = (string?)e.Element(A + "t") ?? "";
                    var pieces = text.Split('\v', '\r', '\n'); // a vertical tab inside a run is a line break
                    for (var k = 0; k < pieces.Length; k++)
                    {
                        if (k > 0) paragraph.Inlines.Add(new BreakInline { Props = Scaled(props, scale) });
                        if (pieces[k].Length > 0) paragraph.Inlines.Add(new TextInline { Props = Scaled(props, scale), Text = pieces[k] });
                    }
                }
                else if (e.Name == A + "br") paragraph.Inlines.Add(new BreakInline { Props = Scaled(run, scale) });
            }
            if (p.Element(A + "endParaRPr") is { } end && paragraph.Inlines.Count == 0)
            {
                var mark = run.Clone();
                ApplyRun(part, end, mark);
                paragraph.MarkProps = Scaled(mark, scale);
            }
            if (bullet != null && paragraph.Inlines.Count > 0) { para.Bullet = bullet; para.BulletProps = paragraph.Inlines[0].Props; }
            blocks.Add(paragraph);
        }
        return blocks;
    }

    private static RunProps Scaled(RunProps props, float scale)
    {
        if (scale == 1) return props;
        var copy = props.Clone();
        copy.Size *= scale;
        return copy;
    }

    private static void ApplyLevel(Part part, XElement? pPr, ParaProps para, RunProps run, ref string? bullet)
    {
        if (pPr == null) return;
        para.Align = (string?)pPr.Attribute("algn") switch { "ctr" => Align.Center, "r" => Align.Right, "just" or "dist" => Align.Justify, "l" => Align.Left, _ => para.Align };
        if ((float?)pPr.Attribute("marL") is { } left) para.IndentLeft = left / Emu;
        if ((float?)pPr.Attribute("indent") is { } indent) para.FirstLine = indent / Emu;
        ApplyRun(part, pPr.Element(A + "defRPr"), run);

        float? Spacing(string name)
        {
            var e = pPr.Element(A + name);
            if ((float?)e?.Element(A + "spcPts")?.Attribute("val") is { } points) return points / 100f;
            if ((float?)e?.Element(A + "spcPct")?.Attribute("val") is { } percent) return run.Size * 1.2f * percent / 100000f;
            return null;
        }
        para.SpaceBefore = Spacing("spcBef") ?? para.SpaceBefore;
        para.SpaceAfter = Spacing("spcAft") ?? para.SpaceAfter;
        if (pPr.Element(A + "lnSpc") is { } lineSpacing)
        {
            if ((float?)lineSpacing.Element(A + "spcPct")?.Attribute("val") is { } percent) { para.Line = 240f * percent / 100000f; para.LineRule = "auto"; }
            else if ((float?)lineSpacing.Element(A + "spcPts")?.Attribute("val") is { } points) { para.Line = points / 100f * 20; para.LineRule = "exact"; }
        }

        if (pPr.Element(A + "buNone") != null) bullet = null;
        else if (pPr.Element(A + "buAutoNum") != null) bullet = "#";
        else if ((string?)pPr.Element(A + "buChar")?.Attribute("char") is { Length: > 0 } ch)
            bullet = ch is "–" or "-" or "•" or "o" or "»" or "▪" or "■" ? ch : "•"; // symbol-font bullet codes are not mapped glyph by glyph
    }

    private static void ApplyRun(Part part, XElement? rPr, RunProps run)
    {
        if (rPr == null) return;
        if ((float?)rPr.Attribute("sz") is { } size) run.Size = size / 100f;
        if ((string?)rPr.Attribute("b") is { } bold) run.Bold = bold is "1" or "true";
        if ((string?)rPr.Attribute("i") is { } italic) run.Italic = italic is "1" or "true";
        if ((string?)rPr.Attribute("u") is { } underline) run.Underline = underline != "none";
        if (Color(part, rPr.Element(A + "solidFill")) is { } color) run.Color = color;
        if ((string?)rPr.Element(A + "latin")?.Attribute("typeface") is { Length: > 0 } face)
            run.Font = face.StartsWith("+mj", StringComparison.Ordinal) ? part.Theme.Major : face.StartsWith("+mn", StringComparison.Ordinal) ? part.Theme.Minor : face;
    }

    private static Table BuildTable(Context context, Part part, XElement tbl)
    {
        var table = new Table { Borders = new Borders { Top = true, Left = true, Bottom = true, Right = true, InsideH = true, InsideV = true } };
        table.Grid = tbl.Element(A + "tblGrid")?.Elements(A + "gridCol").Select(c => ((float?)c.Attribute("w") ?? 0) / Emu).ToArray() ?? [];
        var sources = new List<XElement?> { context.DefaultTextStyle, null };
        // a table that references a table style gets the look of the default "medium style, accent 1":
        // accent header with bold white text, alternating light bands, no visible grid lines
        var properties = tbl.Element(A + "tblPr");
        var styled = properties?.Element(A + "tableStyleId") != null;
        var accent = SchemeColor(part, "accent1", null) ?? new SKColor(0xFF4472C4);
        bool Flag(string name) => (string?)properties?.Attribute(name) is "1" or "true";
        if (styled) table.Borders = default;
        var rowIndex = 0;
        foreach (var tr in tbl.Elements(A + "tr"))
        {
            var header = styled && Flag("firstRow") && rowIndex == 0;
            var band = styled && Flag("bandRow") && !header ? ((rowIndex - (Flag("firstRow") ? 1 : 0)) % 2 == 0 ? 0.25f : 0.125f) : 0f;
            rowIndex++;
            var row = new Row { MinHeight = ((float?)tr.Attribute("h") ?? 0) / Emu };
            foreach (var tc in tr.Elements(A + "tc"))
            {
                if ((string?)tc.Attribute("hMerge") is "1" or "true") continue; // covered by the previous cell's span
                var cell = new Cell { Span = Math.Clamp((int?)tc.Attribute("gridSpan") ?? 1, 1, Cell.MaxSpan) };
                if (tc.Element(A + "txBody") is { } body) cell.Blocks = BuildText(part, body, sources, false, header ? SKColors.White : null, 1, forceBold: header);
                if (Color(part, tc.Element(A + "tcPr")?.Element(A + "solidFill")) is { } explicitFill) cell.Fill = explicitFill;
                else if (header) cell.Fill = accent;
                else if (band > 0) cell.Fill = SlideRenderer.Adjust(accent, alpha: band);
                row.Cells.Add(cell);
            }
            if (row.Cells.Count > 0) table.Rows.Add(row);
        }
        if (table.Grid.Length == 0 && table.Rows.Count > 0) table.Grid = new float[Math.Min(table.Rows.Max(r => r.Cells.Sum(c => c.Span)), Cell.MaxSpan)];
        return table;
    }
}
