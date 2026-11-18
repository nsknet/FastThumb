using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FastThumb.Images;

/// <summary>
/// Encapsulated PostScript, by the preview it carries: drawing the PostScript itself takes a PostScript interpreter, which this
/// library does not have. In order of preference: the TIFF of a DOS EPS (binary header C5 D0 D3 C6) unless it is 1-bit, an
/// EPSI preview of 8 bits or more, the thumbnail of a Photoshop EPS (its image resources, in hex), the colour thumbnail
/// Illustrator writes (%AI7_Thumbnail), then, for a file Illustrator wrote (EPS or PostScript-based .ai), its artwork drawn
/// from its paths (<see cref="AiPaths"/>), then 1-bit previews (TIFF, EPSI). With none, the file is refused; its size is
/// still known from %%BoundingBox.
/// </summary>
internal static class EpsPreview
{
    private static ReadOnlySpan<byte> DosSignature => [0xC5, 0xD0, 0xD3, 0xC6];

    /// <summary>
    /// A DOS EPS; a PostScript file that says it is EPS on its first line, that Illustrator wrote (a PostScript-based .ai file
    /// is the same language), or that is named as either. A PDF-based .ai file starts with %PDF and is read as a PDF.
    /// </summary>
    public static bool Claims(ReadOnlySpan<byte> d, string? extension)
    {
        if (d.Length >= 4 && d[..4].SequenceEqual(DosSignature)) return true;
        var named = (extension ?? "").ToLowerInvariant() is ".eps" or ".epsf" or ".epsi" or ".ai";
        if (!d.StartsWith("%!PS-Adobe-"u8)) return d.StartsWith("%!"u8) && named;
        var line = d[..Math.Min(d.Length, 80)];
        var end = line.IndexOfAny((byte)'\r', (byte)'\n');
        return (end < 0 ? line : line[..end]).IndexOf(" EPSF"u8) >= 0 || named || AiPaths.IsIllustrator(d);
    }

    /// <summary>The PostScript section, and the TIFF preview of a DOS EPS, if any.</summary>
    private static (ReadOnlyMemory<byte> PostScript, byte[]? Tiff) Sections(byte[] data)
    {
        if (!data.AsSpan().StartsWith(DosSignature)) return (data, null);
        if (data.Length < 30) throw new InvalidDocumentException("The DOS EPS header is cut short.");
        var ps = Section(data, 4);
        var tiff = Section(data, 20);
        return (data.AsMemory(ps.Start, ps.Length), tiff.Length > 0 ? data.AsSpan(tiff.Start, tiff.Length).ToArray() : null);
    }

    private static (int Start, int Length) Section(byte[] data, int at)
    {
        var start = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
        var length = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 4));
        if (length == 0) return (0, 0);
        if (start > (uint)data.Length || length > (uint)data.Length - start) throw new InvalidDocumentException("A DOS EPS section runs past the end of the file.");
        return ((int)start, (int)length);
    }

    public static (int Width, int Height) Size(byte[] data)
    {
        var (ps, tiff) = Sections(data);
        if (BoundingBox(ps.Span) is { } box) return box;
        using var preview = Decode(ps.Span, tiff, DecodeRequest.Full(long.MaxValue));
        return (preview.Width, preview.Height);
    }

    public static NetVips.Image Decode(byte[] data, DecodeRequest request)
    {
        var (ps, tiff) = Sections(data);
        return Decode(ps.Span, tiff, request);
    }

    private static NetVips.Image Decode(ReadOnlySpan<byte> ps, byte[]? tiff, DecodeRequest request)
    {
        // a 1-bit preview is a dithered stand-in: a grey or colour one comes first, however small
        var oneBit = tiff != null && TiffWalker.Open(tiff) is { Directories.Count: > 0 } walker && (walker.Get(walker.Directories[0], 0x0102) ?? 1) == 1;
        if (tiff != null && !oneBit) return ManagedImages.Embedded(tiff, request);
        var preview = Epsi(ps, request, minDepth: 8) ?? Photoshop(ps, request) ?? Illustrator(ps, request);
        if (preview != null) return preview;
        // no picture of its own: Illustrator's artwork is drawn from its paths, still better than a 1-bit stand-in
        if (AiPaths.IsIllustrator(ps)) return AiPaths.Render(ps, request);
        if (tiff != null) return ManagedImages.Embedded(tiff, request);
        return Epsi(ps, request, minDepth: 1)
            ?? throw new UnsupportedFormatException("The EPS file has no preview to show (drawing PostScript itself needs a PostScript interpreter).");
    }

    /// <summary>The JPEG thumbnail among the image resources a Photoshop EPS writes in hex between %BeginPhotoshop and %EndPhotoshop.</summary>
    private static NetVips.Image? Photoshop(ReadOnlySpan<byte> ps, DecodeRequest request)
    {
        var at = ps.IndexOf("%BeginPhotoshop:"u8);
        if (at < 0) return null;
        var resources = Hex(Body(ps, at, "%EndPhotoshop"u8), null);
        return PsdDecoder.Thumbnail(resources, 0, resources.Length, request);
    }

    /// <summary>%%BoundingBox (in points; "(atend)" sends it to the trailer), rounded out to whole points.</summary>
    private static (int Width, int Height)? BoundingBox(ReadOnlySpan<byte> ps)
    {
        var key = "%%BoundingBox:"u8;
        var at = ps.IndexOf(key);
        if (at < 0) return null;
        var values = Numbers(Line(ps, at + key.Length));
        if (values.Count < 4)
        {
            at = ps.LastIndexOf(key);
            values = Numbers(Line(ps, at + key.Length));
            if (values.Count < 4) return null;
        }
        var width = (int)Math.Ceiling(values[2] - values[0]);
        var height = (int)Math.Ceiling(values[3] - values[1]);
        return width > 0 && height > 0 ? (width, height) : null;
    }

    /// <summary>
    /// EPSI: "%%BeginPreview: width height depth lines", then hex rows (in comment lines) up to %%EndPreview: top row first, each
    /// padded to a byte, 0 white and the largest value black.
    /// </summary>
    private static NetVips.Image? Epsi(ReadOnlySpan<byte> ps, DecodeRequest request, int minDepth)
    {
        var key = "%%BeginPreview:"u8;
        var at = ps.IndexOf(key);
        if (at < 0) return null;
        var values = Numbers(Line(ps, at + key.Length));
        if (values.Count < 3) return null;
        int width = (int)values[0], height = (int)values[1], depth = (int)values[2];
        if (depth is not (1 or 2 or 4 or 8) || depth < minDepth) return null;
        request.Check(width, height);
        var body = Body(ps, at, "%%EndPreview"u8);
        var rowBytes = (width * depth + 7) / 8;
        var packed = Hex(body, rowBytes * height);   // within MaxPixels
        var pixels = new byte[(long)width * height];
        var max = (1 << depth) - 1;
        for (var y = 0; y < height; y++)
        {
            if ((y & 63) == 0) Guard.Check();
            for (var x = 0; x < width; x++)
            {
                var bit = x * depth;
                var b = packed[y * rowBytes + (bit >> 3)];
                var value = (b >> (8 - depth - (bit & 7))) & max;
                pixels[y * width + x] = (byte)(255 - value * 255 / max);
            }
        }
        return ManagedImages.FromPixels(pixels, width, height, 1);
    }

    /// <summary>
    /// Illustrator's "%AI7_Thumbnail: width height 8", then hex data between %%BeginData and %%EndData: a palette of 256 RGB
    /// entries, then one index a pixel, top row first, run-length encoded when it starts with "RLE" (0xFD n b: b repeated n
    /// times; 0xFD 0xFD: 0xFD itself).
    /// </summary>
    private static NetVips.Image? Illustrator(ReadOnlySpan<byte> ps, DecodeRequest request)
    {
        var key = "%AI7_Thumbnail:"u8;
        var at = ps.IndexOf(key);
        if (at < 0) return null;
        var values = Numbers(Line(ps, at + key.Length));
        if (values.Count < 3 || values[2] != 8) return null;
        int width = (int)values[0], height = (int)values[1];
        request.Check(width, height);
        var begin = ps[at..].IndexOf("%%BeginData:"u8);
        if (begin < 0) return null;
        begin += at;
        var data = Hex(Body(ps, begin, "%%EndData"u8), null);
        if (data.Length < 768) return null;
        var palette = data.AsSpan(0, 768);
        var stored = data.AsSpan(768);
        var count = width * height;
        var indices = new byte[count];
        if (stored.StartsWith("RLE"u8))
        {
            stored = stored[3..];
            int i = 0, o = 0;
            while (i < stored.Length && o < count)
            {
                var b = stored[i++];
                if (b != 0xFD) { indices[o++] = b; continue; }
                if (i >= stored.Length) break;
                b = stored[i++];
                if (b == 0xFD) { indices[o++] = b; continue; }
                if (i >= stored.Length) break;
                var run = Math.Min(b, count - o);
                indices.AsSpan(o, run).Fill(stored[i++]);
                o += run;
            }
        }
        else stored[..Math.Min(stored.Length, count)].CopyTo(indices);
        var pixels = new byte[(long)count * 3];
        for (var p = 0; p < count; p++) palette.Slice(indices[p] * 3, 3).CopyTo(pixels.AsSpan(p * 3));
        return ManagedImages.FromPixels(pixels, width, height, 3);
    }

    /// <summary>From the line after <paramref name="at"/> up to <paramref name="end"/> (or the end of the PostScript).</summary>
    private static ReadOnlySpan<byte> Body(ReadOnlySpan<byte> ps, int at, ReadOnlySpan<byte> end)
    {
        var start = at + Line(ps, at).Length;
        var length = ps[start..].IndexOf(end);
        return length < 0 ? ps[start..] : ps.Slice(start, length);
    }

    /// <summary>The rest of the line from <paramref name="at"/>.</summary>
    private static ReadOnlySpan<byte> Line(ReadOnlySpan<byte> ps, int at)
    {
        var rest = ps[at..];
        var end = rest.IndexOfAny((byte)'\r', (byte)'\n');
        return end < 0 ? rest : rest[..end];
    }

    private static List<double> Numbers(ReadOnlySpan<byte> line)
    {
        var numbers = new List<double>();
        foreach (var word in Encoding.ASCII.GetString(line).Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || double.IsNaN(value) || Math.Abs(value) > 1e7) break;
            numbers.Add(value);
        }
        return numbers;
    }

    /// <summary>
    /// Hex digits to bytes, skipping everything else (comment marks, line ends). With a <paramref name="size"/>, exactly that many
    /// bytes, the ones the text does not reach left zero; without, as many as there are.
    /// </summary>
    private static byte[] Hex(ReadOnlySpan<byte> text, int? size)
    {
        var output = new byte[size ?? text.Length / 2];
        int o = 0, high = -1;
        foreach (var c in text)
        {
            var v = c switch { >= (byte)'0' and <= (byte)'9' => c - '0', >= (byte)'A' and <= (byte)'F' => c - 'A' + 10, >= (byte)'a' and <= (byte)'f' => c - 'a' + 10, _ => -1 };
            if (v < 0) continue;
            if (high < 0) { high = v; continue; }
            output[o++] = (byte)(high << 4 | v);
            high = -1;
            if (o == output.Length) break;
        }
        return size != null || o == output.Length ? output : output.AsSpan(0, o).ToArray();
    }
}
