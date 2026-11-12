using FastThumb.Layout;
using FastThumb.Scene;
using SkiaSharp;

namespace FastThumb.Slides;

internal static class SlideRenderer
{
    /// <summary>One scene per slide. Shapes are drawn back to front: fill, outline, picture, then text.</summary>
    public static PagedDocument Render(SlideDeck deck)
    {
        var document = new PagedDocument();
        var text = Paginator.ForBoxes(document);
        // one decoded image per picture: readers share the bytes of a part referenced many times
        var images = new Dictionary<byte[], SKImage?>(ReferenceEqualityComparer.Instance);
        foreach (var slide in deck.Slides.Take(SlideDeck.MaxSlides))
        {
            var scene = new PageScene(deck.Width, deck.Height);
            Guard.Check();
            document.Pages.Add(scene);
            var page = new SKRect(0, 0, deck.Width, deck.Height);
            if (slide.Background != SKColors.White) scene.Shape(ShapeKind.Rect, page, slide.Background, null, 0);
            DrawImage(document, images, scene, slide.BackgroundImage, page);

            foreach (var shape in slide.Shapes)
            {
                if (shape.Kind == ShapeKind.Line)
                {
                    if (shape.Stroke is { } color) scene.Line(shape.Bounds.Left, shape.Bounds.Top, shape.Bounds.Right, shape.Bounds.Bottom, shape.StrokeWidth, color);
                    continue;
                }
                if (shape.Fill != null || shape.Stroke != null) scene.Shape(shape.Kind, shape.Bounds, shape.Fill, shape.Stroke, shape.StrokeWidth);
                DrawImage(document, images, scene, shape.Image, shape.Bounds);
                if (shape.Text.Count == 0) continue;
                var box = new SKRect(shape.Bounds.Left + shape.InsetLeft, shape.Bounds.Top + shape.InsetTop,
                    shape.Bounds.Right - shape.InsetRight, shape.Bounds.Bottom - shape.InsetBottom);
                if (box.Width > 4) text.LayoutBox(scene, shape.Text, box, (int)shape.Anchor);
            }
        }
        if (document.Pages.Count == 0) document.Pages.Add(new PageScene(deck.Width, deck.Height));
        return document;
    }

    private static void DrawImage(PagedDocument document, Dictionary<byte[], SKImage?> images, PageScene scene, byte[]? encoded, SKRect target)
    {
        if (encoded == null || target.Width <= 0 || target.Height <= 0) return;
        if (!images.TryGetValue(encoded, out var image))
        {
            image = Guard.Image(encoded); // null for formats Skia cannot decode (EMF/WMF) or too large: skipped
            images[encoded] = image == null ? null : document.Own(image);
        }
        if (image != null) scene.Image(image, target);
    }

    /// <summary>HSL lightness adjustment used by theme colour modifiers (lumMod / lumOff, tint, shade).</summary>
    public static SKColor Adjust(SKColor color, float lumMod = 1, float lumOff = 0, float tint = -1, float shade = -1, float alpha = 1)
    {
        if (lumMod != 1 || lumOff != 0)
        {
            color.ToHsl(out var h, out var s, out var l);
            color = SKColor.FromHsl(h, s, Math.Clamp(l * lumMod + lumOff * 100, 0, 100));
        }
        if (tint >= 0) color = new SKColor((byte)(color.Red + (255 - color.Red) * (1 - tint)), (byte)(color.Green + (255 - color.Green) * (1 - tint)), (byte)(color.Blue + (255 - color.Blue) * (1 - tint)));
        if (shade >= 0) color = new SKColor((byte)(color.Red * shade), (byte)(color.Green * shade), (byte)(color.Blue * shade));
        if (alpha < 1)
        {
            // no transparency in the scene: blend onto white
            color = new SKColor((byte)(color.Red * alpha + 255 * (1 - alpha)), (byte)(color.Green * alpha + 255 * (1 - alpha)), (byte)(color.Blue * alpha + 255 * (1 - alpha)));
        }
        return color;
    }
}
