using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using NetVips;

namespace FastThumb.Tests.Fixtures;

/// <summary>Raster images drawn with libvips, plus hand-crafted PNG headers.</summary>
internal static class ImageFiles
{
    /// <summary>A gradient with a block in the corner, so the picture is never blank. 3 bands, sRGB.</summary>
    private static Image Picture(int width, int height)
    {
        using var xy = Image.Xyz(width, height);
        using var gx = xy[0] * (255.0 / Math.Max(1, width - 1));
        using var gy = xy[1] * (255.0 / Math.Max(1, height - 1));
        using var blue = Image.Black(width, height) + 128;
        using var rgb = gx.Bandjoin(gy, blue).Cast(Enums.BandFormat.Uchar);
        using var white = (Image.Black(Math.Max(1, width / 4), Math.Max(1, height / 4), bands: 3) + 255).Cast(Enums.BandFormat.Uchar);
        using var marked = rgb.Insert(white, 0, 0);
        return marked.Copy(interpretation: Enums.Interpretation.Srgb);
    }

    public static byte[] Png(int width = 320, int height = 200) { using var image = Picture(width, height); return image.WriteToBuffer(".png"); }

    public static byte[] Jpeg(int width = 320, int height = 200) { using var image = Picture(width, height); return image.WriteToBuffer(".jpg"); }

    public static byte[] WebP(int width = 320, int height = 200) { using var image = Picture(width, height); return image.WriteToBuffer(".webp"); }

    /// <summary>
    /// A valid PNG signature + IHDR declaring <paramref name="width"/> x <paramref name="height"/> RGB pixels (correct CRCs),
    /// followed by a tiny IDAT and IEND: a few dozen bytes claiming an enormous picture.
    /// </summary>
    public static byte[] PngHeaderOnly(int width, int height)
    {
        using var output = new MemoryStream();
        output.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;   // bit depth
        ihdr[9] = 2;   // colour type RGB
        Chunk(output, "IHDR", ihdr);
        using (var compressed = new MemoryStream())
        {
            using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) z.Write(new byte[64]);
            Chunk(output, "IDAT", compressed.ToArray());
        }
        Chunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void Chunk(Stream output, string type, byte[] data)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, data.Length);
        output.Write(buffer);
        var typeAndData = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        output.Write(typeAndData);
        BinaryPrimitives.WriteUInt32BigEndian(buffer, Crc32(typeAndData));
        output.Write(buffer);
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }

    /// <summary>Lowest and highest pixel value over every band of an encoded image: equal for a blank picture.</summary>
    public static (double Min, double Max) Range(byte[] encoded)
    {
        using var image = Image.NewFromBuffer(encoded);
        return (image.Min(), image.Max());
    }

    /// <summary>The size libvips reads from an encoded image's header.</summary>
    public static (int Width, int Height) Size(byte[] encoded)
    {
        using var image = Image.NewFromBuffer(encoded);
        return (image.Width, image.Height);
    }
}
