using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using FastThumb.Layout;
using SkiaSharp;

namespace FastThumb.Scene;

/// <summary>
/// Minimal one-page PDF writer: Catalog / Pages / Page / content stream, one Type0 (Identity-H) font per
/// typeface with a glyf-subset TrueType program, optional ToUnicode, image XObjects. Text is written with the
/// glyph ids and positions the paginator already computed — nothing is shaped or measured here.
/// Throws NotSupportedException for fonts without TrueType outlines so the caller can fall back.
/// </summary>
internal static class PdfSink
{
    private sealed class FontUse(int index, FontProgram program)
    {
        public readonly int Index = index;
        public readonly FontProgram Program = program;
        public readonly SortedDictionary<ushort, int> Glyphs = [];   // glyph id → unicode scalar (or -1)
    }

    public static byte[] Write(IReadOnlyList<PageScene> scenes)
    {
        var fonts = new Dictionary<SKTypeface, FontUse>(ReferenceEqualityComparer.Instance);
        var images = new List<SKImage>();
        var streams = new List<string>(scenes.Count);
        foreach (var scene in scenes)
        {
            Guard.Check();
            streams.Add(PageContent(scene, fonts, images));
        }

        var pdf = new ObjectWriter();
        int catalog = pdf.Reserve(), pages = pdf.Reserve(), resources = pdf.Reserve();

        var fontRefs = new StringBuilder();
        foreach (var use in fonts.Values)
        {
            int type0 = pdf.Reserve(), cid = pdf.Reserve(), descriptor = pdf.Reserve(), file = pdf.Reserve(), toUnicode = pdf.Reserve();
            var p = use.Program;
            var name = "AAAAAA+" + p.PostScriptName;
            double k = 1000.0 / p.UnitsPerEm;

            var widths = new StringBuilder("[");
            foreach (var glyph in use.Glyphs.Keys) widths.Append(glyph).Append(" [").Append(N(p.Advance(glyph) * k)).Append("] ");
            widths.Append(']');

            pdf.Set(type0, $"<< /Type /Font /Subtype /Type0 /BaseFont /{name} /Encoding /Identity-H /DescendantFonts [{cid} 0 R] /ToUnicode {toUnicode} 0 R >>");
            pdf.Set(cid, $"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /{name} /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> " +
                         $"/FontDescriptor {descriptor} 0 R /DW 1000 /W {widths} /CIDToGIDMap /Identity >>");
            pdf.Set(descriptor, $"<< /Type /FontDescriptor /FontName /{name} /Flags 4 /FontBBox [{N(p.XMin * k)} {N(p.YMin * k)} {N(p.XMax * k)} {N(p.YMax * k)}] " +
                                $"/ItalicAngle 0 /Ascent {N(p.Ascent * k)} /Descent {N(p.Descent * k)} /CapHeight {N(p.Ascent * k)} /StemV 80 /FontFile2 {file} 0 R >>");
            var subset = p.Subset(use.Glyphs.Keys);
            pdf.SetStream(file, $"/Length1 {subset.Length}", subset);
            pdf.SetStream(toUnicode, "", Encoding.ASCII.GetBytes(ToUnicode(use.Glyphs)));
            fontRefs.Append("/F").Append(use.Index).Append(' ').Append(type0).Append(" 0 R ");
        }

        var imageRefs = new StringBuilder();
        for (var i = 0; i < images.Count; i++)
        {
            var id = pdf.Reserve();
            WriteImage(pdf, id, images[i]);
            imageRefs.Append("/Im").Append(i + 1).Append(' ').Append(id).Append(" 0 R ");
        }
        pdf.Set(resources, $"<< /Font << {fontRefs}>> /XObject << {imageRefs}>> >>"); // one dictionary shared by every page

        var kids = new StringBuilder();
        for (var i = 0; i < scenes.Count; i++)
        {
            int page = pdf.Reserve(), stream = pdf.Reserve();
            pdf.Set(page, $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 {N(scenes[i].Width)} {N(scenes[i].Height)}] /Contents {stream} 0 R /Resources {resources} 0 R >>");
            pdf.SetStream(stream, "", Encoding.ASCII.GetBytes(streams[i]));
            kids.Append(page).Append(" 0 R ");
        }
        pdf.Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R >>");
        pdf.Set(pages, $"<< /Type /Pages /Kids [{kids}] /Count {scenes.Count} >>");
        return pdf.Finish(catalog);
    }

    /// <summary>Content stream of one page; registers the fonts, glyphs and images it uses.</summary>
    private static string PageContent(PageScene scene, Dictionary<SKTypeface, FontUse> fonts, List<SKImage> images)
    {
        var content = new StringBuilder(4096);
        content.Append("1 0 0 -1 0 ").Append(N(scene.Height)).Append(" cm\n"); // y grows downwards, like the scene

        foreach (var op in scene.Ops)
        {
            switch (op)
            {
                case PageScene.TextOp text when text.Glyphs.Length > 0:
                    var typeface = text.Font.Typeface;
                    if (!fonts.TryGetValue(typeface, out var use))
                    {
                        var program = FontProgram.Get(typeface) ?? throw new NotSupportedException($"No TrueType outlines: {typeface.FamilyName}");
                        fonts[typeface] = use = new FontUse(fonts.Count + 1, program);
                    }
                    var mapped = text.Source != null && text.Source.Length == text.Glyphs.Length;
                    content.Append("BT /F").Append(use.Index).Append(' ').Append(N(text.Font.Size)).Append(" Tf ");
                    Rgb(content, text.Color).Append(" rg 1 0 0 -1 ").Append(N(text.Positions[0].X)).Append(' ').Append(N(text.Positions[0].Y)).Append(" Tm <");
                    for (var i = 0; i < text.Glyphs.Length; i++)
                    {
                        var glyph = text.Glyphs[i];
                        content.Append(glyph.ToString("X4"));
                        if (!use.Glyphs.ContainsKey(glyph)) use.Glyphs[glyph] = mapped ? text.Source![i] : -1;
                    }
                    content.Append("> Tj ET\n");
                    break;

                case PageScene.LineOp line:
                    Rgb(content, line.Color).Append(" RG ").Append(N(line.Width)).Append(" w ")
                        .Append(N(line.X1)).Append(' ').Append(N(line.Y1)).Append(" m ")
                        .Append(N(line.X2)).Append(' ').Append(N(line.Y2)).Append(" l S\n");
                    break;

                case PageScene.ShapeOp shape:
                    if (shape.Fill is { } fill) Rgb(content, fill).Append(" rg ");
                    if (shape.Stroke is { } stroke) Rgb(content, stroke).Append(" RG ").Append(N(shape.StrokeWidth)).Append(" w ");
                    AppendPath(content, shape.Kind, shape.Bounds);
                    content.Append(shape.Fill != null && shape.Stroke != null ? "B\n" : shape.Fill != null ? "f\n" : "S\n");
                    break;

                case PageScene.PathOp path:
                    if (path.Fill is { } pathFill) Rgb(content, pathFill).Append(" rg ");
                    if (path.Stroke is { } pathStroke) Rgb(content, pathStroke).Append(" RG ").Append(N(path.StrokeWidth)).Append(" w ");
                    AppendOutline(content, path.Path);
                    var evenOdd = path.Path.FillType == SKPathFillType.EvenOdd ? "*" : "";
                    content.Append(path.Fill != null && path.Stroke != null ? "B" + evenOdd : path.Fill != null ? "f" + evenOdd : "S").Append('\n');
                    break;

                case PageScene.ImageOp image:
                    images.Add(image.Image);
                    var r = image.Target;
                    content.Append("q ").Append(N(r.Width)).Append(" 0 0 ").Append(N(-r.Height)).Append(' ')
                        .Append(N(r.Left)).Append(' ').Append(N(r.Bottom)).Append(" cm /Im").Append(images.Count).Append(" Do Q\n");
                    break;
            }
        }
        return content.ToString();
    }

    /// <summary>Any Skia path as PDF path operators; quadratic and conic pieces become cubic Béziers.</summary>
    private static void AppendOutline(StringBuilder content, SKPath path)
    {
        void Point(SKPoint p) => content.Append(N(p.X)).Append(' ').Append(N(p.Y)).Append(' ');
        void Quad(SKPoint a, SKPoint control, SKPoint b)
        {
            Point(new SKPoint(a.X + (control.X - a.X) * 2 / 3, a.Y + (control.Y - a.Y) * 2 / 3));
            Point(new SKPoint(b.X + (control.X - b.X) * 2 / 3, b.Y + (control.Y - b.Y) * 2 / 3));
            Point(b);
            content.Append("c ");
        }
        using var iterator = path.CreateRawIterator();
        var points = new SKPoint[4];
        SKPathVerb verb;
        while ((verb = iterator.Next(points)) != SKPathVerb.Done)
        {
            switch (verb)
            {
                case SKPathVerb.Move: Point(points[0]); content.Append("m "); break;
                case SKPathVerb.Line: Point(points[1]); content.Append("l "); break;
                case SKPathVerb.Quad: Quad(points[0], points[1], points[2]); break;
                case SKPathVerb.Conic:
                    var quads = SKPath.ConvertConicToQuads(points[0], points[1], points[2], iterator.ConicWeight(), 2);
                    for (var i = 0; i + 2 < quads.Length; i += 2) Quad(quads[i], quads[i + 1], quads[i + 2]);
                    break;
                case SKPathVerb.Cubic: Point(points[1]); Point(points[2]); Point(points[3]); content.Append("c "); break;
                case SKPathVerb.Close: content.Append("h "); break;
            }
        }
    }

    /// <summary>Path of a rectangle, rounded rectangle (kind 1) or ellipse (kind 2); curves are cubic Béziers.</summary>
    private static void AppendPath(StringBuilder content, int kind, SKRect r)
    {
        if (kind == 0) { content.Append(N(r.Left)).Append(' ').Append(N(r.Top)).Append(' ').Append(N(r.Width)).Append(' ').Append(N(r.Height)).Append(" re "); return; }
        float rx = kind == 2 ? r.Width / 2 : Math.Min(r.Width, r.Height) * 0.1667f, ry = kind == 2 ? r.Height / 2 : rx;
        float kx = rx * 0.5523f, ky = ry * 0.5523f;
        void Move(float x, float y) => content.Append(N(x)).Append(' ').Append(N(y)).Append(" m ");
        void Line(float x, float y) => content.Append(N(x)).Append(' ').Append(N(y)).Append(" l ");
        void Curve(float x1, float y1, float x2, float y2, float x, float y) => content.Append(N(x1)).Append(' ').Append(N(y1)).Append(' ').Append(N(x2)).Append(' ').Append(N(y2)).Append(' ').Append(N(x)).Append(' ').Append(N(y)).Append(" c ");
        Move(r.Left + rx, r.Top);
        Line(r.Right - rx, r.Top); Curve(r.Right - rx + kx, r.Top, r.Right, r.Top + ry - ky, r.Right, r.Top + ry);
        Line(r.Right, r.Bottom - ry); Curve(r.Right, r.Bottom - ry + ky, r.Right - rx + kx, r.Bottom, r.Right - rx, r.Bottom);
        Line(r.Left + rx, r.Bottom); Curve(r.Left + rx - kx, r.Bottom, r.Left, r.Bottom - ry + ky, r.Left, r.Bottom - ry);
        Line(r.Left, r.Top + ry); Curve(r.Left, r.Top + ry - ky, r.Left + rx - kx, r.Top, r.Left + rx, r.Top);
        content.Append("h ");
    }

    private static void WriteImage(ObjectWriter pdf, int id, SKImage image)
    {
        using var encoded = image.EncodedData;
        if (encoded != null)
        {
            var span = encoded.AsSpan();
            if (span.Length > 3 && span[0] == 0xFF && span[1] == 0xD8)
            {
                using var codec = SKCodec.Create(encoded);
                var space = codec?.Info.ColorType == SKColorType.Gray8 ? "/DeviceGray" : "/DeviceRGB";
                // JPEG source is passed through untouched: no decode, no re-encode
                pdf.SetStream(id, $"/Type /XObject /Subtype /Image /Width {image.Width} /Height {image.Height} /ColorSpace {space} /BitsPerComponent 8 /Filter /DCTDecode", span.ToArray(), compress: false);
                return;
            }
        }
        using var bitmap = SKBitmap.FromImage(image);
        var pixels = bitmap.Pixels;
        var rgb = new byte[pixels.Length * 3];
        for (var i = 0; i < pixels.Length; i++)
        {
            // flatten transparency onto white, PDF image XObjects here carry no soft mask
            var c = pixels[i];
            var a = c.Alpha / 255f;
            rgb[i * 3] = (byte)(c.Red * a + 255 * (1 - a));
            rgb[i * 3 + 1] = (byte)(c.Green * a + 255 * (1 - a));
            rgb[i * 3 + 2] = (byte)(c.Blue * a + 255 * (1 - a));
        }
        pdf.SetStream(id, $"/Type /XObject /Subtype /Image /Width {image.Width} /Height {image.Height} /ColorSpace /DeviceRGB /BitsPerComponent 8", rgb);
    }

    private static string ToUnicode(SortedDictionary<ushort, int> glyphs)
    {
        var sb = new StringBuilder(
            "/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def " +
            "/CMapName /Adobe-Identity-UCS def /CMapType 2 def 1 begincodespacerange <0000> <FFFF> endcodespacerange\n");
        var known = glyphs.Where(g => g.Value >= 0).ToList();
        for (var i = 0; i < known.Count; i += 100)
        {
            var chunk = known.Skip(i).Take(100).ToList();
            sb.Append(chunk.Count).Append(" beginbfchar\n");
            foreach (var (glyph, unicode) in chunk) sb.Append('<').Append(glyph.ToString("X4")).Append("> <").Append(unicode.ToString("X4")).Append(">\n");
            sb.Append("endbfchar\n");
        }
        return sb.Append("endcmap CMapName currentdict /CMap defineresource pop end end").ToString();
    }

    private static string N(double value) => Math.Round(value, 3).ToString("0.###", CultureInfo.InvariantCulture);

    private static StringBuilder Rgb(StringBuilder sb, SKColor c)
        => sb.Append(N(c.Red / 255.0)).Append(' ').Append(N(c.Green / 255.0)).Append(' ').Append(N(c.Blue / 255.0));

    private sealed class ObjectWriter
    {
        private readonly List<byte[]?> _objects = [];

        public int Reserve() { _objects.Add(null); return _objects.Count; }
        public void Set(int id, string body) => _objects[id - 1] = Encoding.ASCII.GetBytes(body);

        public void SetStream(int id, string dictionary, byte[] data, bool compress = true)
        {
            if (compress)
            {
                using var buffer = new MemoryStream();
                using (var zlib = new ZLibStream(buffer, CompressionLevel.Fastest, leaveOpen: true)) zlib.Write(data);
                data = buffer.ToArray();
                dictionary += " /Filter /FlateDecode";
            }
            var head = Encoding.ASCII.GetBytes($"<< {dictionary} /Length {data.Length} >>\nstream\n");
            var tail = "\nendstream"u8;
            var body = new byte[head.Length + data.Length + tail.Length];
            head.CopyTo(body, 0);
            data.CopyTo(body, head.Length);
            tail.CopyTo(body.AsSpan(head.Length + data.Length));
            _objects[id - 1] = body;
        }

        public byte[] Finish(int root)
        {
            using var output = new MemoryStream();
            void Ascii(string text) => output.Write(Encoding.ASCII.GetBytes(text));
            output.Write("%PDF-1.7\n%\xE2\xE3\xCF\xD3\n"u8);
            var offsets = new long[_objects.Count];
            for (var i = 0; i < _objects.Count; i++)
            {
                offsets[i] = output.Position;
                Ascii($"{i + 1} 0 obj\n");
                output.Write(_objects[i] ?? "null"u8.ToArray());
                Ascii("\nendobj\n");
            }
            var xref = output.Position;
            Ascii($"xref\n0 {_objects.Count + 1}\n0000000000 65535 f \n");
            foreach (var offset in offsets) Ascii($"{offset:D10} 00000 n \n");
            Ascii($"trailer\n<< /Size {_objects.Count + 1} /Root {root} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            return output.ToArray();
        }
    }
}

/// <summary>
/// Parsed TrueType face, cached per typeface for the whole process. Produces subsets that keep glyph ids
/// (so the content stream can use the layout's glyph ids directly) but carry outlines only for used glyphs.
/// </summary>
internal sealed class FontProgram
{
    private static readonly ConcurrentDictionary<SKTypeface, FontProgram?> Cache = new(ReferenceEqualityComparer.Instance);

    private readonly byte[] _head, _hhea, _maxp, _hmtx, _glyf;
    private readonly byte[]? _cvt, _fpgm, _prep;
    private readonly uint[] _loca;
    private readonly int _numGlyphs, _numHMetrics;

    public int UnitsPerEm { get; }
    public short XMin { get; }
    public short YMin { get; }
    public short XMax { get; }
    public short YMax { get; }
    public short Ascent { get; }
    public short Descent { get; }
    public string PostScriptName { get; }

    public static FontProgram? Get(SKTypeface typeface) => Cache.GetOrAdd(typeface, static face =>
    {
        try { return new FontProgram(face); }
        catch (Exception) { return null; }
    });

    private static uint Tag(string tag) => (uint)(tag[0] << 24 | tag[1] << 16 | tag[2] << 8 | tag[3]);
    private static byte[]? Table(SKTypeface face, string tag) => face.TryGetTableData(Tag(tag), out var data) ? data : null;
    private static byte[] Required(SKTypeface face, string tag) => Table(face, tag) ?? throw new NotSupportedException("missing table " + tag);

    private FontProgram(SKTypeface face)
    {
        _head = Required(face, "head");
        _hhea = Required(face, "hhea");
        _maxp = Required(face, "maxp");
        _hmtx = Required(face, "hmtx");
        _glyf = Required(face, "glyf");
        var loca = Required(face, "loca");
        _cvt = Table(face, "cvt ");
        _fpgm = Table(face, "fpgm");
        _prep = Table(face, "prep");

        UnitsPerEm = BinaryPrimitives.ReadUInt16BigEndian(_head.AsSpan(18));
        XMin = BinaryPrimitives.ReadInt16BigEndian(_head.AsSpan(36));
        YMin = BinaryPrimitives.ReadInt16BigEndian(_head.AsSpan(38));
        XMax = BinaryPrimitives.ReadInt16BigEndian(_head.AsSpan(40));
        YMax = BinaryPrimitives.ReadInt16BigEndian(_head.AsSpan(42));
        var longLoca = BinaryPrimitives.ReadInt16BigEndian(_head.AsSpan(50)) != 0;
        Ascent = BinaryPrimitives.ReadInt16BigEndian(_hhea.AsSpan(4));
        Descent = BinaryPrimitives.ReadInt16BigEndian(_hhea.AsSpan(6));
        _numHMetrics = BinaryPrimitives.ReadUInt16BigEndian(_hhea.AsSpan(34));
        _numGlyphs = BinaryPrimitives.ReadUInt16BigEndian(_maxp.AsSpan(4));

        _loca = new uint[_numGlyphs + 1];
        for (var i = 0; i <= _numGlyphs; i++)
            _loca[i] = longLoca ? BinaryPrimitives.ReadUInt32BigEndian(loca.AsSpan(i * 4)) : (uint)BinaryPrimitives.ReadUInt16BigEndian(loca.AsSpan(i * 2)) * 2;

        var name = new StringBuilder();
        foreach (var ch in face.FamilyName + (face.IsBold ? "-Bold" : "") + (face.IsItalic ? "-Italic" : ""))
            if (char.IsAsciiLetterOrDigit(ch) || ch == '-') name.Append(ch);
        PostScriptName = name.Length > 0 ? name.ToString() : "Font";
    }

    public int Advance(ushort glyph)
    {
        var index = Math.Min(glyph, _numHMetrics - 1);
        return BinaryPrimitives.ReadUInt16BigEndian(_hmtx.AsSpan(index * 4));
    }

    private ReadOnlySpan<byte> Outline(int glyph)
    {
        if (glyph >= _numGlyphs) return default;
        uint start = _loca[glyph], end = _loca[glyph + 1];
        return end > start && end <= _glyf.Length ? _glyf.AsSpan((int)start, (int)(end - start)) : default;
    }

    public byte[] Subset(IEnumerable<ushort> glyphs)
    {
        // closure over composite glyphs: a composite references other glyph ids that must stay too
        // every glyph is expanded once (.notdef = 0 included), so a composite that refers to itself cannot loop
        var keep = new HashSet<int>();
        var pending = new Stack<int>(glyphs.Select(g => (int)g).Prepend(0));
        while (pending.Count > 0)
        {
            var glyph = pending.Pop();
            if (!keep.Add(glyph)) continue;
            var data = Outline(glyph);
            if (data.Length < 10 || BinaryPrimitives.ReadInt16BigEndian(data) >= 0) continue;
            var at = 10;
            while (at + 4 <= data.Length)
            {
                var flags = BinaryPrimitives.ReadUInt16BigEndian(data[at..]);
                pending.Push(BinaryPrimitives.ReadUInt16BigEndian(data[(at + 2)..]));
                at += 4 + ((flags & 0x1) != 0 ? 4 : 2);
                if ((flags & 0x8) != 0) at += 2; else if ((flags & 0x40) != 0) at += 4; else if ((flags & 0x80) != 0) at += 8;
                if ((flags & 0x20) == 0) break;
            }
        }

        using var glyf = new MemoryStream();
        var loca = new byte[(_numGlyphs + 1) * 4];
        for (var glyph = 0; glyph < _numGlyphs; glyph++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(glyph * 4), (uint)glyf.Position);
            if (!keep.Contains(glyph)) continue;
            glyf.Write(Outline(glyph));
            while (glyf.Position % 4 != 0) glyf.WriteByte(0);
        }
        BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(_numGlyphs * 4), (uint)glyf.Position);

        var head = (byte[])_head.Clone();
        head.AsSpan(8, 4).Clear();                                   // checkSumAdjustment
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(50), 1);     // long loca

        var tables = new List<(string Tag, byte[] Data)> { ("glyf", glyf.ToArray()), ("head", head), ("hhea", _hhea), ("hmtx", _hmtx), ("loca", loca), ("maxp", _maxp) };
        if (_cvt != null) tables.Add(("cvt ", _cvt));
        if (_fpgm != null) tables.Add(("fpgm", _fpgm));
        if (_prep != null) tables.Add(("prep", _prep));
        tables.Sort((a, b) => string.CompareOrdinal(a.Tag, b.Tag));

        var count = tables.Count;
        var power = 1; var log = 0;
        while (power * 2 <= count) { power *= 2; log++; }
        var size = 12 + 16 * count + tables.Sum(t => (t.Data.Length + 3) & ~3);
        var font = new byte[size];
        BinaryPrimitives.WriteUInt32BigEndian(font, 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(font.AsSpan(4), (ushort)count);
        BinaryPrimitives.WriteUInt16BigEndian(font.AsSpan(6), (ushort)(power * 16));
        BinaryPrimitives.WriteUInt16BigEndian(font.AsSpan(8), (ushort)log);
        BinaryPrimitives.WriteUInt16BigEndian(font.AsSpan(10), (ushort)(count * 16 - power * 16));
        var offset = 12 + 16 * count;
        for (var i = 0; i < count; i++)
        {
            var (tag, data) = tables[i];
            data.CopyTo(font, offset);
            var record = font.AsSpan(12 + 16 * i);
            BinaryPrimitives.WriteUInt32BigEndian(record, Tag(tag));
            BinaryPrimitives.WriteUInt32BigEndian(record[4..], Checksum(font.AsSpan(offset, (data.Length + 3) & ~3)));
            BinaryPrimitives.WriteUInt32BigEndian(record[8..], (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(record[12..], (uint)data.Length);
            offset += (data.Length + 3) & ~3;
        }
        return font;
    }

    private static uint Checksum(ReadOnlySpan<byte> padded)
    {
        uint sum = 0;
        for (var i = 0; i + 4 <= padded.Length; i += 4) sum += BinaryPrimitives.ReadUInt32BigEndian(padded[i..]);
        return sum;
    }
}
