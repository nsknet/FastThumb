using System.Buffers.Binary;

namespace FastThumb.Images;

/// <summary>
/// The directories (IFDs) of a TIFF structure, for camera RAW files: the IFD chain, SubIFDs and the Exif IFD, without reading any
/// image data. Accepts the variants camera makers use for the magic number (Olympus RO/SR, Panasonic U). Every offset is checked
/// against the file, and each directory is read once, so loops in the structure end.
/// </summary>
internal sealed class TiffWalker
{
    public readonly record struct Entry(ushort Type, uint Count, int ValueAt);

    private readonly byte[] _data;
    private readonly int _start;

    public bool Little { get; }

    /// <summary>Every directory found, IFD0 first.</summary>
    public List<Dictionary<ushort, Entry>> Directories { get; } = [];

    private const int MaxDirectories = 64;

    private TiffWalker(byte[] data, int start, bool little)
    {
        _data = data;
        _start = start;
        Little = little;
    }

    /// <summary>"II" or "MM", then 42 or a maker's variant.</summary>
    public static bool IsTiff(ReadOnlySpan<byte> d) =>
        d.Length >= 8 && ((d[0] == 'I' && d[1] == 'I' && BinaryPrimitives.ReadUInt16LittleEndian(d[2..]) is 42 or 0x4F52 or 0x5352 or 0x55)
            || (d[0] == 'M' && d[1] == 'M' && BinaryPrimitives.ReadUInt16BigEndian(d[2..]) is 42 or 0x4F52 or 0x5352));

    /// <summary>"II" or "MM" and 42: what libvips reads as TIFF.</summary>
    public static bool IsStandard(ReadOnlySpan<byte> d) =>
        d.Length >= 8 && ((d[0] == 'I' && d[1] == 'I' && d[2] == 42 && d[3] == 0) || (d[0] == 'M' && d[1] == 'M' && d[2] == 0 && d[3] == 42));

    /// <summary>The structure starting at <paramref name="start"/> (offsets in it are relative to that), or null if it is not TIFF.</summary>
    public static TiffWalker? Open(byte[] data, int start = 0, bool followSubDirectories = true)
    {
        if (start < 0 || start > data.Length - 8 || !IsTiff(data.AsSpan(start))) return null;
        var tiff = new TiffWalker(data, start, data[start] == 'I');
        var pending = new Queue<uint>();
        var seen = new HashSet<uint>();
        pending.Enqueue(tiff.U32(start + 4));
        while (pending.Count > 0 && tiff.Directories.Count < MaxDirectories)
        {
            Guard.Check();
            var offset = pending.Dequeue();
            if (offset == 0 || !seen.Add(offset) || offset > (uint)(data.Length - start - 2)) continue;
            var at = start + (int)offset;
            int count = tiff.U16(at);
            if ((long)at + 2 + count * 12L + 4 > data.Length) continue;
            var directory = new Dictionary<ushort, Entry>();
            for (var i = 0; i < count; i++)
            {
                var e = at + 2 + i * 12;
                var type = tiff.U16(e + 2);
                var n = tiff.U32(e + 4);
                long size = n * (long)TypeSize(type);
                var valueAt = size <= 4 ? e + 8 : start + (long)tiff.U32(e + 8);
                if (size == 0 || valueAt + size > data.Length) continue;
                directory.TryAdd(tiff.U16(e), new Entry(type, n, (int)valueAt));
            }
            tiff.Directories.Add(directory);
            pending.Enqueue(tiff.U32(at + 2 + count * 12));
            if (!followSubDirectories) continue;
            foreach (var tag in new ushort[] { 0x014A, 0x8769 })                 // SubIFDs, Exif IFD
                if (directory.TryGetValue(tag, out var sub))
                    for (var k = 0; k < Math.Min(sub.Count, 16); k++) pending.Enqueue(tiff.Value(sub, k));
        }
        return tiff;
    }

    private static int TypeSize(ushort type) => type switch
    {
        1 or 2 or 6 or 7 => 1,
        3 or 8 => 2,
        4 or 9 or 11 or 13 => 4,
        5 or 10 or 12 => 8,
        _ => 0,
    };

    /// <summary>Value <paramref name="index"/> of an integer entry (BYTE, SHORT, LONG, IFD).</summary>
    public uint Value(Entry entry, int index = 0) => entry.Type switch
    {
        1 or 7 => _data[entry.ValueAt + index],
        3 => U16(entry.ValueAt + index * 2),
        4 or 13 => U32(entry.ValueAt + index * 4),
        _ => 0,
    };

    public uint? Get(Dictionary<ushort, Entry> directory, ushort tag) =>
        directory.TryGetValue(tag, out var entry) && entry.Count > 0 ? Value(entry) : null;

    /// <summary>A file offset given in the structure, as an index into the whole file.</summary>
    public long Offset(uint value) => _start + (long)value;

    private ushort U16(int at) => Little ? BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(at)) : BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(at));

    private uint U32(int at) => Little ? BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(at)) : BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(at));
}
