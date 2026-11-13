using FastThumb.Images;
using FastThumb.Tests.Fixtures;
using Xunit.Abstractions;

namespace FastThumb.Tests;

/// <summary>
/// The managed JPEG 2000 decoder against OpenJPEG (and ffmpeg's decoder where Pillow refuses a reduction): every committed file
/// of tests/fixtures/jpeg2000 at every reduction it has a reference picture for.
/// </summary>
public class Jpeg2000Tests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> References => Jpeg2000Files.References.Select(r => new object[] { r.File, r.Reduce });

    [Theory]
    [MemberData(nameof(References))]
    public void Decodes_like_the_reference(string file, int reduce)
    {
        var data = File.ReadAllBytes(Path.Combine(Jpeg2000Files.Directory!, file));
        using var expected = NetVips.Image.NewFromFile(Path.Combine(Jpeg2000Files.Directory!, $"{file}.r{reduce}.png"));
        var (width, height) = (expected.Width, expected.Height);
        using var image = ManagedImages.Decode(data, null, new DecodeRequest(long.MaxValue, (w, h) => w >= width && h >= height))!;
        Assert.Equal((width, height), (image.Width, image.Height));
        Assert.Equal(expected.Bands, image.Bands);
        var actualPixels = image.WriteToMemory<byte>();
        var expectedPixels = expected.WriteToMemory<byte>();
        var worst = 0;
        long sum = 0;
        for (var i = 0; i < actualPixels.Length; i++)
        {
            var d = Math.Abs(actualPixels[i] - expectedPixels[i]);
            worst = Math.Max(worst, d);
            sum += d;
        }
        var mean = (double)sum / actualPixels.Length;
        output.WriteLine($"{file} r{reduce}: {width} x {height}, max {worst}, mean {mean:F3}");
        var (maxAllowed, meanAllowed) = Jpeg2000Files.Tolerance(file);
        Assert.InRange(worst, 0, maxAllowed);
        Assert.InRange(mean, 0, meanAllowed);
    }

    private static readonly ThumbnailGenerator Generator = new(ThumbnailOptions.Default with { Sizes = [200] });

    [Theory]
    [InlineData(100, 160)]          // 80 px (two halvings) would be too small for 100
    [InlineData(80, 80)]
    [InlineData(5000, 320)]         // never more than the picture
    public void Decodes_at_the_smallest_resolution_that_is_big_enough(int side, int width)
    {
        var data = Jpeg2000Files.Read("lossless-lrcp.jp2");
        using var image = ManagedImages.Decode(data, null, new DecodeRequest(long.MaxValue, (w, h) => Math.Max(w, h) >= side))!;
        Assert.Equal(width, image.Width);
    }

    [Fact]
    public void A_thumbnail_only_request_is_served_from_a_reduced_decode()
    {
        var options = ThumbnailOptions.Default with { Sizes = [100], IncludeFullSize = false };
        var result = new ThumbnailGenerator(options).Generate(Jpeg2000Files.Read("lossy-rpcl.jp2"), "photo.jp2");
        Assert.Equal((100, 63), (result.Find(100)!.Width, result.Find(100)!.Height));
        Assert.Null(result.FullSize);
    }

    [Fact]
    public void A_picture_larger_than_max_pixels_is_reduced_until_it_fits()
    {
        var tight = new ThumbnailGenerator(ThumbnailOptions.Default with { Limits = new ResourceLimits { MaxPixels = 20_000 } });
        var full = tight.Generate(Jpeg2000Files.Read("lossless-lrcp.jp2")).FullSize!;
        Assert.Equal((160, 100), (full.Width, full.Height));
    }

    [Fact]
    public void Jp2_and_raw_codestreams_are_told_by_their_signature()
    {
        Assert.Equal(DocumentFormat.Image, ThumbnailGenerator.DetectFormat(Jpeg2000Files.Read("lossless-lrcp.jp2"), null));
        Assert.Equal(DocumentFormat.Image, ThumbnailGenerator.DetectFormat(Jpeg2000Files.Read("layers-rlcp.j2k"), "misnamed.txt"));
        var info = Generator.Inspect(Jpeg2000Files.Read("odd-333x211.jp2"));
        Assert.Equal((333, 211), (info.Width, info.Height));
    }

    /// <summary>A codestream with one tile and one component, its header declaring the size and levels, and almost no data.</summary>
    private static byte[] HeaderOnly(int width, int height, int levels)
    {
        using var output = new MemoryStream();
        void U16(int v) { output.WriteByte((byte)(v >> 8)); output.WriteByte((byte)v); }
        void U32(int v) { U16(v >> 16); U16(v & 0xFFFF); }
        U16(0xFF4F);
        U16(0xFF51); U16(41); U16(0); U32(width); U32(height); U32(0); U32(0); U32(width); U32(height); U32(0); U32(0); U16(1);
        output.Write([7, 1, 1]);
        U16(0xFF52); U16(12); output.Write([0, 0, 0, 1, 0, (byte)levels, 4, 4, 0, 1]);
        U16(0xFF5C); U16(3 + 3 * levels + 1); output.WriteByte(0x40); for (var i = 0; i < 3 * levels + 1; i++) output.WriteByte(8 << 3);
        U16(0xFF90); U16(10); U16(0); U32(0); output.Write([0, 1]);
        U16(0xFF93);
        output.Write(new byte[16]);
        U16(0xFFD9);
        return output.ToArray();
    }

    [Fact]
    public void A_header_declaring_a_huge_size_is_refused_when_it_cannot_be_reduced()
    {
        var huge = HeaderOnly(60000, 60000, 0);
        Assert.Equal(DocumentFormat.Image, ThumbnailGenerator.DetectFormat(huge));
        Assert.Throws<ResourceLimitExceededException>(() => Generator.Generate(huge, "huge.j2k"));
        // with levels to drop, it is decoded small: there is no data, so the picture is flat grey
        var reducible = HeaderOnly(60000, 60000, 8);
        var full = Generator.Generate(reducible, "huge.j2k").FullSize!;
        Assert.InRange(full.Width, 1, 16384);
    }

    [Fact]
    public void A_cut_file_keeps_what_it_has()
    {
        var data = Jpeg2000Files.Read("lossless-lrcp.jp2");
        var result = Generator.Generate(data[..(data.Length * 2 / 3)], "cut.jp2");
        Assert.Equal((320, 200), (result.FullSize!.Width, result.FullSize.Height));
    }

    [Fact]
    public void Progression_order_changes_are_refused_as_unsupported()
    {
        var data = HeaderOnly(64, 64, 1);
        var sot = data.AsSpan().IndexOf([(byte)0xFF, (byte)0x90]);
        byte[] poc = [0xFF, 0x5F, 0x00, 0x09, 0, 0, 0, 1, 1, 1, 0];
        var withPoc = data[..sot].Concat(poc).Concat(data[sot..]).ToArray();
        Assert.Throws<UnsupportedFormatException>(() => Generator.Generate(withPoc, "poc.j2k"));
    }
}
