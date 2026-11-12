using System.Xml;
using SkiaSharp;

namespace FastThumb.Layout;

internal enum Align { Left, Center, Right, Justify }

internal sealed class RunProps
{
    public string Font = "Times New Roman";
    public float Size = 10f;
    public bool Bold, Italic, Underline, Strike;
    public SKColor Color = SKColors.Black;
    public RunProps Clone() => (RunProps)MemberwiseClone();
}

internal sealed class ParaProps
{
    public Align Align;
    public float IndentLeft, IndentRight, FirstLine;      // points; FirstLine < 0 means hanging
    public float SpaceBefore, SpaceAfter;                  // points
    public float Line = 240f;                              // 240ths of a line (auto) or twips (exact/atLeast)
    public string LineRule = "auto";
    public bool PageBreakBefore;
    public string? Bullet;                                 // drawn at the first-line start; text begins at IndentLeft
    public RunProps? BulletProps;
    public List<(float Position, int Kind)>? Tabs;          // custom tab stops from the left edge; kind 0 left, 1 centre, 2 right
    public bool BorderBottom, BreakAfter;                  // paragraph bottom rule; section break (new page) after it
    public SKColor? BorderColor;                           // colour of the bottom rule (Word blue when null)
    public SKColor? Shade;                                 // paragraph background
    public bool AutoBefore, AutoAfter;                     // Word "auto" spacing: 14pt, collapsing with the neighbour
    public ParaProps Clone() => (ParaProps)MemberwiseClone();
}

internal abstract class Block;

internal abstract class Inline { public required RunProps Props; }

internal sealed class TextInline : Inline { public required string Text; }

internal sealed class TabInline : Inline;

/// <summary>Soft = Word's cached "a page ended here last time it was rendered" marker.</summary>
internal sealed class BreakInline : Inline { public bool Page; public bool Soft; }

internal sealed class ImageInline : Inline { public required string RelationshipId; public float Width, Height; }

/// <summary>A floating (anchored) picture or text box: positioned on the page, outside the text flow.</summary>
internal sealed class FloatInline : Inline
{
    public string? RelationshipId; public List<Block>? Text; public SKColor? Fill;
    public float X, Y, Width, Height;
    public string RelativeH = "column", RelativeV = "paragraph";
    public string? AlignH, AlignV;
}

internal sealed class Paragraph : Block
{
    public required ParaProps Props;
    public required RunProps MarkProps;
    public List<Inline> Inlines = [];
}

internal struct Borders { public bool? Top, Left, Bottom, Right, InsideH, InsideV; }

internal sealed class Cell
{
    public const int MaxSpan = 1000;                       // columns one cell may span: the table grid is sized from it
    public int Span = 1; public List<Block> Blocks = []; public Borders Borders; public SKColor? Fill;
}

internal sealed class Row { public List<Cell> Cells = []; public float MinHeight; }

internal sealed class Table : Block
{
    public float[] Grid = [];
    public List<Row> Rows = [];
    public Borders Borders;
    public float Indent;
    public Align Align;
    public float PadLeft = 5.4f, PadRight = 5.4f, PadTop, PadBottom;   // default cell margins (Word: 0.075" left / right)
}

internal sealed record PageSetup(float Width, float Height, float Top, float Right, float Bottom, float Left)
{
    /// <summary>Largest page side, in points (the PDF limit, 200"); sizes come from the file, so they are clamped here.</summary>
    public const float MaxSide = 14400;

    public float Width { get; init; } = Side(Width, 612);
    public float Height { get; init; } = Side(Height, 792);
    public float Top { get; init; } = Margin(Top);
    public float Right { get; init; } = Margin(Right);
    public float Bottom { get; init; } = Margin(Bottom);
    public float Left { get; init; } = Margin(Left);

    public static readonly PageSetup Default = new(612, 792, 72, 72, 72, 72);

    public static float Side(float value, float fallback) => float.IsNaN(value) ? fallback : Math.Clamp(value, 1, MaxSide);
    private static float Margin(float value) => float.IsNaN(value) ? 0 : Math.Clamp(value, -MaxSide, MaxSide);
}

// ---------------------------------------------------------------------------------------------
// FormatReader for DOCX: ZIP + XmlReader. Body blocks are produced lazily so the paginator can
// stop pulling as soon as page 1 is full — content after that is never parsed.
// ---------------------------------------------------------------------------------------------

/// <summary>What the paginator needs from any format reader.</summary>
internal interface IPageSource
{
    PageSetup Page { get; }
    /// <summary>Lazily yields top-level blocks; stop enumerating to stop reading.</summary>
    IEnumerable<Block> Blocks();
    SKImage? LoadImage(string id);
    IReadOnlyList<Block> Header(bool firstPage) => [];
    IReadOnlyList<Block> Footer(bool firstPage) => [];
    float HeaderDistance => 36;
    float FooterDistance => 36;
}

/// <summary>A document already turned into blocks (formats that are parsed in one go).</summary>
internal sealed class StaticSource(PageSetup page, List<Block> blocks) : IPageSource
{
    public PageSetup Page { get; } = page;
    public IEnumerable<Block> Blocks() => blocks;
    public SKImage? LoadImage(string id) => null;
}
