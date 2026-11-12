// Smoke test of the packed FastThumb package, run by eng/smoke/run.sh: every native piece is exercised once, loaded
// from the package the way an application gets it.
using System.Text;
using FastThumb;
using SkiaSharp;

var generator = new ThumbnailGenerator(ThumbnailOptions.Default with { Sizes = [300], IncludeFullSize = false });
var failures = 0;

void Check(string name, Func<ThumbnailResult> make, DocumentFormat expected, bool previewPdf)
{
    try
    {
        var result = make();
        var image = result.Images.Single();
        var ok = result.Format == expected && Math.Max(image.Width, image.Height) == 300 && image.Data.Length > 100
            && (result.PreviewPdf != null) == previewPdf;
        Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}: {result.Format}, {result.PageCount} page(s), {image}");
        if (!ok) failures++;
    }
    catch (Exception e)
    {
        Console.WriteLine($"FAIL {name}: {e}");
        failures++;
    }
}

// a sheet: reader, layout, Skia raster, preview PDF writer, libvips encoder
var csv = Encoding.UTF8.GetBytes("Item,Quantity,Price\nPaper,10,2.50\nInk,3,19.90\n");
Check("csv", () => generator.Generate(csv, "items.csv"), DocumentFormat.Csv, previewPdf: true);

// a PDF: PDFium
using (var stream = new MemoryStream())
{
    using (var document = SKDocument.CreatePdf(stream))
    {
        var canvas = document.BeginPage(400, 300);
        using var paint = new SKPaint { Color = SKColors.SteelBlue };
        canvas.DrawRect(20, 20, 360, 260, paint);
        document.EndPage();
    }
    var pdf = stream.ToArray();
    Check("pdf", () => generator.Generate(pdf, "page.pdf"), DocumentFormat.Pdf, previewPdf: false);
}

// a video: thumbvideo (only when the repository has a fixture)
if (args.Length > 0 && File.Exists(args[0]))
{
    var options = generator.Options with { Sizes = [32] };
    var result = new ThumbnailGenerator(options).GenerateFromFile(args[0]);
    var ok = result.Format == DocumentFormat.Video && result.Images.Count == 1;
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} video: {result.Images.FirstOrDefault()}");
    if (!ok) failures++;
}
else Console.WriteLine("skip video: no fixture");

return failures == 0 ? 0 : 1;
