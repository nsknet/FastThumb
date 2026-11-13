using FastThumb.Images;
using FastThumb.Tests.Fixtures;

namespace FastThumb.Tests;

/// <summary>The image formats read in managed code (no loader in the bundled libvips): pixels, detection and limits.</summary>
public class ManagedImageTests
{
    private static readonly ThumbnailGenerator Generator = new(ThumbnailOptions.Default with { Sizes = [200] });

    /// <summary>Decoded pixels, interleaved, and the band count.</summary>
    private static (byte[] Pixels, int Bands, int Width, int Height) Pixels(byte[] data, string? extension)
    {
        using var image = ManagedImages.Decode(data, extension, DecodeRequest.Full(long.MaxValue)) ?? throw new Xunit.Sdk.XunitException("not claimed");
        return (image.WriteToMemory<byte>(), image.Bands, image.Width, image.Height);
    }

    /// <summary>Largest difference of any band of any pixel from the test picture, after <paramref name="expected"/> maps it.</summary>
    private static int Difference(byte[] data, string? extension, Func<byte, byte, byte, (byte R, byte G, byte B)> expected, int width = 320, int height = 200)
    {
        var (pixels, bands, w, h) = Pixels(data, extension);
        Assert.Equal((width, height), (w, h));
        var rgb = ImageFiles.Rgb(width, height);
        var worst = 0;
        for (var i = 0; i < width * height; i++)
        {
            var (r, g, b) = expected(rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2]);
            if (bands >= 3)
                worst = Math.Max(worst, Math.Max(Math.Abs(pixels[i * bands] - r), Math.Max(Math.Abs(pixels[i * bands + 1] - g), Math.Abs(pixels[i * bands + 2] - b))));
            else worst = Math.Max(worst, Math.Abs(pixels[i * bands] - r));
        }
        return worst;
    }

    private static (byte, byte, byte) Same(byte r, byte g, byte b) => (r, g, b);

    private static (byte, byte, byte) Red(byte r, byte g, byte b) => (r, r, r);

    // ---- TGA -------------------------------------------------------------------------------------

    [Theory]
    [InlineData(TgaKind.Colour24, false, false)]
    [InlineData(TgaKind.Colour24, true, false)]
    [InlineData(TgaKind.Colour24, true, true)]
    [InlineData(TgaKind.Colour32, false, true)]
    [InlineData(TgaKind.Colour32, true, false)]
    public void Tga_true_colour_is_exact(TgaKind kind, bool rle, bool topDown)
    {
        var tga = RasterFiles.Tga(kind: kind, rle: rle, topDown: topDown);
        Assert.Equal(0, Difference(tga, ".tga", Same));
        Assert.Equal(kind == TgaKind.Colour32 ? 4 : 3, Pixels(tga, ".tga").Bands);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Tga_16_bit_keeps_5_bits_a_band(bool rle) =>
        Assert.InRange(Difference(RasterFiles.Tga(kind: TgaKind.Colour16, rle: rle), ".tga", Same), 0, 7);

    [Theory]
    [InlineData(TgaKind.Grey8, false)]
    [InlineData(TgaKind.Grey8, true)]
    [InlineData(TgaKind.Mapped8, false)]
    [InlineData(TgaKind.Mapped8, true)]
    public void Tga_grey_and_colour_mapped_are_exact(TgaKind kind, bool rle) =>
        Assert.Equal(0, Difference(RasterFiles.Tga(kind: kind, rle: rle), ".tga", Red));

    [Fact]
    public void Tga_is_told_by_its_extension_or_its_footer()
    {
        var plain = RasterFiles.Tga(footer: false);
        Assert.Equal(DocumentFormat.Image, ThumbnailGenerator.DetectFormat(plain, "picture.tga"));
        Assert.Equal(DocumentFormat.Image, ThumbnailGenerator.DetectFormat(plain, "PICTURE.TGA"));
        Assert.Equal(DocumentFormat.Text, ThumbnailGenerator.DetectFormat(plain, null));
        Assert.Equal(DocumentFormat.Image, ThumbnailGenerator.DetectFormat(RasterFiles.Tga(footer: true), null));
        // the name does not turn another image into a TGA
        Assert.Equal((320, 200), ImageFiles.Size(Generator.Generate(ImageFiles.Png(), "really-a-png.tga").FullSize!.Data));
    }

    [Fact]
    public void Tga_declaring_a_huge_size_is_refused()
    {
        var tga = RasterFiles.TgaHeaderOnly(65535, 65535);
        Assert.Throws<ResourceLimitExceededException>(() => Generator.Generate(tga, "huge.tga"));
    }

    [Fact]
    public void Tga_that_is_cut_short_is_damaged_or_partly_black()
    {
        var plain = RasterFiles.Tga(rle: false, footer: false);
        Assert.Throws<InvalidDocumentException>(() => Generator.Generate(plain[..5000], "cut.tga"));
        var packed = RasterFiles.Tga(rle: true, footer: false);
        Assert.Single(Generator.Generate(packed[..(packed.Length / 2)], "cut.tga").Images, i => i.MaxDimension == 200);
    }

    [Fact]
    public void Tga_with_a_bad_header_is_refused_not_laid_out_as_text()
    {
        var tga = RasterFiles.Tga();
        tga[2] = 7;   // no such image type
        Assert.Throws<InvalidDocumentException>(() => Generator.Generate(tga, "bad.tga"));
    }
}
