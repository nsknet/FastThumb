using System.Buffers.Binary;
using System.IO.Compression;

namespace FastThumb.Images;

/// <summary>
/// Photoshop PSD and PSB (large document): the merged composite that Photoshop stores after the layers, so no layer is
/// composited here. Bitmap, greyscale, duotone, indexed, RGB, CMYK and Lab at 1, 8 or 16 bits; raw, PackBits or ZIP.
/// What cannot be read that way (32-bit, multichannel) falls back to the JPEG thumbnail in the image resources.
/// </summary>
internal static class PsdDecoder
{
    private const int HeaderSize = 26;

    public static bool Claims(ReadOnlySpan<byte> d) =>
        d.Length >= 6 && d[0] == '8' && d[1] == 'B' && d[2] == 'P' && d[3] == 'S' && d[4] == 0 && d[5] is 1 or 2;

    /// <summary>The sections of the file located, nothing decoded.</summary>
    private readonly record struct Layout(int Width, int Height, int Channels, int Depth, int Mode, bool Large,
        int Palette, int Resources, int ResourcesLength, bool MergedAlpha, int ImageData);

    public static (int Width, int Height) Size(byte[] data)
    {
        var d = data.AsSpan();
        if (d.Length < HeaderSize || !Claims(d)) throw new InvalidDocumentException("The PSD header is not valid.");
        return ((int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(d[18..])), (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(d[14..])));
    }

    private static Layout Locate(byte[] data)
    {
        var d = data.AsSpan();
        var (width, height) = Size(data);
        var large = d[5] == 2;
        int channels = BinaryPrimitives.ReadUInt16BigEndian(d[12..]), depth = BinaryPrimitives.ReadUInt16BigEndian(d[22..]), mode = BinaryPrimitives.ReadUInt16BigEndian(d[24..]);
        if (channels is < 1 or > 56) throw new InvalidDocumentException($"The PSD declares {channels} channels.");

        var at = HeaderSize;
        var paletteLength = Length32(d, ref at);
        var palette = at;
        at = Skip(d, at, paletteLength);
        var resourcesLength = Length32(d, ref at);
        var resources = at;
        at = Skip(d, at, resourcesLength);

        // layer and mask information: only the layer count is read, whose sign tells whether the composite has transparency
        var layersLength = large ? Length64(d, ref at) : Length32(d, ref at);
        var layers = at;
        at = Skip(d, at, layersLength);
        var mergedAlpha = false;
        if (layersLength >= (large ? 10 : 6))
        {
            var info = layers;
            var infoLength = large ? Length64(d, ref info) : Length32(d, ref info);
            if (infoLength >= 2) mergedAlpha = BinaryPrimitives.ReadInt16BigEndian(d[info..]) < 0;
        }
        return new(width, height, channels, depth, mode, large, paletteLength >= 768 ? palette : -1, resources, resourcesLength, mergedAlpha, at);
    }

    public static NetVips.Image Decode(byte[] data, DecodeRequest request)
    {
        var psd = Locate(data);
        request.Check(psd.Width, psd.Height);
        var composite = psd.Depth is 1 or 8 or 16 && psd.Mode switch
        {
            0 => psd.Depth == 1,
            1 or 8 => true,                                   // greyscale, duotone (shown as its first ink, grey)
            2 => psd.Depth == 8 && psd.Palette >= 0,
            3 or 9 => psd.Channels >= 3,
            4 => psd.Channels >= 4,
            _ => false,                                       // multichannel
        };
        if (composite) return Composite(data, psd);
        return Thumbnail(data, psd) ?? throw new UnsupportedFormatException(
            $"Photoshop colour mode {psd.Mode} at {psd.Depth} bits has no composite this library reads, and the file has no thumbnail.");
    }

    private static NetVips.Image Composite(byte[] data, Layout psd)
    {
        int width = psd.Width, height = psd.Height;
        var colour = psd.Mode is 3 or 4 or 9 or 2;
        var colourChannels = psd.Mode switch { 3 or 9 => 3, 4 => 4, _ => 1 };
        var alpha = psd.MergedAlpha && psd.Channels > colourChannels;
        var read = colourChannels + (alpha ? 1 : 0);
        var bands = (colour ? 3 : 1) + (alpha ? 1 : 0);
        var rowBytes = psd.Depth == 1 ? (width + 7) / 8 : width * (psd.Depth / 8);

        // the channels needed, planar, 8 bits a sample (16-bit keeps its high byte)
        var planes = new byte[read][];
        for (var c = 0; c < read; c++) planes[c] = new byte[(long)width * height];
        var d = data.AsSpan();
        var at = psd.ImageData;
        var compression = BinaryPrimitives.ReadUInt16BigEndian(d[at..]);
        at += 2;
        var row = new byte[rowBytes];
        switch (compression)
        {
            case 0:
                if (at + (long)read * height * rowBytes > d.Length) throw new InvalidDocumentException("The PSD image data runs past the end of the file.");
                for (var c = 0; c < read; c++)
                    for (var y = 0; y < height; y++)
                    {
                        if ((y & 63) == 0) Guard.Check();
                        Store(d.Slice(at + (c * height + y) * rowBytes, rowBytes), psd.Depth, planes[c].AsSpan(y * width, width));
                    }
                break;
            case 1:
                {
                    // a byte count for every row of every channel, then the rows
                    var countSize = psd.Large ? 4 : 2;
                    var counts = at;
                    long next = at + (long)psd.Channels * height * countSize;
                    for (var c = 0; c < read; c++)
                        for (var y = 0; y < height; y++)
                        {
                            if ((y & 63) == 0) Guard.Check();
                            var index = counts + (c * height + y) * countSize;
                            var length = countSize == 4 ? (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(d[index..])) : BinaryPrimitives.ReadUInt16BigEndian(d[index..]);
                            if (next + length > d.Length) throw new InvalidDocumentException("A PSD row runs past the end of the file.");
                            PackBits(d.Slice((int)next, length), row);
                            Store(row, psd.Depth, planes[c].AsSpan(y * width, width));
                            next += length;
                        }
                    break;
                }
            case 2 or 3:
                {
                    // ZIP, without or with prediction (each sample stored as the difference from the one before it in its row)
                    using var zip = Guard.Limit(new ZLibStream(new MemoryStream(data, at, data.Length - at, false), CompressionMode.Decompress),
                        (long)psd.Channels * height * rowBytes, "PSD image data");
                    for (var c = 0; c < read; c++)
                        for (var y = 0; y < height; y++)
                        {
                            if ((y & 63) == 0) Guard.Check();
                            zip.ReadExactly(row);
                            if (compression == 3) Unpredict(row, psd.Depth);
                            Store(row, psd.Depth, planes[c].AsSpan(y * width, width));
                        }
                    break;
                }
            default:
                throw new UnsupportedFormatException($"PSD compression {compression} is not known.");
        }

        var pixels = new byte[(long)width * height * bands];
        var count = width * height;
        var palette = psd.Palette;
        for (var i = 0; i < count; i++)
        {
            if ((i & 0xFFFF) == 0) Guard.Check();
            var o = i * bands;
            switch (psd.Mode)
            {
                case 0 or 1 or 8: pixels[o] = planes[0][i]; break;
                case 2:
                    var p = planes[0][i];
                    pixels[o] = data[palette + p]; pixels[o + 1] = data[palette + 256 + p]; pixels[o + 2] = data[palette + 512 + p];
                    break;
                case 3:
                    pixels[o] = planes[0][i]; pixels[o + 1] = planes[1][i]; pixels[o + 2] = planes[2][i];
                    break;
                case 4:
                    // stored as 255 - ink; a plain conversion without a colour profile
                    int k = planes[3][i];
                    pixels[o] = (byte)(planes[0][i] * k / 255); pixels[o + 1] = (byte)(planes[1][i] * k / 255); pixels[o + 2] = (byte)(planes[2][i] * k / 255);
                    break;
                case 9:
                    Lab(planes[0][i], planes[1][i], planes[2][i], pixels.AsSpan(o, 3));
                    break;
            }
            if (alpha) pixels[o + bands - 1] = planes[colourChannels][i];
        }
        return ManagedImages.FromPixels(pixels, width, height, bands);
    }

    /// <summary>One row of samples as 8-bit values: 1-bit is black where set, 16-bit keeps its high byte.</summary>
    private static void Store(ReadOnlySpan<byte> row, int depth, Span<byte> target)
    {
        switch (depth)
        {
            case 8: row[..target.Length].CopyTo(target); break;
            case 16: for (var x = 0; x < target.Length; x++) target[x] = row[x * 2]; break;
            default: for (var x = 0; x < target.Length; x++) target[x] = (row[x >> 3] & (0x80 >> (x & 7))) != 0 ? (byte)0 : (byte)255; break;
        }
    }

    /// <summary>PackBits: n + 1 literal bytes for n in 0..127, the next byte 1 - n times for n in -127..-1. A short row stays zero.</summary>
    private static void PackBits(ReadOnlySpan<byte> input, Span<byte> output)
    {
        output.Clear();
        int i = 0, o = 0;
        while (i < input.Length && o < output.Length)
        {
            var n = (sbyte)input[i++];
            if (n >= 0)
            {
                var take = Math.Min(Math.Min(n + 1, input.Length - i), output.Length - o);
                input.Slice(i, take).CopyTo(output[o..]);
                i += n + 1;
                o += n + 1;
            }
            else if (n != -128)
            {
                if (i >= input.Length) break;
                var take = Math.Min(1 - n, output.Length - o);
                output.Slice(o, take).Fill(input[i++]);
                o += 1 - n;
            }
        }
    }

    /// <summary>ZIP with prediction: each sample was stored as the difference from the previous one in the row.</summary>
    private static void Unpredict(Span<byte> row, int depth)
    {
        if (depth == 16)
            for (var x = 2; x + 1 < row.Length; x += 2)
                BinaryPrimitives.WriteUInt16BigEndian(row[x..], (ushort)(BinaryPrimitives.ReadUInt16BigEndian(row[x..]) + BinaryPrimitives.ReadUInt16BigEndian(row[(x - 2)..])));
        else
            for (var x = 1; x < row.Length; x++) row[x] += row[x - 1];
    }

    /// <summary>CIE L*a*b* (D50, as Photoshop stores it) to sRGB.</summary>
    private static void Lab(byte l, byte a, byte b, Span<byte> rgb)
    {
        var fy = (l * 100.0 / 255 + 16) / 116;
        var fx = fy + (a - 128) / 500.0;
        var fz = fy - (b - 128) / 200.0;
        static double F(double t) => t > 6.0 / 29 ? t * t * t : 3 * (6.0 / 29) * (6.0 / 29) * (t - 4.0 / 29);
        double x = 0.9642 * F(fx), y = F(fy), z = 0.8249 * F(fz);
        // Bradford-adapted D50 XYZ to linear sRGB
        var r = 3.1338561 * x - 1.6168667 * y - 0.4906146 * z;
        var g = -0.9787684 * x + 1.9161415 * y + 0.0334540 * z;
        var bl = 0.0719453 * x - 0.2289914 * y + 1.4052427 * z;
        static byte Gamma(double c)
        {
            c = Math.Clamp(c, 0, 1);
            return (byte)Math.Round(255 * (c <= 0.0031308 ? 12.92 * c : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055));
        }
        rgb[0] = Gamma(r);
        rgb[1] = Gamma(g);
        rgb[2] = Gamma(bl);
    }

    /// <summary>The JPEG thumbnail of image resource 1036 (RGB) or, from Photoshop 4, 1033 (BGR), or null.</summary>
    private static NetVips.Image? Thumbnail(byte[] data, Layout psd)
    {
        var d = data.AsSpan();
        var end = psd.Resources + psd.ResourcesLength;
        var at = psd.Resources;
        while (at + 12 <= end)
        {
            Guard.Check();
            if (!d.Slice(at, 4).SequenceEqual("8BIM"u8)) break;
            var id = BinaryPrimitives.ReadUInt16BigEndian(d[(at + 4)..]);
            var name = d[at + 6];
            at += 6 + ((name + 2) & ~1);                       // the Pascal name with its length byte, padded to even
            var size = Length32(d, ref at);
            if ((long)at + size > end) break;
            if (id is 1036 or 1033 && size > 28)
            {
                var jpeg = d.Slice(at + 28, size - 28).ToArray();
                var image = NetVips.Image.NewFromBuffer(jpeg);
                if (id == 1036) return image;
                using (image)
                {
                    using var r = image[2];
                    using var g = image[1];
                    using var b = image[0];
                    return r.Bandjoin(g, b);
                }
            }
            at = Skip(d, at, (size + 1) & ~1);
        }
        return null;
    }

    private static int Length32(ReadOnlySpan<byte> d, ref int at)
    {
        var length = BinaryPrimitives.ReadUInt32BigEndian(d[at..]);
        at += 4;
        if (length > (uint)(d.Length - at)) throw new InvalidDocumentException("A PSD section runs past the end of the file.");
        return (int)length;
    }

    private static int Length64(ReadOnlySpan<byte> d, ref int at)
    {
        var length = BinaryPrimitives.ReadUInt64BigEndian(d[at..]);
        at += 8;
        if (length > (ulong)(d.Length - at)) throw new InvalidDocumentException("A PSB section runs past the end of the file.");
        return (int)length;
    }

    private static int Skip(ReadOnlySpan<byte> d, int at, int length) =>
        length <= d.Length - at ? at + length : throw new InvalidDocumentException("A PSD section runs past the end of the file.");
}
