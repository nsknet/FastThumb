using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using FastThumb.Scene;
using SkiaSharp;

namespace FastThumb.Fixed;

/// <summary>
/// XPS / OpenXPS: a fixed-layout package. Every page is already a list of positioned glyph runs, paths and
/// images, so there is nothing to lay out: the page markup is replayed straight into a PageScene.
/// Handled: nested canvases with transforms and opacity, glyph runs with embedded (also obfuscated) fonts,
/// explicit glyph indices / advances / offsets, paths (abbreviated syntax and PathGeometry), solid brushes,
/// image brushes, resource dictionaries. Approximated: gradients (first stop), clipping and tiling (ignored).
/// </summary>
internal static class XpsReader
{
    private const float Unit = 72f / 96f;   // XPS units are 1/96 inch
    private const int MaxPages = 500;
    private const int MaxCluster = 64;
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    public static PagedDocument Read(byte[] data, int? maxPages = null)
    {
        using var zip = new ZipArchive(new MemoryStream(data, false), ZipArchiveMode.Read);
        var package = new Package(zip);
        try
        {
            // a page listed again is drawn once: each listing would parse the part again
            foreach (var path in package.PagePaths().Distinct(StringComparer.OrdinalIgnoreCase).Take(Math.Min(MaxPages, maxPages ?? int.MaxValue)))
            {
                Guard.Check();
                if (package.Xml(path) is { } page) package.Document.Pages.Add(new PageReader(package, path).Render(page));
            }
            if (package.Document.Pages.Count == 0) throw new InvalidDataException("XPS package has no pages");
            return package.Document;
        }
        catch { package.Document.Dispose(); throw; }
    }

    /// <summary>The ZIP parts plus the fonts and images already loaded from them (shared by all pages).</summary>
    private sealed class Package(ZipArchive zip)
    {
        private readonly Dictionary<string, ZipArchiveEntry> _entries = zip.Entries
            .GroupBy(e => e.FullName.TrimStart('/'), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, SKTypeface?> _typefaces = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, SKImage?> _images = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<(SKTypeface, float, bool, bool), SKFont> _fonts = [];

        public PagedDocument Document { get; } = new();

        public byte[]? Bytes(string path) => _entries.TryGetValue(path, out var entry) ? Guard.ReadAll(entry) : null;

        public XElement? Xml(string path) => Bytes(path) is { } bytes ? Guard.LoadXml(new MemoryStream(bytes)).Root : null;

        private readonly Dictionary<string, XElement?> _dictionaries = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>A resource dictionary part, parsed once for all the pages that use it.</summary>
        public XElement? Dictionary(string path) => _dictionaries.TryGetValue(path, out var hit) ? hit : _dictionaries[path] = Xml(path);

        /// <summary>Part name of a reference found in the part <paramref name="from"/>; the fragment (font face index) is dropped.</summary>
        public static string Resolve(string from, string reference)
        {
            var hash = reference.IndexOf('#');
            if (hash >= 0) reference = reference[..hash];
            reference = Uri.UnescapeDataString(reference);
            if (reference.StartsWith('/')) return reference.TrimStart('/');
            var parts = new List<string>(from.Split('/')[..^1]);
            foreach (var piece in reference.Split('/'))
            {
                if (piece == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
                else if (piece != ".") parts.Add(piece);
            }
            return string.Join('/', parts);
        }

        /// <summary>Pages in reading order: package relationship → document sequence → documents → pages.</summary>
        public List<string> PagePaths()
        {
            var pages = new List<string>();
            var sequence = Xml("_rels/.rels")?.Elements().FirstOrDefault(r => ((string?)r.Attribute("Type") ?? "").EndsWith("/fixedrepresentation", StringComparison.OrdinalIgnoreCase)) is { } start
                ? Resolve("", (string?)start.Attribute("Target") ?? "") : _entries.Keys.FirstOrDefault(k => k.EndsWith(".fdseq", StringComparison.OrdinalIgnoreCase));
            foreach (var reference in sequence != null ? Xml(sequence)?.Elements().Where(e => e.Name.LocalName == "DocumentReference") ?? [] : [])
            {
                var document = Resolve(sequence!, (string?)reference.Attribute("Source") ?? "");
                foreach (var content in Xml(document)?.Elements().Where(e => e.Name.LocalName == "PageContent") ?? [])
                    pages.Add(Resolve(document, (string?)content.Attribute("Source") ?? ""));
            }
            // a package whose sequence cannot be followed still has its page parts
            if (pages.Count == 0)
                pages.AddRange(_entries.Keys.Where(k => k.EndsWith(".fpage", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(k => k.Length).ThenBy(k => k, StringComparer.OrdinalIgnoreCase));
            return pages;
        }

        public SKTypeface? Typeface(string path, int face)
        {
            var key = path + "#" + face;
            if (_typefaces.TryGetValue(key, out var hit)) return hit;
            SKTypeface? typeface = null;
            if (Bytes(path) is { Length: > 32 } bytes)
            {
                Deobfuscate(bytes, Path.GetFileNameWithoutExtension(path));
                typeface = SKTypeface.FromData(SKData.CreateCopy(bytes), face);
                if (typeface != null) Document.Own(typeface);
            }
            return _typefaces[key] = typeface;
        }

        /// <summary>
        /// Embedded fonts are usually stored obfuscated: the first 32 bytes are XOR-ed with the GUID that is the
        /// part's file name, last GUID byte first. A part that already starts with a font signature is left alone.
        /// </summary>
        private static void Deobfuscate(byte[] font, string name)
        {
            static bool IsFont(byte[] b) => (b[0] == 0 && b[1] == 1 && b[2] == 0 && b[3] == 0) || (b[0] == 'O' && b[1] == 'T' && b[2] == 'T' && b[3] == 'O') ||
                                            (b[0] == 't' && b[1] == 't' && b[2] == 'c' && b[3] == 'f') || (b[0] == 't' && b[1] == 'r' && b[2] == 'u' && b[3] == 'e');
            if (IsFont(font) || !Guid.TryParse(name, out var guid)) return;
            var key = guid.ToByteArray();
            // the GUID as written, i.e. the first three groups big-endian
            (key[0], key[3]) = (key[3], key[0]); (key[1], key[2]) = (key[2], key[1]); (key[4], key[5]) = (key[5], key[4]); (key[6], key[7]) = (key[7], key[6]);
            for (var i = 0; i < 32; i++) font[i] ^= key[15 - i % 16];
        }

        public SKFont Font(SKTypeface typeface, float size, bool bold, bool italic)
        {
            var key = (typeface, MathF.Round(size, 2), bold, italic);
            if (_fonts.TryGetValue(key, out var font)) return font;
            font = Document.Own(new SKFont(typeface, key.Item2) { Hinting = SKFontHinting.None, LinearMetrics = true, Subpixel = true, Embolden = bold, SkewX = italic ? -0.33f : 0 });
            return _fonts[key] = font;
        }

        public SKImage? Image(string path)
        {
            if (_images.TryGetValue(path, out var hit)) return hit;
            SKImage? image = null;
            if (Bytes(path) is { } bytes)
            {
                image = Guard.Image(bytes);
                if (image != null) Document.Own(image);
            }
            return _images[path] = image;
        }
    }

    /// <summary>Replays one FixedPage into a scene.</summary>
    private sealed class PageReader(Package package, string pagePath)
    {
        private readonly Dictionary<string, XElement> _resources = new(StringComparer.Ordinal);
        private PageScene _scene = null!;

        public PageScene Render(XElement page)
        {
            // sizes from the file: clamped like every other page size
            _scene = new PageScene(Layout.PageSetup.Side(Number(page, "Width", 816) * Unit, 816 * Unit), Layout.PageSetup.Side(Number(page, "Height", 1056) * Unit, 1056 * Unit));
            _collected.Add(pagePath);
            Collect(page, pagePath);
            Visit(page, SKMatrix.CreateScale(Unit, Unit), 1);
            return _scene;
        }

        /// <summary>Keyed resources of the page (and of dictionaries stored in their own part); scoping is not modelled.</summary>
        private void Collect(XElement root, string part)
        {
            Guard.Deeper();
            foreach (var element in root.Descendants())
            {
                if ((string?)element.Attribute(Xaml + "Key") is { } key) _resources.TryAdd(key, element);
                if (element.Name.LocalName == "ResourceDictionary" && (string?)element.Attribute("Source") is { } source)
                {
                    // a part is read once per page (a dictionary may name itself or the page): its keys are already in
                    var path = Package.Resolve(part, source);
                    if (_collected.Add(path) && package.Dictionary(path) is { } dictionary) { _remoteBase[dictionary] = path; Collect(dictionary, path); }
                }
            }
        }

        private readonly HashSet<string> _collected = new(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<XElement, string> _remoteBase = [];

        /// <summary>The part an element's relative references are resolved against.</summary>
        private string BaseOf(XElement element) => _remoteBase.TryGetValue(element.AncestorsAndSelf().Last(), out var path) ? path : pagePath;

        private static float Number(XElement element, string name, float fallback)
            => float.TryParse((string?)element.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;

        private static float[] Numbers(string text)
            => text.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries).Select(t => float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0).ToArray();

        /// <summary>A property given as an attribute, a {StaticResource} reference or a child property element.</summary>
        private object? Property(XElement element, string name)
        {
            if ((string?)element.Attribute(name) is { } text)
            {
                if (!text.StartsWith("{StaticResource", StringComparison.Ordinal)) return text;
                return _resources.GetValueOrDefault(text[15..].Trim(' ', '}'));
            }
            return element.Elements().FirstOrDefault(c => c.Name.LocalName == element.Name.LocalName + "." + name)?.Elements().FirstOrDefault();
        }

        private SKMatrix Matrix(XElement element, string name)
        {
            var value = Property(element, name);
            var numbers = Numbers(value as string ?? (string?)(value as XElement)?.Attribute("Matrix") ?? "");
            return numbers.Length == 6 ? new SKMatrix(numbers[0], numbers[2], numbers[4], numbers[1], numbers[3], numbers[5], 0, 0, 1) : SKMatrix.Identity;
        }

        private static float ScaleOf(SKMatrix m) => MathF.Sqrt(MathF.Abs(m.ScaleX * m.ScaleY - m.SkewX * m.SkewY));

        private static SKColor? Color(string? text, float opacity)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = text.Trim();
            SKColor color;
            if (text.StartsWith("sc#", StringComparison.OrdinalIgnoreCase))
            {
                // scRGB: linear components, optional alpha first
                var c = Numbers(text[3..]);
                if (c.Length < 3) return null;
                static byte Gamma(float v) => (byte)Math.Clamp(MathF.Round((v <= 0.0031308f ? 12.92f * v : 1.055f * MathF.Pow(v, 1 / 2.4f) - 0.055f) * 255), 0, 255);
                color = new SKColor(Gamma(c[^3]), Gamma(c[^2]), Gamma(c[^1]), (byte)Math.Clamp(MathF.Round((c.Length > 3 ? c[0] : 1) * 255), 0, 255));
            }
            else if (text.StartsWith('#') && uint.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
                color = text.Length == 7 ? new SKColor(0xFF000000 | value) : new SKColor(value);
            else color = SKColors.Black;   // ContextColor and other colour spaces
            return color.WithAlpha((byte)Math.Clamp(MathF.Round(color.Alpha * opacity), 0, 255));
        }

        /// <summary>A brush as a flat colour, or the ImageBrush element itself. Gradients collapse to their first stop.</summary>
        private (SKColor? Color, XElement? Image) Brush(object? value, float opacity)
        {
            if (value is string text) return (Color(text, opacity), null);
            if (value is not XElement brush) return (null, null);
            opacity *= Number(brush, "Opacity", 1);
            return brush.Name.LocalName switch
            {
                "SolidColorBrush" => (Color((string?)brush.Attribute("Color"), opacity), null),
                "ImageBrush" => (null, brush),
                _ => (Color((string?)brush.Descendants().FirstOrDefault(e => e.Name.LocalName == "GradientStop")?.Attribute("Color"), opacity), null),
            };
        }

        private void Visit(XElement element, SKMatrix matrix, float opacity)
        {
            Guard.Deeper();
            matrix = SKMatrix.Concat(matrix, Matrix(element, "RenderTransform"));
            opacity *= Number(element, "Opacity", 1);
            switch (element.Name.LocalName)
            {
                case "FixedPage" or "Canvas":
                    foreach (var child in element.Elements()) Visit(child, matrix, opacity);
                    break;
                case "Path": DrawPath(element, matrix, opacity); break;
                case "Glyphs": DrawGlyphs(element, matrix, opacity); break;
            }
        }

        private SKPath? Geometry(object? data)
        {
            if (data is string text)
            {
                // abbreviated syntax = SVG path syntax with an optional fill-rule prefix (F0 even-odd, F1 non-zero)
                text = text.TrimStart();
                var nonZero = false;
                if (text.StartsWith('F') && text.Length > 1) { nonZero = text.TrimStart('F', ' ').StartsWith('1'); text = text.TrimStart('F', ' ')[1..]; }
                var parsed = SKPath.ParseSvgPathData(text);
                if (parsed != null) parsed.FillType = nonZero ? SKPathFillType.Winding : SKPathFillType.EvenOdd;
                return parsed;
            }
            if (data is not XElement geometry) return null;
            var path = (string?)geometry.Attribute("Figures") is { } figures ? SKPath.ParseSvgPathData(figures) ?? new SKPath() : new SKPath();
            foreach (var figure in geometry.Elements().Where(e => e.Name.LocalName == "PathFigure"))
            {
                var start = Numbers((string?)figure.Attribute("StartPoint") ?? "");
                if (start.Length < 2) continue;
                path.MoveTo(start[0], start[1]);
                foreach (var segment in figure.Elements())
                {
                    var p = Numbers((string?)segment.Attribute("Points") ?? (string?)segment.Attribute("Point") ?? "");
                    switch (segment.Name.LocalName)
                    {
                        case "PolyBezierSegment": for (var i = 0; i + 5 < p.Length; i += 6) path.CubicTo(p[i], p[i + 1], p[i + 2], p[i + 3], p[i + 4], p[i + 5]); break;
                        case "PolyQuadraticBezierSegment": for (var i = 0; i + 3 < p.Length; i += 4) path.QuadTo(p[i], p[i + 1], p[i + 2], p[i + 3]); break;
                        default: for (var i = 0; i + 1 < p.Length; i += 2) path.LineTo(p[i], p[i + 1]); break;   // lines; an arc is cut to its chord
                    }
                }
                if ((string?)figure.Attribute("IsClosed") == "true") path.Close();
            }
            path.FillType = (string?)geometry.Attribute("FillRule") == "NonZero" ? SKPathFillType.Winding : SKPathFillType.EvenOdd;
            path.Transform(Matrix(geometry, "Transform"));
            return path;
        }

        private void DrawPath(XElement element, SKMatrix matrix, float opacity)
        {
            using var local = Geometry(Property(element, "Data"));
            if (local == null || local.IsEmpty) return;
            var (fill, imageBrush) = Brush(Property(element, "Fill"), opacity);
            var (stroke, _) = Brush(Property(element, "Stroke"), opacity);
            if (imageBrush != null)
            {
                // the picture sits in the brush viewport; tiling and the path outline as a clip are ignored
                var viewport = Numbers((string?)imageBrush.Attribute("Viewport") ?? "");
                var source = ((string?)imageBrush.Attribute("ImageSource") ?? "").Trim('{', '}').Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var name = source.Length > 1 && source[0] == "ColorConvertedBitmap" ? source[1] : source.FirstOrDefault();
                if (name != null && package.Image(Package.Resolve(BaseOf(imageBrush), name)) is { } image)
                {
                    var area = viewport.Length == 4 ? new SKRect(viewport[0], viewport[1], viewport[0] + viewport[2], viewport[1] + viewport[3]) : local.Bounds;
                    _scene.Image(image, SKMatrix.Concat(matrix, Matrix(imageBrush, "Transform")).MapRect(area));
                }
            }
            if (fill == null && stroke == null) return;
            var path = package.Document.Own(new SKPath(local));
            path.Transform(matrix);
            _scene.Path(path, fill, stroke, Number(element, "StrokeThickness", 1) * ScaleOf(matrix));
        }

        private void DrawGlyphs(XElement element, SKMatrix matrix, float opacity)
        {
            var uri = (string?)element.Attribute("FontUri");
            if (uri == null) return;
            var face = uri.IndexOf('#') is var hash and >= 0 && int.TryParse(uri.AsSpan(hash + 1), out var index) ? index : 0;
            var typeface = package.Typeface(Package.Resolve(BaseOf(element), uri), face);
            if (typeface == null) return;
            var em = Number(element, "FontRenderingEmSize", 12);
            var scale = ScaleOf(matrix);
            var simulations = (string?)element.Attribute("StyleSimulations") ?? "";
            var font = package.Font(typeface, em * scale, simulations.Contains("Bold", StringComparison.Ordinal), simulations.Contains("Italic", StringComparison.Ordinal));
            var color = Brush(Property(element, "Fill"), opacity).Color ?? SKColors.Black;
            var rightToLeft = ((int)Number(element, "BidiLevel", 0) & 1) == 1;

            var text = (string?)element.Attribute("UnicodeString") ?? "";
            if (text.StartsWith("{}", StringComparison.Ordinal)) text = text[2..];
            var specs = ((string?)element.Attribute("Indices") ?? "").Split(';');

            // one entry per glyph: "(chars:glyphs)index,advance,uOffset,vOffset", every part optional
            var glyphs = new List<ushort>();
            var source = new List<char>();
            var explicitAdvance = new List<float?>();
            var offsets = new List<SKPoint>();
            int character = 0, spec = 0;
            // markup has an entry per glyph or a character per glyph: a run never holds more glyphs than that
            var maxGlyphs = (long)specs.Length + text.Length;
            while ((spec < specs.Length || character < text.Length) && glyphs.Count < maxGlyphs)
            {
                var entry = spec < specs.Length ? specs[spec] : "";
                spec++;
                int clusterCharacters = 1, clusterGlyphs = 1;
                if (entry.StartsWith('(') && entry.IndexOf(')') is var close and > 0)
                {
                    var cluster = entry[1..close].Split(':');
                    _ = int.TryParse(cluster[0], out clusterCharacters);
                    if (cluster.Length > 1) _ = int.TryParse(cluster[1], out clusterGlyphs);
                    entry = entry[(close + 1)..];
                    // real clusters are a few characters / glyphs (ligatures, combining marks)
                    clusterCharacters = Math.Min(clusterCharacters, MaxCluster);
                    clusterGlyphs = Math.Min(clusterGlyphs, MaxCluster);
                }
                for (var g = 0; g < Math.Max(1, clusterGlyphs); g++)
                {
                    if (g > 0) { entry = spec < specs.Length ? specs[spec] : ""; spec++; }
                    var parts = entry.Split(',');
                    ushort glyph;
                    if (ushort.TryParse(parts[0], out var given)) glyph = given;
                    else if (character < text.Length)
                    {
                        var pair = char.IsHighSurrogate(text[character]) && character + 1 < text.Length;
                        glyph = typeface.GetGlyph(pair ? char.ConvertToUtf32(text[character], text[character + 1]) : text[character]);
                        if (pair && clusterCharacters == 1) clusterCharacters = 2;
                    }
                    else continue;
                    float? Part(int i) => parts.Length > i && float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v * em / 100 : null;
                    glyphs.Add(glyph);
                    source.Add(character < text.Length ? text[character] : '�');
                    explicitAdvance.Add(Part(1));
                    offsets.Add(new SKPoint(Part(2) ?? 0, -(Part(3) ?? 0)));
                }
                character += Math.Max(1, clusterCharacters);
            }
            if (glyphs.Count == 0) return;

            var natural = font.GetGlyphWidths(glyphs.ToArray());   // at the scaled size
            var origin = new SKPoint(Number(element, "OriginX", 0), Number(element, "OriginY", 0));
            var positions = new SKPoint[glyphs.Count];
            var pen = origin.X;
            for (var i = 0; i < glyphs.Count; i++)
            {
                var advance = explicitAdvance[i] ?? natural[i] / scale;
                if (rightToLeft) pen -= advance;
                positions[i] = matrix.MapPoint(new SKPoint(pen + offsets[i].X, origin.Y + offsets[i].Y));
                if (!rightToLeft) pen += advance;
            }

            // the PDF sink places a run by its first glyph only, so a run ends wherever spacing departs from the font's own
            var first = 0;
            for (var i = 1; i <= glyphs.Count; i++)
            {
                if (i < glyphs.Count && Math.Abs(positions[i].X - (positions[i - 1].X + natural[i - 1])) < 0.1f && Math.Abs(positions[i].Y - positions[i - 1].Y) < 0.05f) continue;
                _scene.Text(font, glyphs.GetRange(first, i - first).ToArray(), positions[first..i], color, new string(source.GetRange(first, i - first).ToArray()));
                first = i;
            }
        }
    }
}
