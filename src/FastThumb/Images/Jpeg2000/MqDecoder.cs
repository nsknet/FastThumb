namespace FastThumb.Images.Jpeg2000;

/// <summary>
/// The MQ arithmetic decoder of JPEG 2000 (ITU-T T.800 Annex C), over one terminated segment of a code-block. Bytes past the end
/// of the segment read as 0xFF, as the standard's decoder expects of a terminated segment.
/// </summary>
internal struct MqDecoder
{
    // Qe, next state after an MPS, next state after an LPS, and whether an LPS switches the MPS (Table C.2)
    private static readonly ushort[] Qe =
    [
        0x5601, 0x3401, 0x1801, 0x0AC1, 0x0521, 0x0221, 0x5601, 0x5401, 0x4801, 0x3801, 0x3001, 0x2401, 0x1C01, 0x1601,
        0x5601, 0x5401, 0x5101, 0x4801, 0x3801, 0x3401, 0x3001, 0x2801, 0x2401, 0x2201, 0x1C01, 0x1801, 0x1601, 0x1401,
        0x1201, 0x1101, 0x0AC1, 0x09C1, 0x08A1, 0x0521, 0x0441, 0x02A1, 0x0221, 0x0141, 0x0111, 0x0085, 0x0049, 0x0025,
        0x0015, 0x0009, 0x0005, 0x0001, 0x5601,
    ];

    private static readonly byte[] NextMps =
    [
        1, 2, 3, 4, 5, 38, 7, 8, 9, 10, 11, 12, 13, 29, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32,
        33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 45, 46,
    ];

    private static readonly byte[] NextLps =
    [
        1, 6, 9, 12, 29, 33, 6, 14, 14, 14, 17, 18, 20, 21, 14, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28,
        29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 46,
    ];

    private static readonly bool[] Switch = [true, false, false, false, false, false, true, false, false, false, false, false, false, false, true];

    // one entry a state: Qe (bits 0-15), next state after an MPS (16-21) and after an LPS (22-27), switch on LPS (28)
    private static readonly uint[] States = Enumerable.Range(0, Qe.Length)
        .Select(i => Qe[i] | (uint)NextMps[i] << 16 | (uint)NextLps[i] << 22 | (i < Switch.Length && Switch[i] ? 1u << 28 : 0)).ToArray();

    private byte[] _data;
    private int _end;
    private int _at;
    private uint _high, _low, _a;
    private int _ct;

    public void Start(byte[] data, int start, int end)
    {
        _data = data;
        _at = start;
        _end = end;
        _high = Byte(start);
        _low = 0;
        ByteIn();
        _high = ((_high << 7) & 0xFFFF) | ((_low >> 9) & 0x7F);
        _low = (_low << 7) & 0xFFFF;
        _ct -= 7;
        _a = 0x8000;
    }

    private readonly uint Byte(int at) => at < _end ? _data[at] : 0xFFu;

    private void ByteIn()
    {
        if (Byte(_at) == 0xFF)
        {
            if (Byte(_at + 1) > 0x8F)
            {
                _low += 0xFF00;
                _ct = 8;
            }
            else
            {
                _at++;
                _low += Byte(_at) << 9;
                _ct = 7;
            }
        }
        else
        {
            _at++;
            _low += Byte(_at) << 8;
            _ct = 8;
        }
        if (_low > 0xFFFF)
        {
            _high += _low >> 16;
            _low &= 0xFFFF;
        }
    }

    /// <summary>One decision in context <paramref name="cx"/>: state index in bits 1+, MPS in bit 0.</summary>
    public int Decode(byte[] contexts, int cx)
    {
        int context = contexts[cx], mps = context & 1;
        var entry = States[context >> 1];
        var qe = entry & 0xFFFF;
        var a = _a - qe;
        int d;
        if (_high < qe)
        {
            // LPS sub-interval, with conditional exchange
            if (a < qe)
            {
                d = mps;
                context = (int)(entry >> 16 & 63) << 1 | mps;
            }
            else
            {
                d = 1 ^ mps;
                if ((entry & 1u << 28) != 0) mps = d;
                context = (int)(entry >> 22 & 63) << 1 | mps;
            }
            a = qe;
        }
        else
        {
            _high -= qe;
            if ((a & 0x8000) != 0)
            {
                _a = a;
                return mps;
            }
            if (a < qe)
            {
                d = 1 ^ mps;
                if ((entry & 1u << 28) != 0) mps = d;
                context = (int)(entry >> 22 & 63) << 1 | mps;
            }
            else
            {
                d = mps;
                context = (int)(entry >> 16 & 63) << 1 | mps;
            }
        }
        do
        {
            if (_ct == 0) ByteIn();
            a <<= 1;
            _high = ((_high << 1) & 0xFFFF) | ((_low >> 15) & 1);
            _low = (_low << 1) & 0xFFFF;
            _ct--;
        }
        while ((a & 0x8000) == 0);
        _a = a;
        contexts[cx] = (byte)context;
        return d;
    }
}

/// <summary>Raw (bypass) bits of a code-block segment: a 0xFF byte is followed by a byte whose top bit is a stuffed 0.</summary>
internal struct RawBits
{
    private byte[] _data;
    private int _at, _end, _ct;
    private uint _c;

    public void Start(byte[] data, int start, int end)
    {
        _data = data;
        _at = start;
        _end = end;
        _c = 0;
        _ct = 0;
    }

    public int Bit()
    {
        if (_ct == 0)
        {
            if (_c == 0xFF)
            {
                var next = _at < _end ? _data[_at] : 0xFFu;
                if (next > 0x8F) { _c = 0xFF; _ct = 8; }
                else { _c = next; _at++; _ct = 7; }
            }
            else
            {
                _c = _at < _end ? _data[_at] : 0xFFu;
                _at++;
                _ct = 8;
            }
        }
        _ct--;
        return (int)((_c >> _ct) & 1);
    }
}
