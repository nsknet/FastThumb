using System.IO.Compression;
using System.Xml.Linq;
using FastThumb.Layout;
using FastThumb.Words;
using SkiaSharp;

namespace FastThumb.Slides;

/// <summary>
/// OpenDocument Presentation: ODP / OTP (ZIP with content.xml + styles.xml) and FODP (one flat XML file).
/// Text and its styles are read with the same code as ODT; this class adds pages, frames, shapes and pictures.
/// Master-page decoration is not drawn.
/// </summary>
internal static class OdpReader
{
    private static readonly XNamespace Office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private static readonly XNamespace Draw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
    private static readonly XNamespace Text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private static readonly XNamespace Table = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    private static readonly XNamespace Style = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
    private static readonly XNamespace Fo = "urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0";
    private static readonly XNamespace Svg = "urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0";
    private static readonly XNamespace Presentation = "urn:oasis:names:tc:opendocument:xmlns:presentation:1.0";
    private static readonly XNamespace Xlink = "http://www.w3.org/1999/xlink";

    public static SlideDeck Read(byte[] data, bool flat)
    {
        XElement? content, styles;
        Func<string, byte[]?> media;
        ZipArchive? zip = null;
        if (flat)
        {
            content = styles = Guard.ParseXml(FormatDetector.Decode(data, null)).Root;
            media = _ => null;
        }
        else
        {
            zip = new ZipArchive(new MemoryStream(data, false), ZipArchiveMode.Read);
            XElement? Load(string name)
            {
                var entry = zip.GetEntry(name);
                if (entry == null) return null;
                using var stream = Guard.Open(entry);
                return Guard.LoadXml(stream).Root;
            }
            content = Load("content.xml");
            styles = Load("styles.xml");
            var read = new Dictionary<string, byte[]?>(StringComparer.Ordinal);   // a picture used many times is read once, its array shared
            media = name =>
            {
                if (read.TryGetValue(name, out var hit)) return hit;
                var entry = zip.GetEntry(name);
                return read[name] = entry == null ? null : Guard.ReadAll(entry);
            };
        }
        using var archive = zip;
        if (content == null) throw new InvalidDataException("ODP content not found");

        var context = OdtReader.CreateContext(styles, content);
        var deck = new SlideDeck { Width = 793.7f, Height = 595.3f };
        if (styles?.Descendants(Style + "page-layout-properties").FirstOrDefault(p => p.Attribute(Fo + "page-width") != null) is { } layout)
        {
            deck.Width = FormatDetector.Length((string?)layout.Attribute(Fo + "page-width")) ?? deck.Width;
            deck.Height = FormatDetector.Length((string?)layout.Attribute(Fo + "page-height")) ?? deck.Height;
        }

        foreach (var page in content.Descendants(Draw + "page"))
        {
            if (deck.Slides.Count >= SlideDeck.MaxSlides) break;
            var slide = new SlideData();
            deck.Slides.Add(slide);
            if (Graphic(context, (string?)page.Attribute(Draw + "style-name"), "fill", Style + "drawing-page-properties") == "solid" &&
                FormatDetector.Color(Graphic(context, (string?)page.Attribute(Draw + "style-name"), "fill-color", Style + "drawing-page-properties")) is { } background)
                slide.Background = background;
            AddShapes(context, page, slide, media);
        }
        return deck;
    }

    /// <summary>A draw: attribute of a graphic style, searched up the parent-style chain.</summary>
    private static string? Graphic(OdtReader.Context context, string? styleName, string attribute, XName? properties = null, XNamespace? ns = null)
    {
        for (var guard = 0; styleName != null && guard < 20 && context.Styles.TryGetValue(styleName, out var style); guard++)
        {
            if ((string?)style.Element(properties ?? Style + "graphic-properties")?.Attribute((ns ?? Draw) + attribute) is { } value) return value;
            styleName = (string?)style.Attribute(Style + "parent-style-name");
        }
        return null;
    }

    private static SKRect? Box(XElement e)
    {
        float? L(string name) => FormatDetector.Length((string?)e.Attribute(Svg + name));
        if (L("x") is { } x && L("y") is { } y && L("width") is { } w && L("height") is { } h) return SKRect.Create(x, y, w, h);
        return null;
    }

    private static void AddShapes(OdtReader.Context context, XElement container, SlideData slide, Func<string, byte[]?> media)
    {
        Guard.Deeper();
        foreach (var e in container.Elements())
        {
            var name = e.Name.LocalName;
            if (e.Name.Namespace != Draw) continue;
            if (name == "g") { AddShapes(context, e, slide, media); continue; }
            var styleName = (string?)e.Attribute(Presentation + "style-name") ?? (string?)e.Attribute(Draw + "style-name");

            if (name is "line" or "connector")
            {
                float L(string n) => FormatDetector.Length((string?)e.Attribute(Svg + n)) ?? 0;
                slide.Shapes.Add(new SlideShape
                {
                    Kind = ShapeKind.Line,
                    Bounds = new SKRect(L("x1"), L("y1"), L("x2"), L("y2")),
                    Stroke = FormatDetector.Color(Graphic(context, styleName, "stroke-color", ns: Svg)) ?? SKColors.Black,
                    StrokeWidth = Math.Max(0.5f, FormatDetector.Length(Graphic(context, styleName, "stroke-width", ns: Svg)) ?? 0.75f),
                });
                continue;
            }
            if (name is not ("frame" or "custom-shape" or "rect" or "ellipse") || Box(e) is not { } bounds) continue;

            var shape = new SlideShape { Bounds = bounds };
            if (name == "frame")
            {
                if (e.Element(Draw + "image") is { } image)
                {
                    shape.Image = (string?)image.Attribute(Xlink + "href") is { } href ? media(href.TrimStart('.', '/')) : null;
                    if (shape.Image == null && image.Element(Office + "binary-data") is { } binary) shape.Image = Convert.FromBase64String(binary.Value);
                    if (shape.Image != null) slide.Shapes.Add(shape);
                    continue;
                }
                if (e.Element(Table + "table") != null)
                {
                    OdtReader.ReadBlocks(context, e, shape.Text, null);
                    shape.InsetLeft = shape.InsetRight = shape.InsetTop = shape.InsetBottom = 0;
                    if (shape.Text.Count > 0) slide.Shapes.Add(shape);
                    continue;
                }
            }
            else
            {
                var geometry = (string?)e.Element(Draw + "enhanced-geometry")?.Attribute(Draw + "type");
                shape.Kind = name == "ellipse" || geometry == "ellipse" ? ShapeKind.Ellipse : geometry is "round-rectangle" ? ShapeKind.RoundRect : ShapeKind.Rect;
            }

            // fill and outline: frames holding text default to none, drawn shapes to the style's values
            var fill = Graphic(context, styleName, "fill");
            if (fill == "solid" || (fill == null && name != "frame" && Graphic(context, styleName, "fill-color") != null))
                shape.Fill = FormatDetector.Color(Graphic(context, styleName, "fill-color"));
            var stroke = Graphic(context, styleName, "stroke");
            if (stroke is "solid" or "dash" || (stroke == null && name != "frame"))
            {
                shape.Stroke = FormatDetector.Color(Graphic(context, styleName, "stroke-color", ns: Svg)) ?? (name == "frame" ? null : SKColors.Black);
                shape.StrokeWidth = Math.Max(0.5f, FormatDetector.Length(Graphic(context, styleName, "stroke-width", ns: Svg)) ?? 0.75f);
            }
            shape.Anchor = Graphic(context, styleName, "textarea-vertical-align") switch { "middle" => TextAnchor.Middle, "bottom" => TextAnchor.Bottom, _ => TextAnchor.Top };
            float Padding(string side, float fallback) => FormatDetector.Length(Graphic(context, styleName, "padding-" + side, ns: Fo)) ?? fallback;
            shape.InsetLeft = Padding("left", 7.1f); shape.InsetRight = Padding("right", 7.1f); shape.InsetTop = Padding("top", 3.5f); shape.InsetBottom = Padding("bottom", 3.5f);

            // text defaults come from the shape's own (presentation / graphic) style, then each paragraph refines them
            var scoped = context.Scoped();
            OdtReader.Resolve(context, styleName, scoped.DefaultPara, scoped.DefaultRun);
            OdtReader.Resolve(context, (string?)e.Attribute(Draw + "text-style-name"), scoped.DefaultPara, scoped.DefaultRun);
            OdtReader.ReadBlocks(scoped, e.Element(Draw + "text-box") ?? e, shape.Text, null);
            if (shape.Text.OfType<Paragraph>().All(p => p.Inlines.Count == 0) && !shape.Text.OfType<Table>().Any()) shape.Text.Clear();

            if (shape.Fill != null || shape.Stroke != null || shape.Text.Count > 0) slide.Shapes.Add(shape);
        }
    }
}
