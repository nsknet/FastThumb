using System.Buffers.Binary;
using System.Text;

namespace FastThumb;

/// <summary>Compound File Binary container: just enough to pull named streams out of a .doc.</summary>
internal sealed class CompoundFile
{
    private const uint EndOfChain = 0xFFFFFFFE;
    private readonly byte[] _file;
    private readonly int _sectorSize, _miniSectorSize, _miniCutoff;
    private readonly uint[] _fat, _miniFat;
    private readonly Dictionary<string, (uint Start, int Size)> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly byte[] _miniStream = [];

    public CompoundFile(byte[] file)
    {
        _file = file;
        if (file.Length < 512 || BinaryPrimitives.ReadUInt64LittleEndian(file) != 0xE11AB1A1E011CFD0)
            throw new InvalidDataException("Not a compound file (.doc)");
        // the header fields size every allocation below, so only the values the format allows are accepted
        var sectorShift = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(30));
        var miniSectorShift = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(32));
        if (sectorShift is not (9 or 12) || miniSectorShift != 6) throw new InvalidDataException("Compound file: bad sector size");
        _sectorSize = 1 << sectorShift;
        _miniSectorSize = 1 << miniSectorShift;
        var sectorCount = file.Length / _sectorSize;
        var fatSectors = U32(44);
        if (fatSectors > sectorCount) throw new InvalidDataException("Compound file: bad FAT size");
        var firstDirectory = U32(48);
        _miniCutoff = (int)Math.Min(U32(56), int.MaxValue);
        if (_miniCutoff != 4096) throw new InvalidDataException("Compound file: bad mini stream cutoff");
        uint firstMiniFat = U32(60), firstDifat = U32(68);
        if (U32(72) > sectorCount) throw new InvalidDataException("Compound file: bad DIFAT size");

        // DIFAT: 109 entries in the header, the rest chained through DIFAT sectors
        var difat = new List<uint>((int)fatSectors);
        for (var i = 0; i < 109 && difat.Count < fatSectors; i++) difat.Add(U32(76 + i * 4));
        var seen = new bool[sectorCount + 1];
        for (var sector = firstDifat; difat.Count < fatSectors && sector < EndOfChain;)
        {
            var at = Offset(sector);
            if (at + _sectorSize > file.Length) break;
            Visit(seen, sector);
            for (var i = 0; i < _sectorSize / 4 - 1 && difat.Count < fatSectors; i++) difat.Add(U32((int)at + i * 4));
            sector = U32((int)at + _sectorSize - 4);
        }

        var perSector = _sectorSize / 4;
        _fat = new uint[difat.Count * perSector];
        for (var i = 0; i < difat.Count; i++)
        {
            var at = Offset(difat[i]);
            if (at + _sectorSize > file.Length) break;
            for (var j = 0; j < perSector; j++) _fat[i * perSector + j] = U32((int)at + j * 4);
        }

        var directory = ReadChain(firstDirectory, int.MaxValue);
        (uint Start, int Size) root = default;
        for (var at = 0; at + 128 <= directory.Length; at += 128)
        {
            var type = directory[at + 66];
            if (type is not (2 or 5)) continue;
            var nameLength = Math.Clamp(BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(at + 64)) - 2, 0, 62);
            var name = Encoding.Unicode.GetString(directory, at, nameLength);
            var entry = (BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(at + 116)), (int)BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(at + 120)));
            var sizeOk = entry.Item2 >= 0 && entry.Item2 <= file.Length;
            if (type == 5) root = sizeOk ? entry : throw new InvalidDataException("Compound file: bad stream size");
            else if (sizeOk) _entries.TryAdd(name, entry);   // a stream claiming more than the file holds is left out
        }

        var miniFatBytes = ReadChain(firstMiniFat, int.MaxValue);
        _miniFat = new uint[miniFatBytes.Length / 4];
        for (var i = 0; i < _miniFat.Length; i++) _miniFat[i] = BinaryPrimitives.ReadUInt32LittleEndian(miniFatBytes.AsSpan(i * 4));
        if (root.Size > 0) _miniStream = ReadChain(root.Start, root.Size);
    }

    private uint U32(int at) => BinaryPrimitives.ReadUInt32LittleEndian(_file.AsSpan(at));
    private long Offset(uint sector) => (sector + 1L) * _sectorSize;

    /// <summary>Marks a sector of a chain walk; a sector reached twice is a loop.</summary>
    private static void Visit(bool[] seen, uint sector)
    {
        if (sector >= seen.Length) return;
        if (seen[sector]) throw new InvalidDataException("Compound file: sector chain loops");
        seen[sector] = true;
    }

    private byte[] ReadChain(uint start, int size)
    {
        using var output = new MemoryStream();
        var seen = new bool[_file.Length / _sectorSize + 1];
        for (var sector = start; sector < EndOfChain && sector < _fat.Length; sector = _fat[sector])
        {
            var at = Offset(sector);
            if (at >= _file.Length) break;
            Visit(seen, sector);
            output.Write(_file, (int)at, (int)Math.Min(_sectorSize, _file.Length - at));
            if (output.Length >= size) break;
        }
        var data = output.ToArray();
        return size < data.Length ? data[..size] : data;
    }

    public bool Has(string name) => _entries.ContainsKey(name);

    public byte[]? Stream(string name)
    {
        if (!_entries.TryGetValue(name, out var entry)) return null;
        if (entry.Size >= _miniCutoff) return ReadChain(entry.Start, entry.Size);
        var data = new byte[entry.Size];
        var written = 0;
        var seen = new bool[_miniFat.Length];
        for (var sector = entry.Start; sector < EndOfChain && sector < _miniFat.Length && written < data.Length; sector = _miniFat[sector])
        {
            Visit(seen, sector);
            var at = (long)sector * _miniSectorSize;
            var count = (int)Math.Min(_miniSectorSize, Math.Min(data.Length - written, _miniStream.Length - at));
            if (count <= 0) break;
            Array.Copy(_miniStream, at, data, written, count);
            written += count;
        }
        return data;
    }
}
