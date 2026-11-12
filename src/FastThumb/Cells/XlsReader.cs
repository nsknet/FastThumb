using System.Buffers.Binary;
using System.Text;
using FastThumb.Layout;
using SkiaSharp;

namespace FastThumb.Cells;

/// <summary>
/// Binary Excel 97-2003 (XLS / XLT, BIFF8), written against the public [MS-XLS] record layouts.
/// Globals: shared strings (across CONTINUE records), fonts, number formats, cell formats, palette, sheet list.
/// Sheets: geometry, cells (numbers, RK, strings, cached formula results, booleans/errors, blanks with
/// formatting), merges, page setup, margins, manual breaks. Encrypted files and BIFF5 (Excel 95) are rejected.
/// </summary>
internal static class XlsReader
{
    private sealed class Font { public RunProps Props = new() { Font = "Arial", Size = 10 }; public int Color = 0x7FFF; }

    public static List<SheetSnapshot> Read(byte[] data)
    {
        var container = new CompoundFile(data);
        var book = container.Stream("Workbook") ?? container.Stream("Book") ?? throw new InvalidDataException("Workbook stream not found");
        int U16(int at) => at + 2 <= book.Length ? BinaryPrimitives.ReadUInt16LittleEndian(book.AsSpan(at)) : 0;
        int I32(int at) => at + 4 <= book.Length ? BinaryPrimitives.ReadInt32LittleEndian(book.AsSpan(at)) : 0;
        double F64(int at) => at + 8 <= book.Length ? BinaryPrimitives.ReadDoubleLittleEndian(book.AsSpan(at)) : 0;

        if (U16(0) != 0x0809 || U16(4) != 0x0600) throw new NotSupportedException("Only BIFF8 (Excel 97-2003) workbooks are supported");

        var strings = new List<string>();
        var fonts = new List<Font>();
        var formats = new Dictionary<int, string>();
        var cellFormats = new List<(int Font, int Format, int Flags, uint Border, uint Fill, int Colors)>();
        var palette = (uint[])SheetSnapshot.IndexedColors.Clone();
        var sheetInfos = new List<(int Offset, bool Visible, bool Worksheet, string Name)>();
        var date1904 = false;

        SKColor Indexed(int index) => index is >= 0 and < 64 ? new SKColor(0xFF000000 | palette[index]) : index == 65 ? SKColors.White : SKColors.Black;

        // ---- workbook globals ----
        for (var at = 0; at + 4 <= book.Length;)
        {
            int id = U16(at), length = U16(at + 2), d = at + 4;
            switch (id)
            {
                case 0x2F: throw new NotSupportedException("Encrypted workbook");
                case 0x22: date1904 = U16(d) == 1; break;
                case 0x31:
                    var font = new Font();
                    font.Props.Size = U16(d) / 20f;
                    font.Props.Italic = (U16(d + 2) & 2) != 0;
                    font.Color = U16(d + 4);
                    font.Props.Bold = U16(d + 6) >= 600;
                    font.Props.Underline = book[d + 10] != 0;
                    font.Props.Font = ShortString(book, d + 14);
                    fonts.Add(font);
                    if (fonts.Count == 4) fonts.Add(font); // index 4 is never stored: keep later indices aligned
                    break;
                case 0x41E: formats[U16(d)] = LongString(book, d + 2); break;
                case 0xE0: cellFormats.Add((U16(d), U16(d + 2), U16(d + 6), (uint)I32(d + 10), (uint)I32(d + 14), U16(d + 18))); break;
                case 0x92:
                    for (int i = 0, count = U16(d); i < count && i < 56; i++) palette[8 + i] = (uint)(book[d + 2 + i * 4] << 16 | book[d + 3 + i * 4] << 8 | book[d + 4 + i * 4]);
                    break;
                case 0x85: sheetInfos.Add((I32(d), book[d + 4] == 0, book[d + 5] == 0, ShortString(book, d + 6))); break;
                case 0xFC: at = ReadSharedStrings(book, at, strings); continue;
            }
            if (id == 0x0A) break;
            at = d + length;
        }
        foreach (var font in fonts) font.Props.Color = font.Color == 0x7FFF ? SKColors.Black : Indexed(font.Color);

        var styles = cellFormats.Select(xf =>
        {
            var style = new CellStyle { Format = NumberFormat.Code(xf.Format, formats) };
            if (xf.Font < fonts.Count) style.Font = fonts[xf.Font].Props;
            style.Align = (xf.Flags & 7) switch { 1 => HAlign.Left, 2 or 6 => HAlign.Center, 3 => HAlign.Right, _ => HAlign.General };
            style.Wrap = (xf.Flags & 8) != 0;
            style.VAlign = ((xf.Flags >> 4) & 7) switch { 0 => 0, 1 => 1, _ => 2 };
            style.BorderLeft = (xf.Border & 0xF) != 0; style.BorderRight = (xf.Border >> 4 & 0xF) != 0;
            style.BorderTop = (xf.Border >> 8 & 0xF) != 0; style.BorderBottom = (xf.Border >> 12 & 0xF) != 0;
            if ((xf.Fill >> 26) == 1) style.Fill = Indexed(xf.Colors & 0x7F);            // solid pattern: foreground colour
            else if ((xf.Fill >> 26) > 1) style.Fill = Indexed(xf.Colors >> 7 & 0x7F);   // other patterns: background colour as an approximation
            return style;
        }).ToList();
        CellStyle Style(int index) => index < styles.Count ? styles[index] : SheetSnapshot.DefaultStyle;

        var defaultFont = fonts.Count > 0 ? fonts[0].Props : SheetSnapshot.DefaultStyle.Font;

        // ---- sheets ----
        var sheets = new List<SheetSnapshot>();
        foreach (var info in sheetInfos)
        {
            var sheet = new SheetSnapshot { Name = info.Name, Visible = info.Visible && info.Worksheet, DefaultRowHeight = 12.75f, DefaultColumnWidth = SheetSnapshot.WidthToPoints(8.43 + 5.0 / 7, defaultFont) };
            sheets.Add(sheet);
            if (!sheet.Visible || info.Offset <= 0 || info.Offset + 4 > book.Length) continue;
            (int Row, int Column)? pendingString = null;
            var autoRatio = SheetSnapshot.AutoRowRatio(defaultFont, sheet.DefaultRowHeight);
            sheet.DefaultRowHeight *= autoRatio;
            var validSetup = true;
            var columnBudget = 1 << 20;                          // COLINFO ranges in all: each may repeat a thousand columns

            // a column past the kept ones goes to a cell that is thrown away (BIFF8 itself stops at 255)
            var discarded = new CellData();
            CellData Cell(int row, int column) => column <= SheetSnapshot.MaxColumn ? sheet.Cell(row, column) : discarded;
            void Number(int row, int column, int xf, double value)
            {
                var cell = Cell(row, column);
                cell.Style = Style(xf);
                cell.Text = NumberFormat.Format(value, cell.Style.Format, date1904);
                cell.Numeric = true;
            }
            static double Rk(int rk)
            {
                double value = (rk & 2) != 0 ? rk >> 2 : BitConverter.Int64BitsToDouble((long)(rk & 0xFFFFFFFC) << 32);
                return (rk & 1) != 0 ? value / 100 : value;
            }

            for (var at = info.Offset; at + 4 <= book.Length;)
            {
                int id = U16(at), length = U16(at + 2), d = at + 4;
                if (id == 0x0A) break;
                if (d + length > book.Length) break;
                switch (id)
                {
                    case 0x225:
                        autoRatio = (U16(d) & 1) != 0 ? 1f : SheetSnapshot.AutoRowRatio(defaultFont, U16(d + 2) / 20f);
                        sheet.DefaultRowHeight = U16(d + 2) / 20f * autoRatio;
                        break;
                    case 0x55: sheet.DefaultColumnWidth = SheetSnapshot.WidthToPoints(U16(d) + 5.0 / 7, defaultFont); break;
                    case 0x7D:
                        for (int c = U16(d), last = Math.Min(U16(d + 2), U16(d) + 1000); c <= last && columnBudget-- > 0; c++)
                        {
                            sheet.ColumnWidths[c] = SheetSnapshot.WidthToPoints(U16(d + 4) / 256.0, defaultFont);
                            if ((U16(d + 8) & 1) != 0) sheet.HiddenColumns.Add(c);
                        }
                        break;
                    case 0x208:
                        var height = U16(d + 6);
                        if ((height & 0x8000) == 0) sheet.RowHeights[U16(d)] = (height & 0x7FFF) / 20f * ((I32(d + 12) & 0x40) != 0 ? 1f : autoRatio);
                        if ((I32(d + 12) & 0x20) != 0) sheet.HiddenRows.Add(U16(d));
                        break;
                    case 0xFD:
                        var shared = Cell(U16(d), U16(d + 2));
                        shared.Style = Style(U16(d + 4));
                        var index = I32(d + 6);
                        shared.Text = index >= 0 && index < strings.Count ? strings[index] : "";
                        break;
                    case 0x204:
                        var label = Cell(U16(d), U16(d + 2));
                        label.Style = Style(U16(d + 4));
                        label.Text = LongString(book, d + 6);
                        break;
                    case 0x203: Number(U16(d), U16(d + 2), U16(d + 4), F64(d + 6)); break;
                    case 0x27E: Number(U16(d), U16(d + 2), U16(d + 4), Rk(I32(d + 6))); break;
                    case 0xBD:
                        for (int column = U16(d + 2), p = d + 4; p + 6 <= d + length - 2; p += 6, column++) Number(U16(d), column, U16(p), Rk(I32(p + 2)));
                        break;
                    case 0x201: Cell(U16(d), U16(d + 2)).Style = Style(U16(d + 4)); break;
                    case 0xBE:
                        for (int column = U16(d + 2), p = d + 4; p + 2 <= d + length - 2; p += 2, column++) Cell(U16(d), column).Style = Style(U16(p));
                        break;
                    case 0x205:
                        var flag = Cell(U16(d), U16(d + 2));
                        flag.Style = Style(U16(d + 4));
                        flag.Centered = true;
                        flag.Text = book[d + 7] == 0 ? (book[d + 6] != 0 ? "TRUE" : "FALSE") : ErrorText(book[d + 6]);
                        break;
                    case 0x06:
                        // formula: the cached result is in the record; a string result follows in a STRING record
                        int row = U16(d), col = U16(d + 2);
                        if (U16(d + 12) == 0xFFFF)
                        {
                            var result = Cell(row, col);
                            result.Style = Style(U16(d + 4));
                            switch (book[d + 6])
                            {
                                case 0: pendingString = (row, col); break;
                                case 1: result.Text = book[d + 8] != 0 ? "TRUE" : "FALSE"; result.Centered = true; break;
                                case 2: result.Text = ErrorText(book[d + 8]); result.Centered = true; break;
                            }
                        }
                        else Number(row, col, U16(d + 4), F64(d + 6));
                        break;
                    case 0x207:
                        if (pendingString is { } target) Cell(target.Row, target.Column).Text = LongString(book, d);
                        pendingString = null;
                        break;
                    case 0xE5:
                        for (int i = 0, count = U16(d); i < count && d + 2 + i * 8 + 8 <= d + length && sheet.Merges.Count < SheetSnapshot.MaxMerges; i++)
                            sheet.Merges.Add((U16(d + 2 + i * 8), U16(d + 6 + i * 8), U16(d + 4 + i * 8), U16(d + 8 + i * 8)));
                        break;
                    case 0x26: sheet.MarginLeft = (float)F64(d) * 72; break;
                    case 0x27: sheet.MarginRight = (float)F64(d) * 72; break;
                    case 0x28: sheet.MarginTop = (float)F64(d) * 72; break;
                    case 0x29: sheet.MarginBottom = (float)F64(d) * 72; break;
                    case 0x2B: sheet.PrintGridlines = U16(d) != 0; break;
                    case 0x81: sheet.FitToPage = (U16(d) & 0x100) != 0; break;
                    case 0xA1:
                        var bits = U16(d + 10);
                        validSetup = (bits & 4) == 0; // fNoPls: paper, scale and orientation were never set
                        if (validSetup)
                        {
                            (sheet.PaperWidth, sheet.PaperHeight) = SheetSnapshot.Paper(U16(d));
                            if (U16(d + 2) is > 0 and < 1000) sheet.Scale = U16(d + 2) / 100f;
                            sheet.Landscape = (bits & 2) == 0;
                        }
                        sheet.FitWidth = U16(d + 6); sheet.FitHeight = U16(d + 8);
                        break;
                    case 0x1B:
                        for (int i = 0, count = U16(d); i < count && d + 2 + i * 6 + 2 <= d + length; i++) sheet.RowBreaks.Add(U16(d + 2 + i * 6));
                        break;
                    case 0x1A:
                        for (int i = 0, count = U16(d); i < count && d + 2 + i * 6 + 2 <= d + length; i++) sheet.ColumnBreaks.Add(U16(d + 2 + i * 6));
                        break;
                }
                at = d + length;
            }
        }
        return sheets;
    }

    private static string ErrorText(byte code) => code switch { 0x00 => "#NULL!", 0x07 => "#DIV/0!", 0x0F => "#VALUE!", 0x17 => "#REF!", 0x1D => "#NAME?", 0x24 => "#NUM!", 0x2A => "#N/A", _ => "#ERR" };

    /// <summary>String with a 1-byte length (font and sheet names).</summary>
    private static string ShortString(byte[] b, int at)
    {
        if (at + 2 > b.Length) return "";
        int count = b[at], wide = b[at + 1] & 1;
        return wide != 0 ? Encoding.Unicode.GetString(b, at + 2, Math.Min(count * 2, b.Length - at - 2)) : Encoding.Latin1.GetString(b, at + 2, Math.Min(count, b.Length - at - 2));
    }

    /// <summary>String with a 2-byte length (labels, formats, formula results).</summary>
    private static string LongString(byte[] b, int at)
    {
        if (at + 3 > b.Length) return "";
        int count = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at)), flags = b[at + 2], start = at + 3;
        if ((flags & 8) != 0) start += 2;
        if ((flags & 4) != 0) start += 4;
        return (flags & 1) != 0 ? Encoding.Unicode.GetString(b, start, Math.Min(count * 2, Math.Max(0, b.Length - start))) : Encoding.Latin1.GetString(b, start, Math.Min(count, Math.Max(0, b.Length - start)));
    }

    /// <summary>
    /// The shared string table runs over the SST record and any number of CONTINUE records. A string may be split
    /// at a record boundary, in which case the continuation starts with a fresh flags byte (it can switch between
    /// 8-bit and 16-bit characters mid-string). Returns the offset of the first record after the table.
    /// </summary>
    private static int ReadSharedStrings(byte[] b, int at, List<string> strings)
    {
        int U16(int p) => p + 2 <= b.Length ? BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p)) : 0;
        var end = at + 4 + U16(at + 2);           // end of the current record's data
        var p = at + 4;
        var total = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p + 4));
        p += 8;

        bool NextRecord()
        {
            // step into the following CONTINUE record, if there is one
            if (end + 4 > b.Length || U16(end) != 0x3C) return false;
            p = end + 4;
            end = p + U16(end + 2);
            return true;
        }

        for (var i = 0; i < total; i++)
        {
            if (p >= end && !NextRecord()) break;
            if (p + 3 > b.Length) break;
            int count = U16(p), flags = b[p + 2];
            p += 3;
            int runs = 0, extra = 0;
            if ((flags & 8) != 0) { runs = U16(p); p += 2; }
            if ((flags & 4) != 0) { extra = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p)); p += 4; }
            var wide = (flags & 1) != 0;
            var text = new StringBuilder(count);
            while (text.Length < count)
            {
                if (p >= end)
                {
                    if (!NextRecord()) break;
                    wide = (b[p++] & 1) != 0;   // continuation restates the character width
                }
                var available = Math.Min(count - text.Length, (end - p) / (wide ? 2 : 1));
                if (available <= 0) { p = end; continue; }
                text.Append(wide ? Encoding.Unicode.GetString(b, p, available * 2) : Encoding.Latin1.GetString(b, p, available));
                p += available * (wide ? 2 : 1);
            }
            strings.Add(text.ToString());
            // formatting runs and phonetic data follow the characters and may also cross record boundaries
            for (var skip = runs * 4 + extra; skip > 0;)
            {
                if (p >= end && !NextRecord()) break;
                var step = Math.Min(skip, end - p);
                p += step;
                skip -= step;
            }
        }
        while (end + 4 <= b.Length && U16(end) == 0x3C) end = end + 4 + U16(end + 2); // any unread CONTINUE records
        return end;
    }
}
