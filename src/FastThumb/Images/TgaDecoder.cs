using System.Buffers.Binary;

namespace FastThumb.Images;

/// <summary>
/// Truevision TGA: colour-mapped, true-colour and greyscale, plain or run-length encoded, 8 to 32 bits per pixel.
/// The format has no signature, so it is told by its extension or the TGA 2.0 footer, and the header is checked strictly.
/// </summary>
internal static class TgaDecoder
{
    private const int HeaderSize = 18;
    private static ReadOnlySpan<byte> Footer => "TRUEVISION-XFILE.\0"u8;

    public static bool Claims(ReadOnlySpan<byte> d, string? extension)
    {
        if (d.Length >= HeaderSize + Footer.Length && d[^Footer.Length..].SequenceEqual(Footer) && Valid(d)) return true;
        // named as TGA: claimed even when the header is wrong, so a damaged file is refused instead of laid out as text;
        // bytes of another image format still win over the name
        return (extension ?? "").ToLowerInvariant() is ".tga" or ".icb" or ".vda" or ".vst" && !FormatDetector.IsImage(d, null);
    }

    private static bool Valid(ReadOnlySpan<byte> d)
    {
        if (d.Length < HeaderSize) return false;
        var mapped = d[1];
        var type = d[2];
        var entryBits = d[7];
        var bits = d[16];
        if (mapped > 1 || (d[17] & 0xC0) != 0) return false;
        if (BinaryPrimitives.ReadUInt16LittleEndian(d[12..]) == 0 || BinaryPrimitives.ReadUInt16LittleEndian(d[14..]) == 0) return false;
        if (mapped == 1 && entryBits is not (15 or 16 or 24 or 32)) return false;
        return (type & ~8) switch
        {
            1 => mapped == 1 && bits is 8 or 16,
            2 => bits is 15 or 16 or 24 or 32,
            3 => bits is 8 or 16,
            _ => false,
        };
    }

    public static (int Width, int Height) Size(byte[] data)
    {
        if (!Valid(data)) throw new InvalidDocumentException("The TGA header is not valid.");
        return (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(12)), BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(14)));
    }

    public static NetVips.Image Decode(byte[] data, DecodeRequest request)
    {
        var (width, height) = Size(data);
        request.Check(width, height);
        var type = data[2];
        var bits = data[16];
        var descriptor = data[17];
        var pixelBytes = (bits + 7) / 8;
        var greyscale = (type & ~8) == 3;

        // the colour map, as RGBA
        var offset = HeaderSize + data[0];
        byte[]? palette = null;
        var first = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(3));
        if (data[1] == 1)
        {
            int count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(5)), entryBytes = (data[7] + 7) / 8;
            palette = new byte[count * 4];
            for (var i = 0; i < count; i++) Colour(data.AsSpan(offset + i * entryBytes, entryBytes), data[7], palette.AsSpan(i * 4, 4));
            offset += count * entryBytes;
        }

        // every pixel as stored (run-length packets expanded), in file order
        var count32 = (long)width * height;
        var stored = new byte[count32 * pixelBytes];
        if ((type & 8) == 0) data.AsSpan(offset, stored.Length).CopyTo(stored);
        else Unpack(data.AsSpan(offset), stored, pixelBytes);

        // grey + alpha is 16 bits; colour has alpha when the descriptor gives it bits and the pixel (or palette entry) has room for it.
        // An alpha band that is all zero is dropped: many writers leave it unset, and the picture is meant to be opaque.
        var colourBits = palette != null ? data[7] : bits;
        var alpha = greyscale ? bits == 16 : (descriptor & 0x0F) != 0 && colourBits is 16 or 32;
        var bands = greyscale ? (alpha ? 2 : 1) : alpha ? 4 : 3;
        var pixels = new byte[count32 * bands];
        Span<byte> rgba = stackalloc byte[4];
        var rightToLeft = (descriptor & 0x10) != 0;
        var topDown = (descriptor & 0x20) != 0;
        var anyAlpha = false;
        for (var y = 0; y < height; y++)
        {
            if ((y & 63) == 0) Guard.Check();
            var row = topDown ? y : height - 1 - y;
            for (var x = 0; x < width; x++)
            {
                var source = stored.AsSpan((y * width + x) * pixelBytes, pixelBytes);   // at most MaxPixels x 4: within int
                var column = rightToLeft ? width - 1 - x : x;
                var target = pixels.AsSpan((row * width + column) * bands, bands);
                if (greyscale)
                {
                    target[0] = source[0];
                    if (alpha) { target[1] = source[1]; anyAlpha |= source[1] != 0; }
                    continue;
                }
                if (palette != null)
                {
                    var index = (pixelBytes == 1 ? source[0] : BinaryPrimitives.ReadUInt16LittleEndian(source)) - first;
                    if ((uint)index < (uint)(palette.Length / 4)) palette.AsSpan(index * 4, 4).CopyTo(rgba);
                    else rgba.Clear();
                }
                else Colour(source, bits, rgba);
                rgba[..bands].CopyTo(target);
                if (alpha) anyAlpha |= rgba[3] != 0;
            }
        }
        if (alpha && !anyAlpha)
        {
            // drop the empty alpha band
            var opaque = new byte[count32 * (bands - 1)];
            for (long i = 0, j = 0; i < pixels.Length; i += bands, j += bands - 1) pixels.AsSpan((int)i, bands - 1).CopyTo(opaque.AsSpan((int)j));
            return ManagedImages.FromPixels(opaque, width, height, bands - 1);
        }
        return ManagedImages.FromPixels(pixels, width, height, bands);
    }

    /// <summary>One true-colour value (BGR order, 15/16-bit packed 5-5-5 with an attribute bit) as RGBA.</summary>
    private static void Colour(ReadOnlySpan<byte> s, int bits, Span<byte> rgba)
    {
        switch (bits)
        {
            case 32: rgba[0] = s[2]; rgba[1] = s[1]; rgba[2] = s[0]; rgba[3] = s[3]; break;
            case 24: rgba[0] = s[2]; rgba[1] = s[1]; rgba[2] = s[0]; rgba[3] = 255; break;
            default:
                int v = BinaryPrimitives.ReadUInt16LittleEndian(s), r = (v >> 10) & 31, g = (v >> 5) & 31, b = v & 31;
                rgba[0] = (byte)((r << 3) | (r >> 2));
                rgba[1] = (byte)((g << 3) | (g >> 2));
                rgba[2] = (byte)((b << 3) | (b >> 2));
                // the attribute bit set means transparent, as the files in use have it (and as Pillow reads them)
                rgba[3] = bits == 16 && (v & 0x8000) != 0 ? (byte)0 : (byte)255;
                break;
        }
    }

    /// <summary>
    /// Run-length packets: a header byte, then one pixel repeated (high bit set) or that many literal pixels, (low 7 bits) + 1
    /// times. Packets may cross rows. Output is bounded by the picture, input by the file: a short file leaves the rest black.
    /// </summary>
    private static void Unpack(ReadOnlySpan<byte> input, Span<byte> output, int pixelBytes)
    {
        int i = 0, o = 0;
        while (o < output.Length && i < input.Length)
        {
            if ((o & 0xFFFF) < pixelBytes * 128) Guard.Check();
            var header = input[i++];
            var count = ((header & 0x7F) + 1) * pixelBytes;
            count = Math.Min(count, output.Length - o);
            if ((header & 0x80) != 0)
            {
                if (i + pixelBytes > input.Length) break;
                var pixel = input.Slice(i, pixelBytes);
                for (var k = 0; k < count; k += pixelBytes) pixel.CopyTo(output[(o + k)..]);
                i += pixelBytes;
            }
            else
            {
                var take = Math.Min(count, input.Length - i);
                input.Slice(i, take).CopyTo(output[o..]);
                i += count;
            }
            o += count;
        }
    }
}
