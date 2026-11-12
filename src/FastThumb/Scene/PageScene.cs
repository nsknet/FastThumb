using SkiaSharp;

namespace FastThumb.Scene;

/// <summary>
/// Retained display list for one page. Text keeps glyph ids + absolute positions, so neither sink shapes or
/// measures again. The same scene is replayed onto a raster canvas (thumbnail) and written to PDF (preview).
/// </summary>
internal sealed class PageScene(float width, float height)
{
    internal abstract record Op;
    /// <summary>Source = the characters behind the glyphs (same length), kept only for the PDF ToUnicode map.</summary>
    internal sealed record TextOp(SKFont Font, ushort[] Glyphs, SKPoint[] Positions, SKColor Color, string? Source) : Op;
    internal sealed record LineOp(float X1, float Y1, float X2, float Y2, float Width, SKColor Color) : Op;
    internal sealed record ImageOp(SKImage Image, SKRect Target) : Op;
    /// <summary>Kind: 0 rectangle, 1 rounded rectangle, 2 ellipse (same numbering as ShapeKind).</summary>
    internal sealed record ShapeOp(int Kind, SKRect Bounds, SKColor? Fill, SKColor? Stroke, float StrokeWidth) : Op;

    /// <summary>Free-form outline, already in page coordinates (fixed-layout formats).</summary>
    internal sealed record PathOp(SKPath Path, SKColor? Fill, SKColor? Stroke, float StrokeWidth) : Op;

    private readonly List<Op> _ops = [];

    internal IReadOnlyList<Op> Ops => _ops;
    public float Width { get; } = width;
    public float Height { get; } = height;
    public int Count => _ops.Count;

    public void Text(SKFont font, ushort[] glyphs, SKPoint[] positions, SKColor color, string? source = null) => _ops.Add(new TextOp(font, glyphs, positions, color, source));
    public void Line(float x1, float y1, float x2, float y2, float width, SKColor color) => _ops.Add(new LineOp(x1, y1, x2, y2, width, color));
    public void Image(SKImage image, SKRect target) => _ops.Add(new ImageOp(image, target));
    public void Shape(Enum kind, SKRect bounds, SKColor? fill, SKColor? stroke, float strokeWidth) => _ops.Add(new ShapeOp(Convert.ToInt32(kind), bounds, fill, stroke, strokeWidth));

    public void Path(SKPath path, SKColor? fill, SKColor? stroke, float strokeWidth) => _ops.Add(new PathOp(path, fill, stroke, strokeWidth));

    /// <summary>Puts a filled rectangle underneath everything drawn from op <paramref name="index"/> on (a cell background).</summary>
    public void InsertFill(int index, SKRect bounds, SKColor fill) => _ops.Insert(Math.Clamp(index, 0, _ops.Count), new ShapeOp(0, bounds, fill, null, 0));

    public void InsertImage(int index, SKImage image, SKRect target) => _ops.Insert(Math.Clamp(index, 0, _ops.Count), new ImageOp(image, target));

    /// <summary>Drops everything drawn after <paramref name="count"/> ops (a table row being moved to the next page).</summary>
    public void Truncate(int count) { if (count < _ops.Count) _ops.RemoveRange(Math.Max(0, count), _ops.Count - Math.Max(0, count)); }

    public void Draw(SKCanvas canvas)
    {
        using var paint = new SKPaint { IsAntialias = true };
        using var builder = new SKTextBlobBuilder();
        foreach (var op in _ops)
        {
            switch (op)
            {
                case TextOp text:
                    builder.AddPositionedRun(text.Glyphs, text.Font, text.Positions);
                    using (var blob = builder.Build())
                    {
                        paint.Style = SKPaintStyle.Fill;
                        paint.Color = text.Color;
                        if (blob != null) canvas.DrawText(blob, 0, 0, paint);
                    }
                    break;
                case LineOp line:
                    paint.Style = SKPaintStyle.Stroke;
                    paint.StrokeWidth = line.Width;
                    paint.Color = line.Color;
                    canvas.DrawLine(line.X1, line.Y1, line.X2, line.Y2, paint);
                    break;
                case ShapeOp shape:
                    for (var pass = 0; pass < 2; pass++)
                    {
                        var color = pass == 0 ? shape.Fill : shape.Stroke;
                        if (color == null) continue;
                        paint.Style = pass == 0 ? SKPaintStyle.Fill : SKPaintStyle.Stroke;
                        paint.StrokeWidth = shape.StrokeWidth;
                        paint.Color = color.Value;
                        if (shape.Kind == 2) canvas.DrawOval(shape.Bounds, paint);
                        else if (shape.Kind == 1) { var radius = Math.Min(shape.Bounds.Width, shape.Bounds.Height) * 0.1667f; canvas.DrawRoundRect(shape.Bounds, radius, radius, paint); }
                        else canvas.DrawRect(shape.Bounds, paint);
                    }
                    break;
                case PathOp path:
                    for (var pass = 0; pass < 2; pass++)
                    {
                        var color = pass == 0 ? path.Fill : path.Stroke;
                        if (color == null) continue;
                        paint.Style = pass == 0 ? SKPaintStyle.Fill : SKPaintStyle.Stroke;
                        paint.StrokeWidth = path.StrokeWidth;
                        paint.Color = color.Value;
                        canvas.DrawPath(path.Path, paint);
                    }
                    break;
                case ImageOp image:
                    canvas.DrawImage(image.Image, image.Target, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                    break;
            }
        }
    }

    /// <summary>RasterSink: page scaled so its long side is <paramref name="longSide"/> pixels.</summary>
    public SKBitmap ToBitmap(int longSide)
    {
        if (!(Width > 0 && Height > 0)) throw new InvalidDataException("The page has no size.");
        var scale = longSide / Math.Max(Width, Height);
        // a very long, thin page still gets one pixel across
        var bitmap = new SKBitmap(new SKImageInfo(Math.Max(1, (int)Math.Round(Width * scale)), Math.Max(1, (int)Math.Round(Height * scale)), SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        canvas.Scale(scale);
        Draw(canvas);
        return bitmap;
    }
}

/// <summary>All laid-out pages of one document plus the fonts and images their scenes reference.</summary>
internal sealed class PagedDocument : IDisposable
{
    private readonly List<IDisposable> _owned = [];

    public List<PageScene> Pages { get; } = [];

    public T Own<T>(T resource) where T : IDisposable { _owned.Add(resource); return resource; }

    /// <summary>PdfSink: every page, subset fonts. Skia's PDF backend is only a fallback for non-TrueType fonts.</summary>
    public byte[] ToPdf()
    {
        try { return PdfSink.Write(Pages); }
        catch (NotSupportedException) { }

        using var stream = new MemoryStream();
        using (var document = SKDocument.CreatePdf(stream))
        {
            foreach (var page in Pages)
            {
                page.Draw(document.BeginPage(page.Width, page.Height));
                document.EndPage();
            }
            document.Close();
        }
        return stream.ToArray();
    }

    public void Dispose()
    {
        foreach (var resource in _owned) resource.Dispose();
    }
}
