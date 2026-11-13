using System.Numerics;

namespace FastThumb.Images.Jpeg2000;

/// <summary>
/// One tile, from its packets to the samples of each component at a reduced resolution (ITU-T T.800 Annex B, E, F). Every
/// packet header is read, since packets of the resolutions left out sit between the ones needed; only the code-blocks of the
/// resolutions kept go through tier 1 and the inverse wavelet. A tile whose data ends early keeps what it has.
/// </summary>
internal sealed class TileDecoder
{
    /// <summary>Code-blocks one image may declare across its tiles (each costs a few dozen bytes of state).</summary>
    public const int MaxBlocks = 1 << 22;

    /// <summary>Packets one tile may declare.</summary>
    private const long MaxPackets = 1L << 24;

    public sealed class Budget { public int Blocks; }

    private sealed class Band
    {
        public int Kind;                   // 0 LL, 1 HL, 2 LH, 3 HH
        public int X0, Y0, X1, Y1;
        public int BlockWidth, BlockHeight;    // exponents
        public int Step;                   // index of its quantization step
        public int Level;                  // decompositions from the tile-component to this band
        public float[]? Coefficients;
        public int Width => X1 - X0;
        public int Height => Y1 - Y0;
    }

    private sealed class Resolution
    {
        public int X0, Y0, X1, Y1;
        public int PrecinctWidth, PrecinctHeight;      // exponents
        public int Across, Down;
        public Band[] Bands = [];
        public Precinct?[] Precincts = [];
    }

    private sealed class Segment
    {
        public int MaxPasses, Passes;
        public readonly List<(int Start, int Length)> Chunks = [];
    }

    private sealed class Block
    {
        public int X0, Y0, X1, Y1;
        public bool Included;
        public int Lblock = 3;
        public int ZeroPlanes;
        public int Passes;
        public List<Segment>? Segments;
    }

    private sealed class BandPrecinct
    {
        public required Band Band;
        public int Across, Down;
        public Block[] Blocks = [];
        public TagTree? Inclusion, ZeroPlanes;
    }

    private sealed class Precinct { public BandPrecinct[] Bands = []; }

    private sealed class TileComponent
    {
        public int X0, Y0, X1, Y1;
        public Resolution[] Resolutions = [];
        public int Keep;                   // highest resolution decoded
        public required CodingStyle Style;
    }

    private readonly Codestream _stream;
    private readonly TileParameters _parameters;
    private readonly byte[] _data;
    private readonly int _start, _end;
    private readonly Budget _budget;
    private readonly TileComponent[] _components;
    private readonly int _x0, _y0, _x1, _y1;

    public TileDecoder(Codestream stream, int tile, TileParameters parameters, byte[] data, int start, int end, int reduce, Budget budget)
    {
        _stream = stream;
        _parameters = parameters;
        _data = data;
        _start = start;
        _end = end;
        _budget = budget;
        int p = tile % stream.TilesAcross, q = tile / stream.TilesAcross;
        _x0 = (int)Math.Max(stream.TileX0 + (long)p * stream.TileWidth, stream.X0);
        _x1 = (int)Math.Min(stream.TileX0 + (long)(p + 1) * stream.TileWidth, stream.Width);
        _y0 = (int)Math.Max(stream.TileY0 + (long)q * stream.TileHeight, stream.Y0);
        _y1 = (int)Math.Min(stream.TileY0 + (long)(q + 1) * stream.TileHeight, stream.Height);
        _components = new TileComponent[stream.Components.Length];
        long packets = 0;
        for (var c = 0; c < _components.Length; c++)
        {
            var component = stream.Components[c];
            var style = parameters.Styles[c];
            var tc = _components[c] = new TileComponent
            {
                Style = style,
                X0 = Geometry.CeilDiv(_x0, component.Dx),
                Y0 = Geometry.CeilDiv(_y0, component.Dy),
                X1 = Geometry.CeilDiv(_x1, component.Dx),
                Y1 = Geometry.CeilDiv(_y1, component.Dy),
                Keep = style.Levels - reduce,
                Resolutions = new Resolution[style.Levels + 1],
            };
            for (var r = 0; r <= style.Levels; r++)
            {
                var res = tc.Resolutions[r] = Resolve(tc, r);
                packets += (long)res.Across * res.Down * parameters.Layers;
            }
        }
        if (packets > MaxPackets) throw new ResourceLimitExceededException($"A JPEG 2000 tile declares {packets:N0} packets.");
    }

    private static Resolution Resolve(TileComponent tc, int r)
    {
        var style = tc.Style;
        var n = style.Levels - r;
        var res = new Resolution
        {
            X0 = Geometry.CeilShift(tc.X0, n),
            Y0 = Geometry.CeilShift(tc.Y0, n),
            X1 = Geometry.CeilShift(tc.X1, n),
            Y1 = Geometry.CeilShift(tc.Y1, n),
            PrecinctWidth = style.PrecinctWidth[r],
            PrecinctHeight = style.PrecinctHeight[r],
        };
        if (res.X1 > res.X0 && res.Y1 > res.Y0)
        {
            res.Across = Geometry.CeilShift(res.X1, res.PrecinctWidth) - (res.X0 >> res.PrecinctWidth);
            res.Down = Geometry.CeilShift(res.Y1, res.PrecinctHeight) - (res.Y0 >> res.PrecinctHeight);
        }
        var precincts = (long)res.Across * res.Down;
        if (precincts > 1 << 26) throw new ResourceLimitExceededException("A JPEG 2000 resolution declares too many precincts.");
        res.Precincts = new Precinct?[precincts];
        var level = r == 0 ? style.Levels : style.Levels - r + 1;
        int blockWidth = Math.Min(style.BlockWidth, r == 0 ? res.PrecinctWidth : res.PrecinctWidth - 1);
        int blockHeight = Math.Min(style.BlockHeight, r == 0 ? res.PrecinctHeight : res.PrecinctHeight - 1);
        res.Bands = r == 0 ? [MakeBand(tc, 0, level, 0, blockWidth, blockHeight)]
            : [MakeBand(tc, 1, level, 1 + (r - 1) * 3, blockWidth, blockHeight), MakeBand(tc, 2, level, 2 + (r - 1) * 3, blockWidth, blockHeight), MakeBand(tc, 3, level, 3 + (r - 1) * 3, blockWidth, blockHeight)];
        return res;
    }

    private static Band MakeBand(TileComponent tc, int kind, int level, int step, int blockWidth, int blockHeight)
    {
        long xo = kind is 1 or 3 ? 1L << (level - 1) : 0, yo = kind is 2 or 3 ? 1L << (level - 1) : 0;
        return new Band
        {
            Kind = kind,
            Level = level,
            Step = step,
            X0 = (int)Geometry.CeilDiv(tc.X0 - xo, 1L << level),
            Y0 = (int)Geometry.CeilDiv(tc.Y0 - yo, 1L << level),
            X1 = (int)Geometry.CeilDiv(tc.X1 - xo, 1L << level),
            Y1 = (int)Geometry.CeilDiv(tc.Y1 - yo, 1L << level),
            BlockWidth = blockWidth,
            BlockHeight = blockHeight,
        };
    }

    /// <summary>Samples of each component at the kept resolution, before the component transform and level shift.</summary>
    public (float[] Samples, int X0, int Y0, int Width, int Height)[] Decode()
    {
        ReadPackets();
        var tier1 = new Tier1();
        var result = new (float[], int, int, int, int)[_components.Length];
        for (var c = 0; c < _components.Length; c++)
        {
            var tc = _components[c];
            for (var r = 0; r <= tc.Keep; r++)
            {
                var res = tc.Resolutions[r];
                foreach (var band in res.Bands) band.Coefficients = new float[Math.Max(0, band.Width) * Math.Max(0, band.Height)];
                foreach (var precinct in res.Precincts)
                    if (precinct != null)
                        foreach (var part in precinct.Bands)
                            foreach (var block in part.Blocks) DecodeBlock(tier1, c, part.Band, block);
            }
            var keep = tc.Resolutions[tc.Keep];
            result[c] = (Wavelet.Reconstruct(tc.Resolutions.Take(tc.Keep + 1).Select(r => (r.X0, r.Y0, r.X1, r.Y1, r.Bands.Select(b => (b.X0, b.Y0, b.X1, b.Y1, b.Coefficients!)).ToArray())).ToArray(), tc.Style.Reversible),
                keep.X0, keep.Y0, keep.X1 - keep.X0, keep.Y1 - keep.Y0);
        }
        return result;
    }

    // ---- packets ---------------------------------------------------------------------------------

    private sealed class EndOfTile : Exception;

    private sealed class PacketReader(byte[] data, int start, int end)
    {
        private int _at = start, _bits, _byte;
        private bool _ff;

        public int Position => _at;
        public bool AtEnd => _at >= end;
        public int End => end;

        public int Bit()
        {
            if (_bits == 0)
            {
                if (_at >= end) throw new EndOfTile();
                _byte = data[_at++];
                _bits = _ff ? 7 : 8;               // after 0xFF, the top bit of the next byte is a stuffed 0
                _ff = _byte == 0xFF;
            }
            _bits--;
            return (_byte >> _bits) & 1;
        }

        public int Bits(int count)
        {
            var value = 0;
            while (count-- > 0) value = (value << 1) | Bit();
            return value;
        }

        /// <summary>The end of a packet header: the rest of the byte, and the byte after a 0xFF.</summary>
        public void Align()
        {
            _bits = 0;
            if (_ff) { _ff = false; _at++; }
        }

        public bool Next(int marker) => _at + 1 < end && data[_at] == marker >> 8 && data[_at + 1] == (marker & 0xFF);

        public void Skip(int count) => _at += count;
    }

    private void ReadPackets()
    {
        var reader = new PacketReader(_data, _start, _end);
        var pending = new List<(Segment Segment, int Length)>();
        try
        {
            foreach (var (layer, r, c, k) in Order())
            {
                if (reader.AtEnd) break;
                if ((k & 255) == 0) Guard.Check();
                ReadPacket(reader, pending, layer, r, c, k);
            }
        }
        catch (EndOfTile) { }   // the data ends early: the code-blocks keep what they received
    }

    private void ReadPacket(PacketReader reader, List<(Segment Segment, int Length)> pending, int layer, int r, int c, int k)
    {
        if (_parameters.Sop && reader.Next(0xFF91)) reader.Skip(6);
        if (reader.Bit() == 0)
        {
            reader.Align();
            if (_parameters.Eph && reader.Next(0xFF92)) reader.Skip(2);
            return;
        }
        var res = _components[c].Resolutions[r];
        var precinct = res.Precincts[k] ??= CreatePrecinct(_components[c], res, r, k);
        var style = _components[c].Style.BlockStyle;
        pending.Clear();
        foreach (var part in precinct.Bands)
            for (var i = 0; i < part.Blocks.Length; i++)
            {
                var block = part.Blocks[i];
                var included = block.Included ? reader.Bit() == 1 : part.Inclusion!.Decode(reader.Bit, i, layer + 1);
                if (!included) continue;
                if (!block.Included)
                {
                    var planes = 1;
                    while (!part.ZeroPlanes!.Decode(reader.Bit, i, planes))
                        if (++planes > 64) throw new InvalidDocumentException("A JPEG 2000 code-block declares too many zero bit-planes.");
                    block.ZeroPlanes = planes - 1;
                    block.Included = true;
                }
                var passes = Passes(reader);
                while (reader.Bit() == 1)
                    if (++block.Lblock > 32) throw new InvalidDocumentException("A JPEG 2000 code-block length is too long.");
                for (var remaining = passes; remaining > 0;)
                {
                    block.Segments ??= [];
                    var segment = block.Segments.Count > 0 && block.Segments[^1].Passes < block.Segments[^1].MaxPasses ? block.Segments[^1] : NewSegment(block, style);
                    var n = Math.Min(segment.MaxPasses - segment.Passes, remaining);
                    var bits = block.Lblock + BitOperations.Log2((uint)n);
                    if (bits > 30) throw new InvalidDocumentException("A JPEG 2000 code-block length is too long.");
                    pending.Add((segment, reader.Bits(bits)));
                    segment.Passes += n;
                    block.Passes += n;
                    remaining -= n;
                }
                if (block.Passes > 255) throw new InvalidDocumentException("A JPEG 2000 code-block declares too many coding passes.");
            }
        reader.Align();
        if (_parameters.Eph && reader.Next(0xFF92)) reader.Skip(2);
        foreach (var (segment, length) in pending)
        {
            var available = Math.Min(length, reader.End - reader.Position);
            if (available > 0) segment.Chunks.Add((reader.Position, available));
            if (available < length) throw new EndOfTile();
            reader.Skip(length);
        }
    }

    /// <summary>Number of new coding passes: 1, 2, 3 to 5, 6 to 36, 37 to 164 (Table B.4).</summary>
    private static int Passes(PacketReader reader)
    {
        if (reader.Bit() == 0) return 1;
        if (reader.Bit() == 0) return 2;
        var v = reader.Bits(2);
        if (v < 3) return 3 + v;
        v = reader.Bits(5);
        if (v < 31) return 6 + v;
        return 37 + reader.Bits(7);
    }

    /// <summary>A codeword segment ends after every pass (TERMALL), or around the raw passes of bypass mode, or never.</summary>
    private static Segment NewSegment(Block block, int style)
    {
        var segments = block.Segments!;
        var max = (style & Tier1.StyleTerminateAll) != 0 ? 1
            : (style & Tier1.StyleBypass) != 0 ? segments.Count == 0 ? 10 : segments[^1].MaxPasses is 1 or 10 ? 2 : 1
            : int.MaxValue;
        var segment = new Segment { MaxPasses = max };
        segments.Add(segment);
        return segment;
    }

    private Precinct CreatePrecinct(TileComponent tc, Resolution res, int r, int k)
    {
        int px = (res.X0 >> res.PrecinctWidth) + k % res.Across, py = (res.Y0 >> res.PrecinctHeight) + k / res.Across;
        var precinct = new Precinct { Bands = new BandPrecinct[res.Bands.Length] };
        for (var b = 0; b < res.Bands.Length; b++)
        {
            var band = res.Bands[b];
            int shiftX = r == 0 ? res.PrecinctWidth : res.PrecinctWidth - 1, shiftY = r == 0 ? res.PrecinctHeight : res.PrecinctHeight - 1;
            long x0 = Math.Max((long)px << shiftX, band.X0), x1 = Math.Min((long)(px + 1) << shiftX, band.X1);
            long y0 = Math.Max((long)py << shiftY, band.Y0), y1 = Math.Min((long)(py + 1) << shiftY, band.Y1);
            var part = precinct.Bands[b] = new BandPrecinct { Band = band };
            if (x0 >= x1 || y0 >= y1) continue;
            long bx0 = x0 >> band.BlockWidth, by0 = y0 >> band.BlockHeight;
            part.Across = (int)(Geometry.CeilDiv(x1, 1L << band.BlockWidth) - bx0);
            part.Down = (int)(Geometry.CeilDiv(y1, 1L << band.BlockHeight) - by0);
            _budget.Blocks += part.Across * part.Down;
            if (_budget.Blocks > MaxBlocks) throw new ResourceLimitExceededException("The JPEG 2000 image declares too many code-blocks.");
            part.Blocks = new Block[part.Across * part.Down];
            for (var j = 0; j < part.Down; j++)
                for (var i = 0; i < part.Across; i++)
                    part.Blocks[j * part.Across + i] = new Block
                    {
                        X0 = (int)Math.Max((bx0 + i) << band.BlockWidth, x0),
                        X1 = (int)Math.Min((bx0 + i + 1) << band.BlockWidth, x1),
                        Y0 = (int)Math.Max((by0 + j) << band.BlockHeight, y0),
                        Y1 = (int)Math.Min((by0 + j + 1) << band.BlockHeight, y1),
                    };
            part.Inclusion = new TagTree(part.Across, part.Down);
            part.ZeroPlanes = new TagTree(part.Across, part.Down);
        }
        return precinct;
    }

    /// <summary>Every packet of the tile as (layer, resolution, component, precinct), in the tile's progression order (B.12).</summary>
    private IEnumerable<(int Layer, int Resolution, int Component, int Precinct)> Order()
    {
        var layers = _parameters.Layers;
        var resolutions = _components.Max(tc => tc.Resolutions.Length);
        switch (_parameters.Progression)
        {
            case 0:                                                          // layer, resolution, component, position
                for (var l = 0; l < layers; l++)
                    for (var r = 0; r < resolutions; r++)
                        for (var c = 0; c < _components.Length; c++)
                            if (r < _components[c].Resolutions.Length)
                                for (var k = 0; k < _components[c].Resolutions[r].Precincts.Length; k++) yield return (l, r, c, k);
                break;
            case 1:                                                          // resolution, layer, component, position
                for (var r = 0; r < resolutions; r++)
                    for (var l = 0; l < layers; l++)
                        for (var c = 0; c < _components.Length; c++)
                            if (r < _components[c].Resolutions.Length)
                                for (var k = 0; k < _components[c].Resolutions[r].Precincts.Length; k++) yield return (l, r, c, k);
                break;
            default:
                {
                    // position orders: each precinct is visited where it starts on the reference grid (or at the tile's edge)
                    var precincts = new List<(int R, int C, int K, long X, long Y)>();
                    for (var c = 0; c < _components.Length; c++)
                    {
                        var component = _stream.Components[c];
                        var tc = _components[c];
                        for (var r = 0; r < tc.Resolutions.Length; r++)
                        {
                            var res = tc.Resolutions[r];
                            var n = tc.Style.Levels - r;
                            for (var k = 0; k < res.Precincts.Length; k++)
                            {
                                long px = (res.X0 >> res.PrecinctWidth) + k % res.Across, py = (res.Y0 >> res.PrecinctHeight) + k / res.Across;
                                precincts.Add((r, c, k, Math.Max(_x0, (long)component.Dx * (px << (res.PrecinctWidth + n))),
                                    Math.Max(_y0, (long)component.Dy * (py << (res.PrecinctHeight + n)))));
                            }
                        }
                    }
                    Comparison<(int R, int C, int K, long X, long Y)> compare = _parameters.Progression switch
                    {
                        2 => (a, b) => a.R != b.R ? a.R.CompareTo(b.R) : a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X != b.X ? a.X.CompareTo(b.X) : a.C.CompareTo(b.C),
                        3 => (a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X != b.X ? a.X.CompareTo(b.X) : a.C != b.C ? a.C.CompareTo(b.C) : a.R.CompareTo(b.R),
                        _ => (a, b) => a.C != b.C ? a.C.CompareTo(b.C) : a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X != b.X ? a.X.CompareTo(b.X) : a.R.CompareTo(b.R),
                    };
                    precincts.Sort(compare);
                    foreach (var (r, c, k, _, _) in precincts)
                        for (var l = 0; l < layers; l++) yield return (l, r, c, k);
                    break;
                }
        }
    }

    // ---- tier 1 and dequantization ---------------------------------------------------------------

    private byte[] _scratch = new byte[4096];
    private readonly List<(int Start, int End, int Passes)> _ranges = [];

    private void DecodeBlock(Tier1 tier1, int c, Band band, Block block)
    {
        if (!block.Included || block.Passes == 0 || block.Segments == null || block.X1 <= block.X0 || block.Y1 <= block.Y0) return;
        var component = _stream.Components[c];
        var tc = _components[c];
        var quantization = _parameters.Quantizations[c];
        var (exponent, mantissa) = quantization.Step(band.Step, tc.Style.Levels, band.Level);
        var roi = _parameters.RoiShift[c];
        var planes = quantization.GuardBits + exponent - 1 + roi - block.ZeroPlanes;
        if (planes <= 0) return;
        if (planes > 30) throw new UnsupportedFormatException("The JPEG 2000 image has more bit-planes than this decoder handles.");

        // the segments, each made contiguous
        var total = 0;
        foreach (var segment in block.Segments) foreach (var chunk in segment.Chunks) total += chunk.Length;
        if (_scratch.Length < total) _scratch = new byte[Math.Max(total, _scratch.Length * 2)];
        _ranges.Clear();
        var at = 0;
        foreach (var segment in block.Segments)
        {
            var start = at;
            foreach (var (chunkStart, length) in segment.Chunks)
            {
                Buffer.BlockCopy(_data, chunkStart, _scratch, at, length);
                at += length;
            }
            _ranges.Add((start, at, segment.Passes));
        }
        int width = block.X1 - block.X0, height = block.Y1 - block.Y0;
        tier1.Decode(_scratch, _ranges, width, height, planes, block.Passes, band.Kind, tc.Style.BlockStyle);

        var reversible = tc.Style.Reversible;
        var gain = band.Kind switch { 0 => 0, 3 => 2, _ => 1 };
        var delta = reversible ? 1f : (float)(Math.Pow(2, component.Precision + gain - exponent) * (1 + mantissa / 2048.0));
        var magnitude = tier1.Magnitude;
        var coefficients = band.Coefficients!;
        for (var y = 0; y < height; y++)
        {
            var row = (block.Y0 + y - band.Y0) * band.Width + block.X0 - band.X0;
            for (var x = 0; x < width; x++)
            {
                var m = magnitude[y * width + x];
                if (m == 0) continue;
                if (roi > 0 && m >> 1 >= 1 << roi) m >>= roi;        // inside the region of interest: scaled up by the encoder
                var value = reversible ? m >> 1 : m * 0.5f * delta;
                coefficients[row + x] = tier1.IsNegative(x, y) ? -value : value;
            }
        }
    }
}

/// <summary>A tag tree (B.10.2): a quadtree of minima, read from the root down, each node only as far as a threshold asks.</summary>
internal sealed class TagTree
{
    private readonly int[] _value, _low, _parent;

    public TagTree(int width, int height)
    {
        var sizes = new List<(int Width, int Height)>();
        int w = width, h = height;
        while (true)
        {
            sizes.Add((w, h));
            if (w <= 1 && h <= 1) break;
            w = (w + 1) / 2;
            h = (h + 1) / 2;
        }
        var count = sizes.Sum(s => s.Width * s.Height);
        _value = new int[count];
        _low = new int[count];
        _parent = new int[count];
        Array.Fill(_value, int.MaxValue);
        var offset = 0;
        for (var level = 0; level < sizes.Count; level++)
        {
            var (lw, lh) = sizes[level];
            var next = offset + lw * lh;
            for (var y = 0; y < lh; y++)
                for (var x = 0; x < lw; x++)
                    _parent[offset + y * lw + x] = level + 1 < sizes.Count ? next + (y / 2) * sizes[level + 1].Width + x / 2 : -1;
            offset = next;
        }
    }

    /// <summary>Whether leaf <paramref name="leaf"/> is below <paramref name="threshold"/>, reading the bits that takes.</summary>
    public bool Decode(Func<int> bit, int leaf, int threshold)
    {
        Span<int> path = stackalloc int[32];
        var depth = 0;
        var node = leaf;
        while (_parent[node] >= 0)
        {
            path[depth++] = node;
            node = _parent[node];
        }
        var low = 0;
        while (true)
        {
            if (low > _low[node]) _low[node] = low;
            else low = _low[node];
            while (low < threshold && low < _value[node])
            {
                if (bit() != 0) _value[node] = low;
                else low++;
            }
            _low[node] = low;
            if (depth == 0) break;
            node = path[--depth];
        }
        return _value[node] < threshold;
    }
}
