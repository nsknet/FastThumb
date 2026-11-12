namespace FastThumb.Tests;

public class OptionsTests
{
    public static IEnumerable<object[]> InvalidOptions()
    {
        var d = ThumbnailOptions.Default;
        object[] Case(string name, ThumbnailOptions options) => [name, options];
        yield return Case("size 0", d with { Sizes = [0] });
        yield return Case("size -1", d with { Sizes = [100, -1] });
        yield return Case("size 65501", d with { Sizes = [65501] });
        yield return Case("sizes null", d with { Sizes = null! });
        yield return Case("quality 0", d with { Quality = 0 });
        yield return Case("quality 101", d with { Quality = 101 });
        yield return Case("png level -1", d with { PngCompressionLevel = -1 });
        yield return Case("png level 10", d with { PngCompressionLevel = 10 });
        yield return Case("format undefined", d with { Format = (ThumbnailFormat)42 });
        yield return Case("video position 1.5", d with { Video = new VideoOptions { Position = 1.5 } });
        yield return Case("video position -0.1", d with { Video = new VideoOptions { Position = -0.1 } });
        yield return Case("video position NaN", d with { Video = new VideoOptions { Position = double.NaN } });
        yield return Case("video timeout zero", d with { Video = new VideoOptions { Timeout = TimeSpan.Zero } });
        yield return Case("video timeout negative", d with { Video = new VideoOptions { Timeout = TimeSpan.FromSeconds(-1) } });
        yield return Case("video timeout infinite", d with { Video = new VideoOptions { Timeout = Timeout.InfiniteTimeSpan } });
        yield return Case("video null", d with { Video = null! });
        yield return Case("max pixels 0", d with { Limits = new ResourceLimits { MaxPixels = 0 } });
        yield return Case("max input bytes 0", d with { Limits = new ResourceLimits { MaxInputBytes = 0 } });
        yield return Case("max input bytes over 2 GiB", d with { Limits = new ResourceLimits { MaxInputBytes = 3L << 30 } });
        yield return Case("limits null", d with { Limits = null! });
        yield return Case("outputs none", d with { Outputs = 0 });
        yield return Case("outputs unknown flag", d with { Outputs = (ThumbnailOutputs)4 });
        yield return Case("no image requested", d with { Sizes = [], IncludeFullSize = false });
    }

    [Theory]
    [MemberData(nameof(InvalidOptions))]
    public void Validate_rejects_out_of_range_values(string name, ThumbnailOptions options)
    {
        Assert.False(string.IsNullOrEmpty(name));
        Assert.ThrowsAny<ArgumentException>(options.Validate);
    }

    [Theory]
    [MemberData(nameof(InvalidOptions))]
    public void The_constructor_validates(string name, ThumbnailOptions options)
    {
        Assert.False(string.IsNullOrEmpty(name));
        Assert.ThrowsAny<ArgumentException>(() => new ThumbnailGenerator(options));
    }

    public static IEnumerable<object[]> ValidOptions()
    {
        var d = ThumbnailOptions.Default;
        yield return [d];
        yield return [d with { Sizes = [1, 65500] }];
        yield return [d with { Quality = 1 }];
        yield return [d with { Quality = 100, Format = ThumbnailFormat.Jpeg }];
        yield return [d with { PngCompressionLevel = 0 }];
        yield return [d with { PngCompressionLevel = 9 }];
        yield return [d with { Video = new VideoOptions { Position = 0, Timeout = TimeSpan.FromMilliseconds(1) } }];
        yield return [d with { Video = new VideoOptions { Position = 1 } }];
        yield return [d with { Limits = new ResourceLimits { MaxPixels = 1, MaxInputBytes = 1 } }];
        yield return [d with { Limits = new ResourceLimits { MaxInputBytes = int.MaxValue } }];
        // nothing to draw is fine when only the PDF is wanted
        yield return [d with { Outputs = ThumbnailOutputs.PreviewPdf, Sizes = [], IncludeFullSize = false }];
        yield return [d with { Sizes = [], IncludeFullSize = true }];
    }

    [Theory]
    [MemberData(nameof(ValidOptions))]
    public void Validate_accepts_values_in_range(ThumbnailOptions options)
    {
        options.Validate();
        Assert.Same(options, new ThumbnailGenerator(options).Options);
    }

    [Fact]
    public void Defaults_are_as_documented()
    {
        var d = ThumbnailOptions.Default;
        Assert.Equal(ThumbnailOutputs.All, d.Outputs);
        Assert.Equal(new[] { 2500, 1000, 500, 300, 100 }, d.Sizes);
        Assert.True(d.IncludeFullSize);
        Assert.Equal(ThumbnailFormat.Png, d.Format);
        Assert.Equal(80, d.Quality);
        Assert.Equal(3, d.PngCompressionLevel);
        Assert.Equal(0.5, d.Video.Position);
        Assert.Equal(TimeSpan.FromSeconds(60), d.Video.Timeout);
        Assert.Equal(16384L * 16384, d.Limits.MaxPixels);
        Assert.Equal(1L << 30, d.Limits.MaxInputBytes);
        Assert.Same(d, new ThumbnailGenerator().Options);
        Assert.Same(d, ThumbnailGenerator.Default.Options);
    }

    [Fact]
    public void Options_are_immutable_records()
    {
        var a = ThumbnailOptions.Default with { Quality = 50 };
        var b = ThumbnailOptions.Default with { Quality = 50 };
        Assert.Equal(a, b);
        Assert.NotEqual(ThumbnailOptions.Default, a);
        Assert.Equal(80, ThumbnailOptions.Default.Quality);
    }
}
