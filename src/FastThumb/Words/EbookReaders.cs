using System.Buffers.Binary;
using System.Text;
using FastThumb.Layout;

namespace FastThumb.Words;

/// <summary>MOBI / AZW3: PalmDB container, PalmDOC-compressed text records holding HTML-like markup.</summary>
internal static class MobiReader
{
    public static IPageSource Read(byte[] data)
    {
        int U16(int at) => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at));
        uint U32(int at) => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at));

        var records = U16(76);
        var offsets = new int[records + 1];
        for (var i = 0; i < records; i++) offsets[i] = (int)U32(78 + i * 8);
        offsets[records] = data.Length;

        var header = offsets[0];
        var compression = U16(header);
        var textRecords = U16(header + 8);
        if (U16(header + 12) != 0) throw new NotSupportedException("Encrypted MOBI");
        if (compression is not (1 or 2)) throw new NotSupportedException($"MOBI compression {compression} (HUFF/CDIC) is not supported");

        var encoding = Encoding.UTF8;
        var extraFlags = 0;
        if (data.AsSpan(header + 16, 4).SequenceEqual("MOBI"u8))
        {
            var mobiLength = (int)U32(header + 20);
            if (U32(header + 28) == 1252) encoding = Encoding.GetEncoding(1252);
            if (mobiLength >= 0xE4) extraFlags = U16(header + 0xF2);
        }

        using var text = new MemoryStream();
        for (var r = 1; r <= textRecords && r < records && text.Length < MaxText; r++)   // records may overlap: the text is capped
        {
            var record = data.AsSpan(offsets[r], offsets[r + 1] - offsets[r]);
            var length = record.Length;
            // trailing entries: one backward-encoded size per set flag bit above bit 0, then the multibyte-overlap byte
            for (var bit = 15; bit >= 1 && length > 0; bit--)
            {
                if ((extraFlags & (1 << bit)) == 0) continue;
                var size = 0;
                for (var i = 0; i < 4 && length - 1 - i >= 0; i++)
                {
                    var b = record[length - 1 - i];
                    size |= (b & 0x7F) << (7 * i);
                    if ((b & 0x80) != 0) break;
                }
                length -= Math.Min(size, length);
            }
            if ((extraFlags & 1) != 0 && length > 0) length -= Math.Min((record[length - 1] & 3) + 1, length);
            record = record[..length];
            if (compression == 1) text.Write(record); else PalmDoc(record, text);
        }
        return new StaticSource(PageSetup.Default, HtmlReader.ParseBlocks(encoding.GetString(text.ToArray())));
    }

    private const int MaxText = 64 << 20, MaxRecord = 64 << 10;   // a text record holds 4 KB of text

    private static void PalmDoc(ReadOnlySpan<byte> input, MemoryStream output)
    {
        var start = output.Length;
        for (var i = 0; i < input.Length && output.Length - start < MaxRecord;)
        {
            var b = input[i++];
            if (b is 0 or (>= 9 and <= 0x7F)) output.WriteByte(b);
            else if (b <= 8) { for (var k = 0; k < b && i < input.Length; k++) output.WriteByte(input[i++]); }
            else if (b >= 0xC0) { output.WriteByte((byte)' '); output.WriteByte((byte)(b ^ 0x80)); }
            else if (i < input.Length)
            {
                var pair = ((b << 8) | input[i++]) & 0x3FFF;
                int distance = pair >> 3, count = (pair & 7) + 3;
                for (var k = 0; k < count; k++)
                {
                    var from = output.Length - distance;
                    if (from < start) break;                 // a back-reference never leaves its own record
                    output.WriteByte(output.GetBuffer()[from]);
                }
            }
        }
    }
}

/// <summary>CHM (compiled HTML help): ITSF container, LZX-compressed content section, topics are HTML files.</summary>
internal static class ChmReader
{
    private const string ContentName = "::DataSpace/Storage/MSCompressed/Content";
    private const string ControlName = "::DataSpace/Storage/MSCompressed/ControlData";
    private const string ResetName = "::DataSpace/Storage/MSCompressed/Transform/{7FC28940-9D31-11D0-9B27-00A0C91E9C7C}/InstanceData/ResetTable";
    private const int MaxHtmlBytes = 2 * 1024 * 1024;
    private const int MaxFrameInput = 0x12000;   // 32 KB of literals at 16 bits each, plus the block's tables

    public static IPageSource Read(byte[] data)
    {
        // #SYSTEM entry code 2 names the default topic: it goes first, the rest follow in directory order
        string? first = null;
        if (Extract(data, name => name == "/#SYSTEM", MaxHtmlBytes).FirstOrDefault().Data is { Length: > 8 } system)
        {
            for (var at = 4; at + 4 <= system.Length;)
            {
                int code = BinaryPrimitives.ReadUInt16LittleEndian(system.AsSpan(at)), length = BinaryPrimitives.ReadUInt16LittleEndian(system.AsSpan(at + 2));
                if (at + 4 + length > system.Length) break;
                if (code == 2) { first = "/" + Encoding.Latin1.GetString(system, at + 4, length).TrimEnd('\0').TrimStart('/'); break; }
                at += 4 + length;
            }
        }
        // only what the budget below reads is copied out
        var files = Extract(data, name => name.EndsWith(".htm", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".html", StringComparison.OrdinalIgnoreCase), MaxHtmlBytes, first);

        var blocks = new List<Block>();
        var budget = MaxHtmlBytes;
        foreach (var (name, content) in files.OrderBy(f => string.Equals(f.Name, first, StringComparison.OrdinalIgnoreCase) ? 0 : 1))
        {
            if (budget <= 0) break;
            budget -= content.Length;
            blocks.AddRange(HtmlReader.ParseBlocks(FormatDetector.DecodeHtml(content)));
        }
        if (blocks.Count == 0) throw new InvalidDataException("CHM has no HTML topics");
        return new StaticSource(PageSetup.Default, blocks);
    }

    /// <param name="maxBytes">entries are copied until this many bytes are reached (the entry that crosses it included)</param>
    /// <param name="first">copied before the others, so the budget reaches it</param>
    public static List<(string Name, byte[] Data)> Extract(byte[] data, Func<string, bool> wanted, long maxBytes = long.MaxValue, string? first = null)
    {
        if (data.Length < 0x60 || !data.AsSpan(0, 4).SequenceEqual("ITSF"u8)) throw new InvalidDataException("Not a CHM file");
        long L64(int at) => BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(at));
        int I32(int at) => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at));

        var version = I32(4);
        var directory = (int)L64(0x48);
        var contentOffset = version >= 3 ? L64(0x58) : directory + L64(0x50);
        if (!data.AsSpan(directory, 4).SequenceEqual("ITSP"u8)) throw new InvalidDataException("CHM directory not found");
        int headerLength = I32(directory + 8), chunkSize = I32(directory + 0x10), chunks = I32(directory + 0x2C);
        // the chunk walk is sized from these: each chunk must have room for its header, and all of them must be in the file
        if (headerLength < 0 || chunkSize < 0x14 || chunks < 0 || directory + (long)headerLength + (long)chunks * chunkSize > data.Length)
            throw new InvalidDataException("CHM directory is corrupt");

        var entries = new List<(string Name, int Section, long Offset, long Length)>();
        for (var c = 0; c < chunks; c++)
        {
            var chunk = directory + headerLength + c * chunkSize;
            if (chunk + chunkSize > data.Length || !data.AsSpan(chunk, 4).SequenceEqual("PMGL"u8)) continue;
            var end = chunk + chunkSize - Math.Clamp(I32(chunk + 4), 0, chunkSize - 0x14);   // free space at the end of the chunk
            var at = chunk + 0x14;
            long EncInt() { long value = 0; byte b; do { b = data[at++]; value = (value << 7) | (uint)(b & 0x7F); } while ((b & 0x80) != 0 && at < end); return value; }
            while (at < end)
            {
                var nameLength = (int)EncInt();
                if (nameLength <= 0 || at + nameLength > end) break;
                var name = Encoding.UTF8.GetString(data, at, nameLength);
                at += nameLength;
                entries.Add((name, (int)EncInt(), EncInt(), EncInt()));
            }
        }

        byte[] Section0(string name)
        {
            var e = entries.FirstOrDefault(x => x.Name == name);
            return e.Name == null || e.Length <= 0 ? [] : data.AsSpan((int)(contentOffset + e.Offset), (int)e.Length).ToArray();
        }

        byte[]? uncompressed = null;
        byte[] Section1()
        {
            if (uncompressed != null) return uncompressed;
            byte[] content = Section0(ContentName), control = Section0(ControlName), reset = Section0(ResetName);
            if (content.Length == 0 || control.Length < 0x18 || reset.Length < 0x28) return uncompressed = [];
            var lzxVersion = BinaryPrimitives.ReadInt32LittleEndian(control.AsSpan(8));
            long resetInterval = BinaryPrimitives.ReadUInt32LittleEndian(control.AsSpan(12)), windowSize = BinaryPrimitives.ReadUInt32LittleEndian(control.AsSpan(16));
            long windowsPerReset = BinaryPrimitives.ReadUInt32LittleEndian(control.AsSpan(20));
            if (lzxVersion == 2) { resetInterval *= 0x8000; windowSize *= 0x8000; }
            var blockCount = BinaryPrimitives.ReadInt32LittleEndian(reset.AsSpan(4));
            var tableOffset = BinaryPrimitives.ReadInt32LittleEndian(reset.AsSpan(12));
            var total = BinaryPrimitives.ReadInt64LittleEndian(reset.AsSpan(0x10));
            var blockLength = (int)BinaryPrimitives.ReadInt64LittleEndian(reset.AsSpan(0x20));
            // LZX frames are 32 KB; windows 2^15 to 2^21 (the decoder allocates the window from this)
            var windowBits = System.Numerics.BitOperations.Log2((uint)windowSize);
            if (blockLength != 0x8000 || windowBits is < 15 or > 21) throw new InvalidDataException("CHM: unsupported LZX parameters");
            var resetEvery = (int)Math.Max(1, resetInterval / Math.Max(1, windowSize / 2) * Math.Max(1, windowsPerReset));

            var lzx = new Lzx(windowBits);
            var output = new byte[Math.Min(total, 64L * 1024 * 1024)];
            for (var b = 0; b < blockCount && (long)b * blockLength < output.Length; b++)
            {
                var from = BinaryPrimitives.ReadInt64LittleEndian(reset.AsSpan(tableOffset + b * 8));
                var to = b + 1 < blockCount ? BinaryPrimitives.ReadInt64LittleEndian(reset.AsSpan(tableOffset + (b + 1) * 8)) : content.Length;
                if (from >= content.Length) break;
                if (b % resetEvery == 0) lzx.Reset();
                var size = (int)Math.Min(blockLength, output.Length - (long)b * blockLength);
                // a compressed 32 KB frame is never much larger: the table cannot make each frame re-read the whole section
                lzx.Decompress(content.AsSpan((int)from, (int)(Math.Min(Math.Min(to, content.Length), from + MaxFrameInput) - from)), output.AsSpan(b * blockLength, size));
            }
            return uncompressed = output;
        }

        var result = new List<(string, byte[])>();
        var remaining = maxBytes;
        foreach (var e in entries.OrderBy(x => string.Equals(x.Name, first, StringComparison.OrdinalIgnoreCase) ? 0 : 1))
        {
            if (e.Length <= 0 || !wanted(e.Name)) continue;
            if (remaining <= 0) break;
            if (e.Section == 0) result.Add((e.Name, data.AsSpan((int)(contentOffset + e.Offset), (int)e.Length).ToArray()));
            else if (e.Offset + e.Length <= Section1().Length) result.Add((e.Name, Section1().AsSpan((int)e.Offset, (int)e.Length).ToArray()));
            else continue;
            remaining -= e.Length;
        }
        return result;
    }

    /// <summary>LZX decoder (the scheme used by CHM and CAB): 32 KB frames, verbatim / aligned / uncompressed blocks.</summary>
    private sealed class Lzx
    {
        private static readonly int[] ExtraBits = new int[52], PositionBase = new int[52];
        static Lzx()
        {
            for (int i = 0, j = 0; i < 51; i += 2)
            {
                ExtraBits[i] = ExtraBits[i + 1] = j;
                if (i != 0 && j < 17) j++;
            }
            for (int i = 0, position = 0; i < 51; i++) { PositionBase[i] = position; position += 1 << ExtraBits[i]; }
        }

        private readonly byte[] _window;
        private readonly int _windowSize, _mainElements;
        private readonly byte[] _mainLengths, _lengthLengths = new byte[250 + 14], _alignedLengths = new byte[8], _preLengths = new byte[20];
        private readonly Huffman _main = new(), _length = new(), _aligned = new(), _pre = new();
        private int _position, _r0, _r1, _r2, _blockType, _blockLength, _blockRemaining;
        private bool _headerRead;

        // bit reader over the current frame
        private byte[] _input = [];
        private int _at, _bitsLeft;
        private ulong _bits;

        public Lzx(int windowBits)
        {
            _windowSize = 1 << windowBits;
            _window = new byte[_windowSize];
            var slots = windowBits switch { 20 => 42, 21 => 50, _ => windowBits << 1 };
            _mainElements = 256 + (slots << 3);
            _mainLengths = new byte[_mainElements + 14];
            Reset();
        }

        public void Reset()
        {
            _r0 = _r1 = _r2 = 1;
            _headerRead = false;
            _blockRemaining = 0;
            _blockType = 0;
            _position = 0;
            Array.Clear(_mainLengths);
            Array.Clear(_lengthLengths);
        }

        private void Ensure(int count)
        {
            while (_bitsLeft < count)
            {
                var word = _at + 1 < _input.Length ? _input[_at] | (_input[_at + 1] << 8) : 0;
                _at += 2;
                _bits = (_bits << 16) | (uint)word;
                _bitsLeft += 16;
            }
        }

        private int Read(int count)
        {
            if (count == 0) return 0;
            Ensure(count);
            _bitsLeft -= count;
            return (int)((_bits >> _bitsLeft) & ((1UL << count) - 1));
        }

        private int Symbol(Huffman table)
        {
            int code = 0;
            for (var length = 1; length <= 16; length++)
            {
                code = (code << 1) | Read(1);
                var index = code - table.First[length];
                if (index >= 0 && index < table.Count[length]) return table.Symbols[table.Offset[length] + index];
            }
            throw new InvalidDataException("LZX: bad Huffman code");
        }

        private void ReadLengths(byte[] lengths, int first, int last)
        {
            for (var i = 0; i < 20; i++) _preLengths[i] = (byte)Read(4);
            _pre.Build(_preLengths, 20);
            for (var x = first; x < last;)
            {
                var z = Symbol(_pre);
                if (z == 17) { for (var y = Read(4) + 4; y > 0 && x < last; y--) lengths[x++] = 0; }
                else if (z == 18) { for (var y = Read(5) + 20; y > 0 && x < last; y--) lengths[x++] = 0; }
                else if (z == 19)
                {
                    var y = Read(1) + 4;
                    z = lengths[x] - Symbol(_pre);
                    if (z < 0) z += 17;
                    for (; y > 0 && x < last; y--) lengths[x++] = (byte)z;
                }
                else
                {
                    z = lengths[x] - z;
                    if (z < 0) z += 17;
                    lengths[x++] = (byte)z;
                }
            }
        }

        public void Decompress(ReadOnlySpan<byte> input, Span<byte> output)
        {
            _input = input.ToArray();
            _at = 0; _bitsLeft = 0; _bits = 0;
            var togo = output.Length;

            if (!_headerRead)
            {
                if (Read(1) != 0) { Read(16); Read(16); } // Intel E8 translation size: not applied (help content is not x86 code)
                _headerRead = true;
            }

            while (togo > 0)
            {
                if (_blockRemaining == 0)
                {
                    if (_blockType == 3)
                    {
                        if ((_blockLength & 1) != 0) _at++;
                        _bitsLeft = 0; _bits = 0;
                    }
                    _blockType = Read(3);
                    var high = Read(16);
                    _blockRemaining = _blockLength = (high << 8) | Read(8);
                    switch (_blockType)
                    {
                        case 2:
                            for (var i = 0; i < 8; i++) _alignedLengths[i] = (byte)Read(3);
                            _aligned.Build(_alignedLengths, 8);
                            goto case 1;
                        case 1:
                            ReadLengths(_mainLengths, 0, 256);
                            ReadLengths(_mainLengths, 256, _mainElements);
                            _main.Build(_mainLengths, _mainElements);
                            ReadLengths(_lengthLengths, 0, 249);
                            _length.Build(_lengthLengths, 249);
                            break;
                        case 3:
                            Ensure(16);
                            if (_bitsLeft > 16) _at -= 2;
                            _bitsLeft = 0; _bits = 0;
                            _r0 = Le32(); _r1 = Le32(); _r2 = Le32();
                            break;
                        default: throw new InvalidDataException("LZX: bad block type");
                    }
                }

                var run = Math.Min(_blockRemaining, togo);
                togo -= run;
                _blockRemaining -= run;
                _position &= _windowSize - 1;

                if (_blockType == 3)
                {
                    for (var i = 0; i < run; i++) _window[(_position + i) & (_windowSize - 1)] = _at < _input.Length ? _input[_at++] : (byte)0;
                    _position += run;
                    continue;
                }

                while (run > 0)
                {
                    var element = Symbol(_main);
                    if (element < 256) { _window[_position++ & (_windowSize - 1)] = (byte)element; run--; continue; }
                    element -= 256;
                    var matchLength = element & 7;
                    if (matchLength == 7) matchLength += Symbol(_length);
                    matchLength += 2;
                    var matchOffset = element >> 3;
                    if (matchOffset > 2)
                    {
                        var extra = ExtraBits[matchOffset];
                        if (_blockType == 1)
                        {
                            matchOffset = matchOffset != 3 ? PositionBase[matchOffset] - 2 + Read(extra) : 1;
                        }
                        else
                        {
                            matchOffset = PositionBase[matchOffset] - 2;
                            if (extra > 3) { matchOffset += Read(extra - 3) << 3; matchOffset += Symbol(_aligned); }
                            else if (extra == 3) matchOffset += Symbol(_aligned);
                            else if (extra > 0) matchOffset += Read(extra);
                            else matchOffset = 1;
                        }
                        _r2 = _r1; _r1 = _r0; _r0 = matchOffset;
                    }
                    else if (matchOffset == 0) matchOffset = _r0;
                    else if (matchOffset == 1) { matchOffset = _r1; _r1 = _r0; _r0 = matchOffset; }
                    else { matchOffset = _r2; _r2 = _r0; _r0 = matchOffset; }

                    for (var i = 0; i < matchLength; i++)
                    {
                        _window[_position & (_windowSize - 1)] = _window[(_position - matchOffset) & (_windowSize - 1)];
                        _position++;
                    }
                    run -= matchLength;
                }
            }

            var start = ((_position == 0 ? _windowSize : _position) - output.Length) & (_windowSize - 1);
            for (var i = 0; i < output.Length; i++) output[i] = _window[(start + i) & (_windowSize - 1)];
        }

        private int Le32()
        {
            var value = _at + 4 <= _input.Length ? BinaryPrimitives.ReadInt32LittleEndian(_input.AsSpan(_at)) : 1;
            _at += 4;
            return value;
        }

        /// <summary>Canonical Huffman code: shorter codes first, symbols in ascending order within a length.</summary>
        private sealed class Huffman
        {
            public readonly int[] First = new int[18], Count = new int[18], Offset = new int[18];
            public int[] Symbols = [];

            public void Build(byte[] lengths, int count)
            {
                Array.Clear(Count);
                for (var i = 0; i < count; i++) Count[Math.Min(lengths[i], (byte)17)]++;
                Count[0] = 0;
                Symbols = new int[count];
                int code = 0, offset = 0;
                for (var length = 1; length <= 16; length++)
                {
                    code <<= 1;
                    First[length] = code;
                    Offset[length] = offset;
                    code += Count[length];
                    offset += Count[length];
                }
                var next = (int[])Offset.Clone();
                for (var i = 0; i < count; i++)
                    if (lengths[i] is > 0 and <= 16) Symbols[next[lengths[i]]++] = i;
            }
        }
    }
}
