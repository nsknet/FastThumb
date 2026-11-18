using System.Buffers.Binary;

namespace FastThumb.Tests.Fixtures;

/// <summary>The BMP layouts generated.</summary>
public enum BmpKind { Rgb24, TopDown24, Rgb32, Rgb565, Rle8, Os2Core24 }

/// <summary>BMP files of the test picture, written by hand (libvips has no BMP encoder).</summary>
internal static class BmpFiles
{
    /// <summary>
    /// The test picture as a BMP. <see cref="BmpKind.Rle8"/> is a palette of 256 greys indexed by the red band;
    /// <see cref="BmpKind.Rgb565"/> keeps 5, 6 and 5 bits.
    /// </summary>
    public static byte[] Bmp(BmpKind kind, int width = 64, int height = 40)
    {
        var rgb = ImageFiles.Rgb(width, height);
        var core = kind == BmpKind.Os2Core24;
        var header = core ? 12 : 40;
        var bits = kind switch { BmpKind.Rgb32 => 32, BmpKind.Rgb565 => 16, BmpKind.Rle8 => 8, _ => 24 };
        var table = kind switch { BmpKind.Rle8 => 1024, BmpKind.Rgb565 => 12, _ => 0 };
        var pixels = new MemoryStream();
        var rowBytes = (width * bits + 31) / 32 * 4;
        for (var r = 0; r < height; r++)
        {
            var y = kind == BmpKind.TopDown24 ? r : height - 1 - r;   // bottom row first, except top-down
            var row = new byte[rowBytes];
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 3;
                byte red = rgb[i], green = rgb[i + 1], blue = rgb[i + 2];
                switch (bits)
                {
                    case 24: row[x * 3] = blue; row[x * 3 + 1] = green; row[x * 3 + 2] = red; break;
                    case 32: row[x * 4] = blue; row[x * 4 + 1] = green; row[x * 4 + 2] = red; row[x * 4 + 3] = 255; break;
                    case 16: BinaryPrimitives.WriteUInt16LittleEndian(row.AsSpan(x * 2), (ushort)((red >> 3) << 11 | (green >> 2) << 5 | blue >> 3)); break;
                }
            }
            if (bits == 8)
            {
                // RLE8: every pixel as a run of one, then end of line
                for (var x = 0; x < width; x++) { pixels.WriteByte(1); pixels.WriteByte(rgb[(y * width + x) * 3]); }
                pixels.WriteByte(0); pixels.WriteByte(0);
            }
            else pixels.Write(row);
        }
        if (bits == 8) { pixels.WriteByte(0); pixels.WriteByte(1); }
        var body = pixels.ToArray();

        var offset = 14 + header + table;
        var file = new byte[offset + body.Length];
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(2), (uint)file.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(10), (uint)offset);
        var h = file.AsSpan(14);
        BinaryPrimitives.WriteUInt32LittleEndian(h, (uint)header);
        if (core)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(h[4..], (ushort)width);
            BinaryPrimitives.WriteUInt16LittleEndian(h[6..], (ushort)height);
            BinaryPrimitives.WriteUInt16LittleEndian(h[8..], 1);
            BinaryPrimitives.WriteUInt16LittleEndian(h[10..], 24);
        }
        else
        {
            BinaryPrimitives.WriteInt32LittleEndian(h[4..], width);
            BinaryPrimitives.WriteInt32LittleEndian(h[8..], kind == BmpKind.TopDown24 ? -height : height);
            BinaryPrimitives.WriteUInt16LittleEndian(h[12..], 1);
            BinaryPrimitives.WriteUInt16LittleEndian(h[14..], (ushort)bits);
            BinaryPrimitives.WriteUInt32LittleEndian(h[16..], kind switch { BmpKind.Rle8 => 1u, BmpKind.Rgb565 => 3u, _ => 0u });
            BinaryPrimitives.WriteUInt32LittleEndian(h[20..], (uint)body.Length);
            if (bits == 8) BinaryPrimitives.WriteUInt32LittleEndian(h[32..], 256);
        }
        var t = file.AsSpan(14 + header);
        if (kind == BmpKind.Rle8)
            for (var g = 0; g < 256; g++) { t[g * 4] = t[g * 4 + 1] = t[g * 4 + 2] = (byte)g; }
        if (kind == BmpKind.Rgb565)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(t, 0xF800);
            BinaryPrimitives.WriteUInt32LittleEndian(t[4..], 0x07E0);
            BinaryPrimitives.WriteUInt32LittleEndian(t[8..], 0x001F);
        }
        body.CopyTo(file, offset);
        return file;
    }
}
