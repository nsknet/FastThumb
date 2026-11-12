using System.IO.Compression;
using System.Xml.Linq;
using FastThumb.Layout;
using FastThumb.Words;

namespace FastThumb.Cells;

/// <summary>
/// OpenDocument Spreadsheet (ODS / OTS). Cells carry their displayed text, so no number formatting is needed;
/// styles (font, fill, borders, alignment, wrap), column widths, row heights, merges and the page layout are
/// read with the same style resolution as ODT.
/// </summary>
internal static class OdsReader
{
    private static readonly XNamespace Office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private static readonly XNamespace Table = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    private static readonly XNamespace Text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private static readonly XNamespace Style = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
    private static readonly XNamespace Fo = "urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0";
    private const int MaxRows = 20000, MaxColumns = 500;

    public static List<SheetSnapshot> Read(byte[] data)
    {
        using var zip = new ZipArchive(new MemoryStream(data, false), ZipArchiveMode.Read);
        XElement? Load(string name)
        {
            var entry = zip.GetEntry(name);
            if (entry == null) return null;
            using var stream = Guard.Open(entry);
            return Guard.LoadXml(stream).Root;
        }
        var content = Load("content.xml") ?? throw new InvalidDataException("content.xml not found");
        var styles = Load("styles.xml");
        var context = OdtReader.CreateContext(styles, content);
        // a sheet's page comes from its table style -> master page -> page layout; the header / footer bands sit inside the margins
        var layouts = (styles?.Descendants(Style + "page-layout") ?? []).Where(l => l.Attribute(Style + "name") != null).ToDictionary(l => (string)l.Attribute(Style + "name")!);
        var masters = (styles?.Descendants(Style + "master-page") ?? []).Where(m => m.Attribute(Style + "name") != null).ToDictionary(m => (string)m.Attribute(Style + "name")!, m => (string?)m.Attribute(Style + "page-layout-name"));
        XElement? LayoutOf(string? tableStyle)
        {
            var master = tableStyle != null && context.Styles.TryGetValue(tableStyle, out var style) ? (string?)style.Attribute(Style + "master-page-name") : null;
            if (master != null && masters.TryGetValue(master, out var name) && name != null && layouts.TryGetValue(name, out var hit)) return hit;
            return layouts.Values.FirstOrDefault();
        }
        var cellStyles = new Dictionary<string, CellStyle>(StringComparer.Ordinal);

        CellStyle StyleOf(string? name)
        {
            if (name == null) return SheetSnapshot.DefaultStyle;
            if (cellStyles.TryGetValue(name, out var hit)) return hit;
            var style = new CellStyle { Font = new RunProps { Font = context.DefaultRun.Font, Size = 10 } };
            var paragraph = new ParaProps();
            OdtReader.Resolve(context, name, paragraph, style.Font);
            style.Align = paragraph.Align switch { Align.Center => HAlign.Center, Align.Right => HAlign.Right, Align.Justify => HAlign.Left, _ => HAlign.General };
            // cell-level properties, nearest style first
            for (var current = name; current != null && context.Styles.TryGetValue(current, out var definition); current = (string?)definition.Attribute(Style + "parent-style-name"))
            {
                if (definition.Element(Style + "table-cell-properties") is not { } cp) continue;
                style.Fill ??= FormatDetector.Color((string?)cp.Attribute(Fo + "background-color"));
                bool Side(string side) => ((string?)cp.Attribute(Fo + side) ?? (string?)cp.Attribute(Fo + "border")) is { } value && !value.StartsWith("none", StringComparison.Ordinal);
                style.BorderTop |= Side("border-top"); style.BorderBottom |= Side("border-bottom"); style.BorderLeft |= Side("border-left"); style.BorderRight |= Side("border-right");
                if ((string?)cp.Attribute(Fo + "wrap-option") == "wrap") style.Wrap = true;
                if ((string?)cp.Attribute(Style + "vertical-align") is { } vertical) style.VAlign = vertical switch { "top" => 0, "middle" => 1, _ => 2 };
            }
            return cellStyles[name] = style;
        }
        float? Property(string? styleName, string element, string attribute)
            => styleName != null && context.Styles.TryGetValue(styleName, out var s) ? FormatDetector.Length((string?)s.Element(Style + element)?.Attribute(Style + attribute)) : null;

        var sheets = new List<SheetSnapshot>();
        foreach (var table in content.Descendants(Table + "table"))
        {
            var sheet = new SheetSnapshot { Name = (string?)table.Attribute(Table + "name") ?? "", DefaultRowHeight = 12.8f, DefaultColumnWidth = 64 };
            sheets.Add(sheet);
            if (LayoutOf((string?)table.Attribute(Table + "style-name")) is { } pageLayout && pageLayout.Element(Style + "page-layout-properties") is { } layout)
            {
                float L(string name, float fallback) => FormatDetector.Length((string?)layout.Attribute(Fo + name)) ?? fallback;
                float Band(string name, string gap) => pageLayout.Element(Style + name)?.Element(Style + "header-footer-properties") is { } band
                    ? (FormatDetector.Length((string?)band.Attribute(Fo + "min-height")) ?? 0) + (FormatDetector.Length((string?)band.Attribute(Fo + gap)) ?? 0) : 0;
                float width = PageSetup.Side(L("page-width", 595.3f), 595.3f), height = PageSetup.Side(L("page-height", 841.9f), 841.9f);   // sizes from the file
                sheet.Landscape = width > height;
                (sheet.PaperWidth, sheet.PaperHeight) = (Math.Min(width, height), Math.Max(width, height));
                sheet.MarginLeft = L("margin-left", 56.7f); sheet.MarginRight = L("margin-right", 56.7f); sheet.MarginTop = L("margin-top", 56.7f) + Band("header-style", "margin-bottom"); sheet.MarginBottom = L("margin-bottom", 56.7f) + Band("footer-style", "margin-top");
            }

            var column = 0;
            foreach (var definition in table.Elements(Table + "table-column"))
            {
                var repeat = (int?)definition.Attribute(Table + "number-columns-repeated") ?? 1;
                var width = Property((string?)definition.Attribute(Table + "style-name"), "table-column-properties", "column-width");
                var hidden = (string?)definition.Attribute(Table + "visibility") is "collapse" or "filter";
                for (var i = 0; i < repeat && column < MaxColumns; i++, column++)
                {
                    if (width != null) sheet.ColumnWidths[column] = width.Value;
                    if (hidden) sheet.HiddenColumns.Add(column);
                }
            }

            var row = 0;
            foreach (var tr in table.Descendants(Table + "table-row"))
            {
                if (row > MaxRows) break;
                var repeatRows = Math.Max(1, (int?)tr.Attribute(Table + "number-rows-repeated") ?? 1);   // from the file: never backwards
                var height = Property((string?)tr.Attribute(Table + "style-name"), "table-row-properties", "row-height");
                var rowHasContent = false;
                var col = 0;
                foreach (var tc in tr.Elements())
                {
                    var repeat = Math.Clamp((int?)tc.Attribute(Table + "number-columns-repeated") ?? 1, 1, 1 << 20);   // the index stops past the kept columns
                    if (tc.Name == Table + "covered-table-cell") { col = Math.Min(col + repeat, MaxColumns + 1); continue; }
                    if (tc.Name != Table + "table-cell") continue;
                    var text = string.Join("\n", tc.Elements(Text + "p").Select(p => p.Value));
                    var styleName = (string?)tc.Attribute(Table + "style-name");
                    if (text.Length == 0 && styleName == null) { col = Math.Min(col + repeat, MaxColumns + 1); continue; }
                    var style = StyleOf(styleName);
                    var type = (string?)tc.Attribute(Office + "value-type");
                    for (var i = 0; i < Math.Min(repeat, text.Length > 0 ? 200 : 50) && col + i < MaxColumns; i++)
                    {
                        var cell = sheet.Cell(row, col + i);
                        cell.Style = style;
                        cell.Text = text;
                        cell.Numeric = type is "float" or "percentage" or "currency" or "date" or "time";
                        cell.Centered = type == "boolean";
                    }
                    rowHasContent = true;
                    int spanColumns = (int?)tc.Attribute(Table + "number-columns-spanned") ?? 1, spanRows = (int?)tc.Attribute(Table + "number-rows-spanned") ?? 1;
                    if ((spanColumns > 1 || spanRows > 1) && sheet.Merges.Count < SheetSnapshot.MaxMerges) sheet.Merges.Add((row, col, row + spanRows - 1, col + spanColumns - 1));
                    col = Math.Min(col + repeat, MaxColumns + 1);
                }
                if (height != null) for (var i = 0; i < Math.Min(repeatRows, rowHasContent ? 1000 : 1); i++) sheet.RowHeights[row + i] = height.Value;
                // a repeated row repeats its cells too; huge trailing repeats of empty rows only move the index
                if (rowHasContent && repeatRows > 1 && sheet.Rows.TryGetValue(row, out var source))
                    for (var i = 1; i < Math.Min(repeatRows, 1000); i++) sheet.Rows[row + i] = new SortedList<int, CellData>(source);
                row = (int)Math.Min((long)row + repeatRows, int.MaxValue);
            }
        }
        return sheets;
    }
}
