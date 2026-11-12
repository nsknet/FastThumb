using FastThumb.Layout;
using SkiaSharp;

namespace FastThumb.Slides;

internal enum ShapeKind { Rect, RoundRect, Ellipse, Line }

internal enum TextAnchor { Top, Middle, Bottom }

internal sealed class SlideShape
{
    public SKRect Bounds;
    public ShapeKind Kind;
    public SKColor? Fill, Stroke;
    public float StrokeWidth = 0.75f;
    public byte[]? Image;                       // encoded picture (PNG/JPEG/…), drawn stretched into Bounds
    public List<Block> Text = [];               // paragraphs (and tables) laid out inside the shape
    public TextAnchor Anchor;
    public float InsetLeft = 7.2f, InsetTop = 3.6f, InsetRight = 7.2f, InsetBottom = 3.6f;
}

internal sealed class SlideData
{
    public SKColor Background = SKColors.White;
    public byte[]? BackgroundImage;
    public List<SlideShape> Shapes = [];        // back to front
}

internal sealed class SlideDeck
{
    /// <summary>Most slides read from one file; the rest are left out (page limits, as for documents).</summary>
    public const int MaxSlides = 1000;

    private float _width = 720, _height = 540;
    // points; sizes come from the file, so they are clamped like page sizes
    public float Width { get => _width; set => _width = PageSetup.Side(value, 720); }
    public float Height { get => _height; set => _height = PageSetup.Side(value, 540); }
    public List<SlideData> Slides = [];
}
