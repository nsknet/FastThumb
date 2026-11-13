namespace FastThumb.Images.Jpeg2000;

/// <summary>
/// Embedded block decoding (EBCOT tier 1, ITU-T T.800 Annex D): the bit-planes of one code-block, coded in significance
/// propagation, magnitude refinement and cleanup passes, with every code-block style (bypass, context reset, termination of
/// every pass, vertically causal contexts, segmentation symbols). Each coefficient keeps the significance and sign of its
/// neighbours in its own flags, updated when a neighbour becomes significant, so every context is one table lookup.
/// Magnitudes are kept in half units, each one the midpoint of what the decoded bit-planes leave open, so a truncated
/// code-block is reconstructed as well as it can be.
/// </summary>
internal sealed class Tier1
{
    // neighbour significance (8 bits), neighbour signs (N, S, W, E), then the coefficient's own state
    private const int NW = 1, N = 2, NE = 4, W = 8, E = 16, SW = 32, S = 64, SE = 128;
    private const int NegN = 256, NegS = 512, NegW = 1024, NegE = 2048;
    private const int Sig = 1 << 12, Visit = 1 << 13, Refined = 1 << 14, Negative = 1 << 15;
    private const int Neighbours = 0xFF;
    private const int Below = SW | S | SE | NegS;                  // ignored on the last row of a stripe in causal mode

    private const int RunContext = 17, UniformContext = 18;

    public const int StyleBypass = 1, StyleReset = 2, StyleTerminateAll = 4, StyleVerticallyCausal = 8, StyleSegmentSymbols = 32;

    private int[] _flags = new int[66 * 66];
    private int[] _magnitude = new int[64 * 64];
    private readonly byte[] _contexts = new byte[19];
    private int _width, _height, _stride;
    private bool _causal;
    private MqDecoder _mq;
    private RawBits _raw;
    private bool _rawMode;

    // zero-coding context by the 8 neighbour bits, per band kind: LL and LH, HL, HH (Table D.1)
    private static readonly byte[][] Zero = [Build(0), Build(1), Build(0), Build(3)];

    private static byte[] Build(int band)
    {
        var table = new byte[256];
        for (var bits = 0; bits < 256; bits++)
        {
            int h = ((bits & W) != 0 ? 1 : 0) + ((bits & E) != 0 ? 1 : 0);
            int v = ((bits & N) != 0 ? 1 : 0) + ((bits & S) != 0 ? 1 : 0);
            int d = ((bits & NW) != 0 ? 1 : 0) + ((bits & NE) != 0 ? 1 : 0) + ((bits & SW) != 0 ? 1 : 0) + ((bits & SE) != 0 ? 1 : 0);
            if (band == 3)
            {
                var hv = h + v;
                table[bits] = (byte)(d >= 3 ? 8 : d == 2 ? (hv >= 1 ? 7 : 6) : d == 1 ? (hv >= 2 ? 5 : hv == 1 ? 4 : 3) : hv >= 2 ? 2 : hv);
                continue;
            }
            int a = band == 1 ? v : h, b = band == 1 ? h : v;   // the HL band swaps horizontal and vertical
            table[bits] = (byte)(a == 2 ? 8 : a == 1 ? (b >= 1 ? 7 : d >= 1 ? 6 : 5) : b == 2 ? 4 : b == 1 ? 3 : d >= 2 ? 2 : d);
        }
        return table;
    }

    // sign coding: context (low 5 bits) and the bit it is XOR-ed with (bit 5), by the neighbour bits 0..11 (Table D.3)
    private static readonly byte[] Sign = BuildSign();

    private static byte[] BuildSign()
    {
        byte[] context = [13, 12, 11, 10, 9, 10, 11, 12, 13];
        byte[] flip = [1, 1, 1, 1, 0, 0, 0, 0, 0];
        var table = new byte[4096];
        for (var bits = 0; bits < 4096; bits++)
        {
            static int Of(int bits, int sig, int negative) => (bits & sig) == 0 ? 0 : (bits & negative) != 0 ? -1 : 1;
            var h = Math.Clamp(Of(bits, W, NegW) + Of(bits, E, NegE), -1, 1);
            var v = Math.Clamp(Of(bits, N, NegN) + Of(bits, S, NegS), -1, 1);
            var index = (h + 1) * 3 + v + 1;
            table[bits] = (byte)(context[index] | flip[index] << 5);
        }
        return table;
    }

    /// <summary>
    /// Decodes <paramref name="passes"/> coding passes of a <paramref name="width"/> x <paramref name="height"/> code-block whose
    /// most significant bit-plane is <paramref name="planes"/> - 1. <paramref name="segments"/> holds each terminated segment
    /// (start, end in <paramref name="data"/>, number of passes). The result is in <see cref="Magnitude"/> (half units) and
    /// <see cref="IsNegative"/>; <paramref name="band"/> is 0 LL, 1 HL, 2 LH, 3 HH.
    /// </summary>
    public void Decode(byte[] data, List<(int Start, int End, int Passes)> segments, int width, int height, int planes, int passes, int band, int style)
    {
        _width = width;
        _height = height;
        _stride = width + 2;
        var size = _stride * (height + 2);
        if (_flags.Length < size) _flags = new int[size];
        if (_magnitude.Length < width * height) _magnitude = new int[width * height];
        Array.Clear(_flags, 0, size);
        Array.Clear(_magnitude, 0, width * height);
        _causal = (style & StyleVerticallyCausal) != 0;
        ResetContexts();

        var zero = Zero[band];
        int segment = -1, segmentEnd = 0;
        for (var pass = 0; pass < passes; pass++)
        {
            var plane = planes - 1 - (pass + 2) / 3;
            if (plane < 0) break;
            var kind = pass == 0 ? 2 : (pass - 1) % 3;   // 0 significance propagation, 1 magnitude refinement, 2 cleanup
            if (pass >= segmentEnd)
            {
                if (++segment >= segments.Count) break;
                segmentEnd += segments[segment].Passes;
                var (start, end, _) = segments[segment];
                _rawMode = (style & StyleBypass) != 0 && pass >= 10 && kind != 2;
                if (_rawMode) _raw.Start(data, start, end);
                else _mq.Start(data, start, end);
            }
            switch (kind)
            {
                case 0: Propagate(plane, zero); break;
                case 1: Refine(plane); break;
                default: Cleanup(plane, zero, (style & StyleSegmentSymbols) != 0); break;
            }
            if ((style & StyleReset) != 0) ResetContexts();
        }
    }

    public ReadOnlySpan<int> Magnitude => _magnitude.AsSpan(0, _width * _height);

    public bool IsNegative(int x, int y) => (_flags[(y + 1) * _stride + x + 1] & Negative) != 0;

    private void ResetContexts()
    {
        Array.Clear(_contexts);
        _contexts[0] = 4 << 1;
        _contexts[RunContext] = 3 << 1;
        _contexts[UniformContext] = 46 << 1;
    }

    /// <summary>The neighbour bits of a coefficient on row <paramref name="y"/>: in causal mode, nothing below a stripe.</summary>
    private int Around(int p, int y) => _causal && (y & 3) == 3 ? _flags[p] & ~Below : _flags[p];

    private void BecomeSignificant(int p, int index, int y, int plane)
    {
        var sign = Sign[Around(p, y) & 0xFFF];
        var negative = (_rawMode ? _raw.Bit() : _mq.Decode(_contexts, sign & 31) ^ (sign >> 5)) != 0;
        var f = _flags;
        int s = _stride;
        f[p - s - 1] |= SE;
        f[p - s + 1] |= SW;
        f[p + s - 1] |= NE;
        f[p + s + 1] |= NW;
        if (negative)
        {
            f[p - s] |= S | NegS;
            f[p + s] |= N | NegN;
            f[p - 1] |= E | NegE;
            f[p + 1] |= W | NegW;
            f[p] |= Sig | Negative;
        }
        else
        {
            f[p - s] |= S;
            f[p + s] |= N;
            f[p - 1] |= E;
            f[p + 1] |= W;
            f[p] |= Sig;
        }
        _magnitude[index] = 3 << plane;   // 1.5 x 2^plane, in half units
    }

    private void Propagate(int plane, byte[] zero)
    {
        for (var y0 = 0; y0 < _height; y0 += 4)
        {
            var y1 = Math.Min(y0 + 4, _height);
            for (var x = 0; x < _width; x++)
                for (var y = y0; y < y1; y++)
                {
                    var p = (y + 1) * _stride + x + 1;
                    var f = Around(p, y);
                    if ((f & Sig) != 0 || (f & Neighbours) == 0) continue;
                    _flags[p] |= Visit;
                    var bit = _rawMode ? _raw.Bit() : _mq.Decode(_contexts, zero[f & Neighbours]);
                    if (bit != 0) BecomeSignificant(p, y * _width + x, y, plane);
                }
        }
    }

    private void Refine(int plane)
    {
        var half = 1 << plane;                // the midpoint moves by a quarter of the interval: 2^(plane - 1), in half units
        for (var y0 = 0; y0 < _height; y0 += 4)
        {
            var y1 = Math.Min(y0 + 4, _height);
            for (var x = 0; x < _width; x++)
                for (var y = y0; y < y1; y++)
                {
                    var p = (y + 1) * _stride + x + 1;
                    var f = _flags[p];
                    if ((f & (Sig | Visit)) != Sig) continue;
                    var context = (f & Refined) != 0 ? 16 : (Around(p, y) & Neighbours) != 0 ? 15 : 14;
                    var bit = _rawMode ? _raw.Bit() : _mq.Decode(_contexts, context);
                    _magnitude[y * _width + x] += bit != 0 ? half : -half;
                    _flags[p] = f | Refined;
                }
        }
    }

    private void Cleanup(int plane, byte[] zero, bool segmentSymbols)
    {
        for (var y0 = 0; y0 < _height; y0 += 4)
        {
            var y1 = Math.Min(y0 + 4, _height);
            for (var x = 0; x < _width; x++)
            {
                var y = y0;
                var top = (y0 + 1) * _stride + x + 1;
                if (y1 - y0 == 4
                    && ((_flags[top] | _flags[top + _stride] | _flags[top + 2 * _stride]) & (Sig | Visit | Neighbours)) == 0
                    && (Around(top + 3 * _stride, y0 + 3) & (Sig | Visit | Neighbours)) == 0)
                {
                    // run-length mode: four insignificant coefficients with insignificant neighbours
                    if (_mq.Decode(_contexts, RunContext) == 0) continue;
                    var at = _mq.Decode(_contexts, UniformContext) << 1;
                    at |= _mq.Decode(_contexts, UniformContext);
                    y = y0 + at;
                    BecomeSignificant((y + 1) * _stride + x + 1, y * _width + x, y, plane);
                    y++;
                }
                for (; y < y1; y++)
                {
                    var p = (y + 1) * _stride + x + 1;
                    var f = Around(p, y);
                    if ((f & (Sig | Visit)) != 0) continue;
                    if (_mq.Decode(_contexts, zero[f & Neighbours]) != 0) BecomeSignificant(p, y * _width + x, y, plane);
                }
            }
        }
        for (var y = 0; y < _height; y++)
            for (int x = 0, p = (y + 1) * _stride + 1; x < _width; x++, p++) _flags[p] &= ~Visit;
        if (segmentSymbols)
            for (var k = 0; k < 4; k++) _mq.Decode(_contexts, UniformContext);
    }
}
