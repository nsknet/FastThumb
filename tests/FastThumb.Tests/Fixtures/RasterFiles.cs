using System.Buffers.Binary;
using System.Text;

namespace FastThumb.Tests.Fixtures;

/// <summary>Pixel layouts of the generated TGA files.</summary>
public enum TgaKind { Colour24, Colour32, Colour16, Grey8, Mapped8 }

/// <summary>
/// Hand-written files of the formats FastThumb reads in managed code (no encoder for them in libvips), all of the test
/// picture from <see cref="ImageFiles.Rgb"/>.
/// </summary>
internal static class RasterFiles
{
    /// <summary>
    /// A TGA of the test picture. Grey and mapped files use the red band as the grey level (the mapped one through a palette of
    /// 256 greys), 16-bit keeps the top 5 bits of each band.
    /// </summary>
    public static byte[] Tga(int width = 320, int height = 200, TgaKind kind = TgaKind.Colour24, bool rle = true, bool topDown = false, bool footer = true)
    {
        var rgb = ImageFiles.Rgb(width, height);
        var pixelBytes = kind switch { TgaKind.Colour32 => 4, TgaKind.Colour24 => 3, TgaKind.Colour16 => 2, _ => 1 };
        var mapped = kind == TgaKind.Mapped8;
        var header = new byte[18];
        header[1] = (byte)(mapped ? 1 : 0);
        header[2] = (byte)((kind switch { TgaKind.Grey8 => 3, TgaKind.Mapped8 => 1, _ => 2 }) | (rle ? 8 : 0));
        if (mapped) { BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(5), 256); header[7] = 24; }
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14), (ushort)height);
        header[16] = (byte)(pixelBytes * 8);
        header[17] = (byte)((kind == TgaKind.Colour32 ? 8 : kind == TgaKind.Colour16 ? 1 : 0) | (topDown ? 0x20 : 0));

        var pixels = new byte[width * height * pixelBytes];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var row = topDown ? y : height - 1 - y;
                var s = (row * width + x) * 3;
                var d = pixels.AsSpan((y * width + x) * pixelBytes, pixelBytes);
                byte r = rgb[s], g = rgb[s + 1], b = rgb[s + 2];
                switch (kind)
                {
                    case TgaKind.Colour32: d[0] = b; d[1] = g; d[2] = r; d[3] = 255; break;
                    case TgaKind.Colour24: d[0] = b; d[1] = g; d[2] = r; break;
                    case TgaKind.Colour16: BinaryPrimitives.WriteUInt16LittleEndian(d, (ushort)(0x8000 | (r >> 3) << 10 | (g >> 3) << 5 | b >> 3)); break;
                    default: d[0] = r; break;
                }
            }

        using var output = new MemoryStream();
        output.Write(header);
        if (mapped) for (var i = 0; i < 256; i++) output.Write([(byte)i, (byte)i, (byte)i]);
        output.Write(rle ? Rle(pixels, pixelBytes) : pixels);
        if (footer)
        {
            output.Write(new byte[8]);   // no extension area, no developer directory
            output.Write("TRUEVISION-XFILE.\0"u8);
        }
        return output.ToArray();
    }

    /// <summary>TGA run-length packets: runs of 2 or more equal pixels, literal packets between them, at most 128 pixels each.</summary>
    private static byte[] Rle(byte[] pixels, int pixelBytes)
    {
        using var output = new MemoryStream();
        var count = pixels.Length / pixelBytes;
        Span<byte> Pixel(int i) => pixels.AsSpan(i * pixelBytes, pixelBytes);
        var at = 0;
        while (at < count)
        {
            var run = 1;
            while (at + run < count && run < 128 && Pixel(at + run).SequenceEqual(Pixel(at))) run++;
            if (run > 1)
            {
                output.WriteByte((byte)(0x80 | (run - 1)));
                output.Write(Pixel(at));
                at += run;
                continue;
            }
            var literal = 1;
            while (at + literal < count && literal < 128 && (at + literal + 1 >= count || !Pixel(at + literal).SequenceEqual(Pixel(at + literal + 1)))) literal++;
            output.WriteByte((byte)(literal - 1));
            output.Write(pixels.AsSpan(at * pixelBytes, literal * pixelBytes));
            at += literal;
        }
        return output.ToArray();
    }

    /// <summary>A TGA header declaring <paramref name="width"/> x <paramref name="height"/> px, followed by almost nothing.</summary>
    public static byte[] TgaHeaderOnly(int width, int height)
    {
        var data = new byte[64];
        data[2] = 10;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), (ushort)height);
        data[16] = 24;
        return data;
    }

    /// <summary>
    /// A PSD (or PSB) of the test picture with only the composite image. The channels are chosen so that every mode decodes to
    /// a known picture: CMYK stores R, G, B as its inverted C, M, Y with no black ink (so reads back as the picture), greyscale and
    /// indexed (a palette of greys) use the red band, bitmap is black where red &lt; 128. Alpha is a constant 128 channel marked as
    /// the composite's transparency by a negative layer count. <paramref name="thumbnail"/> becomes image resource 1036.
    /// </summary>
    public static byte[] Psd(int width = 320, int height = 200, int mode = 3, int depth = 8, int compression = 1, bool large = false,
        bool alpha = false, byte[]? thumbnail = null)
    {
        var rgb = ImageFiles.Rgb(width, height);
        byte[] Plane(Func<int, byte> sample) { var plane = new byte[width * height]; for (var i = 0; i < plane.Length; i++) plane[i] = sample(i); return plane; }
        var planes = new List<byte[]>();
        switch (mode)
        {
            case 3 or 4:
                for (var c = 0; c < 3; c++) { var band = c; planes.Add(Plane(i => rgb[i * 3 + band])); }
                if (mode == 4) planes.Add(Plane(_ => 255));
                break;
            default:
                planes.Add(Plane(i => rgb[i * 3]));
                break;
        }
        if (alpha) planes.Add(Plane(_ => 128));

        // the planes as stored rows
        var rowBytes = depth == 1 ? (width + 7) / 8 : width * depth / 8;
        var rows = new List<byte[]>();
        foreach (var plane in planes)
            for (var y = 0; y < height; y++)
            {
                var row = new byte[rowBytes];
                for (var x = 0; x < width; x++)
                {
                    var v = plane[y * width + x];
                    switch (depth)
                    {
                        case 1: if (v < 128) row[x >> 3] |= (byte)(0x80 >> (x & 7)); break;
                        case 8: row[x] = v; break;
                        case 16: BinaryPrimitives.WriteUInt16BigEndian(row.AsSpan(x * 2), (ushort)(v * 257)); break;
                        case 32: BinaryPrimitives.WriteSingleBigEndian(row.AsSpan(x * 4), v / 255f); break;
                    }
                }
                rows.Add(row);
            }

        using var output = new MemoryStream();
        void U16(int v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v); output.Write(b); }
        void U32(long v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, (uint)v); output.Write(b); }
        void U64(long v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, (ulong)v); output.Write(b); }
        output.Write("8BPS"u8);
        U16(large ? 2 : 1);
        output.Write(new byte[6]);
        U16(planes.Count);
        U32(height);
        U32(width);
        U16(depth);
        U16(mode);
        if (mode == 2) { U32(768); for (var c = 0; c < 3; c++) for (var i = 0; i < 256; i++) output.WriteByte((byte)i); }
        else U32(0);
        if (thumbnail != null)
        {
            var size = 28 + thumbnail.Length;
            U32(12 + size + (size & 1));
            output.Write("8BIM"u8);
            U16(1036);
            U16(0);                                   // empty name, padded
            U32(size);
            U32(1); U32(width / 2); U32(height / 2); U32((width / 2 * 24 + 31) / 32 * 4); U32(0); U32(thumbnail.Length); U16(24); U16(1);
            output.Write(thumbnail);
            if ((size & 1) != 0) output.WriteByte(0);
        }
        else U32(0);
        if (alpha)
        {
            if (large) { U64(10); U64(2); } else { U32(6); U32(2); }
            U16(0xFFFF);                              // layer count -1: the first alpha channel is the composite's transparency
        }
        else if (large) U64(0);
        else U32(0);

        U16(compression);
        switch (compression)
        {
            case 0:
                foreach (var row in rows) output.Write(row);
                break;
            case 1:
                var packed = rows.Select(PackBits).ToList();
                foreach (var row in packed) if (large) U32(row.Length); else U16(row.Length);
                foreach (var row in packed) output.Write(row);
                break;
            default:
                using (var zip = new System.IO.Compression.ZLibStream(output, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
                    foreach (var row in rows)
                    {
                        if (compression == 3)
                        {
                            // each sample as the difference from the one before it in the row
                            if (depth == 16)
                                for (var x = row.Length - 2; x >= 2; x -= 2)
                                    BinaryPrimitives.WriteUInt16BigEndian(row.AsSpan(x), (ushort)(BinaryPrimitives.ReadUInt16BigEndian(row.AsSpan(x)) - BinaryPrimitives.ReadUInt16BigEndian(row.AsSpan(x - 2))));
                            else
                                for (var x = row.Length - 1; x >= 1; x--) row[x] -= row[x - 1];
                        }
                        zip.Write(row);
                    }
                break;
        }
        return output.ToArray();
    }

    /// <summary>PackBits: runs of 3 or more equal bytes as a repeat, everything else literal, at most 128 bytes a packet.</summary>
    private static byte[] PackBits(byte[] row)
    {
        using var output = new MemoryStream();
        var at = 0;
        while (at < row.Length)
        {
            var run = 1;
            while (at + run < row.Length && run < 128 && row[at + run] == row[at]) run++;
            if (run >= 3)
            {
                output.WriteByte((byte)(257 - run));
                output.WriteByte(row[at]);
                at += run;
                continue;
            }
            var literal = 0;
            while (at + literal < row.Length && literal < 128
                && !(at + literal + 2 < row.Length && row[at + literal] == row[at + literal + 1] && row[at + literal] == row[at + literal + 2])) literal++;
            if (literal == 0) literal = 1;
            output.WriteByte((byte)(literal - 1));
            output.Write(row, at, literal);
            at += literal;
        }
        return output.ToArray();
    }

    /// <summary>A PSD header declaring <paramref name="width"/> x <paramref name="height"/> px, with empty sections and no pixels.</summary>
    public static byte[] PsdHeaderOnly(int width, int height)
    {
        var data = new byte[26 + 12 + 2 + 64];
        "8BPS"u8.CopyTo(data);
        data[5] = 1;
        data[13] = 3;
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(14), (uint)height);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(18), (uint)width);
        data[23] = 8;
        data[25] = 3;
        data[26 + 12 + 1] = 1;                        // RLE
        return data;
    }

    internal static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);
}
