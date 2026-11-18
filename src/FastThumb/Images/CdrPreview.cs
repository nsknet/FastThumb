using System.Buffers.Binary;
using System.IO.Compression;

namespace FastThumb.Images;

/// <summary>
/// CorelDRAW drawings, by the preview they carry: drawing the vector content itself would take a whole engine. A RIFF file
/// ("RIFF", then "CDR" and a version letter) keeps a small bitmap in its DISP chunk, a device-independent bitmap without the
/// BMP file header. From version X4 the drawing is a ZIP package: its preview is an image under previews/, else the DISP
/// chunk of the RIFF document inside it. Previews are small (typically 128 to 256 pixels).
/// </summary>
internal static class CdrPreview
{
    /// <summary>Largest package entry read, preview or inner document.</summary>
    private const long MaxEntry = 64L << 20;

    public static bool Claims(ReadOnlySpan<byte> d, string? extension)
    {
        if (IsRiff(d)) return true;
        if (d.Length < 4 || d[0] != 'P' || d[1] != 'K' || d[2] != 3 || d[3] != 4) return false;
        if ((extension ?? "").ToLowerInvariant() == ".cdr") return true;
        // the central directory, at the end, names the package's parts
        var tail = d[Math.Max(0, d.Length - 65536)..];
        return tail.IndexOf("content/riffData.cdr"u8) >= 0 || (tail.IndexOf("content/root.dat"u8) >= 0 && tail.IndexOf("previews/"u8) >= 0);
    }

    private static bool IsRiff(ReadOnlySpan<byte> d) =>
        d.Length >= 12 && d[..4].SequenceEqual("RIFF"u8) && (d.Slice(8, 3).SequenceEqual("CDR"u8) || d.Slice(8, 3).SequenceEqual("cdr"u8));

    public static NetVips.Image Decode(byte[] data, DecodeRequest request) =>
        ManagedImages.Encoded(Preview(data) ?? throw new UnsupportedFormatException("The CorelDRAW file has no preview picture to show."), request);

    public static (int Width, int Height) Size(byte[] data, DecodeRequest request)
    {
        using var image = Decode(data, request);
        return (image.Width, image.Height);
    }

    /// <summary>The encoded preview (PNG, JPEG or BMP), or null.</summary>
    private static byte[]? Preview(byte[] data)
    {
        if (IsRiff(data)) return Disp(data);
        using var zip = new ZipArchive(new MemoryStream(data, false), ZipArchiveMode.Read);
        var preview = zip.Entries
            .Where(e => e.FullName.StartsWith("previews/", StringComparison.OrdinalIgnoreCase)
                && Path.GetExtension(e.FullName).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp")
            .OrderByDescending(e => e.Length)
            .FirstOrDefault();
        if (preview != null) return Read(preview);
        foreach (var name in new[] { "content/riffData.cdr", "content/root.dat" })
            if (zip.GetEntry(name) is { } inner && Read(inner) is { } riff && IsRiff(riff) && Disp(riff) is { } bitmap) return bitmap;
        return null;
    }

    private static byte[] Read(ZipArchiveEntry entry)
    {
        if (entry.Length > MaxEntry) throw new ResourceLimitExceededException($"A CorelDRAW package part is larger than {MaxEntry:N0} bytes.");
        using var stream = Guard.Limit(entry.Open(), MaxEntry, "CorelDRAW package part");
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>The DISP chunk's bitmap with a BMP file header in front, from the top-level chunks and LIST chunks of the RIFF form.</summary>
    private static byte[]? Disp(byte[] data)
    {
        var d = data.AsSpan();
        var end = (int)Math.Min(d.Length, 8L + BinaryPrimitives.ReadUInt32LittleEndian(d[4..]));
        return Chunks(d, 12, end, 0);
    }

    private static byte[]? Chunks(ReadOnlySpan<byte> d, int at, int end, int depth)
    {
        while (at + 8 <= end)
        {
            Guard.Check();
            var id = d.Slice(at, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(d[(at + 4)..]);
            var body = at + 8;
            if (size > (uint)(end - body)) return null;
            if (id.SequenceEqual("DISP"u8)) return Bitmap(d.Slice(body, (int)size));
            if (id.SequenceEqual("LIST"u8) && size >= 4 && depth < 8 && Chunks(d, body + 4, body + (int)size, depth + 1) is { } found) return found;
            at = body + (int)((size + 1) & ~1u);
        }
        return null;
    }

    /// <summary>
    /// A device-independent bitmap, which may follow a few bytes of its own, as a BMP file: the info header is found by its
    /// size field and sane dimensions, and the pixel offset is worked out from the header and the colour table.
    /// </summary>
    private static byte[]? Bitmap(ReadOnlySpan<byte> disp)
    {
        for (var skip = 0; skip <= 8 && skip + 40 <= disp.Length; skip += 4)
        {
            var dib = disp[skip..];
            var header = BinaryPrimitives.ReadUInt32LittleEndian(dib);
            if (header is not (40 or 52 or 56 or 108 or 124)) continue;
            var width = BinaryPrimitives.ReadInt32LittleEndian(dib[4..]);
            var height = BinaryPrimitives.ReadInt32LittleEndian(dib[8..]);
            int planes = BinaryPrimitives.ReadUInt16LittleEndian(dib[12..]), bits = BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]);
            if (width is <= 0 or > 65535 || height == 0 || Math.Abs(height) > 65535 || planes != 1 || bits is not (1 or 4 or 8 or 16 or 24 or 32)) continue;
            var compression = BinaryPrimitives.ReadUInt32LittleEndian(dib[16..]);
            var used = BinaryPrimitives.ReadUInt32LittleEndian(dib[32..]);
            long colours = used is > 0 and <= 256 ? used : bits <= 8 ? 1L << bits : 0;
            long table = colours * 4 + (header == 40 && compression == 3 ? 12 : 0);
            var offset = 14 + header + table;
            if (offset - 14 > dib.Length) continue;
            var bmp = new byte[14 + dib.Length];
            bmp[0] = (byte)'B';
            bmp[1] = (byte)'M';
            BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(2), (uint)bmp.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(10), (uint)offset);
            dib.CopyTo(bmp.AsSpan(14));
            return bmp;
        }
        return null;
    }
}
