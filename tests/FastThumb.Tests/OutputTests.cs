using System.Text.RegularExpressions;
using FastThumb.Tests.Fixtures;
using NetVips;

namespace FastThumb.Tests;

/// <summary>Text, MaxPages, Cover thumbnails and placeholders.</summary>
public class OutputTests
{
    private static ThumbnailResult Generate(Sample sample, ThumbnailOptions options) =>
        new ThumbnailGenerator(options).Generate(sample.Bytes, sample.FileName);

    private static readonly ThumbnailOptions TextOnly = ThumbnailOptions.Default with { Outputs = ThumbnailOutputs.Text };

    // ---- text ------------------------------------------------------------------------------------

    [Fact]
    public void Text_of_a_document_comes_per_page_with_words_spaced()
    {
        var result = Generate(Samples.Docx3, TextOnly);
        Assert.Empty(result.Images);
        Assert.Null(result.PreviewPdf);
        Assert.Equal(3, result.PageTexts!.Count);
        for (var page = 1; page <= 3; page++)
        {
            Assert.StartsWith($"Page {page} heading\n", result.PageTexts[page - 1]);
            Assert.Contains("The quick brown fox jumps over the lazy dog. 0123456789\n", result.PageTexts[page - 1]);
        }
        Assert.Equal(string.Join("\n\n", result.PageTexts.Select(p => p.TrimEnd('\n'))), result.Text);
    }

    [Theory]
    [InlineData("rtf", "RTF heading")]
    [InlineData("html", "HTML heading")]
    [InlineData("markdown", "Markdown heading")]
    [InlineData("text", "The quick brown fox jumps over the lazy dog.")]
    [InlineData("odt", "")]
    [InlineData("pptx", "")]
    public void Text_is_read_from_every_kind_of_document(string name, string expected)
    {
        var result = Generate(Samples.Get(name), TextOnly);
        Assert.NotNull(result.PageTexts);
        Assert.Equal(result.PageCount, result.PageTexts.Count);
        Assert.False(string.IsNullOrWhiteSpace(result.Text));
        Assert.Contains(expected, result.Text);
        Assert.DoesNotContain("  ", result.Text);
    }

    [Fact]
    public void Spreadsheet_cells_on_one_row_stay_on_one_line()
    {
        var text = Generate(Samples.Csv, TextOnly).Text!;
        Assert.Matches(new Regex(@"^Name[ \t]+Quantity[ \t]+Price$", RegexOptions.Multiline), text);
        Assert.Matches(new Regex(@"^Item 25[ \t]+25[ \t]+37\.50$", RegexOptions.Multiline), text);
    }

    [Fact]
    public void Text_of_a_pdf_comes_from_its_text_layer()
    {
        var result = Generate(Samples.Pdf3, TextOnly);
        Assert.Equal(3, result.PageCount);
        Assert.Equal(["Page 1\n", "Page 2\n", "Page 3\n"], result.PageTexts);
        Assert.False(result.HasMorePages);
    }

    [Theory]
    [MemberData(nameof(Samples.RasterImageData), MemberType = typeof(Samples))]
    public void An_image_has_no_text(string name)
    {
        var result = Generate(Samples.Get(name), ThumbnailOptions.Default with { Outputs = ThumbnailOutputs.All | ThumbnailOutputs.Text, Sizes = [50] });
        Assert.Empty(result.PageTexts!);
        Assert.Equal("", result.Text);
        Assert.Equal(2, result.Images.Count);
    }

    [Theory]
    [MemberData(nameof(Samples.AllData), MemberType = typeof(Samples))]
    public void Text_and_placeholder_are_null_unless_requested(string name)
    {
        var result = Generate(Samples.Get(name), ThumbnailOptions.Default with { Sizes = [50] });
        Assert.Null(result.PageTexts);
        Assert.Null(result.Text);
        Assert.Null(result.Placeholder);
        Assert.False(result.HasMorePages);
    }

    // ---- MaxPages --------------------------------------------------------------------------------

    [Fact]
    public void MaxPages_stops_the_layout_and_says_so()
    {
        var options = ThumbnailOptions.Default with { Sizes = [100], MaxPages = 1, Outputs = ThumbnailOutputs.All | ThumbnailOutputs.Text };
        var result = Generate(Samples.Docx3, options);
        Assert.Equal(1, result.PageCount);
        Assert.True(result.HasMorePages);
        Assert.Single(result.PageTexts!);
        Assert.Equal(1, PdfPages(result.PreviewPdf!));

        var all = Generate(Samples.Docx3, options with { MaxPages = 3 });
        Assert.Equal(3, all.PageCount);
        Assert.False(all.HasMorePages);
        Assert.Equal(3, PdfPages(all.PreviewPdf!));
        Assert.False(Generate(Samples.Docx3, options with { MaxPages = 10 }).HasMorePages);
    }

    [Fact]
    public void MaxPages_does_not_change_the_thumbnail_of_page_1()
    {
        var options = ThumbnailOptions.Default with { Sizes = [300], Outputs = ThumbnailOutputs.Images };
        var all = Generate(Samples.Docx3, options);
        var first = Generate(Samples.Docx3, options with { MaxPages = 1 });
        Assert.Equal(all.Images.Select(i => i.Data), first.Images.Select(i => i.Data));
    }

    [Fact]
    public void MaxPages_limits_slides()
    {
        var result = Generate(Samples.Pptx, ThumbnailOptions.Default with { Sizes = [100], MaxPages = 1 });
        Assert.Equal(1, result.PageCount);
        Assert.True(result.HasMorePages);
        Assert.Equal(1, PdfPages(result.PreviewPdf!));
    }

    [Fact]
    public void MaxPages_limits_the_text_of_a_pdf_but_not_its_page_count()
    {
        var result = Generate(Samples.Pdf3, TextOnly with { MaxPages = 2 });
        Assert.Equal(3, result.PageCount);
        Assert.True(result.HasMorePages);
        Assert.Equal(["Page 1\n", "Page 2\n"], result.PageTexts);
    }

    /// <summary>Page objects in a PDF written by this library (each page dictionary has "/Type /Page" once).</summary>
    private static int PdfPages(byte[] pdf) =>
        Regex.Matches(System.Text.Encoding.Latin1.GetString(pdf), @"/Type\s*/Page(?![s\w])").Count;

    // ---- Cover -----------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Samples.AllData), MemberType = typeof(Samples))]
    public void Cover_gives_square_boxes_and_keeps_the_full_size_whole(string name)
    {
        var sample = Samples.Get(name);
        var contain = Generate(sample, ThumbnailOptions.Default with { Sizes = [100], Outputs = ThumbnailOutputs.Images });
        var cover = Generate(sample, ThumbnailOptions.Default with { Sizes = [160, 100], Fit = ThumbnailFit.Cover, Outputs = ThumbnailOutputs.Images });
        Assert.Equal((contain.FullSize!.Width, contain.FullSize.Height), (cover.FullSize!.Width, cover.FullSize.Height));
        foreach (var image in cover.Images.Where(i => !i.IsFullSize))
        {
            Assert.Equal(image.Width, image.Height);
            Assert.Equal((image.Width, image.Height), ImageFiles.Size(image.Data));
            var (min, max) = ImageFiles.Range(image.Data);
            Assert.True(max > min, "blank thumbnail");
        }
        Assert.Equal((100, 100), (cover.Find(100)!.Width, cover.Find(100)!.Height));
    }

    [Theory]
    [InlineData(16.0 / 9, 320, 320, 180)]
    [InlineData(0.5, 200, 100, 200)]
    [InlineData(1, 64, 64, 64)]
    public void Cover_follows_the_aspect_ratio(double ratio, int size, int width, int height)
    {
        var result = Generate(Samples.Docx, ThumbnailOptions.Default with
        {
            Sizes = [size],
            IncludeFullSize = false,
            Fit = ThumbnailFit.Cover,
            AspectRatio = ratio,
            Outputs = ThumbnailOutputs.Images,
        });
        var image = Assert.Single(result.Images);
        Assert.Equal((width, height), (image.Width, image.Height));
        Assert.Equal((width, height), ImageFiles.Size(image.Data));
    }

    [Fact]
    public void Cover_never_enlarges_an_image()
    {
        // the 320 x 200 picture cannot fill a 300 x 300 box: the largest square it fills is 200 x 200
        var result = Generate(Samples.Png, ThumbnailOptions.Default with { Sizes = [300, 50], IncludeFullSize = false, Fit = ThumbnailFit.Cover });
        Assert.Equal((200, 200), (result.Find(300)!.Width, result.Find(300)!.Height));
        Assert.Equal((50, 50), (result.Find(50)!.Width, result.Find(50)!.Height));
    }

    [Fact]
    public void Cover_of_a_document_keeps_the_top_of_the_page()
    {
        // the heading is at the top: a wide crop from the top has dark pixels, the same crop from the centre would be blank
        var result = Generate(Samples.Docx, ThumbnailOptions.Default with
        {
            Sizes = [400],
            IncludeFullSize = false,
            Fit = ThumbnailFit.Cover,
            AspectRatio = 4,
            Outputs = ThumbnailOutputs.Images,
        });
        var (min, _) = ImageFiles.Range(result.Images[0].Data);
        Assert.True(min < 128, "the heading should be in the crop");
    }

    // ---- placeholder -----------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Samples.AllData), MemberType = typeof(Samples))]
    public void Placeholder_is_made_for_every_input(string name)
    {
        var result = Generate(Samples.Get(name), ThumbnailOptions.Default with { Outputs = ThumbnailOutputs.Placeholder });
        Assert.Empty(result.Images);
        Assert.Null(result.PreviewPdf);
        var placeholder = Assert.IsType<ThumbnailPlaceholder>(result.Placeholder);
        Assert.InRange(placeholder.ThumbHash.Length, 5, 40);
        Assert.Matches("^#[0-9a-f]{6}$", placeholder.AverageColor);
        Assert.Equal(placeholder.ThumbHash, Convert.FromBase64String(placeholder.ThumbHashBase64));
    }

    [Fact]
    public void Placeholder_is_the_same_with_or_without_images()
    {
        var alone = Generate(Samples.Png, ThumbnailOptions.Default with { Outputs = ThumbnailOutputs.Placeholder });
        var withImages = Generate(Samples.Png, ThumbnailOptions.Default with { Outputs = ThumbnailOutputs.All | ThumbnailOutputs.Placeholder });
        Assert.Equal(6, withImages.Images.Count);
        Assert.Equal(alone.Placeholder!.ThumbHash, withImages.Placeholder!.ThumbHash);
        Assert.Equal(alone.Placeholder.AverageColor, withImages.Placeholder.AverageColor);
    }

    [Theory]
    [InlineData(255, 0, 0, 300, 200)]
    [InlineData(20, 120, 220, 64, 256)]
    [InlineData(255, 255, 255, 50, 50)]
    public void Placeholder_of_a_plain_picture_has_its_colour(int r, int g, int b, int width, int height)
    {
        using var plain = (Image.Black(width, height, bands: 3) + new double[] { r, g, b }).Cast(Enums.BandFormat.Uchar).Copy(interpretation: Enums.Interpretation.Srgb);
        var png = plain.WriteToBuffer(".png");
        var placeholder = new ThumbnailGenerator(ThumbnailOptions.Default with { Outputs = ThumbnailOutputs.Placeholder }).Generate(png).Placeholder!;
        Assert.Equal($"#{r:x2}{g:x2}{b:x2}", placeholder.AverageColor);

        // decode the average colour the way ThumbHash readers do (thumbHashToAverageRGBA of the reference implementation)
        var decoded = PageAverage(placeholder);
        Assert.InRange(decoded[0], r / 255.0 - 0.05, r / 255.0 + 0.05);
        Assert.InRange(decoded[1], g / 255.0 - 0.05, g / 255.0 + 0.05);
        Assert.InRange(decoded[2], b / 255.0 - 0.05, b / 255.0 + 0.05);
        Assert.Equal(width > height, (placeholder.ThumbHash[4] & 0x80) != 0);   // landscape flag
    }

    [Fact]
    public void Placeholder_of_a_document_page_is_mostly_white()
    {
        var placeholder = Generate(Samples.Docx, ThumbnailOptions.Default with { Outputs = ThumbnailOutputs.Placeholder }).Placeholder!;
        var rgb = Convert.FromHexString(placeholder.AverageColor[1..]);
        Assert.All(rgb, channel => Assert.True(channel > 220));
    }

    [Fact]
    public void Placeholder_keeps_transparency()
    {
        using var rgb = (Image.Black(40, 40, bands: 3) + new double[] { 0, 0, 255 }).Cast(Enums.BandFormat.Uchar);
        using var alpha = (Image.Black(40, 40) + 0).Cast(Enums.BandFormat.Uchar).Insert((Image.Black(20, 40) + 255).Cast(Enums.BandFormat.Uchar), 0, 0);
        using var rgba = rgb.Bandjoin(alpha).Copy(interpretation: Enums.Interpretation.Srgb);
        var placeholder = new ThumbnailGenerator(ThumbnailOptions.Default with { Outputs = ThumbnailOutputs.Placeholder })
            .Generate(rgba.WriteToBuffer(".png")).Placeholder!;
        Assert.True((placeholder.ThumbHash[2] & 0x80) != 0, "the alpha flag should be set");
        Assert.Equal("#0000ff", placeholder.AverageColor);
    }

    /// <summary>Average R, G, B (0 to 1) decoded from a ThumbHash header, as the reference decoder does.</summary>
    private static double[] PageAverage(ThumbnailPlaceholder placeholder)
    {
        var hash = placeholder.ThumbHash;
        var header = hash[0] | (hash[1] << 8) | (hash[2] << 16);
        var l = (header & 63) / 63.0;
        var p = ((header >> 6) & 63) / 31.5 - 1;
        var q = ((header >> 12) & 63) / 31.5 - 1;
        var b = l - 2.0 / 3 * p;
        var r = (3 * l - b + q) / 2;
        var g = r - q;
        return [Math.Clamp(r, 0, 1), Math.Clamp(g, 0, 1), Math.Clamp(b, 0, 1)];
    }

    // ---- options ---------------------------------------------------------------------------------

    [Fact]
    public void New_options_are_validated()
    {
        Assert.Throws<ArgumentException>(() => new ThumbnailGenerator(ThumbnailOptions.Default with { MaxPages = 0 }));
        Assert.Throws<ArgumentException>(() => new ThumbnailGenerator(ThumbnailOptions.Default with { AspectRatio = 0 }));
        Assert.Throws<ArgumentException>(() => new ThumbnailGenerator(ThumbnailOptions.Default with { AspectRatio = double.NaN }));
        Assert.Throws<ArgumentException>(() => new ThumbnailGenerator(ThumbnailOptions.Default with { Fit = (ThumbnailFit)7 }));
        _ = new ThumbnailGenerator(ThumbnailOptions.Default with { Outputs = ThumbnailOutputs.Text });
        _ = new ThumbnailGenerator(ThumbnailOptions.Default with { Outputs = ThumbnailOutputs.Placeholder });
    }

    [Fact]
    public void All_still_means_images_and_preview_pdf()
    {
        Assert.Equal(ThumbnailOutputs.Images | ThumbnailOutputs.PreviewPdf, ThumbnailOutputs.All);
    }
}
