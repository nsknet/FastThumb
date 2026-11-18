using FastThumb.Tests.Fixtures;

namespace FastThumb.Tests;

/// <summary>
/// BMP by content, layered PSD without a real composite, Illustrator (PostScript and PDF based), OpenDocument drawings,
/// SVGZ, CorelDRAW previews and DICOM.
/// </summary>
public class AddedFormatTests
{
    private static readonly ThumbnailGenerator Generator = new(ThumbnailOptions.Default with { Sizes = [200] });

    /// <summary>The full-size picture of an input, decoded: interleaved bytes, band count, width, height.</summary>
    private static (byte[] Pixels, int Bands, int Width, int Height) Full(byte[] data, string? fileName)
    {
        var result = Generator.Generate(data, fileName);
        using var image = NetVips.Image.NewFromBuffer(result.FullSize!.Data);
        return (image.WriteToMemory<byte>(), image.Bands, image.Width, image.Height);
    }

    private static (int R, int G, int B) At((byte[] Pixels, int Bands, int Width, int Height) picture, int x, int y)
    {
        var i = (y * picture.Width + x) * picture.Bands;
        return picture.Bands >= 3 ? (picture.Pixels[i], picture.Pixels[i + 1], picture.Pixels[i + 2]) : (picture.Pixels[i], picture.Pixels[i], picture.Pixels[i]);
    }

    private static void Near((int R, int G, int B) expected, (int R, int G, int B) actual, int tolerance = 2)
    {
        Assert.True(Math.Abs(expected.R - actual.R) <= tolerance && Math.Abs(expected.G - actual.G) <= tolerance && Math.Abs(expected.B - actual.B) <= tolerance,
            $"expected {expected}, got {actual}");
    }

    /// <summary>Largest difference from the test picture, after <paramref name="map"/>.</summary>
    private static int Difference((byte[] Pixels, int Bands, int Width, int Height) picture, Func<byte, byte, byte, (int, int, int)> map)
    {
        var rgb = ImageFiles.Rgb(picture.Width, picture.Height);
        var worst = 0;
        for (var i = 0; i < picture.Width * picture.Height; i++)
        {
            var (r, g, b) = map(rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2]);
            var (pr, pg, pb) = At(picture, i % picture.Width, i / picture.Width);
            worst = Math.Max(worst, Math.Max(Math.Abs(pr - r), Math.Max(Math.Abs(pg - g), Math.Abs(pb - b))));
        }
        return worst;
    }

    // ---- BMP -------------------------------------------------------------------------------------

    [Theory]
    [InlineData(BmpKind.Rgb24, 0)]
    [InlineData(BmpKind.TopDown24, 0)]
    [InlineData(BmpKind.Rgb32, 0)]
    [InlineData(BmpKind.Rgb565, 8)]
    [InlineData(BmpKind.Os2Core24, 0)]
    public void Bmp_is_recognised_without_a_file_name(BmpKind kind, int tolerance)
    {
        var bmp = BmpFiles.Bmp(kind);
        Assert.Equal(DocumentFormat.Image, ThumbnailGenerator.DetectFormat(bmp));
        Assert.InRange(Difference(Full(bmp, null), (r, g, b) => (r, g, b)), 0, tolerance);
    }

    [Fact]
    public void Rle8_bmp_is_decoded() =>
        Assert.Equal(0, Difference(Full(BmpFiles.Bmp(BmpKind.Rle8), null), (r, _, _) => (r, r, r)));

    [Theory]
    [InlineData("BM is how this note starts, and it is plain text that goes on for a while.")]
    [InlineData("BMW")]
    public void Text_starting_with_BM_is_still_text(string text) =>
        Assert.Equal(DocumentFormat.Text, ThumbnailGenerator.DetectFormat(System.Text.Encoding.ASCII.GetBytes(text + new string(' ', 40)), "note.txt"));

    // ---- PSD layers ------------------------------------------------------------------------------

    private static readonly PsdLayer Background = new(0, 0, 80, 80, 200, 100, 50);

    [Fact]
    public void Psd_without_a_real_composite_is_drawn_from_its_layers()
    {
        var psd = PsdLayerFiles.Psd(80, 80,
        [
            Background,
            new(10, 10, 30, 30, 255, 0, 0) { Opacity = 128 },
            new(40, 10, 60, 30, 128, 128, 128) { Blend = "mul " },
            new(0, 0, 80, 80, 0, 255, 0) { Hidden = true },
            new(10, 40, 30, 60, 0, 0, 255) { HalfMask = true },
            new(0, 35, 40, 70, 255, 255, 0) { Clipped = true },
            new(0, 0, 0, 0, 0, 0, 0) { Section = 3 },
            new(0, 0, 80, 80, 0, 0, 0),
            new(0, 0, 0, 0, 0, 0, 0) { Section = 1, Hidden = true },
            new(60, 60, 80, 80, 0, 0, 255) { Alpha = 0 },
        ]);
        var picture = Full(psd, "layers.psd");
        Assert.Equal((80, 80), (picture.Width, picture.Height));
        Near((200, 100, 50), At(picture, 5, 5));                         // background only
        Near((228, 50, 25), At(picture, 20, 20));                        // red at half opacity
        Near((100, 50, 25), At(picture, 50, 20));                        // multiply by mid grey
        Near((255, 255, 0), At(picture, 15, 50));                        // the clipped layer over the visible half of the masked one
        Near((200, 100, 50), At(picture, 25, 50));                       // masked out: the clipped layer too
        Near((200, 100, 50), At(picture, 5, 65));                        // clipped layer outside its base
        Near((200, 100, 50), At(picture, 70, 70));                       // transparent layer
    }

    [Fact]
    public void Psd_with_a_real_composite_keeps_it()
    {
        var psd = PsdLayerFiles.Psd(40, 40, [new(0, 0, 40, 40, 0, 0, 0)], realComposite: true);
        Near((255, 255, 255), At(Full(psd, "flat.psd"), 20, 20));
    }

    [Fact]
    public void Psd_layers_that_cannot_be_read_fall_back_to_the_thumbnail()
    {
        var psd = PsdLayerFiles.Psd(40, 40, [new(0, 0, 40, 40, 0, 0, 0)], thumbnail: ImageFiles.Jpeg(32, 32));
        // the layer record's blend signature, broken: the records cannot be read
        var at = psd.AsSpan().IndexOf("8BIMnorm"u8);
        "XXXX"u8.CopyTo(psd.AsSpan(at));
        var picture = Full(psd, "broken.psd");
        Assert.Equal((32, 32), (picture.Width, picture.Height));
    }

    // ---- Illustrator -----------------------------------------------------------------------------

    [Theory]
    [InlineData(false, "art.ai")]
    [InlineData(false, null)]
    [InlineData(true, "art.eps")]
    public void PostScript_illustrator_artwork_is_drawn_from_its_paths(bool eps, string? fileName)
    {
        var ai = DrawingFiles.IllustratorPaths(eps);
        Assert.Equal(DocumentFormat.Image, ThumbnailGenerator.DetectFormat(ai, fileName));
        var picture = Full(ai, fileName);
        Assert.Equal((200, 100), (picture.Width, picture.Height));
        Near((255, 0, 0), At(picture, 50, 50), 4);                       // CMYK red, left half
        Near((0, 0, 255), At(picture, 130, 30), 4);                      // compound path
        Near((255, 255, 255), At(picture, 150, 50), 4);                  // its hole
        Near((0, 255, 0), At(picture, 150, 5), 4);                       // stroked line at y = 95 pt
        Near((255, 255, 255), At(picture, 195, 50), 4);                  // hidden layer, guide
    }

    [Fact]
    public void Illustrator_thumbnail_is_used_when_there_is_one()
    {
        var eps = System.Text.Encoding.ASCII.GetString(RasterFiles.Eps(EpsKind.Illustrator)).Replace("EPSF-3.0", "", StringComparison.Ordinal);
        var picture = Full(System.Text.Encoding.ASCII.GetBytes(eps), "thumb.ai");
        Assert.Equal((320, 200), (picture.Width, picture.Height));
    }

    [Fact]
    public void Illustrator_artwork_with_too_many_points_is_refused() =>
        Assert.Throws<ResourceLimitExceededException>(() => Generator.Generate(DrawingFiles.IllustratorManyPoints(2_100_000), "many.ai"));

    [Fact]
    public void Pdf_illustrator_without_pdf_content_shows_its_xmp_thumbnail()
    {
        var result = Generator.Generate(DrawingFiles.IllustratorWithoutPdfContent(), "art.ai");
        Assert.Equal(DocumentFormat.Pdf, result.Format);
        Assert.Equal((64, 40), ImageFiles.Size(result.FullSize!.Data));
    }

    [Fact]
    public void Pdf_illustrator_without_pdf_content_or_thumbnail_is_refused() =>
        Assert.Throws<UnsupportedFormatException>(() => Generator.Generate(DrawingFiles.IllustratorWithoutPdfContent(thumbnail: false), "art.ai"));

    // ---- OpenDocument drawings, SVGZ, CorelDRAW -------------------------------------------------------

    [Theory]
    [InlineData(false, DocumentFormat.Odg)]
    [InlineData(true, DocumentFormat.Fodg)]
    public void OpenDocument_drawing_is_drawn_as_a_page(bool flat, DocumentFormat format)
    {
        var result = Generator.Generate(DrawingFiles.Odg(flat), flat ? "d.fodg" : "d.odg");
        Assert.Equal(format, result.Format);
        Assert.NotNull(result.PreviewPdf);
        using var image = NetVips.Image.NewFromBuffer(result.FullSize!.Data);
        Assert.Equal(2.0, (double)image.Width / image.Height, 1);         // 200 x 100 mm
        var pixels = image.WriteToMemory<byte>();
        var i = ((int)(image.Height * 0.35) * image.Width + (int)(image.Width * 0.25)) * image.Bands;
        Assert.True(pixels[i + 2] > 200 && pixels[i] < 60, "the blue rectangle");
    }

    [Fact]
    public void Svgz_is_inflated_and_drawn()
    {
        var picture = Full(DrawingFiles.Svgz(), "red.svgz");
        Assert.Equal((120, 80), (picture.Width, picture.Height));
        Near((255, 0, 0), At(picture, 60, 40));
    }

    [Fact]
    public void Svgz_bomb_is_refused()
    {
        var generator = new ThumbnailGenerator(ThumbnailOptions.Default with { Limits = new ResourceLimits { MaxInputBytes = 4 << 20 } });
        Assert.Throws<ResourceLimitExceededException>(() => generator.Generate(DrawingFiles.GzipOfZeros(64 << 20), "bomb.svgz"));
    }

    [Fact]
    public void Gzip_without_svgz_name_is_not_an_image() =>
        Assert.NotEqual(DocumentFormat.Image, ThumbnailGenerator.DetectFormat(DrawingFiles.Svgz(), "data.gz"));

    [Fact]
    public void Riff_cdr_shows_its_disp_preview()
    {
        var cdr = DrawingFiles.CdrRiff();
        Assert.Equal(DocumentFormat.Image, ThumbnailGenerator.DetectFormat(cdr));
        Assert.Equal(0, Difference(Full(cdr, "drawing.cdr"), (r, g, b) => (r, g, b)));
    }

    [Fact]
    public void Packaged_cdr_shows_its_preview_image()
    {
        var picture = Full(DrawingFiles.CdrPackage(), null);
        Assert.Equal((96, 60), (picture.Width, picture.Height));
    }

    [Fact]
    public void Packaged_cdr_without_a_preview_image_uses_the_inner_disp()
    {
        var picture = Full(DrawingFiles.CdrPackage(thumbnail: false), "drawing.cdr");
        Assert.Equal((32, 20), (picture.Width, picture.Height));
    }

    [Fact]
    public void Cdr_without_a_preview_is_refused() =>
        Assert.Throws<UnsupportedFormatException>(() => Generator.Generate(DrawingFiles.CdrRiff(preview: false), "drawing.cdr"));

    // ---- DICOM -----------------------------------------------------------------------------------

    [Theory]
    [InlineData(DicomKind.Mono16Window)]
    [InlineData(DicomKind.Implicit16)]
    [InlineData(DicomKind.Rle16)]
    public void Dicom_16_bit_goes_through_its_window(DicomKind kind)
    {
        var dicom = DicomFiles.Dicom(kind);
        Assert.Equal(DocumentFormat.Image, ThumbnailGenerator.DetectFormat(dicom));
        Assert.InRange(Difference(Full(dicom, "scan.dcm"), (r, _, _) => (r, r, r)), 0, 2);
    }

    [Fact]
    public void Dicom_monochrome1_is_inverted()
    {
        var normal = Full(DicomFiles.Dicom(DicomKind.Mono8), "a.dcm");
        var inverted = Full(DicomFiles.Dicom(DicomKind.Mono1), "b.dcm");
        for (var i = 0; i < normal.Width * normal.Height; i += 97)
            Assert.InRange(At(normal, i % normal.Width, i / normal.Width).R + At(inverted, i % normal.Width, i / normal.Width).R, 254, 256);
    }

    [Fact]
    public void Dicom_rgb_is_exact() =>
        Assert.Equal(0, Difference(Full(DicomFiles.Dicom(DicomKind.Rgb8), "colour.dcm"), (r, g, b) => (r, g, b)));

    [Fact]
    public void Dicom_declaring_a_huge_image_is_refused() =>
        Assert.Throws<ResourceLimitExceededException>(() => Generator.Generate(DicomFiles.Dicom(DicomKind.Mono8, declaredRows: 60000), "huge.dcm"));

    [Fact]
    public void Dicom_cut_short_is_damaged()
    {
        var dicom = DicomFiles.Dicom(DicomKind.Mono16Window);
        Assert.Throws<InvalidDocumentException>(() => Generator.Generate(dicom[..(dicom.Length - 100)], "cut.dcm"));
    }

    // ---- extensions --------------------------------------------------------------------------------

    [Theory]
    [InlineData(".ai")]
    [InlineData(".odg")]
    [InlineData(".otg")]
    [InlineData(".fodg")]
    [InlineData(".svgz")]
    [InlineData(".cdr")]
    [InlineData(".dcm")]
    [InlineData(".dicom")]
    public void New_extensions_are_listed(string extension) => Assert.Contains(extension, ThumbnailGenerator.SupportedExtensions);
}
