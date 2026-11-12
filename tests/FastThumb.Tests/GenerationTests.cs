using FastThumb.Tests.Fixtures;

namespace FastThumb.Tests;

public class GenerationTests
{
    private static readonly ThumbnailOptions Small = ThumbnailOptions.Default with { Sizes = [400, 100] };

    private static ThumbnailResult Generate(Sample sample, ThumbnailOptions? options = null) =>
        new ThumbnailGenerator(options ?? Small).Generate(sample.Bytes, sample.FileName);

    [Theory]
    [MemberData(nameof(Samples.AllData), MemberType = typeof(Samples))]
    public void Reports_format_and_page_count(string name)
    {
        var sample = Samples.Get(name);
        var result = Generate(sample);
        Assert.Equal(sample.Format, result.Format);
        if (sample.Pages is { } pages) Assert.Equal(pages, result.PageCount);
        else Assert.True(result.PageCount >= 1);
    }

    [Theory]
    [MemberData(nameof(Samples.AllData), MemberType = typeof(Samples))]
    public void Images_come_full_size_first_then_in_the_order_of_sizes(string name)
    {
        var result = Generate(Samples.Get(name));
        Assert.Equal(3, result.Images.Count);
        Assert.True(result.Images[0].IsFullSize);
        Assert.Same(result.Images[0], result.FullSize);
        Assert.Equal("full", result.Images[0].Name);
        Assert.Equal(new int?[] { null, 400, 100 }, result.Images.Select(i => i.MaxDimension));
        Assert.Same(result.Images[1], result.Find(400));
        Assert.Null(result.Find(123));
    }

    [Theory]
    [MemberData(nameof(Samples.AllData), MemberType = typeof(Samples))]
    public void Every_image_is_a_non_blank_png_of_the_reported_size(string name)
    {
        var result = Generate(Samples.Get(name));
        foreach (var image in result.Images)
        {
            Assert.Equal(ThumbnailFormat.Png, image.Format);
            Assert.Equal((image.Width, image.Height), ImageFiles.Size(image.Data));
            var (min, max) = ImageFiles.Range(image.Data);
            Assert.True(max - min > 0, $"{image} is blank");
        }
    }

    [Theory]
    [MemberData(nameof(Samples.DocumentData), MemberType = typeof(Samples))]
    public void Document_thumbnails_have_the_requested_longest_side(string name)
    {
        var result = Generate(Samples.Get(name), ThumbnailOptions.Default with { Sizes = [1200, 400, 33] });
        foreach (var image in result.Images.Where(i => !i.IsFullSize))
            Assert.Equal(image.MaxDimension, Math.Max(image.Width, image.Height));
    }

    [Fact]
    public void Pdf_thumbnails_have_the_requested_longest_side_and_the_full_size_is_72_dpi()
    {
        var result = Generate(Samples.Pdf, ThumbnailOptions.Default with { Sizes = [1200, 400, 33] });
        Assert.Equal((612, 792), (result.FullSize!.Width, result.FullSize.Height));   // US Letter at 72 DPI
        foreach (var image in result.Images.Where(i => !i.IsFullSize))
            Assert.Equal(image.MaxDimension, Math.Max(image.Width, image.Height));
    }

    [Fact]
    public void Document_full_size_is_the_page_at_96_dpi()
    {
        var result = Generate(Samples.Docx);
        Assert.Equal((816, 1056), (result.FullSize!.Width, result.FullSize.Height));   // 8.5 x 11 in at 96 DPI
    }

    [Theory]
    [MemberData(nameof(Samples.RasterImageData), MemberType = typeof(Samples))]
    public void Images_are_never_enlarged(string name)
    {
        var result = Generate(Samples.Get(name), ThumbnailOptions.Default with { Sizes = [5000, 320, 100] });
        Assert.Equal((320, 200), (result.FullSize!.Width, result.FullSize.Height));
        Assert.Equal((320, 200), (result.Find(5000)!.Width, result.Find(5000)!.Height));
        Assert.Equal((320, 200), (result.Find(320)!.Width, result.Find(320)!.Height));
        var small = result.Find(100)!;
        Assert.Equal(100, small.Width);
        Assert.InRange(small.Height, 62, 63);
    }

    [Fact]
    public void Svg_is_rendered_at_its_declared_size()
    {
        var result = Generate(Samples.Svg);
        Assert.Equal((200, 100), (result.FullSize!.Width, result.FullSize.Height));
        Assert.Equal((200, 100), (result.Find(400)!.Width, result.Find(400)!.Height));
        Assert.Equal((100, 50), (result.Find(100)!.Width, result.Find(100)!.Height));
    }

    [Theory]
    [MemberData(nameof(Samples.AllData), MemberType = typeof(Samples))]
    public void Preview_pdf_only_for_paginated_documents_with_every_page(string name)
    {
        var sample = Samples.Get(name);
        var result = Generate(sample);
        if (!sample.Paginated)
        {
            Assert.Null(result.PreviewPdf);
            return;
        }
        Assert.NotNull(result.PreviewPdf);
        Assert.True(result.PreviewPdf.AsSpan().StartsWith("%PDF"u8));
        Assert.Equal(result.PageCount, FastThumb.Scene.Pdfium.PageCount(result.PreviewPdf));
    }

    [Theory]
    [InlineData(ThumbnailFormat.WebP, "docx")]
    [InlineData(ThumbnailFormat.WebP, "png")]
    [InlineData(ThumbnailFormat.WebP, "pdf")]
    [InlineData(ThumbnailFormat.Jpeg, "docx")]
    [InlineData(ThumbnailFormat.Jpeg, "png")]
    [InlineData(ThumbnailFormat.Jpeg, "pdf")]
    [InlineData(ThumbnailFormat.Png, "jpeg")]
    public void Encodes_the_requested_format(ThumbnailFormat format, string name)
    {
        var options = Small with { Format = format, Quality = 50 };
        var result = Generate(Samples.Get(name), options);
        Assert.Equal(3, result.Images.Count);
        foreach (var image in result.Images)
        {
            Assert.Equal(format, image.Format);
            var data = image.Data.AsSpan();
            switch (format)
            {
                case ThumbnailFormat.WebP: Assert.True(data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8)); break;
                case ThumbnailFormat.Jpeg: Assert.True(data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF); break;
                default: Assert.True(data[..4].SequenceEqual(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' })); break;
            }
            Assert.Equal((image.Width, image.Height), ImageFiles.Size(image.Data));
        }
        Assert.Equal(format switch { ThumbnailFormat.WebP => "webp", ThumbnailFormat.Jpeg => "jpg", _ => "png" }, options.FileExtension);
    }

    [Fact]
    public void Lower_quality_gives_smaller_files()
    {
        var low = Generate(Samples.Png, Small with { Format = ThumbnailFormat.Jpeg, Quality = 5 }).FullSize!.Data.Length;
        var high = Generate(Samples.Png, Small with { Format = ThumbnailFormat.Jpeg, Quality = 100 }).FullSize!.Data.Length;
        Assert.True(low < high, $"quality 5: {low} bytes, quality 100: {high} bytes");
    }

    [Theory]
    [MemberData(nameof(Samples.DocumentData), MemberType = typeof(Samples))]
    public void Preview_pdf_only_gives_no_images(string name)
    {
        var sample = Samples.Get(name);
        var result = Generate(sample, ThumbnailOptions.Default with { Outputs = ThumbnailOutputs.PreviewPdf });
        Assert.Empty(result.Images);
        Assert.Null(result.FullSize);
        Assert.NotNull(result.PreviewPdf);
        Assert.Equal(sample.Format, result.Format);
        Assert.Equal(result.PageCount, FastThumb.Scene.Pdfium.PageCount(result.PreviewPdf));
    }

    [Theory]
    [InlineData("pdf-3-pages")]
    [InlineData("png")]
    public void Preview_pdf_only_for_a_pdf_or_an_image_gives_nothing_but_facts(string name)
    {
        var sample = Samples.Get(name);
        var result = Generate(sample, ThumbnailOptions.Default with { Outputs = ThumbnailOutputs.PreviewPdf });
        Assert.Empty(result.Images);
        Assert.Null(result.PreviewPdf);
        Assert.Equal(sample.Pages, result.PageCount);
    }

    [Theory]
    [InlineData("docx")]
    [InlineData("pptx")]
    [InlineData("pdf")]
    [InlineData("png")]
    public void Images_only_gives_no_preview_pdf(string name)
    {
        var result = Generate(Samples.Get(name), Small with { Outputs = ThumbnailOutputs.Images });
        Assert.Null(result.PreviewPdf);
        Assert.Equal(3, result.Images.Count);
    }

    [Theory]
    [InlineData("docx")]
    [InlineData("xlsx")]
    [InlineData("pdf")]
    [InlineData("png")]
    public void Without_full_size_only_the_requested_sizes_come_back(string name)
    {
        var result = Generate(Samples.Get(name), Small with { IncludeFullSize = false, Sizes = [64, 256] });
        Assert.Null(result.FullSize);
        Assert.Equal(new int?[] { 64, 256 }, result.Images.Select(i => i.MaxDimension));
        Assert.All(result.Images, i => Assert.False(i.IsFullSize));
    }

    [Fact]
    public void Full_size_alone_without_sizes()
    {
        var result = Generate(Samples.Docx, ThumbnailOptions.Default with { Sizes = [] });
        var image = Assert.Single(result.Images);
        Assert.True(image.IsFullSize);
        Assert.Equal((816, 1056), (image.Width, image.Height));
    }

    [Fact]
    public void Duplicate_sizes_each_get_an_image()
    {
        var result = Generate(Samples.Docx, Small with { Sizes = [100, 100], IncludeFullSize = false });
        Assert.Equal(2, result.Images.Count);
        Assert.Equal(result.Images[0].Data, result.Images[1].Data);
    }

    [Fact]
    public void Serial_and_parallel_encoding_give_the_same_bytes()
    {
        var parallel = Generate(Samples.Pptx, Small with { EncodeInParallel = true });
        var serial = Generate(Samples.Pptx, Small with { EncodeInParallel = false });
        Assert.Equal(parallel.Images.Select(i => i.Data), serial.Images.Select(i => i.Data));
    }

    [Fact]
    public void Slides_keep_the_deck_aspect_ratio()
    {
        var image = Generate(Samples.Pptx).Find(400)!;
        Assert.Equal((400, 225), (image.Width, image.Height));   // 16:9
    }

    [Fact]
    public void Default_generator_produces_the_default_sizes()
    {
        var result = ThumbnailGenerator.Default.Generate(Samples.Png.Bytes);
        Assert.Equal(ThumbnailOptions.DefaultSizes.Select(s => (int?)s).Prepend(null), result.Images.Select(i => i.MaxDimension));
        Assert.True(result.Timings.Total >= TimeSpan.Zero);
    }
}
