using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using FastThumb.Layout;
using SkiaSharp;

namespace FastThumb.Cells;

/// <summary>
/// Excel binary workbook (XLSB): the same package as XLSX, but workbook, styles, shared strings and sheets are
/// streams of binary records (public MS-XLSB specification) instead of XML. The theme and the relationship parts
/// stay XML. Produces the same model as XlsxReader; formula cells show their cached value.
/// Not handled: table styles, charts, images, conditional formatting.
/// </summary>
internal static class XlsbReader
{
    private const int MaxRows = 20000;
    private const int MaxColumns = (1 << 14) - 1;                // widths and hidden flags: Excel's last column, XFD

    public static List<SheetSnapshot> Read(byte[] data)
    {
        using var zip = new ZipArchive(new MemoryStream(data, false), ZipArchiveMode.Read);
        byte[]? Part(string path)
        {
            var entry = zip.GetEntry(path);
            return entry == null ? null : Guard.ReadAll(entry);
        }
        XElement? Xml(string path) => Part(path) is { } bytes ? Guard.LoadXml(new MemoryStream(bytes)).Root : null;

        var workbook = Part("xl/workbook.bin") ?? throw new InvalidDataException("xl/workbook.bin not found");
        XNamespace rel = "http://schemas.openxmlformats.org/package/2006/relationships";
        var targets = (Xml("xl/_rels/workbook.bin.rels")?.Elements(rel + "Relationship") ?? [])
            .Where(r => r.Attribute("Id") != null && r.Attribute("Target") != null)
            .ToDictionary(r => (string)r.Attribute("Id")!, r => ((string)r.Attribute("Target")!) is var t && t.StartsWith('/') ? t.TrimStart('/') : "xl/" + t);

        var theme = XlsxReader.ReadTheme(Xml("xl/theme/theme1.xml"));
        var styles = ReadStyles(Part("xl/styles.bin"), theme);
        var strings = new List<string>();
        if (Part("xl/sharedStrings.bin") is { } shared)
            foreach (var (type, body) in Records(shared))
                if (type == 19) strings.Add(WideString(body, 1, out _));   // BrtSSTItem: flags byte, then the text (runs follow)

        var date1904 = false;
        var sheets = new List<SheetSnapshot>();
        foreach (var (type, body) in Records(workbook))
        {
            if (type == 153 && body.Length >= 4) date1904 = (body[0] & 1) != 0;          // BrtWbProp
            if (type != 156 || body.Length < 12) continue;                                // BrtBundleSh
            var sheet = new SheetSnapshot { Visible = BinaryPrimitives.ReadUInt32LittleEndian(body) == 0 };
            var id = WideString(body, 8, out var next);
            sheet.Name = WideString(body, next, out _);
            sheets.Add(sheet);
            if (!sheet.Visible || !targets.TryGetValue(id, out var path) || Part(path) is not { } part) continue;
            var defaultFont = styles.Count > 0 ? styles[0].Font : SheetSnapshot.DefaultStyle.Font;
            sheet.DefaultColumnWidth = SheetSnapshot.WidthToPoints(8.43 + 5.0 / 7, defaultFont);
            ReadSheet(part, sheet, strings, styles, date1904, defaultFont);
        }
        return sheets;
    }

    /// <summary>Record stream: type and length are variable-length integers (7 bits per byte, low group first).</summary>
    private static IEnumerable<(int Type, byte[] Body)> Records(byte[] data)
    {
        var position = 0;
        while (position < data.Length)
        {
            int type = data[position++];
            if ((type & 0x80) != 0 && position < data.Length) type = (type & 0x7F) | (data[position++] << 7);
            int length = 0, shift = 0;
            while (position < data.Length)
            {
                var part = data[position++];
                length |= (part & 0x7F) << shift;
                shift += 7;
                if ((part & 0x80) == 0 || shift >= 28) break;
            }
            if (length < 0 || position + length > data.Length) yield break;
            yield return (type, data.AsSpan(position, length).ToArray());
            position += length;
        }
    }

    /// <summary>Length-prefixed UTF-16 string; a length of 0xFFFFFFFF means "no string".</summary>
    private static string WideString(byte[] body, int offset, out int next)
    {
        next = offset + 4;
        if (offset + 4 > body.Length) return "";
        var count = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(offset));
        if (count == 0xFFFFFFFF || count == 0 || next + count * 2 > body.Length) return "";
        next += (int)count * 2;
        return Encoding.Unicode.GetString(body, offset + 4, (int)count * 2);
    }

    private static List<CellStyle> ReadStyles(byte[]? data, List<SKColor> theme)
    {
        var result = new List<CellStyle>();
        if (data == null) return result;

        // 8-byte colour: type (auto / indexed / rgb / theme), index, tint, RGBA
        SKColor? Color(byte[] b, int o)
        {
            if (o + 8 > b.Length) return null;
            SKColor? color = (b[o] >> 1) switch
            {
                1 => b[o + 1] < SheetSnapshot.IndexedColors.Length ? new SKColor(0xFF000000 | SheetSnapshot.IndexedColors[b[o + 1]]) : b[o + 1] == 65 ? SKColors.White : SKColors.Black,
                2 => new SKColor(b[o + 4], b[o + 5], b[o + 6]),
                3 => b[o + 1] < theme.Count ? theme[b[o + 1]] : null,
                _ => null,
            };
            var tint = BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(o + 2)) / 32767.0;
            return color != null && tint != 0 ? XlsxReader.Tint(color.Value, tint) : color;
        }

        var formats = new Dictionary<int, string>();
        var fonts = new List<RunProps>();
        var fills = new List<SKColor?>();
        var borders = new List<(bool Left, bool Top, bool Right, bool Bottom)>();
        var inCellFormats = false;
        foreach (var (type, b) in Records(data))
        {
            switch (type)
            {
                case 44 when b.Length >= 6: formats[BinaryPrimitives.ReadUInt16LittleEndian(b)] = WideString(b, 2, out _); break;   // BrtFmt
                case 43 when b.Length >= 25:                                                                                          // BrtFont
                    fonts.Add(new RunProps
                    {
                        Size = BinaryPrimitives.ReadUInt16LittleEndian(b) / 20f,
                        Italic = (b[2] & 2) != 0,
                        Bold = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(4)) >= 600,
                        Underline = b[8] != 0,
                        Color = Color(b, 12) ?? SKColors.Black,
                        Font = WideString(b, 21, out _) is { Length: > 0 } name ? name : "Calibri",
                    });
                    break;
                case 45 when b.Length >= 20:                                                                                          // BrtFill
                    var pattern = BinaryPrimitives.ReadUInt32LittleEndian(b);
                    fills.Add(pattern == 0 ? null : pattern == 1 ? Color(b, 4) : Color(b, 12) ?? Color(b, 4));
                    break;
                case 46 when b.Length >= 41:                                                                                          // BrtBorder: top, bottom, left, right (style + colour each)
                    bool Side(int index) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(1 + index * 10)) != 0;
                    borders.Add((Side(2), Side(0), Side(3), Side(1)));
                    break;
                case 617: inCellFormats = true; break;                                                                                // BrtBeginCellXFs
                case 618: inCellFormats = false; break;
                case 47 when inCellFormats && b.Length >= 14:                                                                         // BrtXF
                    var style = new CellStyle();
                    int fontId = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(4)), fillId = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(6)), borderId = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(8));
                    if (fontId < fonts.Count) style.Font = fonts[fontId];
                    if (fillId < fills.Count) style.Fill = fills[fillId];
                    if (borderId < borders.Count) (style.BorderLeft, style.BorderTop, style.BorderRight, style.BorderBottom) = borders[borderId];
                    style.Format = NumberFormat.Code(BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(2)), formats);
                    style.Align = (b[12] & 7) switch { 1 => HAlign.Left, 2 or 6 => HAlign.Center, 3 => HAlign.Right, _ => HAlign.General };
                    style.VAlign = ((b[12] >> 3) & 7) switch { 0 => 0, 1 => 1, _ => 2 };
                    style.Wrap = (b[12] & 0x40) != 0;
                    result.Add(style);
                    break;
            }
        }
        return result;
    }

    private static void ReadSheet(byte[] data, SheetSnapshot sheet, List<string> strings, List<CellStyle> styles, bool date1904, RunProps defaultFont)
    {
        var row = -1;
        var columnBudget = 1 << 20;                              // column ranges in all: each may repeat a thousand columns
        var autoRatio = SheetSnapshot.AutoRowRatio(defaultFont, sheet.DefaultRowHeight);
        sheet.DefaultRowHeight *= autoRatio;
        foreach (var (type, b) in Records(data))
        {
            switch (type)
            {
                case 147 when b.Length >= 2: sheet.FitToPage = (b[1] & 1) != 0; break;                       // BrtWsProp
                case 485 when b.Length >= 10:                                                                 // BrtWsFmtInfo
                    var defaultWidth = BinaryPrimitives.ReadUInt32LittleEndian(b);
                    if (defaultWidth != 0xFFFFFFFF && defaultWidth > 0) sheet.DefaultColumnWidth = SheetSnapshot.WidthToPoints(defaultWidth / 256.0, defaultFont);
                    var storedDefault = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(6)) / 20f;
                    if (storedDefault > 0)
                    {
                        autoRatio = (b[8] & 1) != 0 ? 1f : SheetSnapshot.AutoRowRatio(defaultFont, storedDefault);
                        sheet.DefaultRowHeight = storedDefault * autoRatio;
                    }
                    break;
                case 60 when b.Length >= 18:                                                                  // BrtColInfo
                    int first = BinaryPrimitives.ReadInt32LittleEndian(b), last = Math.Min(BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(4)), first + 1000);
                    var width = SheetSnapshot.WidthToPoints(BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(8)) / 256.0, defaultFont);
                    for (var c = Math.Max(0, first); c <= last && c <= MaxColumns && columnBudget-- > 0; c++)
                    {
                        sheet.ColumnWidths[c] = width;
                        if ((b[16] & 1) != 0) sheet.HiddenColumns.Add(c);
                    }
                    break;
                case 0 when b.Length >= 12:                                                                   // BrtRowHdr
                    row = BinaryPrimitives.ReadInt32LittleEndian(b);
                    if (row > MaxRows) return;
                    var height = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(8)) / 20f;
                    if (height > 0) sheet.RowHeights[row] = (b[11] & 0x20) != 0 ? height : height * autoRatio;
                    if ((b[11] & 0x10) != 0) sheet.HiddenRows.Add(row);
                    break;
                case >= 1 and <= 11 when b.Length >= 8 && row >= 0:                                           // cells: column, style, then the value
                    if (BinaryPrimitives.ReadInt32LittleEndian(b) is < 0 or > SheetSnapshot.MaxColumn) break;     // past the kept columns
                    var cell = sheet.Cell(row, BinaryPrimitives.ReadInt32LittleEndian(b));
                    var styleIndex = (int)(BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4)) & 0xFFFFFF);
                    var style = cell.Style = styleIndex < styles.Count ? styles[styleIndex] : SheetSnapshot.DefaultStyle;
                    void Number(double value) { cell.Text = NumberFormat.Format(value, style.Format, date1904); cell.Numeric = true; }
                    switch (type)
                    {
                        case 2 when b.Length >= 12:                                                           // RK: packed integer or double, optionally / 100
                            var rk = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(8));
                            var value = (rk & 2) != 0 ? rk >> 2 : BitConverter.Int64BitsToDouble((long)(rk & 0xFFFFFFFC) << 32);
                            Number((rk & 1) != 0 ? value / 100 : value);
                            break;
                        case 5 or 9 when b.Length >= 16: Number(BinaryPrimitives.ReadDoubleLittleEndian(b.AsSpan(8))); break;
                        case 4 or 10 when b.Length >= 9: cell.Text = b[8] != 0 ? "TRUE" : "FALSE"; cell.Centered = true; break;
                        case 3 or 11 when b.Length >= 9:
                            cell.Text = b[8] switch { 0x00 => "#NULL!", 0x07 => "#DIV/0!", 0x0F => "#VALUE!", 0x17 => "#REF!", 0x1D => "#NAME?", 0x24 => "#NUM!", 0x2A => "#N/A", _ => "#ERR" };
                            cell.Centered = true;
                            break;
                        case 6 or 8: cell.Text = WideString(b, 8, out _); break;
                        case 7 when b.Length >= 12:
                            var index = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(8));
                            cell.Text = index >= 0 && index < strings.Count ? strings[index] : "";
                            break;
                    }
                    break;
                case 176 when b.Length >= 16 && sheet.Merges.Count < SheetSnapshot.MaxMerges:                // BrtMergeCell: first row, last row, first column, last column
                    sheet.Merges.Add((BinaryPrimitives.ReadInt32LittleEndian(b), BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(8)),
                        BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(4)), BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(12))));
                    break;
                case 476 when b.Length >= 32:                                                                 // BrtMargins (inches)
                    float Inch(int offset) => (float)(BinaryPrimitives.ReadDoubleLittleEndian(b.AsSpan(offset)) * 72);
                    sheet.MarginLeft = Inch(0); sheet.MarginRight = Inch(8); sheet.MarginTop = Inch(16); sheet.MarginBottom = Inch(24);
                    break;
                case 477 when b.Length >= 2: sheet.PrintGridlines = (b[0] & 8) != 0; break;                  // BrtPrintOptions
                case 478 when b.Length >= 34:                                                                 // BrtPageSetup
                    (sheet.PaperWidth, sheet.PaperHeight) = SheetSnapshot.Paper(BinaryPrimitives.ReadInt32LittleEndian(b));
                    var scale = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(4));
                    sheet.Scale = scale is >= 10 and <= 400 ? scale / 100f : 1;
                    sheet.FitWidth = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(24)); sheet.FitHeight = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(28));
                    sheet.Landscape = (b[32] & 2) != 0;
                    break;
            }
        }
    }
}
