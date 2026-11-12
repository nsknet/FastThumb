using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using FastThumb.Layout;
using SkiaSharp;

namespace FastThumb.Cells;

/// <summary>
/// SpreadsheetML package reader (XLSX / XLSM / XLTX / XLTM). Shared strings, styles (fonts, fills, borders,
/// alignment, number formats, theme and indexed colours), column / row geometry, merges, page setup, print
/// area and manual breaks. Sheet data is streamed. Formula cells show the cached value stored in the file.
/// Not handled: charts, images, conditional formatting, rich text inside a cell (plain text is kept).
/// </summary>
internal static class XlsxReader
{
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const int MaxRows = 20000;
    private const int MaxColumns = (1 << 14) - 1;                // widths and hidden flags: Excel's last column, XFD

    public static List<SheetSnapshot> Read(byte[] data)
    {
        using var zip = new ZipArchive(new MemoryStream(data, false), ZipArchiveMode.Read);
        XElement? Xml(string path)
        {
            var entry = zip.GetEntry(path);
            if (entry == null) return null;
            using var stream = Guard.Open(entry);
            return Guard.LoadXml(stream).Root;
        }

        var workbook = Xml("xl/workbook.xml") ?? throw new InvalidDataException("xl/workbook.xml not found");
        var date1904 = (string?)workbook.Element(S + "workbookPr")?.Attribute("date1904") is "1" or "true";
        var targets = (Xml("xl/_rels/workbook.xml.rels")?.Elements(Rel + "Relationship") ?? [])
            .Where(r => r.Attribute("Id") != null && r.Attribute("Target") != null)
            .ToDictionary(r => (string)r.Attribute("Id")!, r => ((string)r.Attribute("Target")!) is var t && t.StartsWith('/') ? t.TrimStart('/') : "xl/" + t);

        // theme colours in the order the styles' theme index uses (light/dark pairs are swapped on purpose)
        var theme = ReadTheme(Xml("xl/theme/theme1.xml"));

        var styles = ReadStyles(Xml("xl/styles.xml"), theme);

        var strings = new List<string>();
        if (zip.GetEntry("xl/sharedStrings.xml") is { } sharedEntry)
        {
            using var stream = Guard.Open(sharedEntry);
            using var reader = XmlReader.Create(stream, Guard.Xml);
            StringBuilder? current = null;
            var phonetic = 0;
            var more = reader.Read();
            while (more)
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    if (reader.LocalName == "si") { current = new StringBuilder(); if (reader.IsEmptyElement) { strings.Add(""); current = null; } }
                    else if (reader.LocalName == "rPh" && !reader.IsEmptyElement) phonetic++;
                    else if (reader.LocalName == "t" && current != null && phonetic == 0 && !reader.IsEmptyElement)
                    {
                        current.Append(reader.ReadElementContentAsString()); // leaves the reader on the node after </t>
                        continue;
                    }
                }
                else if (reader.NodeType == XmlNodeType.EndElement)
                {
                    if (reader.LocalName == "si") { strings.Add(current?.ToString() ?? ""); current = null; }
                    else if (reader.LocalName == "rPh") phonetic--;
                }
                more = reader.Read();
            }
        }

        var sheets = new List<SheetSnapshot>();
        var index = 0;
        foreach (var element in workbook.Element(S + "sheets")?.Elements(S + "sheet") ?? [])
        {
            var sheet = new SheetSnapshot { Name = (string?)element.Attribute("name") ?? "", Visible = (string?)element.Attribute("state") is null or "visible" };
            var localIndex = index++;
            sheets.Add(sheet);
            if (!sheet.Visible || (string?)element.Attribute(R + "id") is not { } id || !targets.TryGetValue(id, out var path) || zip.GetEntry(path) is not { } entry) continue;
            var defaultFont = styles.Count > 0 ? styles[0].Font : SheetSnapshot.DefaultStyle.Font;
            sheet.DefaultColumnWidth = SheetSnapshot.WidthToPoints(8.43 + 5.0 / 7, defaultFont);
            using (var stream = Guard.Open(entry)) ReadSheet(stream, sheet, strings, styles, date1904, defaultFont);
            ApplyTables(Xml, path, sheet, theme);

            var printArea = workbook.Element(S + "definedNames")?.Elements(S + "definedName")
                .FirstOrDefault(d => (string?)d.Attribute("name") == "_xlnm.Print_Area" && (int?)d.Attribute("localSheetId") == localIndex)?.Value;
            if (printArea != null && ParseRange(printArea[(printArea.LastIndexOf('!') + 1)..].Replace("$", "")) is { } area) sheet.PrintArea = area;
        }
        return sheets;
    }

    /// <summary>Theme colours in the order the styles' theme index uses (light/dark pairs are swapped on purpose).</summary>
    internal static List<SKColor> ReadTheme(XElement? root)
    {
        var theme = new List<SKColor>();
        if (root?.Descendants(A + "clrScheme").FirstOrDefault() is { } scheme)
        {
            var byName = scheme.Elements().ToDictionary(e => e.Name.LocalName, e => FormatDetector.Color("#" + ((string?)e.Element(A + "srgbClr")?.Attribute("val") ?? (string?)e.Element(A + "sysClr")?.Attribute("lastClr"))) ?? SKColors.Black);
            foreach (var name in new[] { "lt1", "dk1", "lt2", "dk2", "accent1", "accent2", "accent3", "accent4", "accent5", "accent6", "hlink", "folHlink" })
                theme.Add(byName.GetValueOrDefault(name, SKColors.Black));
        }
        return theme;
    }

    /// <summary>Tint &gt; 0 lightens towards white, &lt; 0 darkens towards black.</summary>
    internal static SKColor Tint(SKColor color, double tint)
    {
        byte Channel(byte v) => (byte)Math.Clamp(tint > 0 ? v + (255 - v) * tint : v * (1 + tint), 0, 255);
        return new SKColor(Channel(color.Red), Channel(color.Green), Channel(color.Blue));
    }

    /// <summary>
    /// "Format as Table" ranges: their look comes from a named built-in table style, not from cell formats.
    /// The built-in families are approximated: Light 1-7 (bold header, light bands), Light 8-14 (filled header,
    /// row lines), Light 15-21 (grid, light bands), Medium / Dark (filled header, bands). Direct cell fills win.
    /// </summary>
    private static void ApplyTables(Func<string, XElement?> xml, string sheetPath, SheetSnapshot sheet, List<SKColor> theme)
    {
        var folder = sheetPath[..(sheetPath.LastIndexOf('/') + 1)];
        var rels = xml(folder + "_rels/" + sheetPath[folder.Length..] + ".rels");
        if (rels == null) return;
        var derived = new Dictionary<(CellStyle, int), CellStyle>();
        var budget = 8L << 20;                                   // table cells styled in all: tables may be many and overlap
        foreach (var rel in rels.Elements(Rel + "Relationship"))
        {
            if (!((string?)rel.Attribute("Type") ?? "").EndsWith("/table", StringComparison.Ordinal) || (string?)rel.Attribute("Target") is not { } target) continue;
            var parts = new List<string>(folder.Split('/', StringSplitOptions.RemoveEmptyEntries));
            foreach (var piece in target.TrimStart('/').Split('/')) { if (piece == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); } else parts.Add(piece); }
            var table = xml(target.StartsWith('/') ? target.TrimStart('/') : string.Join('/', parts));
            if (table == null || ParseRange((string?)table.Attribute("ref") ?? "") is not { } range) continue;
            var info = table.Element(S + "tableStyleInfo");
            var name = (string?)info?.Attribute("name") ?? "";
            if (name.Length == 0) continue;
            var headerRows = (int?)table.Attribute("headerRowCount") ?? 1;
            var stripes = (string?)info?.Attribute("showRowStripes") is "1" or "true";
            var digits = new string(name.Where(char.IsDigit).ToArray());
            var number = int.TryParse(digits, out var parsed) ? parsed : 1;
            var light = name.Contains("Light", StringComparison.Ordinal);
            var group = (number - 1) / 7;
            var slot = (number - 1) % 7;                       // 0 = neutral, 1-6 = accent 1-6
            var accent = slot == 0 || theme.Count < 10 ? SKColors.Black : theme[3 + slot];
            var band = slot == 0 ? new SKColor(0xFFD9D9D9) : new SKColor((byte)(accent.Red * 0.2 + 204), (byte)(accent.Green * 0.2 + 204), (byte)(accent.Blue * 0.2 + 204));
            var filledHeader = !light || group == 1;
            var rowLines = light && group >= 1;

            CellStyle Derive(CellStyle source, int kind)
            {
                if (derived.TryGetValue((source, kind), out var hit)) return hit;
                var style = source.Clone();
                if (kind == 0)
                {
                    style.Font = style.Font.Clone();
                    style.Font.Bold = true;
                    if (filledHeader) { style.Fill ??= accent; style.Font.Color = SKColors.White; } else style.BorderBottom = true;
                }
                else
                {
                    if (kind == 1) style.Fill ??= band;
                    if (rowLines) style.BorderBottom = true;
                }
                return derived[(source, kind)] = style;
            }

            // only the part of the sheet that is kept: rows up to MaxRows, columns up to MaxColumn
            int lastRow = (int)Math.Min(Math.Min(range.Row2, range.Row1 + (long)MaxRows), MaxRows), lastColumn = (int)Math.Min(Math.Min(range.Column2, range.Column1 + 200L), SheetSnapshot.MaxColumn);
            if (lastRow >= range.Row1 && lastColumn >= range.Column1 && (budget -= (long)(lastRow - range.Row1 + 1) * (lastColumn - range.Column1 + 1)) < 0) break;
            for (var r = range.Row1; r <= lastRow; r++)
            {
                var header = r - range.Row1 < headerRows;
                var kind = header ? 0 : stripes && (!light || group != 1) && (r - range.Row1 - headerRows) % 2 == 0 ? 1 : 2;
                if (kind == 2 && !rowLines) continue;
                for (var c = range.Column1; c <= lastColumn; c++)
                {
                    var cell = sheet.Cell(r, c);
                    cell.Style = Derive(cell.Style, kind);
                }
            }
        }
    }

    private static List<CellStyle> ReadStyles(XElement? root, List<SKColor> theme)
    {
        var result = new List<CellStyle>();
        if (root == null) return result;

        SKColor? Color(XElement? e)
        {
            if (e == null) return null;
            SKColor? color = null;
            if ((string?)e.Attribute("rgb") is { Length: >= 6 } rgb) color = FormatDetector.Color("#" + rgb[^6..]);
            else if ((int?)e.Attribute("theme") is { } themeIndex && themeIndex < theme.Count) color = theme[themeIndex];
            else if ((int?)e.Attribute("indexed") is { } indexed) color = indexed < SheetSnapshot.IndexedColors.Length ? new SKColor(0xFF000000 | SheetSnapshot.IndexedColors[indexed]) : indexed == 65 ? SKColors.White : SKColors.Black;
            return color != null && (double?)e.Attribute("tint") is { } tint && tint != 0 ? Tint(color.Value, tint) : color;
        }

        var formats = (root.Element(S + "numFmts")?.Elements(S + "numFmt") ?? [])
            .Where(f => f.Attribute("numFmtId") != null).ToDictionary(f => (int)f.Attribute("numFmtId")!, f => (string?)f.Attribute("formatCode") ?? "General");
        var fonts = (root.Element(S + "fonts")?.Elements(S + "font") ?? []).Select(f =>
        {
            static bool On(XElement? e) => e != null && (string?)e.Attribute("val") is not ("0" or "false");
            return new RunProps
            {
                Font = (string?)f.Element(S + "name")?.Attribute("val") ?? "Calibri",
                Size = (float?)f.Element(S + "sz")?.Attribute("val") ?? 11,
                Bold = On(f.Element(S + "b")),
                Italic = On(f.Element(S + "i")),
                Underline = f.Element(S + "u") is { } u && (string?)u.Attribute("val") != "none",
                Color = Color(f.Element(S + "color")) ?? SKColors.Black,
            };
        }).ToList();
        var fills = (root.Element(S + "fills")?.Elements(S + "fill") ?? []).Select(f =>
        {
            var pattern = f.Element(S + "patternFill");
            var type = (string?)pattern?.Attribute("patternType");
            if (type is null or "none") return f.Element(S + "gradientFill") is { } gradient ? Color(gradient.Descendants(S + "color").FirstOrDefault()) : null;
            return type == "solid" ? Color(pattern!.Element(S + "fgColor")) : Color(pattern!.Element(S + "bgColor")) ?? Color(pattern.Element(S + "fgColor"));
        }).ToList();
        var borders = (root.Element(S + "borders")?.Elements(S + "border") ?? []).Select(b =>
        {
            bool Side(string name) => (string?)b.Element(S + name)?.Attribute("style") is not (null or "none");
            return (Left: Side("left") || Side("start"), Top: Side("top"), Right: Side("right") || Side("end"), Bottom: Side("bottom"));
        }).ToList();

        foreach (var xf in root.Element(S + "cellXfs")?.Elements(S + "xf") ?? [])
        {
            var style = new CellStyle();
            if ((int?)xf.Attribute("fontId") is { } fontId && fontId < fonts.Count) style.Font = fonts[fontId];
            if ((int?)xf.Attribute("fillId") is { } fillId && fillId < fills.Count) style.Fill = fills[fillId];
            if ((int?)xf.Attribute("borderId") is { } borderId && borderId < borders.Count)
                (style.BorderLeft, style.BorderTop, style.BorderRight, style.BorderBottom) = borders[borderId];
            style.Format = NumberFormat.Code((int?)xf.Attribute("numFmtId") ?? 0, formats);
            if (xf.Element(S + "alignment") is { } alignment)
            {
                style.Align = (string?)alignment.Attribute("horizontal") switch { "left" => HAlign.Left, "center" or "centerContinuous" => HAlign.Center, "right" => HAlign.Right, _ => HAlign.General };
                style.VAlign = (string?)alignment.Attribute("vertical") switch { "top" => 0, "center" => 1, _ => 2 };
                style.Wrap = (string?)alignment.Attribute("wrapText") is "1" or "true";
            }
            result.Add(style);
        }
        return result;
    }

    private static (int Row1, int Column1, int Row2, int Column2)? ParseRange(string range)
    {
        var parts = range.Split(':');
        if (ParseCell(parts[0]) is not { } a) return null;
        var b = parts.Length > 1 ? ParseCell(parts[1]) ?? a : a;
        return (a.Row, a.Column, b.Row, b.Column);
    }

    private static (int Row, int Column)? ParseCell(string reference)
    {
        int column = 0, i = 0;
        for (; i < reference.Length && char.IsAsciiLetter(reference[i]); i++) column = column * 26 + (char.ToUpperInvariant(reference[i]) - 'A' + 1);
        if (i > 3) return null;                                  // past XFD: not a cell (and no overflow)
        return column > 0 && int.TryParse(reference.AsSpan(i), out var row) && row > 0 ? (row - 1, column - 1) : null;
    }

    private static void ReadSheet(Stream stream, SheetSnapshot sheet, List<string> strings, List<CellStyle> styles, bool date1904, RunProps defaultFont)
    {
        using var reader = XmlReader.Create(stream, Guard.Xml);
        float? Number(string name) => float.TryParse(reader.GetAttribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
        bool On(string name) => reader.GetAttribute(name) is "1" or "true";
        var row = -1;
        var columnBudget = 1 << 20;                              // <col> ranges in all: each may repeat a thousand columns
        var autoRatio = SheetSnapshot.AutoRowRatio(defaultFont, sheet.DefaultRowHeight);
        sheet.DefaultRowHeight *= autoRatio;

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;
            switch (reader.LocalName)
            {
                case "sheetFormatPr":
                    if (Number("defaultRowHeight") is { } storedDefault)
                    {
                        autoRatio = On("customHeight") ? 1f : SheetSnapshot.AutoRowRatio(defaultFont, storedDefault);
                        sheet.DefaultRowHeight = storedDefault * autoRatio;
                    }
                    if (Number("defaultColWidth") is { } defaultWidth) sheet.DefaultColumnWidth = SheetSnapshot.WidthToPoints(defaultWidth, defaultFont);
                    break;
                case "pageSetUpPr": sheet.FitToPage = On("fitToPage"); break;
                case "col":
                    int min = (int)(Number("min") ?? 1) - 1, max = Math.Min((int)(Number("max") ?? 1) - 1, min + 1000);
                    for (var c = Math.Max(0, min); c <= max && c <= MaxColumns && columnBudget-- > 0; c++)
                    {
                        if (Number("width") is { } width) sheet.ColumnWidths[c] = SheetSnapshot.WidthToPoints(width, defaultFont);
                        if (On("hidden")) sheet.HiddenColumns.Add(c);
                    }
                    break;
                case "row":
                    row = Number("r") is { } r ? (int)r - 1 : row + 1;
                    if (row > MaxRows) return;
                    if (Number("ht") is { } height) sheet.RowHeights[row] = On("customHeight") ? height : height * autoRatio;
                    if (On("hidden")) sheet.HiddenRows.Add(row);
                    break;
                case "c":
                    var position = ParseCell(reader.GetAttribute("r") ?? "");
                    var type = reader.GetAttribute("t");
                    var styleIndex = (int)(Number("s") ?? 0);
                    var style = styleIndex < styles.Count ? styles[styleIndex] : SheetSnapshot.DefaultStyle;
                    string? value = null;
                    if (!reader.IsEmptyElement)
                    {
                        using var cell = reader.ReadSubtree();
                        while (cell.Read())
                        {
                            if (cell.NodeType != XmlNodeType.Element) continue;
                            if (cell.LocalName == "v") value = cell.ReadElementContentAsString();
                            else if (cell.LocalName == "t") value = (value ?? "") + cell.ReadElementContentAsString(); // inline string
                        }
                    }
                    if (position is not { Row: <= MaxRows, Column: <= SheetSnapshot.MaxColumn }) break;
                    var data = sheet.Cell(position.Value.Row, position.Value.Column);
                    data.Style = style;
                    if (value == null) break;
                    switch (type)
                    {
                        case "s": data.Text = int.TryParse(value, out var shared) && shared < strings.Count ? strings[shared] : ""; break;
                        case "str" or "inlineStr": data.Text = value; break;
                        case "b": data.Text = value == "1" ? "TRUE" : "FALSE"; data.Centered = true; break;
                        case "e": data.Text = value; data.Centered = true; break;
                        default:
                            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) { data.Text = NumberFormat.Format(number, style.Format, date1904); data.Numeric = true; }
                            else data.Text = value;
                            break;
                    }
                    break;
                case "mergeCell":
                    if (sheet.Merges.Count < SheetSnapshot.MaxMerges && ParseRange(reader.GetAttribute("ref") ?? "") is { } merge) sheet.Merges.Add(merge);
                    break;
                case "pageMargins":
                    sheet.MarginLeft = (Number("left") ?? 0.7f) * 72; sheet.MarginRight = (Number("right") ?? 0.7f) * 72;
                    sheet.MarginTop = (Number("top") ?? 0.75f) * 72; sheet.MarginBottom = (Number("bottom") ?? 0.75f) * 72;
                    break;
                case "pageSetup":
                    (sheet.PaperWidth, sheet.PaperHeight) = SheetSnapshot.Paper((int)(Number("paperSize") ?? 1));
                    sheet.Landscape = reader.GetAttribute("orientation") == "landscape";
                    sheet.Scale = (Number("scale") ?? 100) / 100f;
                    sheet.FitWidth = (int)(Number("fitToWidth") ?? 1); sheet.FitHeight = (int)(Number("fitToHeight") ?? 1);
                    break;
                case "printOptions": sheet.PrintGridlines = On("gridLines"); break;
                case "rowBreaks" or "colBreaks":
                    var target = reader.LocalName == "rowBreaks" ? sheet.RowBreaks : sheet.ColumnBreaks;
                    if (reader.IsEmptyElement) break;
                    using (var breaks = reader.ReadSubtree())
                        while (breaks.Read())
                            if (breaks.NodeType == XmlNodeType.Element && breaks.LocalName == "brk" && int.TryParse(breaks.GetAttribute("id"), out var id)) target.Add(id);
                    break;
            }
        }
    }
}
