using System.Buffers.Binary;

namespace FastThumb.Images;

/// <summary>
/// Camera RAW files, by the JPEG preview the camera stores in them: developing the sensor data would need a RAW converter, and
/// the preview is what the camera itself shows. The largest baseline or progressive JPEG is taken, from where the format says
/// it is (TIFF tags of CR2, NEF, ARW, DNG, PEF, ORF, RW2; the RAF header) and from the JPEGs found in the file (maker notes,
/// the full-size JPEG of a CR3). The orientation is the RAW file's, which an embedded preview often does not carry.
/// </summary>
internal static class RawPreview
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cr2", ".cr3", ".crw", ".nef", ".nrw", ".arw", ".srf", ".sr2", ".dng", ".orf", ".rw2", ".raf", ".pef", ".srw", ".erf", ".3fr",
        ".kdc", ".dcr", ".mef", ".mos", ".iiq", ".rwl", ".x3f",
    };

    public static bool Claims(ReadOnlySpan<byte> d, string? extension)
    {
        if (d.Length < 16) return false;
        if (d.StartsWith("FUJIFILMCCD-RAW"u8)) return true;
        if (d[4] == 'f' && d[5] == 't' && d[6] == 'y' && d[7] == 'p' && d.Slice(8, 4).SequenceEqual("crx "u8)) return true;   // CR3
        if (!TiffWalker.IsTiff(d)) return false;
        // Olympus and Panasonic change the magic number; a plain TIFF is a RAW when named as one, a CR2 or a DNG
        if (BinaryPrimitives.ReadUInt16LittleEndian(d[2..]) != 42 && BinaryPrimitives.ReadUInt16BigEndian(d[2..]) != 42) return true;
        if (extension != null && Extensions.Contains(extension)) return true;
        if (d[8] == 'C' && d[9] == 'R') return true;                                                                     // CR2
        return IsDng(d);
    }

    /// <summary>IFD0 has a DNGVersion tag (when IFD0 is within <paramref name="d"/>).</summary>
    private static bool IsDng(ReadOnlySpan<byte> d)
    {
        var little = d[0] == 'I';
        var offset = little ? BinaryPrimitives.ReadUInt32LittleEndian(d[4..]) : BinaryPrimitives.ReadUInt32BigEndian(d[4..]);
        if (offset > (uint)(d.Length - 2)) return false;
        int count = little ? BinaryPrimitives.ReadUInt16LittleEndian(d[(int)offset..]) : BinaryPrimitives.ReadUInt16BigEndian(d[(int)offset..]);
        for (var i = 0; i < count; i++)
        {
            var e = (int)offset + 2 + i * 12;
            if (e + 2 > d.Length) return false;
            var tag = little ? BinaryPrimitives.ReadUInt16LittleEndian(d[e..]) : BinaryPrimitives.ReadUInt16BigEndian(d[e..]);
            if (tag == 0xC612) return true;
        }
        return false;
    }

    /// <summary>A JPEG inside the file and the size its frame header declares.</summary>
    private readonly record struct Candidate(int Start, int Length, int Width, int Height);

    public static (int Width, int Height) Size(byte[] data)
    {
        var (best, orientation) = Find(data);
        return orientation is >= 5 and <= 8 ? (best.Height, best.Width) : (best.Width, best.Height);
    }

    public static NetVips.Image Decode(byte[] data, DecodeRequest request)
    {
        var (best, orientation) = Find(data);
        request.Check(best.Width, best.Height);
        var preview = ManagedImages.Embedded(data.AsSpan(best.Start, best.Length).ToArray(), request);
        if (orientation is not (>= 1 and <= 8)) return preview;
        using (preview) return preview.Mutate(m => m.Set("orientation", orientation));
    }

    private static (Candidate Best, int Orientation) Find(byte[] data)
    {
        var candidates = new List<Candidate>();
        var orientation = 0;
        var d = data.AsSpan();
        if (d.StartsWith("FUJIFILMCCD-RAW"u8) && d.Length >= 92)
            Add(data, candidates, BinaryPrimitives.ReadUInt32BigEndian(d[84..]), BinaryPrimitives.ReadUInt32BigEndian(d[88..]));
        else if (TiffWalker.Open(data) is { } tiff)
        {
            orientation = Orientation(tiff);
            foreach (var directory in tiff.Directories)
            {
                // JPEGInterchangeFormat / Length
                if (tiff.Get(directory, 0x0201) is { } start && tiff.Get(directory, 0x0202) is { } length)
                    Add(data, candidates, tiff.Offset(start), length);
                // a single strip compressed as JPEG (old-style 6, 7, or lossy DNG 34892)
                if (tiff.Get(directory, 0x0103) is 6 or 7 or 34892 && directory.TryGetValue(0x0111, out var strips) && strips.Count == 1
                    && tiff.Get(directory, 0x0117) is { } stripLength)
                    Add(data, candidates, tiff.Offset(tiff.Value(strips)), stripLength);
                // Panasonic JpgFromRaw
                if (directory.TryGetValue(0x002E, out var embedded) && embedded.Type == 7)
                    Add(data, candidates, embedded.ValueAt, embedded.Count);
            }
        }
        else if (d.IndexOf("CMT1"u8) is var cmt and >= 4 && TiffWalker.Open(data, cmt + 4, followSubDirectories: false) is { } cr3)
            orientation = Orientation(cr3);

        Scan(data, candidates);
        if (candidates.Count == 0) throw new UnsupportedFormatException("The RAW file has no JPEG preview this library can show.");
        var best = candidates.MaxBy(c => (long)c.Width * c.Height);
        return (best, orientation);
    }

    private static int Orientation(TiffWalker tiff) => tiff.Directories.Count > 0 ? (int)(tiff.Get(tiff.Directories[0], 0x0112) ?? 0) : 0;

    /// <summary>A JPEG where the structure says, if it is one (a baseline or progressive frame).</summary>
    private static void Add(byte[] data, List<Candidate> candidates, long start, long length)
    {
        if (start <= 0 || length < 4 || start >= data.Length) return;
        length = Math.Min(length, data.Length - start);
        if (Frame(data.AsSpan((int)start, (int)length)) is { } size && !candidates.Exists(c => c.Start == start))
            candidates.Add(new((int)start, (int)length, size.Width, size.Height));
    }

    /// <summary>
    /// Every JPEG in the file that has a whole header (tables, a frame, a scan) and an end: what is stored in maker notes or in a
    /// media box rather than in a tag. Bounded: at most a few hundred start markers are looked at.
    /// </summary>
    private static void Scan(byte[] data, List<Candidate> candidates)
    {
        var d = data.AsSpan();
        var at = 0;
        for (var tries = 0; tries < 256; tries++)
        {
            Guard.Check();
            var next = d[at..].IndexOf([(byte)0xFF, (byte)0xD8, (byte)0xFF]);
            if (next < 0) return;
            var start = at + next;
            at = start + 3;
            if (candidates.Exists(c => start >= c.Start && start < c.Start + c.Length)) continue;   // inside one found already
            if (Frame(d[start..], out var scan) is not { } size) continue;
            var end = d[(start + scan)..].IndexOf([(byte)0xFF, (byte)0xD9]);
            if (end < 0) continue;
            var length = scan + end + 2;
            candidates.Add(new(start, length, size.Width, size.Height));
            at = start + length;
            if (at >= d.Length) return;
        }
    }

    private static (int Width, int Height)? Frame(ReadOnlySpan<byte> jpeg) => Frame(jpeg, out _);

    /// <summary>
    /// The size of a baseline (SOF0), extended (SOF1) or progressive (SOF2) JPEG, from its markers up to the first scan: null for
    /// anything else, lossless JPEG (the sensor data of many RAW files) included. <paramref name="scan"/> is where the scan data starts.
    /// </summary>
    private static (int Width, int Height)? Frame(ReadOnlySpan<byte> jpeg, out int scan)
    {
        scan = 0;
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return null;
        var at = 2;
        (int, int)? size = null;
        var tables = false;
        while (at + 4 <= jpeg.Length)
        {
            if (jpeg[at] != 0xFF) return null;
            var marker = jpeg[at + 1];
            if (marker == 0xFF) { at++; continue; }
            int length = BinaryPrimitives.ReadUInt16BigEndian(jpeg[(at + 2)..]);
            if (length < 2 || at + 2 + length > jpeg.Length) return null;
            switch (marker)
            {
                case 0xC0 or 0xC1 or 0xC2:
                    if (length < 8) return null;
                    int height = BinaryPrimitives.ReadUInt16BigEndian(jpeg[(at + 5)..]), width = BinaryPrimitives.ReadUInt16BigEndian(jpeg[(at + 7)..]);
                    if (width == 0 || height == 0 || jpeg[at + 9] is not (1 or 3)) return null;
                    size = (width, height);
                    break;
                case 0xC3 or (>= 0xC5 and <= 0xC7) or (>= 0xC9 and <= 0xCB) or (>= 0xCD and <= 0xCF):
                    return null;                                                  // lossless, hierarchical or arithmetic-coded
                case 0xC4:
                    tables = true;
                    break;
                case 0xDA:
                    scan = at + 2 + length;
                    return tables ? size : null;
            }
            at += 2 + length;
        }
        return null;
    }
}
