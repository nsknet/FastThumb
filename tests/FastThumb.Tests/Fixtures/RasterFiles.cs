using System.Buffers.Binary;
using System.Text;

namespace FastThumb.Tests.Fixtures;

/// <summary>Pixel layouts of the generated TGA files.</summary>
public enum TgaKind { Colour24, Colour32, Colour16, Grey8, Mapped8 }

/// <summary>Where the generated EPS files keep their preview.</summary>
public enum EpsKind { None, DosTiff, Epsi8, Epsi1, Illustrator, IllustratorRle }

/// <summary>The camera RAW layouts generated.</summary>
public enum RawKind { Cr2, Nef, NefBigEndian, Dng, Orf, Rw2, Raf, Cr3, NoPreview }

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

    /// <summary>
    /// An EPS of a 320 x 200 pt box with a preview of the test picture: a TIFF in a DOS EPS, an EPSI preview (8-bit grey of the
    /// red band, or 1-bit black where red &lt; 128) or an Illustrator thumbnail (a palette of greys indexed by the red band).
    /// </summary>
    public static byte[] Eps(EpsKind preview = EpsKind.DosTiff, int width = 320, int height = 200)
    {
        var rgb = ImageFiles.Rgb(width, height);
        var text = new StringBuilder($"%!PS-Adobe-3.0 EPSF-3.0\n%%BoundingBox: 0 0 {width} {height}\n%%Title: test\n%%EndComments\n");
        void HexLines(IEnumerable<byte> bytes)
        {
            var line = new StringBuilder();
            foreach (var b in bytes)
            {
                line.Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
                if (line.Length >= 60) { text.Append('%').Append(line).Append('\n'); line.Clear(); }
            }
            if (line.Length > 0) text.Append('%').Append(line).Append('\n');
        }
        switch (preview)
        {
            case EpsKind.Epsi8 or EpsKind.Epsi1:
                {
                    var depth = preview == EpsKind.Epsi8 ? 8 : 1;
                    var rowBytes = (width * depth + 7) / 8;
                    var rows = new byte[rowBytes * height];
                    for (var y = 0; y < height; y++)
                        for (var x = 0; x < width; x++)
                        {
                            var r = rgb[(y * width + x) * 3];
                            if (depth == 8) rows[y * rowBytes + x] = (byte)(255 - r);
                            else if (r < 128) rows[y * rowBytes + x / 8] |= (byte)(0x80 >> (x & 7));
                        }
                    text.Append($"%%BeginPreview: {width} {height} {depth} {height}\n");
                    HexLines(rows);
                    text.Append("%%EndPreview\n");
                    break;
                }
            case EpsKind.Illustrator or EpsKind.IllustratorRle:
                {
                    var data = new List<byte>();
                    for (var i = 0; i < 256; i++) data.AddRange([(byte)i, (byte)i, (byte)i]);
                    var indices = Enumerable.Range(0, width * height).Select(i => rgb[i * 3]).ToArray();
                    if (preview == EpsKind.Illustrator) data.AddRange(indices);
                    else
                    {
                        data.AddRange("RLE"u8.ToArray());
                        for (var i = 0; i < indices.Length;)
                        {
                            var run = 1;
                            while (i + run < indices.Length && run < 255 && indices[i + run] == indices[i]) run++;
                            if (run >= 3) { data.AddRange([0xFD, (byte)run, indices[i]]); i += run; continue; }
                            if (indices[i] == 0xFD) data.AddRange([0xFD, 0xFD]);
                            else data.Add(indices[i]);
                            i++;
                        }
                    }
                    text.Append($"%AI7_Thumbnail: {width} {height} 8\n%%BeginData: {data.Count * 2} Hex Bytes\n");
                    HexLines(data);
                    text.Append("%%EndData\n");
                    break;
                }
        }
        text.Append($"newpath 0 0 moveto {width} {height} lineto stroke\nshowpage\n%%EOF\n");
        var postScript = Encoding.ASCII.GetBytes(text.ToString());
        if (preview != EpsKind.DosTiff) return postScript;

        byte[] tiff;
        using (var image = NetVips.Image.NewFromMemory(rgb, width, height, 3, NetVips.Enums.BandFormat.Uchar)) tiff = image.WriteToBuffer(".tif");
        var header = new byte[30];
        header[0] = 0xC5; header[1] = 0xD0; header[2] = 0xD3; header[3] = 0xC6;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 30);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)postScript.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), (uint)(30 + postScript.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), (uint)tiff.Length);
        header[28] = 0xFF; header[29] = 0xFF;          // no checksum
        return [.. header, .. postScript, .. tiff];
    }

    /// <summary>
    /// A camera RAW file reduced to its structure: the test picture as a 320 x 200 JPEG preview where that camera maker puts it,
    /// usually next to a 160 x 100 thumbnail, and a lossless-JPEG "sensor" strip that must never be taken for a preview. The
    /// orientation tag is written when <paramref name="orientation"/> is not 0. <paramref name="preview"/> replaces the JPEG.
    /// </summary>
    public static byte[] Raw(RawKind kind, int orientation = 0, byte[]? preview = null)
    {
        preview ??= ImageFiles.Jpeg();
        var thumbnail = ImageFiles.Jpeg(160, 100);
        byte[] sensor = [0xFF, 0xD8, 0xFF, 0xC4, 0x00, 0x02, 0xFF, 0xC3, 0x00, 0x0B, 0x08, 0x00, 0xC8, 0x01, 0x40, 0x01, 0x01, 0x11, 0x00,
            0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x01, 0x00, 0x00, 0x12, 0x34, 0xFF, 0xD9];
        switch (kind)
        {
            case RawKind.Raf:
                {
                    var header = new byte[100];
                    "FUJIFILMCCD-RAW 0201FF383501"u8.CopyTo(header);
                    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(84), 100);
                    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(88), (uint)preview.Length);
                    return [.. header, .. preview, .. sensor];
                }
            case RawKind.Cr3:
                {
                    var tiff = new Tiff(little: true, magic: 42);
                    var cmt1 = tiff.Directory();
                    if (orientation != 0) cmt1.Short(0x0112, orientation);
                    static byte[] Box(string type, params byte[][] content)
                    {
                        var body = content.SelectMany(c => c).ToArray();
                        var box = new byte[8 + body.Length];
                        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
                        Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
                        body.CopyTo(box, 8);
                        return box;
                    }
                    var canonUuid = Convert.FromHexString("85C0B687820F11E08111F4CE462B6A48");
                    var ftyp = Box("ftyp", "crx "u8.ToArray(), [0, 0, 0, 1], "crx isom"u8.ToArray());
                    var moov = Box("moov", Box("uuid", canonUuid, Box("CMT1", tiff.Build()), Box("THMB", new byte[16], thumbnail)));
                    return [.. ftyp, .. moov, .. Box("mdat", preview, sensor)];
                }
        }

        var magic = kind switch { RawKind.Orf => 0x4F52, RawKind.Rw2 => 0x55, _ => 42 };
        var raw = new Tiff(little: kind != RawKind.NefBigEndian, magic, cr2: kind == RawKind.Cr2);
        var ifd0 = raw.Directory();
        if (orientation != 0) ifd0.Short(0x0112, orientation);
        if (kind == RawKind.Dng) ifd0.Bytes(0xC612, 1, [1, 4, 0, 0]);
        switch (kind)
        {
            case RawKind.Cr2:
                // IFD0: the full-size JPEG as an old-style JPEG strip; IFD1: the thumbnail; IFD3: the sensor data
                ifd0.Short(0x0103, 6).Blob(0x0111, preview).Long(0x0117, preview.Length);
                raw.Directory().Blob(0x0201, thumbnail).Long(0x0202, thumbnail.Length);
                raw.Directory().Short(0x0103, 6).Blob(0x0111, sensor).Long(0x0117, sensor.Length);
                break;
            case RawKind.Nef or RawKind.NefBigEndian or RawKind.Dng:
                // IFD0: the thumbnail; SubIFDs: the preview and the sensor data
                ifd0.Blob(0x0201, thumbnail).Long(0x0202, thumbnail.Length);
                ifd0.SubDirectories(0x014A,
                    raw.Detached().Blob(0x0201, preview).Long(0x0202, preview.Length),
                    raw.Detached().Short(0x0103, 7).Blob(0x0111, sensor).Long(0x0117, sensor.Length));
                break;
            case RawKind.Orf:
                // IFD0: the thumbnail; the preview is in the maker note, which no tag here points to
                ifd0.Blob(0x0201, thumbnail).Long(0x0202, thumbnail.Length);
                raw.Trailer([.. "OLYMPUS\0II"u8.ToArray(), 3, 0, .. preview]);
                break;
            case RawKind.Rw2:
                ifd0.Embedded(0x002E, preview);
                break;
            case RawKind.NoPreview:
                ifd0.Short(0x0103, 7).Blob(0x0111, sensor).Long(0x0117, sensor.Length);
                break;
        }
        return raw.Build();
    }

    /// <summary>A minimal TIFF writer: directories of SHORT/LONG/UNDEFINED entries, with blobs and sub-directories after them.</summary>
    private sealed class Tiff(bool little, int magic, bool cr2 = false)
    {
        public sealed class Ifd(Tiff owner)
        {
            internal readonly List<(ushort Tag, ushort Type, uint Count, Func<uint> Value)> Entries = [];
            internal uint Offset;

            public Ifd Short(ushort tag, int value) { Entries.Add((tag, 3, 1, () => (uint)value)); return this; }

            public Ifd Long(ushort tag, int value) { Entries.Add((tag, 4, 1, () => (uint)value)); return this; }

            public Ifd Bytes(ushort tag, ushort type, byte[] inline)
            {
                Entries.Add((tag, type, (uint)inline.Length, () => owner.Little ? BinaryPrimitives.ReadUInt32LittleEndian(inline) : BinaryPrimitives.ReadUInt32BigEndian(inline)));
                return this;
            }

            public Ifd Blob(ushort tag, byte[] blob) { var at = owner.Place(blob); Entries.Add((tag, 4, 1, () => at.Offset)); return this; }

            public Ifd Embedded(ushort tag, byte[] blob) { var at = owner.Place(blob); Entries.Add((tag, 7, (uint)blob.Length, () => at.Offset)); return this; }

            public Ifd SubDirectories(ushort tag, params Ifd[] children)
            {
                var list = owner.Place(new byte[children.Length * 4]);
                list.Fill = data => { for (var i = 0; i < children.Length; i++) owner.Write32(data, (int)list.Offset + i * 4, children[i].Offset); };
                Entries.Add((tag, 13, (uint)children.Length, () => children.Length == 1 ? children[0].Offset : list.Offset));
                return this;
            }
        }

        private sealed class Placed(byte[] bytes)
        {
            public byte[] Bytes { get; } = bytes;
            public uint Offset;
            public Action<byte[]>? Fill;
        }

        private bool Little => little;

        private readonly List<Ifd> _chain = [];
        private readonly List<Ifd> _detached = [];
        private readonly List<Placed> _blobs = [];

        public Ifd Directory() { var ifd = new Ifd(this); _chain.Add(ifd); return ifd; }

        public Ifd Detached() { var ifd = new Ifd(this); _detached.Add(ifd); return ifd; }

        public void Trailer(byte[] bytes) => Place(bytes);

        private Placed Place(byte[] bytes) { var placed = new Placed(bytes); _blobs.Add(placed); return placed; }

        private void Write16(byte[] d, int at, int v) { if (little) BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(at), (ushort)v); else BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(at), (ushort)v); }

        private void Write32(byte[] d, int at, uint v) { if (little) BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(at), v); else BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(at), v); }

        public byte[] Build()
        {
            // layout: header (16 bytes), every directory, then every blob
            var at = 16u;
            foreach (var ifd in _chain.Concat(_detached)) { ifd.Offset = at; at += 2 + 12 * (uint)ifd.Entries.Count + 4; }
            foreach (var blob in _blobs) { blob.Offset = at; at += (uint)blob.Bytes.Length; }
            var data = new byte[at];
            data[0] = data[1] = (byte)(little ? 'I' : 'M');
            Write16(data, 2, magic);
            Write32(data, 4, _chain.Count > 0 ? _chain[0].Offset : 0);
            if (cr2) { data[8] = (byte)'C'; data[9] = (byte)'R'; data[10] = 2; }
            var all = _chain.Concat(_detached).ToList();
            foreach (var ifd in all)
            {
                var o = (int)ifd.Offset;
                Write16(data, o, ifd.Entries.Count);
                var k = 0;
                foreach (var (tag, type, count, value) in ifd.Entries.OrderBy(e => e.Tag))
                {
                    var e = o + 2 + 12 * k++;
                    Write16(data, e, tag);
                    Write16(data, e + 2, type);
                    Write32(data, e + 4, count);
                    if (type == 3 && count == 1) Write16(data, e + 8, (int)value());
                    else Write32(data, e + 8, value());
                }
                var index = _chain.IndexOf(ifd);
                Write32(data, o + 2 + 12 * ifd.Entries.Count, index >= 0 && index + 1 < _chain.Count ? _chain[index + 1].Offset : 0);
            }
            foreach (var blob in _blobs)
            {
                blob.Bytes.CopyTo(data, blob.Offset);
                blob.Fill?.Invoke(data);
            }
            return data;
        }
    }

    /// <summary>A JPEG whose frame header declares <paramref name="width"/> x <paramref name="height"/> px, its data unchanged.</summary>
    public static byte[] JpegDeclaring(int width, int height)
    {
        var jpeg = ImageFiles.Jpeg();
        var sof = jpeg.AsSpan().IndexOf([(byte)0xFF, (byte)0xC0]);
        BinaryPrimitives.WriteUInt16BigEndian(jpeg.AsSpan(sof + 5), (ushort)height);
        BinaryPrimitives.WriteUInt16BigEndian(jpeg.AsSpan(sof + 7), (ushort)width);
        return jpeg;
    }

    internal static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);
}
