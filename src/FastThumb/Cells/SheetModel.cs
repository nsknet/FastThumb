using FastThumb.Layout;
using SkiaSharp;

namespace FastThumb.Cells;

internal enum HAlign { General, Left, Center, Right }

internal sealed class CellStyle
{
    public RunProps Font = new() { Font = "Calibri", Size = 11 };
    public SKColor? Fill;
    public bool BorderLeft, BorderTop, BorderRight, BorderBottom;
    public HAlign Align;
    public int VAlign = 2;            // 0 top, 1 middle, 2 bottom (Excel's default)
    public bool Wrap;
    public string Format = "General";
    public CellStyle Clone() => (CellStyle)MemberwiseClone();
}

internal sealed class CellData
{
    public string Text = "";
    public bool Numeric;              // right-aligned under "general" alignment
    public bool Centered;             // booleans and errors
    public CellStyle Style = SheetSnapshot.DefaultStyle;
}

internal sealed class SheetSnapshot
{
    public static readonly CellStyle DefaultStyle = new();

    // limits for what a reader keeps from a file: the renderer prints at most 501 columns, and merges are only drawn
    public const int MaxColumn = 500;
    public const int MaxMerges = 1 << 16;

    public string Name = "";
    public bool Visible = true;
    public float PaperWidth = 612, PaperHeight = 792;      // portrait size in points; Landscape swaps them
    public bool Landscape;
    public float MarginLeft = 50.4f, MarginRight = 50.4f, MarginTop = 54, MarginBottom = 54;
    public float Scale = 1;
    public bool FitToPage;
    public int FitWidth = 1, FitHeight = 1;                // 0 = automatic on that axis
    public bool PrintGridlines;
    public float DefaultRowHeight = 15, DefaultColumnWidth = 48;
    public Dictionary<int, float> RowHeights = [], ColumnWidths = [];
    public HashSet<int> HiddenRows = [], HiddenColumns = [];
    public HashSet<int> RowBreaks = [], ColumnBreaks = [];  // a page starts at these indices
    public Dictionary<int, SortedList<int, CellData>> Rows = [];
    public List<(int Row1, int Column1, int Row2, int Column2)> Merges = [];
    public (int Row1, int Column1, int Row2, int Column2)? PrintArea;

    public CellData Cell(int row, int column)
    {
        if (!Rows.TryGetValue(row, out var cells)) Rows[row] = cells = [];
        if (!cells.TryGetValue(column, out var cell)) cells[column] = cell = new CellData();
        return cell;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, float, bool), float> DigitWidths = new();

    /// <summary>
    /// Excel column width (in characters of the workbook's default font) to points. On paper a column is as wide as
    /// that many digits really are in the default font, which is a few percent more than the on-screen pixel width.
    /// </summary>
    public static float WidthToPoints(double characters, RunProps? defaultFont = null)
    {
        defaultFont ??= DefaultStyle.Font;
        var key = (defaultFont.Font, defaultFont.Size, defaultFont.Bold);
        if (!DigitWidths.TryGetValue(key, out var digit))
        {
            using var font = new SKFont(FontCache.Get(defaultFont), defaultFont.Size) { Hinting = SKFontHinting.None, LinearMetrics = true };
            digit = Math.Max(1f, font.MeasureText("0"));
            // keys come from documents: past the cap a width is measured each time, not kept
            if (DigitWidths.Count < 1024 && defaultFont.Font.Length <= 256) DigitWidths.TryAdd(key, digit);
        }
        var pixelDigit = Math.Max(1, Math.Round(digit * 96 / 72));
        var pixels = Math.Truncate((256 * characters + Math.Truncate(128 / pixelDigit)) / 256 * pixelDigit);
        return (float)(pixels / pixelDigit * digit);
    }

    /// <summary>
    /// Rows whose height was never set by hand are sized from the default font when printed (line height plus
    /// a small pad), which is slightly less than the screen height stored in the file. Returns that ratio.
    /// </summary>
    public static float AutoRowRatio(RunProps defaultFont, float storedDefaultHeight)
    {
        // measured on real workbooks against their printed output: the adjustment only applies when the workbook's
        // default font (or a metric-compatible clone, e.g. Carlito for Calibri) is installed; with a look-alike
        // the stored heights are used as they are
        if (!FontCache.IsFaithful(defaultFont)) return 1f;
        using var font = new SKFont(FontCache.Get(defaultFont), defaultFont.Size) { Hinting = SKFontHinting.None, LinearMetrics = true };
        var metrics = font.Metrics;
        var natural = -metrics.Ascent + metrics.Descent + metrics.Leading + 0.75f;
        return storedDefaultHeight > 0 ? Math.Clamp(natural / storedDefaultHeight, 0.85f, 1.1f) : 1f;
    }

    public static (float Width, float Height) Paper(int code) => code switch
    {
        5 => (612, 1008),
        8 => (841.9f, 1190.6f),
        9 => (595.3f, 841.9f),
        11 => (419.5f, 595.3f),
        13 => (498.9f, 708.7f),
        3 => (792, 1224),
        _ => (612, 792),
    };

    public static readonly uint[] IndexedColors =
    [
        0x000000, 0xFFFFFF, 0xFF0000, 0x00FF00, 0x0000FF, 0xFFFF00, 0xFF00FF, 0x00FFFF, 0x000000, 0xFFFFFF, 0xFF0000, 0x00FF00, 0x0000FF, 0xFFFF00, 0xFF00FF, 0x00FFFF,
        0x800000, 0x008000, 0x000080, 0x808000, 0x800080, 0x008080, 0xC0C0C0, 0x808080, 0x9999FF, 0x993366, 0xFFFFCC, 0xCCFFFF, 0x660066, 0xFF8080, 0x0066CC, 0xCCCCFF,
        0x000080, 0xFF00FF, 0xFFFF00, 0x00FFFF, 0x800080, 0x800000, 0x008080, 0x0000FF, 0x00CCFF, 0xCCFFFF, 0xCCFFCC, 0xFFFF99, 0x99CCFF, 0xFF99CC, 0xCC99FF, 0xFFCC99,
        0x3366FF, 0x33CCCC, 0x99CC00, 0xFFCC00, 0xFF9900, 0xFF6600, 0x666699, 0x969696, 0x003366, 0x339966, 0x003300, 0x333300, 0x993300, 0x993366, 0x333399, 0x333333,
    ];
}
