using System.Diagnostics;
using FastThumb.Tests.Fixtures;

namespace FastThumb.Tests;

public class PageDpiTests
{
    private static readonly ThumbnailOptions Images = ThumbnailOptions.Default with { Outputs = ThumbnailOutputs.Images, EncodeInParallel = false };

    [Fact]
    public void A_pdf_page_at_300_dpi_is_drawn_once_at_the_full_size()
    {
        var pdf = PdfFiles.Pdf(1, 612, 792);   // Letter
        var result = new ThumbnailGenerator(Images with { PageDpi = 300 }).Generate(pdf, "a.pdf");

        var full = result.FullSize!;
        Assert.Equal((2550, 3300), (full.Width, full.Height));
        var largest = result.Find(2500)!;
        Assert.Equal(2500, largest.Height);
        Assert.Equal(1932, largest.Width);
        Assert.Equal(100, result.Find(100)!.Height);
    }

    [Fact]
    public void Without_page_dpi_a_pdf_page_keeps_72_dpi_and_a_document_96()
    {
        var pdf = new ThumbnailGenerator(Images).Generate(PdfFiles.Pdf(1, 612, 792), "a.pdf").FullSize!;
        Assert.Equal((612, 792), (pdf.Width, pdf.Height));

        var at96 = new ThumbnailGenerator(Images).Generate(Samples.Docx.Bytes, Samples.Docx.FileName).FullSize!;
        var explicit96 = new ThumbnailGenerator(Images with { PageDpi = 96 }).Generate(Samples.Docx.Bytes, Samples.Docx.FileName).FullSize!;
        Assert.Equal((at96.Width, at96.Height), (explicit96.Width, explicit96.Height));
        Assert.Equal(at96.Data, explicit96.Data);
    }

    [Theory]
    [InlineData("docx")]
    [InlineData("xlsx")]
    [InlineData("pptx")]
    public void A_document_page_follows_page_dpi(string name)
    {
        var sample = Samples.Get(name);
        var at96 = new ThumbnailGenerator(Images with { Sizes = [100] }).Generate(sample.Bytes, sample.FileName).FullSize!;
        var at300 = new ThumbnailGenerator(Images with { Sizes = [100], PageDpi = 300 }).Generate(sample.Bytes, sample.FileName).FullSize!;
        Assert.InRange(at300.Width, at96.Width * 300 / 96 - 2, at96.Width * 300 / 96 + 2);
        Assert.InRange(at300.Height, at96.Height * 300 / 96 - 2, at96.Height * 300 / 96 + 2);
    }

    [Fact]
    public void The_full_size_at_page_dpi_still_fits_max_pixels()
    {
        var options = Images with { PageDpi = 300, Limits = new ResourceLimits { MaxPixels = 1_000_000 } };
        var full = new ThumbnailGenerator(options).Generate(PdfFiles.Pdf(1, 612, 792), "a.pdf").FullSize!;
        Assert.True((long)full.Width * full.Height <= 1_000_000);
        Assert.InRange(full.Width / (double)full.Height, 612 / 792.0 - 0.01, 612 / 792.0 + 0.01);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(1200.5)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Page_dpi_out_of_range_is_rejected(double dpi) =>
        Assert.Throws<ArgumentException>(() => new ThumbnailGenerator(ThumbnailOptions.Default with { PageDpi = dpi }));

    [Fact]
    public void Drawing_a_pdf_page_stops_when_cancelled_and_frees_pdfium()
    {
        var slow = PdfFiles.SlowPdf(10_000);
        var generator = new ThumbnailGenerator(Images with { Sizes = [2500], IncludeFullSize = false });

        // the first page also pays for loading and compiling, by far the most on .NET Framework: time a second one
        generator.Generate(slow, "slow.pdf");
        var clock = Stopwatch.StartNew();
        generator.Generate(slow, "slow.pdf");
        var whole = clock.Elapsed;

        // cancelled a fifth of the way into the page; how long a page takes differs a lot between machines
        using var cancellation = new CancellationTokenSource(whole / 5);
        clock.Restart();
        OperationCanceledException? stopped = null;
        try { generator.Generate(slow, "slow.pdf", cancellation.Token); }
        catch (OperationCanceledException e) { stopped = e; }
        var cancelled = clock.Elapsed;
        // only meaningful when the page takes long enough to draw that stopping early can be told apart
        if (whole >= TimeSpan.FromMilliseconds(300))
        {
            Assert.NotNull(stopped);
            Assert.True(cancelled < whole * 0.75, $"cancelled after {cancelled}, a whole page takes {whole}");
        }

        // the lock was released: the next PDF is drawn normally
        Assert.Single(generator.Generate(PdfFiles.Pdf(1), "a.pdf").Images);
    }

    [Fact]
    public async Task Process_settings_can_be_applied_early_and_more_than_once()
    {
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(ThumbnailGenerator.ApplyProcessSettings)));
        ThumbnailGenerator.ApplyProcessSettings();

        Assert.Equal(0, NetVips.Cache.Max);
    }
}
