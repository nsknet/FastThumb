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

    internal static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);
}
