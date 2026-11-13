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
    public void Tga_16_bit_attribute_bit_set_is_transparent()
    {
        // two pixels, 5-5-5 with the attribute bit clear then set, descriptor giving 1 alpha bit (as Pillow reads them)
        byte[] tga = [0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 1, 0, 16, 0x21, 0x1F, 0x54, 0xEA, 0x83];
        var (pixels, bands, _, _) = Pixels(tga, ".tga");
        Assert.Equal(4, bands);
        Assert.Equal([173, 0, 255, 255, 0, 255, 82, 0], pixels);   // Pillow gives 172 for the 5-bit 21: it rounds down
    }

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

    // ---- PSD -------------------------------------------------------------------------------------

    [Theory]
    [InlineData(3, 8, 0, false)]
    [InlineData(3, 8, 1, false)]
    [InlineData(3, 8, 2, false)]
    [InlineData(3, 8, 3, false)]
    [InlineData(3, 16, 1, false)]
    [InlineData(3, 16, 3, false)]
    [InlineData(3, 8, 1, true)]
    [InlineData(3, 16, 1, true)]
    [InlineData(4, 8, 1, false)]
    [InlineData(4, 16, 0, true)]
    public void Psd_colour_composite_is_exact(int mode, int depth, int compression, bool large)
    {
        var psd = RasterFiles.Psd(mode: mode, depth: depth, compression: compression, large: large);
        Assert.Equal(0, Difference(psd, null, Same));
        Assert.Equal(3, Pixels(psd, null).Bands);
    }

    [Theory]
    [InlineData(1, 8, 1)]
    [InlineData(1, 16, 0)]
    [InlineData(2, 8, 1)]
    [InlineData(2, 8, 2)]
    public void Psd_grey_and_indexed_composite_is_exact(int mode, int depth, int compression) =>
        Assert.Equal(0, Difference(RasterFiles.Psd(mode: mode, depth: depth, compression: compression), null, Red));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Psd_bitmap_is_black_where_set(int compression) =>
        Assert.Equal(0, Difference(RasterFiles.Psd(mode: 0, depth: 1, compression: compression), null, (r, _, _) => r < 128 ? ((byte)0, (byte)0, (byte)0) : ((byte)255, (byte)255, (byte)255)));

    [Fact]
    public void Psd_alpha_is_used_only_when_the_layer_count_says_so()
    {
        var (pixels, bands, _, _) = Pixels(RasterFiles.Psd(alpha: true), null);
        Assert.Equal(4, bands);
        Assert.Equal(128, pixels[3]);
        var grey = Pixels(RasterFiles.Psd(mode: 1, alpha: true, large: true), null);
        Assert.Equal(2, grey.Bands);
        Assert.Equal(128, grey.Pixels[1]);
    }

    [Fact]
    public void Psd_32_bit_falls_back_to_its_thumbnail()
    {
        var psd = RasterFiles.Psd(depth: 32, compression: 0, thumbnail: ImageFiles.Jpeg(160, 100));
        Assert.Equal((160, 100), (Pixels(psd, null).Width, Pixels(psd, null).Height));
        Assert.Equal((320, 200), (Generator.Inspect(psd).Width, Generator.Inspect(psd).Height));
        Assert.Throws<UnsupportedFormatException>(() => Generator.Generate(RasterFiles.Psd(depth: 32, compression: 0), "no-thumbnail.psd"));
    }

    [Fact]
    public void Psd_is_told_by_its_signature()
    {
        Assert.Equal(DocumentFormat.Image, ThumbnailGenerator.DetectFormat(RasterFiles.Psd(), null));
        Assert.Equal(DocumentFormat.Image, ThumbnailGenerator.DetectFormat(RasterFiles.Psd(large: true), "misnamed.txt"));
    }

    [Fact]
    public void Psd_declaring_a_huge_size_is_refused() =>
        Assert.Throws<ResourceLimitExceededException>(() => Generator.Generate(RasterFiles.PsdHeaderOnly(300000, 300000), "huge.psd"));

    [Fact]
    public void Psd_rows_running_past_the_end_are_damaged()
    {
        var psd = RasterFiles.Psd(compression: 0);
        Assert.Throws<InvalidDocumentException>(() => Generator.Generate(psd[..(psd.Length / 2)], "cut.psd"));
    }

    // ---- EPS -------------------------------------------------------------------------------------

    [Fact]
    public void Eps_shows_the_tiff_of_a_dos_eps() => Assert.Equal(0, Difference(RasterFiles.Eps(EpsKind.DosTiff), null, Same));

    [Fact]
    public void Eps_shows_an_epsi_preview_with_0_as_white()
    {
        Assert.Equal(0, Difference(RasterFiles.Eps(EpsKind.Epsi8), null, Red));
        Assert.Equal(0, Difference(RasterFiles.Eps(EpsKind.Epsi1), null,
            (r, _, _) => r < 128 ? ((byte)0, (byte)0, (byte)0) : ((byte)255, (byte)255, (byte)255)));
    }

    [Fact]
    public void Photoshop_eps_shows_its_colour_thumbnail_rather_than_its_1_bit_preview()
    {
        using var expected = NetVips.Image.NewFromBuffer(ImageFiles.BareJpeg(160, 100));
        var (pixels, _, width, height) = Pixels(RasterFiles.Eps(EpsKind.Photoshop), "photo.eps");
        Assert.Equal((160, 100), (width, height));
        Assert.Equal(expected.WriteToMemory<byte>(), pixels);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Eps_shows_the_illustrator_thumbnail(bool rle) =>
        Assert.Equal(0, Difference(RasterFiles.Eps(rle ? EpsKind.IllustratorRle : EpsKind.Illustrator), null, Red));

    [Fact]
    public void Eps_without_a_preview_is_refused_but_inspected_from_its_bounding_box()
    {
        var eps = RasterFiles.Eps(EpsKind.None, 300, 150);
        Assert.Equal(DocumentFormat.Image, ThumbnailGenerator.DetectFormat(eps, null));
        Assert.Throws<UnsupportedFormatException>(() => Generator.Generate(eps, "drawing.eps"));
        var info = Generator.Inspect(eps);
        Assert.Equal((300, 150), (info.Width, info.Height));
    }

    [Fact]
    public void Plain_postscript_is_not_taken_for_eps()
    {
        var ps = RasterFiles.Ascii("%!PS-Adobe-3.0\n%%Pages: 1\nshowpage\n");
        Assert.Equal(DocumentFormat.Text, ThumbnailGenerator.DetectFormat(ps, "document.ps"));
        Assert.Equal(DocumentFormat.Image, ThumbnailGenerator.DetectFormat(ps, "drawing.eps"));
    }

    [Fact]
    public void Eps_previews_declaring_a_huge_size_are_refused()
    {
        var epsi = RasterFiles.Ascii("%!PS-Adobe-3.0 EPSF-3.0\n%%BeginPreview: 60000 60000 8 1\n%00\n%%EndPreview\n");
        Assert.Throws<ResourceLimitExceededException>(() => Generator.Generate(epsi, "huge.eps"));
        var ai = RasterFiles.Ascii("%!PS-Adobe-3.0 EPSF-3.0\n%AI7_Thumbnail: 60000 60000 8\n%%BeginData: 2 Hex Bytes\n%00\n%%EndData\n");
        Assert.Throws<ResourceLimitExceededException>(() => Generator.Generate(ai, "huge.eps"));
    }

    [Fact]
    public void Dos_eps_sections_past_the_end_are_damaged()
    {
        var eps = RasterFiles.Eps(EpsKind.DosTiff);
        Assert.Throws<InvalidDocumentException>(() => Generator.Generate(eps[..(eps.Length - 100)], "cut.eps"));
    }

    // ---- RAW -------------------------------------------------------------------------------------

    public static IEnumerable<object[]> RawKinds => Enum.GetValues<RawKind>().Where(k => k != RawKind.NoPreview).Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(RawKinds))]
    public void Raw_shows_its_largest_jpeg_preview(RawKind kind)
    {
        var raw = RasterFiles.Raw(kind);
        Assert.Equal(DocumentFormat.Image, ThumbnailGenerator.DetectFormat(raw, "photo" + Extension(kind)));
        using var expected = NetVips.Image.NewFromBuffer(ImageFiles.BareJpeg());
        Assert.Equal(expected.WriteToMemory<byte>(), Pixels(raw, Extension(kind)).Pixels);
    }

    private static string Extension(RawKind kind) => kind switch
    {
        RawKind.NefBigEndian => ".nef",
        RawKind.NoPreview => ".nef",
        _ => "." + kind.ToString().ToLowerInvariant(),
    };

    [Theory]
    [InlineData(RawKind.Cr2)]
    [InlineData(RawKind.Dng)]
    [InlineData(RawKind.Orf)]
    [InlineData(RawKind.Rw2)]
    [InlineData(RawKind.Raf)]
    [InlineData(RawKind.Cr3)]
    public void Raw_is_told_without_its_extension(RawKind kind)
    {
        var raw = RasterFiles.Raw(kind);
        Assert.Equal(DocumentFormat.Image, ThumbnailGenerator.DetectFormat(raw, null));
        Assert.Equal((320, 200), (Generator.Inspect(raw).Width, Generator.Inspect(raw).Height));
        Assert.Equal((200, 125), (Generator.Generate(raw).Find(200)!.Width, Generator.Generate(raw).Find(200)!.Height));
    }

    [Fact]
    public void Cr3_is_not_taken_for_a_video() => Assert.False(FormatDetector.IsVideo(RasterFiles.Raw(RawKind.Cr3), ".cr3"));

    [Theory]
    [InlineData(RawKind.Cr2, 6)]
    [InlineData(RawKind.NefBigEndian, 6)]
    [InlineData(RawKind.Cr3, 6)]
    [InlineData(RawKind.Cr2, 1)]
    public void Raw_orientation_turns_the_preview(RawKind kind, int orientation)
    {
        var raw = RasterFiles.Raw(kind, orientation: orientation);
        var (width, height) = orientation == 6 ? (200, 320) : (320, 200);
        var full = Generator.Generate(raw, "photo" + Extension(kind)).FullSize!;
        Assert.Equal((width, height), (full.Width, full.Height));
        var info = Generator.Inspect(raw, "photo" + Extension(kind));
        Assert.Equal((width, height), (info.Width, info.Height));
    }

    [Fact]
    public void Raw_without_a_preview_is_refused() =>
        Assert.Throws<UnsupportedFormatException>(() => Generator.Generate(RasterFiles.Raw(RawKind.NoPreview), "sensor-only.nef"));

    [Fact]
    public void A_plain_tiff_named_as_raw_is_still_read_as_tiff()
    {
        using var picture = NetVips.Image.NewFromBuffer(ImageFiles.Png());
        var tiff = picture.WriteToBuffer(".tif");
        var full = Generator.Generate(tiff, "not-really.nef").FullSize!;
        Assert.Equal((320, 200), (full.Width, full.Height));
        Assert.Equal((320, 200), (Generator.Inspect(tiff, "not-really.nef").Width, Generator.Inspect(tiff, "not-really.nef").Height));
    }

    [Fact]
    public void Raw_preview_declaring_a_huge_size_is_refused() =>
        Assert.Throws<ResourceLimitExceededException>(() =>
            Generator.Generate(RasterFiles.Raw(RawKind.Raf, preview: RasterFiles.JpegDeclaring(65000, 65000)), "huge.raf"));

    [Fact]
    public void Raw_directories_that_loop_end()
    {
        var raw = RasterFiles.Raw(RawKind.Cr2);
        // the last directory's next-directory offset back to IFD0
        var ifd0 = BitConverter.ToInt32(raw, 4);
        var last = ifd0;
        for (var i = 0; i < 2; i++) last = BitConverter.ToInt32(raw, last + 2 + 12 * BitConverter.ToUInt16(raw, last));
        BitConverter.TryWriteBytes(raw.AsSpan(last + 2 + 12 * BitConverter.ToUInt16(raw, last)), ifd0);
        Assert.Equal((320, 200), (Pixels(raw, ".cr2").Width, Pixels(raw, ".cr2").Height));
    }
}
