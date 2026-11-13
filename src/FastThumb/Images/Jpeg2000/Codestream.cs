using System.Buffers.Binary;

namespace FastThumb.Images.Jpeg2000;

/// <summary>One image component: bit depth, signedness and subsampling on the reference grid (SIZ).</summary>
internal sealed record Component(int Precision, bool Signed, int Dx, int Dy);

/// <summary>Coding style of one component (COD / COC).</summary>
internal sealed class CodingStyle
{
    public int Levels;
    public int BlockWidth, BlockHeight;      // exponents
    public int BlockStyle;
    public bool Reversible;
    public int[] PrecinctWidth = [], PrecinctHeight = [];   // exponents, by resolution

    public CodingStyle Clone() => (CodingStyle)MemberwiseClone();
}

/// <summary>Quantization of one component (QCD / QCC): style 0 none, 1 scalar derived, 2 scalar expounded.</summary>
internal sealed class Quantization
{
    public int Style;
    public int GuardBits;
    public (int Exponent, int Mantissa)[] Steps = [];

    /// <summary>Exponent and mantissa of band <paramref name="index"/> (0 LL, then HL, LH, HH by resolution), at <paramref name="level"/> decompositions.</summary>
    public (int Exponent, int Mantissa) Step(int index, int levels, int level)
    {
        if (Style == 1) return (Steps[0].Exponent - levels + level, Steps[0].Mantissa);
        if (index >= Steps.Length) throw new InvalidDocumentException("A JPEG 2000 quantization marker has too few step sizes.");
        return Steps[index];
    }
}

/// <summary>Everything a tile is decoded with: the main header's values, overridden by the tile's own header.</summary>
internal sealed class TileParameters
{
    public int Progression;
    public int Layers;
    public bool Transform;
    public bool Sop, Eph;
    public CodingStyle[] Styles = [];
    public Quantization[] Quantizations = [];
    public int[] RoiShift = [];
}

/// <summary>
/// The codestream, located: the image and tile grid (SIZ), the coding parameters of every tile, and each tile's packet data
/// (its tile-parts, concatenated). Nothing is decoded here. POC, PPM and PPT (progression changes, packed packet headers) are
/// refused: they are rare outside digital cinema, and decoding them would double the packet code.
/// </summary>
internal sealed class Codestream
{
    public int Width, Height, X0, Y0;                       // Xsiz, Ysiz, XOsiz, YOsiz
    public int TileWidth, TileHeight, TileX0, TileY0;
    public int TilesAcross, TilesDown;
    public Component[] Components = [];
    public readonly Dictionary<int, (TileParameters Parameters, List<(int Start, int Length)> Parts)> Tiles = [];

    private byte[] _data = [];

    public byte[] Data => _data;

    public const int MaxTiles = 65535;

    /// <summary>Only SIZ: the size on the reference grid and the components.</summary>
    public static Codestream Header(byte[] data, int start, int end)
    {
        var stream = new Codestream { _data = data };
        if (end - start < 4 || BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(start)) != 0xFF4F)
            throw new InvalidDocumentException("The JPEG 2000 codestream does not start with SOC.");
        var at = start + 2;
        var (marker, segment) = stream.Segment(ref at, end);
        if (marker != 0xFF51) throw new InvalidDocumentException("The JPEG 2000 codestream has no SIZ marker after SOC.");
        stream.Size(stream.Content(segment));
        return stream;
    }

    public static Codestream Parse(byte[] data, int start, int end)
    {
        var stream = Header(data, start, end);
        var at = start + 2;
        stream.Segment(ref at, end);                                       // SIZ again
        var main = new Pieces(stream.Components.Length);
        while (true)
        {
            Guard.Check();
            if (at + 2 > end) throw new InvalidDocumentException("The JPEG 2000 main header has no tile.");
            if (BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at)) == 0xFF90) break;
            var (marker, segment) = stream.Segment(ref at, end);
            stream.Read(main, marker, stream.Content(segment));
        }
        if (main.Cod == null || main.Qcd == null) throw new InvalidDocumentException("The JPEG 2000 main header has no COD or QCD marker.");

        // tile-parts: SOT, the tile-part header, SOD, packet data up to the length SOT gives
        while (at + 12 <= end && BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at)) == 0xFF90)
        {
            Guard.Check();
            var sot = at;
            var segment = stream.Content(stream.Segment(ref at, end).Content);
            if (segment.Length < 8) throw new InvalidDocumentException("A JPEG 2000 SOT marker is too short.");
            int tile = BinaryPrimitives.ReadUInt16BigEndian(segment);
            var length = BinaryPrimitives.ReadUInt32BigEndian(segment[2..]);
            if (tile >= stream.TilesAcross * stream.TilesDown) throw new InvalidDocumentException($"A JPEG 2000 tile-part names tile {tile}, past the last one.");
            var partEnd = length == 0 ? end : (int)Math.Min(end, sot + (long)length);
            if (partEnd <= at) break;
            var own = new Pieces(stream.Components.Length);
            while (at + 2 <= partEnd && BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at)) != 0xFF93)
            {
                var (marker, header) = stream.Segment(ref at, partEnd);
                stream.Read(own, marker, stream.Content(header));
            }
            at += 2;                                                        // SOD
            if (!stream.Tiles.TryGetValue(tile, out var entry))
            {
                // the coding markers of a tile are in its first tile-part
                entry = (stream.Resolve(main, own), []);
                stream.Tiles[tile] = entry;
            }
            var dataEnd = length == 0 && partEnd >= 2 && BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(partEnd - 2)) == 0xFFD9 ? partEnd - 2 : partEnd;
            if (dataEnd > at) entry.Parts.Add((at, dataEnd - at));
            at = partEnd;
        }
        return stream;
    }

    /// <summary>One marker segment: the marker and where its content is (after the length).</summary>
    private (int Marker, (int Start, int Length) Content) Segment(ref int at, int end)
    {
        if (at + 4 > end) throw new InvalidDocumentException("A JPEG 2000 marker runs past the end of the codestream.");
        int marker = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(at)), length = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(at + 2));
        if ((marker & 0xFF00) != 0xFF00 || length < 2 || at + 2 + length > end) throw new InvalidDocumentException("A JPEG 2000 marker segment is damaged.");
        var content = (at + 4, length - 2);
        at += 2 + length;
        return (marker, content);
    }

    private ReadOnlySpan<byte> Content((int Start, int Length) segment) => _data.AsSpan(segment.Start, segment.Length);

    private void Size(ReadOnlySpan<byte> s)
    {
        if (s.Length < 36) throw new InvalidDocumentException("The JPEG 2000 SIZ marker is too short.");
        long width = BinaryPrimitives.ReadUInt32BigEndian(s[2..]), height = BinaryPrimitives.ReadUInt32BigEndian(s[6..]);
        long x0 = BinaryPrimitives.ReadUInt32BigEndian(s[10..]), y0 = BinaryPrimitives.ReadUInt32BigEndian(s[14..]);
        long tileWidth = BinaryPrimitives.ReadUInt32BigEndian(s[18..]), tileHeight = BinaryPrimitives.ReadUInt32BigEndian(s[22..]);
        long tileX0 = BinaryPrimitives.ReadUInt32BigEndian(s[26..]), tileY0 = BinaryPrimitives.ReadUInt32BigEndian(s[30..]);
        int count = BinaryPrimitives.ReadUInt16BigEndian(s[34..]);
        if (width > int.MaxValue / 2 || height > int.MaxValue / 2 || x0 >= width || y0 >= height || tileWidth == 0 || tileHeight == 0
            || tileX0 > x0 || tileY0 > y0 || tileX0 + tileWidth <= x0 || tileY0 + tileHeight <= y0 || count is 0 or > 16384 || s.Length < 36 + 3 * count)
            throw new InvalidDocumentException("The JPEG 2000 SIZ marker is not valid.");
        (Width, Height, X0, Y0) = ((int)width, (int)height, (int)x0, (int)y0);
        (TileWidth, TileHeight, TileX0, TileY0) = ((int)Math.Min(tileWidth, int.MaxValue / 2), (int)Math.Min(tileHeight, int.MaxValue / 2), (int)tileX0, (int)tileY0);
        long across = Geometry.CeilDiv(width - tileX0, TileWidth), down = Geometry.CeilDiv(height - tileY0, TileHeight);
        if (across * down > MaxTiles) throw new UnsupportedFormatException($"The JPEG 2000 image has {across * down:N0} tiles.");
        (TilesAcross, TilesDown) = ((int)across, (int)down);
        Components = new Component[count];
        for (var c = 0; c < count; c++)
        {
            var depth = s[36 + 3 * c];
            int dx = s[37 + 3 * c], dy = s[38 + 3 * c];
            if ((depth & 0x7F) > 37 || dx == 0 || dy == 0) throw new InvalidDocumentException("A JPEG 2000 component is not valid.");
            Components[c] = new Component((depth & 0x7F) + 1, (depth & 0x80) != 0, dx, dy);
        }
    }

    /// <summary>The coding markers of one header, before the precedence rules are applied.</summary>
    private sealed class Pieces(int components)
    {
        public (int Progression, int Layers, bool Transform, bool Sop, bool Eph, CodingStyle Style)? Cod;
        public readonly CodingStyle?[] Coc = new CodingStyle?[components];
        public Quantization? Qcd;
        public readonly Quantization?[] Qcc = new Quantization?[components];
        public readonly int?[] Roi = new int?[components];
    }

    private void Read(Pieces pieces, int marker, ReadOnlySpan<byte> s)
    {
        var wide = Components.Length >= 257;
        int ComponentIndex(ref ReadOnlySpan<byte> rest)
        {
            int c = wide ? BinaryPrimitives.ReadUInt16BigEndian(rest) : rest[0];
            rest = rest[(wide ? 2 : 1)..];
            if (c >= Components.Length) throw new InvalidDocumentException("A JPEG 2000 marker names a component that does not exist.");
            return c;
        }
        switch (marker)
        {
            case 0xFF52:                                                     // COD
                if (s.Length < 10) throw new InvalidDocumentException("A JPEG 2000 COD marker is too short.");
                var mct = s[4];
                if (mct > 1) throw new UnsupportedFormatException("JPEG 2000 multi-component transforms of Part 2 are not supported.");
                pieces.Cod = (s[1], BinaryPrimitives.ReadUInt16BigEndian(s[2..]), mct == 1, (s[0] & 2) != 0, (s[0] & 4) != 0, Style(s[5..], (s[0] & 1) != 0));
                break;
            case 0xFF53:                                                     // COC
                {
                    var rest = s;
                    var c = ComponentIndex(ref rest);
                    if (rest.Length < 1) throw new InvalidDocumentException("A JPEG 2000 COC marker is too short.");
                    pieces.Coc[c] = Style(rest[1..], (rest[0] & 1) != 0);
                    break;
                }
            case 0xFF5C:                                                     // QCD
                pieces.Qcd = Quantize(s);
                break;
            case 0xFF5D:                                                     // QCC
                {
                    var rest = s;
                    var c = ComponentIndex(ref rest);
                    pieces.Qcc[c] = Quantize(rest);
                    break;
                }
            case 0xFF5E:                                                     // RGN
                {
                    var rest = s;
                    var c = ComponentIndex(ref rest);
                    if (rest.Length < 2 || rest[0] != 0) throw new UnsupportedFormatException("This JPEG 2000 region of interest style is not supported.");
                    pieces.Roi[c] = rest[1];
                    break;
                }
            case 0xFF5F:
                throw new UnsupportedFormatException("JPEG 2000 progression order changes (POC) are not supported.");
            case 0xFF60 or 0xFF61:
                throw new UnsupportedFormatException("JPEG 2000 packed packet headers (PPM, PPT) are not supported.");
        }
    }

    private static CodingStyle Style(ReadOnlySpan<byte> s, bool precincts)
    {
        if (s.Length < 5) throw new InvalidDocumentException("A JPEG 2000 coding style is too short.");
        var style = new CodingStyle
        {
            Levels = s[0],
            BlockWidth = (s[1] & 0x0F) + 2,
            BlockHeight = (s[2] & 0x0F) + 2,
            BlockStyle = s[3],
            Reversible = s[4] == 1,
        };
        if (style.Levels > 32 || style.BlockWidth > 10 || style.BlockHeight > 10 || style.BlockWidth + style.BlockHeight > 12)
            throw new InvalidDocumentException("A JPEG 2000 coding style is not valid.");
        if (s[4] > 1) throw new UnsupportedFormatException("JPEG 2000 wavelets of Part 2 are not supported.");
        style.PrecinctWidth = new int[style.Levels + 1];
        style.PrecinctHeight = new int[style.Levels + 1];
        for (var r = 0; r <= style.Levels; r++)
        {
            if (!precincts) { style.PrecinctWidth[r] = style.PrecinctHeight[r] = 15; continue; }
            if (s.Length < 6 + r) throw new InvalidDocumentException("A JPEG 2000 coding style has too few precinct sizes.");
            style.PrecinctWidth[r] = s[5 + r] & 0x0F;
            style.PrecinctHeight[r] = s[5 + r] >> 4;
            if (r > 0 && (style.PrecinctWidth[r] == 0 || style.PrecinctHeight[r] == 0))
                throw new InvalidDocumentException("A JPEG 2000 precinct size is not valid.");
        }
        return style;
    }

    private static Quantization Quantize(ReadOnlySpan<byte> s)
    {
        if (s.Length < 2) throw new InvalidDocumentException("A JPEG 2000 quantization marker is too short.");
        var q = new Quantization { Style = s[0] & 0x1F, GuardBits = s[0] >> 5 };
        var rest = s[1..];
        if (q.Style == 0)
            q.Steps = rest.ToArray().Select(b => (b >> 3, 0)).ToArray();
        else if (q.Style is 1 or 2 && rest.Length >= 2)
        {
            q.Steps = new (int, int)[q.Style == 1 ? 1 : rest.Length / 2];
            for (var i = 0; i < q.Steps.Length; i++)
            {
                var v = BinaryPrimitives.ReadUInt16BigEndian(rest[(i * 2)..]);
                q.Steps[i] = (v >> 11, v & 0x7FF);
            }
        }
        else throw new InvalidDocumentException("A JPEG 2000 quantization marker is not valid.");
        if (q.Steps.Length == 0) throw new InvalidDocumentException("A JPEG 2000 quantization marker has no step size.");
        return q;
    }

    /// <summary>Tile COC, then tile COD, then main COC, then main COD (A.6.1); the same for QCC and QCD.</summary>
    private TileParameters Resolve(Pieces main, Pieces tile)
    {
        var cod = tile.Cod ?? main.Cod!.Value;
        var parameters = new TileParameters
        {
            Progression = cod.Progression,
            Layers = cod.Layers,
            Transform = cod.Transform && Components.Length >= 3,
            Sop = cod.Sop,
            Eph = cod.Eph,
            Styles = new CodingStyle[Components.Length],
            Quantizations = new Quantization[Components.Length],
            RoiShift = new int[Components.Length],
        };
        if (parameters.Progression > 4) throw new InvalidDocumentException("The JPEG 2000 progression order is not valid.");
        if (parameters.Layers == 0) throw new InvalidDocumentException("The JPEG 2000 image declares no quality layer.");
        for (var c = 0; c < Components.Length; c++)
        {
            parameters.Styles[c] = (tile.Coc[c] ?? tile.Cod?.Style ?? main.Coc[c] ?? main.Cod!.Value.Style).Clone();
            parameters.Quantizations[c] = tile.Qcc[c] ?? tile.Qcd ?? main.Qcc[c] ?? main.Qcd!;
            parameters.RoiShift[c] = tile.Roi[c] ?? main.Roi[c] ?? 0;
        }
        return parameters;
    }
}

internal static class Geometry
{
    /// <summary>Ceiling of <paramref name="a"/> / <paramref name="b"/> for b &gt; 0, a of either sign.</summary>
    public static long CeilDiv(long a, long b) => a / b + (a % b > 0 ? 1 : 0);

    public static int CeilDiv(int a, int b) => (int)CeilDiv((long)a, b);

    public static int CeilShift(int a, int shift) => (int)CeilDiv((long)a, 1L << shift);
}
