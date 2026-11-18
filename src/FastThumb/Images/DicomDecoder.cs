using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FastThumb.Images;

/// <summary>
/// DICOM (Part 10 files: a 128-byte preamble, "DICM", the file meta group, then the data set). The first frame of the
/// pixel data is shown: uncompressed (implicit or explicit VR, little or big endian), RLE Lossless, or a JPEG baseline /
/// extended or JPEG 2000 frame, which libvips or the managed JPEG 2000 decoder reads. Monochrome pixels go through the
/// modality rescale and the window (the file's first window, else the frame's own range) to 8 bits; MONOCHROME1 is
/// inverted. RGB and YBR_FULL colour are shown as RGB. Not read: JPEG lossless and JPEG-LS, palette colour, deflated data
/// sets, overlays, and files without the "DICM" preamble.
/// </summary>
internal static class DicomDecoder
{
    private const int MaxDepth = 16;

    public static bool Claims(ReadOnlySpan<byte> d) => d.Length >= 132 && d.Slice(128, 4).SequenceEqual("DICM"u8);

    /// <summary>What is read from the data set: the image pixel module and the values that turn samples into grey levels.</summary>
    private sealed class Header
    {
        public string Syntax = "1.2.840.10008.1.2.1";
        public int Rows, Columns, Samples = 1, BitsAllocated, BitsStored, Planar, Frames = 1;
        public bool Signed;
        public string Photometric = "MONOCHROME2";
        public double? Center, Width;
        public double Slope = 1, Intercept;
        public int PixelAt = -1;
        public long PixelLength;

        public bool Implicit => Syntax == "1.2.840.10008.1.2";
        public bool BigEndian => Syntax == "1.2.840.10008.1.2.2";
        public bool Encapsulated => PixelLength == uint.MaxValue;
    }

    public static (int Width, int Height) Size(byte[] data)
    {
        var header = Read(data);
        return (header.Columns, header.Rows);
    }

    public static NetVips.Image Decode(byte[] data, DecodeRequest request)
    {
        var h = Read(data);
        if (h.PixelAt < 0) throw new UnsupportedFormatException("The DICOM file has no pixel data.");
        if (h.Rows <= 0 || h.Columns <= 0) throw new InvalidDocumentException("The DICOM file declares no image size.");
        request.Check(h.Columns, h.Rows);
        if (h.Photometric is "PALETTE COLOR" or "YBR_FULL_422" && !h.Encapsulated)
            throw new UnsupportedFormatException($"DICOM {h.Photometric} pixels are not read here.");

        if (!h.Encapsulated) return Pixels(data.AsSpan(h.PixelAt, (int)h.PixelLength), h, h.BigEndian, planar: h.Planar == 1);
        var frame = FirstFrame(data, h);
        switch (h.Syntax)
        {
            case "1.2.840.10008.1.2.5":
                return Pixels(Rle(frame, h), h, bigEndian: false, planar: true);
            case "1.2.840.10008.1.2.4.50" or "1.2.840.10008.1.2.4.51":
                return ManagedImages.Embedded(frame, request);
            case "1.2.840.10008.1.2.4.90" or "1.2.840.10008.1.2.4.91":
                return Jpeg2000.Jpeg2000Decoder.Decode(frame, request);
            default:
                throw new UnsupportedFormatException($"DICOM transfer syntax {h.Syntax} is not read here.");
        }
    }

    /// <summary>
    /// The file meta group (always explicit VR little endian), then the data set in its transfer syntax, up to the pixel data.
    /// Sequences are skipped; only top-level values are kept.
    /// </summary>
    private static Header Read(byte[] data)
    {
        if (!Claims(data)) throw new InvalidDocumentException("The DICOM preamble is missing.");
        var header = new Header();
        var at = 132;
        var meta = true;
        while (at + 8 <= data.Length)
        {
            Guard.Check();
            var group = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at));
            if (meta && group != 2)
            {
                meta = false;
                if (header.Syntax == "1.2.840.10008.1.2.1.99") throw new UnsupportedFormatException("Deflated DICOM data sets are not read here.");
            }
            var bigEndian = !meta && header.BigEndian;
            var (tagGroup, element, vr, length, value) = Element(data, ref at, explicitVr: meta || !header.Implicit, bigEndian);
            if (tagGroup == 0x7FE0 && element == 0x0010)
            {
                header.PixelAt = value;
                header.PixelLength = length;
                if (length != uint.MaxValue && value + length > data.Length) throw new InvalidDocumentException("The DICOM pixel data runs past the end of the file.");
                return header;
            }
            if (length == uint.MaxValue || vr == "SQ")
            {
                at = length == uint.MaxValue ? SkipUndefined(data, value, meta || !header.Implicit, bigEndian, 0) : value + (int)length;
                continue;
            }
            if (tagGroup is 0x0002 or 0x0028) Keep(header, tagGroup, element, data.AsSpan(value, (int)Math.Min(length, 256)).ToArray(), bigEndian);
            at = value + (int)length;
        }
        return header;
    }

    /// <summary>One element header; <c>value</c> is where its value starts. Lengths are checked against the file.</summary>
    private static (int Group, int Element, string? Vr, long Length, int Value) Element(byte[] data, ref int at, bool explicitVr, bool bigEndian)
    {
        var d = data.AsSpan();
        Need(data, at, 8);
        int group = U16(d[at..], bigEndian), element = U16(d[(at + 2)..], bigEndian);
        string? vr = null;
        long length;
        if (group == 0xFFFE || !explicitVr)
        {
            // items and delimiters have no VR in any syntax
            length = U32(d[(at + 4)..], bigEndian);
            at += 8;
        }
        else
        {
            vr = Encoding.ASCII.GetString(d.Slice(at + 4, 2));
            if (vr is "OB" or "OD" or "OF" or "OL" or "OV" or "OW" or "SQ" or "SV" or "UC" or "UN" or "UR" or "UT" or "UV")
            {
                Need(data, at, 12);
                length = U32(d[(at + 8)..], bigEndian);
                at += 12;
            }
            else
            {
                length = U16(d[(at + 6)..], bigEndian);
                at += 8;
            }
        }
        if (length != uint.MaxValue && length > data.Length - at) throw new InvalidDocumentException("A DICOM element runs past the end of the file.");
        return (group, element, vr, length, at);
    }

    /// <summary>Past a sequence or item of undefined length: items until the sequence delimiter, elements until the item delimiter.</summary>
    private static int SkipUndefined(byte[] data, int at, bool explicitVr, bool bigEndian, int depth)
    {
        if (depth > MaxDepth) throw new InvalidDocumentException("DICOM sequences are nested too deeply.");
        while (at + 8 <= data.Length)
        {
            Guard.Check();
            var (group, element, _, length, value) = Element(data, ref at, explicitVr, bigEndian);
            if (group == 0xFFFE && element is 0xE0DD or 0xE00D) return value;                // sequence or item delimiter
            at = length == uint.MaxValue ? SkipUndefined(data, value, explicitVr, bigEndian, depth + 1) : value + (int)length;
        }
        throw new InvalidDocumentException("A DICOM sequence has no end.");
    }

    private static void Keep(Header h, int group, int element, byte[] value, bool bigEndian)
    {
        string Text() => Encoding.ASCII.GetString(value).TrimEnd('\0', ' ').Trim();
        int Us() => value.Length >= 2 ? U16(value, bigEndian) : 0;
        double? Ds()
        {
            var first = Text().Split('\\')[0];
            return double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;
        }
        switch ((group << 16) | element)
        {
            case 0x00020010: h.Syntax = Text(); break;
            case 0x00280002: h.Samples = Us(); break;
            case 0x00280004: h.Photometric = Text().ToUpperInvariant(); break;
            case 0x00280006: h.Planar = Us(); break;
            case 0x00280008: h.Frames = (int)Math.Clamp(Ds() ?? 1, 1, int.MaxValue); break;
            case 0x00280010: h.Rows = Us(); break;
            case 0x00280011: h.Columns = Us(); break;
            case 0x00280100: h.BitsAllocated = Us(); break;
            case 0x00280101: h.BitsStored = Us(); break;
            case 0x00280103: h.Signed = Us() == 1; break;
            case 0x00281050: h.Center = Ds(); break;
            case 0x00281051: h.Width = Ds(); break;
            case 0x00281052: h.Intercept = Ds() ?? 0; break;
            case 0x00281053: h.Slope = Ds() is { } s && s != 0 ? s : 1; break;
        }
    }

    /// <summary>
    /// The first frame of encapsulated pixel data: the fragments that the basic offset table gives it, or, without a table, all
    /// fragments for a single-frame image and the first fragment otherwise.
    /// </summary>
    private static byte[] FirstFrame(byte[] data, Header h)
    {
        var fragments = new List<(int At, int Length)>();
        var at = h.PixelAt;
        while (at + 8 <= data.Length)
        {
            Guard.Check();
            var (group, element, _, length, value) = Element(data, ref at, explicitVr: false, bigEndian: false);
            if (group != 0xFFFE || element == 0xE0DD) break;
            if (length == uint.MaxValue) throw new InvalidDocumentException("A DICOM pixel fragment has no length.");
            fragments.Add((value, (int)length));
            at = value + (int)length;
        }
        if (fragments.Count < 2) throw new InvalidDocumentException("The DICOM pixel data has no fragment.");
        var table = fragments[0];
        var frames = fragments.Skip(1).ToList();
        IEnumerable<(int At, int Length)> first;
        if (table.Length >= 8)
        {
            // offsets from the first fragment's item tag: the first frame is everything before the second offset
            var second = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(table.At + 4));
            var origin = frames[0].At - 8;
            first = frames.Where(f => f.At - 8 - origin < second);
        }
        else first = h.Frames == 1 ? frames : frames.Take(1);
        using var output = new MemoryStream();
        foreach (var (start, length) in first) output.Write(data, start, length);
        return output.ToArray();
    }

    /// <summary>
    /// RLE Lossless: a 64-byte header (segment count and offsets), then one PackBits segment a byte plane, most significant
    /// byte first, colour planes in order. Laid out again as little-endian samples, colour by plane.
    /// </summary>
    private static byte[] Rle(byte[] frame, Header h)
    {
        if (frame.Length < 64) throw new InvalidDocumentException("The DICOM RLE header is cut short.");
        var segments = (int)BinaryPrimitives.ReadUInt32LittleEndian(frame);
        var bytesPerSample = Math.Max(1, h.BitsAllocated / 8);
        if (segments is < 1 or > 15 || segments != bytesPerSample * h.Samples) throw new InvalidDocumentException($"The DICOM RLE frame has {segments} segments.");
        var pixels = h.Rows * h.Columns;
        var output = new byte[(long)pixels * bytesPerSample * h.Samples];
        var plane = new byte[pixels];
        for (var s = 0; s < segments; s++)
        {
            var start = (int)BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(4 + s * 4));
            var end = s + 1 < segments ? (int)BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(8 + s * 4)) : frame.Length;
            if (start < 64 || end > frame.Length || end < start) throw new InvalidDocumentException("A DICOM RLE segment runs past its frame.");
            PsdDecoder.PackBits(frame.AsSpan(start, end - start), plane);
            // segment s is byte (bytesPerSample - 1 - s % bytesPerSample) of sample plane s / bytesPerSample
            int sample = s / bytesPerSample, byteIndex = bytesPerSample - 1 - s % bytesPerSample;
            for (var i = 0; i < pixels; i++) output[((long)sample * pixels + i) * bytesPerSample + byteIndex] = plane[i];
        }
        return output;
    }

    /// <summary>Uncompressed samples (8 or 16 bits) to 8-bit grey (rescaled and windowed) or RGB.</summary>
    private static NetVips.Image Pixels(ReadOnlySpan<byte> raw, Header h, bool bigEndian, bool planar)
    {
        int width = h.Columns, height = h.Rows, count = width * height;
        if (h.BitsAllocated is not (8 or 16)) throw new UnsupportedFormatException($"DICOM pixels of {h.BitsAllocated} bits are not read here.");
        var bytes = h.BitsAllocated / 8;
        if (raw.Length < (long)count * bytes * h.Samples) throw new InvalidDocumentException("The DICOM pixel data is cut short.");

        if (h.Samples == 3)
        {
            if (bytes != 1) throw new UnsupportedFormatException("16-bit DICOM colour is not read here.");
            var rgb = new byte[(long)count * 3];
            var ycc = h.Photometric.StartsWith("YBR", StringComparison.Ordinal);
            for (var i = 0; i < count; i++)
            {
                if ((i & 0xFFFF) == 0) Guard.Check();
                int a = planar ? raw[i] : raw[i * 3], b = planar ? raw[count + i] : raw[i * 3 + 1], c = planar ? raw[2 * count + i] : raw[i * 3 + 2];
                if (ycc)
                {
                    // YBR_FULL: full-range BT.601
                    double y = a, cb = b - 128, cr = c - 128;
                    (a, b, c) = (Clamp(y + 1.402 * cr), Clamp(y - 0.344136 * cb - 0.714136 * cr), Clamp(y + 1.772 * cb));
                }
                rgb[i * 3] = (byte)a; rgb[i * 3 + 1] = (byte)b; rgb[i * 3 + 2] = (byte)c;
            }
            return ManagedImages.FromPixels(rgb, width, height, 3);
        }
        if (h.Samples != 1) throw new UnsupportedFormatException($"DICOM images of {h.Samples} samples a pixel are not read here.");

        // stored bits, sign-extended when signed, then the modality rescale
        var stored = h.BitsStored is > 0 and <= 16 ? h.BitsStored : h.BitsAllocated;
        var mask = (1 << stored) - 1;
        var values = new double[count];
        double min = double.MaxValue, max = double.MinValue;
        for (var i = 0; i < count; i++)
        {
            if ((i & 0xFFFF) == 0) Guard.Check();
            int v = bytes == 1 ? raw[i] : U16(raw[(i * 2)..], bigEndian);
            v &= mask;
            if (h.Signed && (v & (1 << (stored - 1))) != 0) v -= 1 << stored;
            var x = v * h.Slope + h.Intercept;
            values[i] = x;
            if (x < min) min = x;
            if (x > max) max = x;
        }
        double center, windowWidth;
        if (h.Center is { } c0 && h.Width is { } w0 && w0 >= 1) (center, windowWidth) = (c0, w0);
        else (center, windowWidth) = ((max + min) / 2 + 0.5, Math.Max(1, max - min + 1));
        var invert = h.Photometric == "MONOCHROME1";
        var grey = new byte[count];
        for (var i = 0; i < count; i++)
        {
            // the linear window function of the standard
            var y = ((values[i] - (center - 0.5)) / (windowWidth - 1 == 0 ? 1 : windowWidth - 1) + 0.5) * 255;
            var level = (byte)Clamp(y);
            grey[i] = invert ? (byte)(255 - level) : level;
        }
        return ManagedImages.FromPixels(grey, width, height, 1);
    }

    private static int Clamp(double v) => (int)Math.Clamp(Math.Round(v), 0, 255);

    private static int U16(ReadOnlySpan<byte> d, bool bigEndian) => bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(d) : BinaryPrimitives.ReadUInt16LittleEndian(d);

    private static long U32(ReadOnlySpan<byte> d, bool bigEndian) => bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(d) : BinaryPrimitives.ReadUInt32LittleEndian(d);

    private static void Need(byte[] data, int at, int count)
    {
        if (at < 0 || at + (long)count > data.Length) throw new InvalidDocumentException("A DICOM element is cut short.");
    }
}
