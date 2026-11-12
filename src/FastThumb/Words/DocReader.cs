using System.Buffers.Binary;
using System.Text;
using FastThumb.Layout;
using SkiaSharp;

namespace FastThumb.Words;

/// <summary>
/// FormatReader for binary Word 97-2003 documents, written against the public [MS-DOC] structure layout.
/// Reads only what page-1 layout needs: fonts, styles, first section, the piece table, and character /
/// paragraph / table properties. Lists, fields' code, pictures, headers, macros etc. are skipped.
/// Blocks are produced in text order, so the paginator's early stop also stops decoding text.
/// </summary>
internal sealed class DocReader : IPageSource
{
    private readonly byte[] _main, _table;
    private readonly List<(int CpStart, int CpEnd, int Fc, bool Compressed)> _pieces = [];
    private readonly List<(uint Start, uint End, int At, int Length)> _chpx = [];
    private readonly List<(uint Start, uint End, int Istd, int At, int Length)> _papx = [];
    private readonly string[] _fonts;
    private readonly int _textLength;

    private sealed class Style { public int Base = -1; public bool Paragraph; public byte[] Papx = [], Chpx = []; public ParaProps? P; public RunProps? R; }
    private readonly Style?[] _styles = [];
    private readonly ParaProps _defaultPara = new();
    private readonly RunProps _defaultRun = new();

    public PageSetup Page { get; }
    public SKImage? LoadImage(string id) => null;

    public DocReader(Stream input)
    {
        using var copy = new MemoryStream();
        input.CopyTo(copy);
        var container = new CompoundFile(copy.ToArray());
        _main = container.Stream("WordDocument") ?? throw new InvalidDataException("WordDocument stream not found");
        if (_main.Length < 0x1AA || U16(_main, 0) != 0xA5EC) throw new NotSupportedException("Unsupported Word binary version");
        var flags = U16(_main, 10);
        if ((flags & 0x0100) != 0) throw new NotSupportedException("Encrypted .doc");
        _table = container.Stream((flags & 0x0200) != 0 ? "1Table" : "0Table") ?? throw new InvalidDataException("Table stream not found");
        _textLength = I32(_main, 76);

        _fonts = ReadFonts(Lcb(15));
        ReadStyles(Lcb(1), out _styles);
        ReadPieces(Lcb(33));
        ReadFormattedDiskPages(Lcb(12), character: true);
        ReadFormattedDiskPages(Lcb(13), character: false);
        _chpx.Sort((a, b) => a.Start.CompareTo(b.Start));
        _papx.Sort((a, b) => a.Start.CompareTo(b.Start));
        Page = ReadFirstSection(Lcb(6));
    }

    /// <summary>Debug aid: prints the first section's raw property list and the resolved page setup.</summary>
    public static int Dump(string path)
    {
        using var file = File.OpenRead(path);
        var reader = new DocReader(file);
        Console.WriteLine($"page: {reader.Page}");
        var plcfSed = reader.Lcb(6);
        Console.WriteLine($"plcfSed length {plcfSed.Length}: {Convert.ToHexString(plcfSed[..Math.Min(plcfSed.Length, 48)])}");
        if (plcfSed.Length >= 20)
        {
            var sections = (plcfSed.Length - 4) / 16;
            var sepx = BinaryPrimitives.ReadInt32LittleEndian(plcfSed[((sections + 1) * 4 + 2)..]);
            Console.WriteLine($"sections {sections}, fcSepx {sepx}");
            if (sepx > 0 && sepx + 2 <= reader._main.Length)
            {
                var size = U16(reader._main, sepx);
                Console.WriteLine($"sepx size {size}: {Convert.ToHexString(reader._main.AsSpan(sepx + 2, Math.Min(size, 120)))}");
            }
        }
        for (var i = 0; i < Math.Min(8, reader._papx.Count); i++)
        {
            var papx = reader._papx[i];
            Console.WriteLine($"papx[{i}] istd {papx.Istd}: {Convert.ToHexString(reader._main.AsSpan(papx.At, papx.Length))}");
        }
        for (var i = 0; i < Math.Min(3, reader._styles.Length); i++)
            if (reader._styles[i] is { } style) Console.WriteLine($"style[{i}] base {style.Base} papx {Convert.ToHexString(style.Papx)} chpx {Convert.ToHexString(style.Chpx)}");
        return 0;
    }

    // ---- low-level helpers ------------------------------------------------------------------------

    private static int U16(byte[] b, int at) => at + 2 <= b.Length ? BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at)) : 0;
    private static int I16(ReadOnlySpan<byte> b) => b.Length >= 2 ? BinaryPrimitives.ReadInt16LittleEndian(b) : 0;
    private static int I32(byte[] b, int at) => at + 4 <= b.Length ? BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at)) : 0;
    private static uint U32(byte[] b, int at) => at + 4 <= b.Length ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at)) : 0;

    /// <summary>The n-th (offset, length) pair of the FIB's FibRgFcLcb block, as a slice of the table stream.</summary>
    private ReadOnlySpan<byte> Lcb(int index)
    {
        int fc = I32(_main, 154 + index * 8), length = I32(_main, 158 + index * 8);
        return fc >= 0 && length > 0 && fc + length <= _table.Length ? _table.AsSpan(fc, length) : default;
    }

    /// <summary>Walks a grpprl. Operand sizes come from the top three bits of the opcode.</summary>
    private static bool NextSprm(ReadOnlySpan<byte> grpprl, ref int at, out int opcode, out ReadOnlySpan<byte> operand)
    {
        opcode = 0; operand = default;
        if (at + 2 > grpprl.Length) return false;
        opcode = BinaryPrimitives.ReadUInt16LittleEndian(grpprl[at..]);
        at += 2;
        int size;
        switch (opcode >> 13)
        {
            case 0 or 1: size = 1; break;
            case 2 or 4 or 5: size = 2; break;
            case 3: size = 4; break;
            case 7: size = 3; break;
            default:
                if (opcode is 0xD608 or 0xD606)
                {
                    if (at + 2 > grpprl.Length) return false;
                    size = BinaryPrimitives.ReadUInt16LittleEndian(grpprl[at..]) - 1;
                    at += 2;
                }
                else
                {
                    if (at >= grpprl.Length) return false;
                    size = grpprl[at++];
                    if (opcode == 0xC615 && size == 255) return false; // variable tab-change record: nothing after it matters here
                }
                break;
        }
        if (size < 0 || at + size > grpprl.Length) return false;
        operand = grpprl.Slice(at, size);
        at += size;
        return true;
    }

    // ---- fonts, styles, section -------------------------------------------------------------------

    private static string[] ReadFonts(ReadOnlySpan<byte> sttb)
    {
        if (sttb.Length < 4) return [];
        var count = BinaryPrimitives.ReadUInt16LittleEndian(sttb);
        var names = new List<string>(count);
        var at = 4;
        for (var i = 0; i < count && at < sttb.Length; i++)
        {
            var length = sttb[at] + 1;
            if (at + length > sttb.Length) break;
            var name = sttb.Slice(at + 40, Math.Max(0, length - 40));
            var chars = new StringBuilder();
            for (var j = 0; j + 1 < name.Length; j += 2)
            {
                var ch = (char)BinaryPrimitives.ReadUInt16LittleEndian(name[j..]);
                if (ch == 0) break;
                chars.Append(ch);
            }
            names.Add(chars.ToString());
            at += length;
        }
        return names.ToArray();
    }

    private string FontName(int index) => index >= 0 && index < _fonts.Length && _fonts[index].Length > 0 ? _fonts[index] : "Times New Roman";

    private void ReadStyles(ReadOnlySpan<byte> stsh, out Style?[] styles)
    {
        styles = [];
        if (stsh.Length < 20) return;
        var headerSize = BinaryPrimitives.ReadUInt16LittleEndian(stsh);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(stsh[2..]);
        var baseSize = BinaryPrimitives.ReadUInt16LittleEndian(stsh[4..]);
        _defaultRun.Font = FontName(BinaryPrimitives.ReadUInt16LittleEndian(stsh[14..]));
        styles = new Style?[count];
        var at = 2 + headerSize;
        for (var i = 0; i < count && at + 2 <= stsh.Length; i++)
        {
            var size = BinaryPrimitives.ReadUInt16LittleEndian(stsh[at..]);
            at += 2;
            if (size == 0) continue;
            if (at + size > stsh.Length) break;
            var std = stsh.Slice(at, size);
            at += size;
            if (std.Length < baseSize + 2) continue;

            var word1 = BinaryPrimitives.ReadUInt16LittleEndian(std[2..]);
            var kind = word1 & 0xF;
            var parent = word1 >> 4;
            var upxCount = BinaryPrimitives.ReadUInt16LittleEndian(std[4..]) & 0xF;
            var style = new Style { Base = parent == 0xFFF ? -1 : parent, Paragraph = kind == 1 };
            int p = baseSize;
            p += 2 + (BinaryPrimitives.ReadUInt16LittleEndian(std[p..]) + 1) * 2; // name: length, chars, terminator
            for (var u = 0; u < upxCount && p + 2 <= std.Length; u++)
            {
                if ((p & 1) != 0) p++;
                if (p + 2 > std.Length) break;
                var length = BinaryPrimitives.ReadUInt16LittleEndian(std[p..]);
                p += 2;
                if (p + length > std.Length) break;
                var data = std.Slice(p, length).ToArray();
                p += length;
                if (kind == 1 && u == 0) style.Papx = data.Length >= 2 ? data[2..] : [];
                else if ((kind == 1 && u == 1) || (kind == 2 && u == 0)) style.Chpx = data;
            }
            styles[i] = style;
        }
    }

    private (ParaProps P, RunProps R) ResolveStyle(int istd, int depth = 0)
    {
        Guard.Deeper();
        var style = istd >= 0 && istd < _styles.Length ? _styles[istd] : null;
        if (style == null || depth > 20) return (_defaultPara, _defaultRun);
        if (style.P != null) return (style.P, style.R!);
        var (baseP, baseR) = style.Base >= 0 && style.Base != istd ? ResolveStyle(style.Base, depth + 1) : (_defaultPara, _defaultRun);
        var p = baseP.Clone();
        var r = baseR.Clone();
        var state = new ParaState();
        ApplyPara(p, style.Papx, ref state);
        ApplyChar(r, style.Chpx, baseR, out _);
        style.P = p;
        style.R = r;
        return (p, r);
    }

    private PageSetup ReadFirstSection(ReadOnlySpan<byte> plcfSed)
    {
        // a section that sets nothing: Letter with 1" margins (what word processors show for such files)
        float width = 12240, height = 15840, left = 1440, right = 1440, top = 1440, bottom = 1440;
        if (plcfSed.Length >= 20)
        {
            var sections = (plcfSed.Length - 4) / 16;
            var sepx = BinaryPrimitives.ReadInt32LittleEndian(plcfSed[((sections + 1) * 4 + 2)..]);
            if (sepx > 0 && sepx + 2 <= _main.Length)
            {
                var size = U16(_main, sepx);
                var grpprl = _main.AsSpan(sepx + 2, Math.Min(size, _main.Length - sepx - 2));
                var at = 0;
                while (NextSprm(grpprl, ref at, out var op, out var operand))
                {
                    switch (op)
                    {
                        case 0xB01F: width = (ushort)I16(operand); break;
                        case 0xB020: height = (ushort)I16(operand); break;
                        case 0xB021: left = (ushort)I16(operand); break;
                        case 0xB022: right = (ushort)I16(operand); break;
                        case 0x9023: top = Math.Abs(I16(operand)); break;
                        case 0x9024: bottom = Math.Abs(I16(operand)); break;
                    }
                }
            }
        }
        return new PageSetup(width / 20, height / 20, top / 20, right / 20, bottom / 20, left / 20);
    }

    // ---- piece table and formatted disk pages -----------------------------------------------------

    private void ReadPieces(ReadOnlySpan<byte> clx)
    {
        var at = 0;
        while (at < clx.Length && clx[at] == 1) at += 3 + BinaryPrimitives.ReadUInt16LittleEndian(clx[(at + 1)..]); // skip Prc entries
        if (at + 5 > clx.Length || clx[at] != 2) throw new InvalidDataException("Piece table not found");
        var length = BinaryPrimitives.ReadInt32LittleEndian(clx[(at + 1)..]);
        var plc = clx.Slice(at + 5, Math.Min(length, clx.Length - at - 5));
        var count = (plc.Length - 4) / 12;
        for (var i = 0; i < count; i++)
        {
            var cpStart = BinaryPrimitives.ReadInt32LittleEndian(plc[(i * 4)..]);
            var cpEnd = BinaryPrimitives.ReadInt32LittleEndian(plc[((i + 1) * 4)..]);
            var fc = BinaryPrimitives.ReadUInt32LittleEndian(plc[((count + 1) * 4 + i * 8 + 2)..]);
            var compressed = (fc & 0x40000000) != 0;
            var offset = (int)(fc & 0x3FFFFFFF);
            _pieces.Add((cpStart, cpEnd, compressed ? offset / 2 : offset, compressed));
        }
    }

    private void ReadFormattedDiskPages(ReadOnlySpan<byte> plcfBte, bool character)
    {
        var count = (plcfBte.Length - 4) / 8;
        for (var i = 0; i < count; i++)
        {
            var page = (int)(BinaryPrimitives.ReadUInt32LittleEndian(plcfBte[((count + 1) * 4 + i * 4)..]) & 0x3FFFFF) * 512;
            if (page <= 0 || page + 512 > _main.Length) continue;
            var runs = _main[page + 511];
            for (var r = 0; r < runs; r++)
            {
                uint start = U32(_main, page + r * 4), end = U32(_main, page + (r + 1) * 4);
                if (character)
                {
                    var offset = _main[page + (runs + 1) * 4 + r] * 2;
                    if (offset == 0) { _chpx.Add((start, end, 0, 0)); continue; }
                    var size = _main[page + offset];
                    if (offset + 1 + size <= 512) _chpx.Add((start, end, page + offset + 1, size));
                }
                else
                {
                    var offset = _main[page + (runs + 1) * 4 + r * 13] * 2;
                    if (offset == 0) { _papx.Add((start, end, 0, 0, 0)); continue; }
                    int size = _main[page + offset], data = page + offset + 1;
                    if (size == 0) { size = _main[page + offset + 1] * 2; data++; }
                    else size = size * 2 - 1;
                    if (size < 2 || data - page + size > 512) { _papx.Add((start, end, 0, 0, 0)); continue; }
                    _papx.Add((start, end, U16(_main, data), data + 2, size - 2));
                }
            }
        }
    }

    private int FindChpx(uint fc)
    {
        int lo = 0, hi = _chpx.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (fc < _chpx[mid].Start) hi = mid - 1;
            else if (fc >= _chpx[mid].End) lo = mid + 1;
            else return mid;
        }
        return -1;
    }

    private int FindPapx(uint fc)
    {
        int lo = 0, hi = _papx.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (fc < _papx[mid].Start) hi = mid - 1;
            else if (fc >= _papx[mid].End) lo = mid + 1;
            else return mid;
        }
        return -1;
    }

    // ---- property application ---------------------------------------------------------------------

    private struct ParaState { public bool InTable, RowEnd, InnerRowEnd; public byte[]? TableDefinition; public Borders TableBorders; public Align TableAlign; }

    private static void ApplyPara(ParaProps p, ReadOnlySpan<byte> grpprl, ref ParaState state)
    {
        var at = 0;
        while (NextSprm(grpprl, ref at, out var op, out var o))
        {
            switch (op)
            {
                case 0x2403 or 0x2461:
                    p.Align = o[0] switch { 1 => Align.Center, 2 => Align.Right, 3 or 4 => Align.Justify, _ => Align.Left };
                    break;
                case 0x840F or 0x845E: p.IndentLeft = I16(o) / 20f; break;
                case 0x840E or 0x845D: p.IndentRight = I16(o) / 20f; break;
                case 0x8411 or 0x8460: p.FirstLine = I16(o) / 20f; break;
                case 0xA413: p.SpaceBefore = I16(o) / 20f; break;
                case 0xA414: p.SpaceAfter = I16(o) / 20f; break;
                case 0x6412:
                    int line = I16(o), multiple = I16(o[2..]);
                    if (multiple != 0) { p.Line = line; p.LineRule = "auto"; }
                    else if (line < 0) { p.Line = -line; p.LineRule = "exact"; }
                    else { p.Line = line; p.LineRule = "atLeast"; }
                    break;
                case 0x2407: p.PageBreakBefore = o[0] != 0; break;
                case 0x245B: p.AutoBefore = o[0] != 0; break;
                case 0x245C: p.AutoAfter = o[0] != 0; break;
                case 0x2416: state.InTable = o[0] != 0; break;
                case 0x6649: state.InTable = BinaryPrimitives.ReadInt32LittleEndian(o) > 0; break;
                case 0x2417: state.RowEnd = o[0] != 0; break;
                case 0x244C: state.InnerRowEnd = o[0] != 0; break;
                case 0xD608: state.TableDefinition = o.ToArray(); break;
                case 0x5400: state.TableAlign = I16(o) switch { 1 => Align.Center, 2 => Align.Right, _ => Align.Left }; break;
                case 0xD605: // six 4-byte borders: top, left, bottom, right, inside horizontal, inside vertical
                    for (var i = 0; i < 6 && i * 4 + 2 <= o.Length; i++) SetTableBorder(ref state.TableBorders, i, o[i * 4 + 1] is not (0 or 0xFF));
                    break;
                case 0xD613: // same six, 8-byte form
                    for (var i = 0; i < 6 && i * 8 + 6 <= o.Length; i++) SetTableBorder(ref state.TableBorders, i, o[i * 8 + 5] is not (0 or 0xFF));
                    break;
            }
        }
    }

    private static void SetTableBorder(ref Borders borders, int index, bool visible)
    {
        switch (index)
        {
            case 0: borders.Top = visible; break;
            case 1: borders.Left = visible; break;
            case 2: borders.Bottom = visible; break;
            case 3: borders.Right = visible; break;
            case 4: borders.InsideH = visible; break;
            case 5: borders.InsideV = visible; break;
        }
    }

    private static readonly uint[] IndexedColors =
    [
        0x000000, 0x000000, 0x0000FF, 0x00FFFF, 0x00FF00, 0xFF00FF, 0xFF0000, 0xFFFF00, 0xFFFFFF,
        0x000080, 0x008080, 0x008000, 0x800080, 0x800000, 0x808000, 0x808080, 0xC0C0C0,
    ];

    /// <param name="styleRun">the paragraph style's run properties: bold/italic operands 0x80 / 0x81 are relative to it</param>
    private void ApplyChar(RunProps r, ReadOnlySpan<byte> grpprl, RunProps styleRun, out bool hidden, int depth = 0)
    {
        Guard.Deeper();
        hidden = false;
        var at = 0;
        while (NextSprm(grpprl, ref at, out var op, out var o))
        {
            switch (op)
            {
                case 0x4A30: // character style: its properties go in before the remaining direct ones
                    var style = I16(o) is var id && id >= 0 && id < _styles.Length ? _styles[id] : null;
                    if (style is { Paragraph: false } && depth < 10) ApplyChar(r, style.Chpx, styleRun, out _, depth + 1);   // a style may name itself
                    break;
                case 0x0835: r.Bold = Toggle(o[0], styleRun.Bold); break;
                case 0x0836: r.Italic = Toggle(o[0], styleRun.Italic); break;
                case 0x2A3E: r.Underline = o[0] != 0; break;
                case 0x4A43: r.Size = (ushort)I16(o) / 2f; break;
                case 0x4A4F: r.Font = FontName((ushort)I16(o)); break;
                case 0x2A42: r.Color = new SKColor(0xFF000000 | IndexedColors[Math.Min((int)o[0], IndexedColors.Length - 1)]); break;
                case 0x6870: r.Color = o[3] == 0xFF ? SKColors.Black : new SKColor(o[0], o[1], o[2]); break;
                case 0x083C or 0x0800: hidden |= Toggle(o[0], false); break; // hidden text, deleted revision
            }
        }
    }

    private static bool Toggle(byte operand, bool styleValue) => operand switch { 0 => false, 1 => true, 0x80 => styleValue, 0x81 => !styleValue, _ => true };

    // ---- text → blocks ----------------------------------------------------------------------------

    private static readonly char[] Cp1252High = "€\u0081‚ƒ„…†‡ˆ‰Š‹Œ\u008DŽ\u008F\u0090‘’“”•–—˜™š›œ\u009DžŸ".ToCharArray();

    public IEnumerable<Block> Blocks()
    {
        // pending content of the paragraph being read: text segments keyed by their CHPX run
        var segments = new List<(object Content, int Chpx)>();
        var text = new StringBuilder();
        var textChpx = -1;
        var fieldCode = new Stack<bool>();
        var openCodes = 0;                                         // entries of fieldCode still in their instruction text
        var budget = Math.Min(_textLength, _main.Length);          // pieces from the file may overlap: no more characters than the text has
        var cells = new List<Cell>();
        var cellBlocks = new List<Block>();

        void FlushText()
        {
            if (text.Length > 0) segments.Add((text.ToString(), textChpx));
            text.Clear();
        }

        foreach (var (cpStart, cpEnd, fcStart, compressed) in _pieces)
        {
            for (var cp = cpStart; cp < cpEnd && cp < _textLength; cp++)
            {
                if (budget-- <= 0) break;
                var fc = fcStart + (cp - cpStart) * (compressed ? 1 : 2);
                if (fc < 0 || fc + (compressed ? 1 : 2) > _main.Length) yield break;
                char ch = compressed ? (char)_main[fc] : (char)BinaryPrimitives.ReadUInt16LittleEndian(_main.AsSpan(fc));
                if (compressed && ch is >= (char)0x80 and <= (char)0x9F) ch = Cp1252High[ch - 0x80];

                if (ch is '\r' or (char)7)
                {
                    FlushText();
                    var papx = FindPapx((uint)fc);
                    var (styleP, styleR) = ResolveStyle(papx >= 0 ? _papx[papx].Istd : 0);
                    var props = styleP.Clone();
                    var state = new ParaState();
                    if (papx >= 0) ApplyPara(props, _main.AsSpan(_papx[papx].At, _papx[papx].Length), ref state);
                    var paragraph = BuildParagraph(segments, props, styleR);
                    segments.Clear();

                    if (state.InnerRowEnd) continue;                    // nested table row mark: its cells stay as plain paragraphs
                    if (!state.InTable)
                    {
                        foreach (var orphan in FlushOrphans()) yield return orphan;
                        yield return paragraph;
                        continue;
                    }
                    if (ch == '\r') { cellBlocks.Add(paragraph); continue; }
                    if (!state.RowEnd)
                    {
                        cellBlocks.Add(paragraph);
                        cells.Add(new Cell { Blocks = [.. cellBlocks] });
                        cellBlocks.Clear();
                        continue;
                    }
                    // row mark: its properties carry the row's column boundaries and cell borders
                    yield return BuildRow(cells, state);
                    cells.Clear();
                    cellBlocks.Clear();
                    continue;
                }

                if (ch == (char)0x13) { fieldCode.Push(true); openCodes++; continue; }
                if (ch == (char)0x14) { if (fieldCode.Count > 0) { if (fieldCode.Pop()) openCodes--; fieldCode.Push(false); } continue; }
                if (ch == (char)0x15) { if (fieldCode.Count > 0 && fieldCode.Pop()) openCodes--; continue; }
                if (openCodes > 0) continue;                             // inside a field's instruction text

                var chpx = FindChpx((uint)fc);
                switch (ch)
                {
                    case '\t': FlushText(); segments.Add((new TabInline { Props = null! }, chpx)); break;
                    case (char)0x0B: FlushText(); segments.Add((new BreakInline { Props = null! }, chpx)); break;
                    case (char)0x0C: FlushText(); segments.Add((new BreakInline { Props = null!, Page = true }, chpx)); break;
                    case (char)0x1E: Append('-'); break;
                    case >= ' ': Append(ch); break;
                }

                void Append(char value)
                {
                    if (chpx != textChpx) { FlushText(); textChpx = chpx; }
                    text.Append(value);
                }
            }
        }

        foreach (var orphan in FlushOrphans()) yield return orphan;

        // table cells whose row mark never arrived (truncated file): keep the text rather than lose it
        IEnumerable<Block> FlushOrphans()
        {
            if (cells.Count == 0 && cellBlocks.Count == 0) yield break;
            foreach (var cell in cells) foreach (var block in cell.Blocks) yield return block;
            foreach (var block in cellBlocks) yield return block;
            cells.Clear();
            cellBlocks.Clear();
        }
    }

    private Paragraph BuildParagraph(List<(object Content, int Chpx)> segments, ParaProps props, RunProps styleRun)
    {
        var paragraph = new Paragraph { Props = props, MarkProps = styleRun };
        RunProps? cached = null;
        var cachedChpx = int.MinValue;
        var cachedHidden = false;
        foreach (var (content, chpx) in segments)
        {
            if (chpx != cachedChpx)
            {
                cached = styleRun.Clone();
                cachedHidden = false;
                if (chpx >= 0) ApplyChar(cached, _main.AsSpan(_chpx[chpx].At, _chpx[chpx].Length), styleRun, out cachedHidden);
                cachedChpx = chpx;
            }
            if (cachedHidden) continue;
            switch (content)
            {
                case string value: paragraph.Inlines.Add(new TextInline { Props = cached!, Text = value }); break;
                case TabInline: paragraph.Inlines.Add(new TabInline { Props = cached! }); break;
                case BreakInline br: paragraph.Inlines.Add(new BreakInline { Props = cached!, Page = br.Page }); break;
            }
        }
        return paragraph;
    }

    /// <summary>One table per row: every row carries its own column boundaries in the binary format.</summary>
    private static Table BuildRow(List<Cell> cells, ParaState state)
    {
        var table = new Table { Borders = state.TableBorders, Align = state.TableAlign };
        var row = new Row();
        row.Cells.AddRange(cells);
        table.Rows.Add(row);

        var definition = state.TableDefinition;
        if (definition is { Length: > 2 })
        {
            int columns = definition[0];
            if (1 + (columns + 1) * 2 <= definition.Length)
            {
                var edges = new float[columns + 1];
                for (var i = 0; i <= columns; i++) edges[i] = BinaryPrimitives.ReadInt16LittleEndian(definition.AsSpan(1 + i * 2)) / 20f;
                table.Grid = new float[columns];
                for (var i = 0; i < columns; i++) table.Grid[i] = Math.Max(0, edges[i + 1] - edges[i]);
                table.Indent = edges[0];

                var descriptors = 1 + (columns + 1) * 2;
                for (var i = 0; i < columns && i < cells.Count && descriptors + (i + 1) * 20 <= definition.Length; i++)
                {
                    var tc = definition.AsSpan(descriptors + i * 20, 20);
                    cells[i].Borders = new Borders
                    {
                        Top = tc[5] is not (0 or 0xFF) ? true : null,
                        Left = tc[9] is not (0 or 0xFF) ? true : null,
                        Bottom = tc[13] is not (0 or 0xFF) ? true : null,
                        Right = tc[17] is not (0 or 0xFF) ? true : null,
                    };
                }
            }
        }
        if (table.Grid.Length == 0) table.Grid = new float[Math.Max(1, cells.Count)];
        while (row.Cells.Count > table.Grid.Length && row.Cells.Count > 1)
        {
            // more cell marks than declared columns: fold the surplus into the last declared cell
            var last = row.Cells[^1];
            row.Cells.RemoveAt(row.Cells.Count - 1);
            row.Cells[^1].Blocks.AddRange(last.Blocks);
        }
        return table;
    }
}
