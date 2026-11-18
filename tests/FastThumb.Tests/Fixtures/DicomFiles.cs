using System.Buffers.Binary;
using System.Text;

namespace FastThumb.Tests.Fixtures;

/// <summary>The DICOM layouts generated.</summary>
public enum DicomKind { Mono16Window, Mono8, Mono1, Rle16, Rgb8, Implicit16 }

/// <summary>
/// DICOM Part 10 files of the test picture written by hand. Monochrome ones store the red band (times 16 at 16 bits, with a
/// window that maps it back to 0-255).
/// </summary>
internal static class DicomFiles
{
    private const string ExplicitLittle = "1.2.840.10008.1.2.1", ImplicitLittle = "1.2.840.10008.1.2", Rle = "1.2.840.10008.1.2.5";

    public static byte[] Dicom(DicomKind kind, int width = 64, int height = 40, int? declaredRows = null)
    {
        var rgb = ImageFiles.Rgb(width, height);
        var syntax = kind switch { DicomKind.Implicit16 => ImplicitLittle, DicomKind.Rle16 => Rle, _ => ExplicitLittle };
        var explicitVr = kind != DicomKind.Implicit16;
        var o = new MemoryStream();
        o.Write(new byte[128]);
        o.Write("DICM"u8);
        Element(o, 0x0002, 0x0010, "UI", Text(syntax, '\0'), explicitVr: true);

        var colour = kind == DicomKind.Rgb8;
        var bits = kind is DicomKind.Mono8 or DicomKind.Mono1 or DicomKind.Rgb8 ? 8 : 16;
        Element(o, 0x0028, 0x0002, "US", U16(colour ? 3 : 1), explicitVr);
        Element(o, 0x0028, 0x0004, "CS", Text(colour ? "RGB" : kind == DicomKind.Mono1 ? "MONOCHROME1" : "MONOCHROME2", ' '), explicitVr);
        if (colour) Element(o, 0x0028, 0x0006, "US", U16(0), explicitVr);
        Element(o, 0x0028, 0x0010, "US", U16(declaredRows ?? height), explicitVr);
        Element(o, 0x0028, 0x0011, "US", U16(declaredRows ?? width), explicitVr);
        Element(o, 0x0028, 0x0100, "US", U16(bits), explicitVr);
        Element(o, 0x0028, 0x0101, "US", U16(bits == 16 ? 12 : 8), explicitVr);
        Element(o, 0x0028, 0x0103, "US", U16(0), explicitVr);
        if (bits == 16)
        {
            Element(o, 0x0028, 0x1050, "DS", Text("2048", ' '), explicitVr);
            Element(o, 0x0028, 0x1051, "DS", Text("4096", ' '), explicitVr);
        }

        var count = width * height;
        byte[] pixels;
        if (colour) pixels = rgb;
        else if (bits == 8) pixels = [.. Enumerable.Range(0, count).Select(i => rgb[i * 3])];
        else
        {
            pixels = new byte[count * 2];
            for (var i = 0; i < count; i++) BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(i * 2), (ushort)(rgb[i * 3] * 16 + 8));
        }
        if (kind != DicomKind.Rle16)
        {
            Element(o, 0x7FE0, 0x0010, bits == 16 ? "OW" : "OB", pixels, explicitVr);
            return o.ToArray();
        }

        // RLE: two segments (high bytes, then low bytes), each in literal runs of up to 128 bytes
        var segments = new List<byte[]>();
        foreach (var high in new[] { true, false })
        {
            var plane = Enumerable.Range(0, count).Select(i => pixels[i * 2 + (high ? 1 : 0)]).ToArray();
            var encoded = new MemoryStream();
            for (var at = 0; at < plane.Length; at += 128)
            {
                var n = Math.Min(128, plane.Length - at);
                encoded.WriteByte((byte)(n - 1));
                encoded.Write(plane, at, n);
            }
            if (encoded.Length % 2 == 1) encoded.WriteByte(0);
            segments.Add(encoded.ToArray());
        }
        var frame = new byte[64 + segments.Sum(s => s.Length)];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, 2);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), 64);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(8), (uint)(64 + segments[0].Length));
        segments[0].CopyTo(frame, 64);
        segments[1].CopyTo(frame, 64 + segments[0].Length);

        o.Write([0xE0, 0x7F, 0x10, 0x00, (byte)'O', (byte)'B', 0, 0, 0xFF, 0xFF, 0xFF, 0xFF]);
        Item(o, 0xE000, []);                                                           // empty basic offset table
        Item(o, 0xE000, frame);
        Item(o, 0xE0DD, []);
        return o.ToArray();
    }

    private static void Item(MemoryStream o, int element, byte[] body)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(b, 0xFFFE);
        BinaryPrimitives.WriteUInt16LittleEndian(b[2..], (ushort)element);
        BinaryPrimitives.WriteUInt32LittleEndian(b[4..], (uint)body.Length);
        o.Write(b);
        o.Write(body);
    }

    private static void Element(MemoryStream o, int group, int element, string vr, byte[] value, bool explicitVr)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)group);
        BinaryPrimitives.WriteUInt16LittleEndian(b[2..], (ushort)element);
        o.Write(b);
        if (!explicitVr)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)value.Length);
            o.Write(b);
        }
        else if (vr is "OB" or "OW")
        {
            o.Write(Encoding.ASCII.GetBytes(vr));
            o.Write(new byte[2]);
            BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)value.Length);
            o.Write(b);
        }
        else
        {
            o.Write(Encoding.ASCII.GetBytes(vr));
            BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)value.Length);
            o.Write(b[..2]);
        }
        o.Write(value);
    }

    private static byte[] U16(int value) { var b = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)value); return b; }

    /// <summary>A text value padded to an even length.</summary>
    private static byte[] Text(string value, char pad) => Encoding.ASCII.GetBytes(value.Length % 2 == 1 ? value + pad : value);
}
