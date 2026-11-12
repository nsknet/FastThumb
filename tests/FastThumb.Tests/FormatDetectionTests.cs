using System.Text;
using FastThumb.Tests.Fixtures;

namespace FastThumb.Tests;

public class FormatDetectionTests
{
    [Theory]
    [MemberData(nameof(Samples.AllData), MemberType = typeof(Samples))]
    public void Detects_each_generated_input(string name)
    {
        var sample = Samples.Get(name);
        Assert.Equal(sample.Format, ThumbnailGenerator.DetectFormat(sample.Bytes, sample.FileName));
    }

    [Theory]
    [InlineData("docx")]
    [InlineData("xlsx")]
    [InlineData("pptx")]
    [InlineData("odt")]
    [InlineData("ods")]
    [InlineData("rtf")]
    [InlineData("html")]
    [InlineData("pdf")]
    [InlineData("png")]
    [InlineData("jpeg")]
    [InlineData("webp")]
    public void Content_wins_over_a_misleading_extension(string name)
    {
        var sample = Samples.Get(name);
        foreach (var misleading in new[] { null, "file.txt", "file.csv", "file.md", "file.pdf", "file.docx", "file.xlsx" })
            Assert.Equal(sample.Format, ThumbnailGenerator.DetectFormat(sample.Bytes, misleading));
    }

    [Theory]
    [InlineData("test.csv", DocumentFormat.Csv)]
    [InlineData("test.tsv", DocumentFormat.Csv)]
    [InlineData("TEST.MD", DocumentFormat.Markdown)]
    [InlineData(".markdown", DocumentFormat.Markdown)]
    [InlineData("test.txt", DocumentFormat.Text)]
    [InlineData("test.htm", DocumentFormat.Html)]
    [InlineData(null, DocumentFormat.Text)]
    [InlineData("noextension", DocumentFormat.Text)]
    public void Plain_text_formats_are_told_apart_by_the_extension(string? fileName, DocumentFormat expected)
    {
        var text = Encoding.UTF8.GetBytes("a,b,c\n1,2,3\n");
        Assert.Equal(expected, ThumbnailGenerator.DetectFormat(text, fileName));
    }

    [Fact]
    public void Svg_needs_its_extension()
    {
        var svg = Samples.Svg.Bytes;
        Assert.Equal(DocumentFormat.Image, ThumbnailGenerator.DetectFormat(svg, "drawing.svg"));
        Assert.Equal(DocumentFormat.Text, ThumbnailGenerator.DetectFormat(svg, null));
    }

    [Fact]
    public void Empty_or_tiny_input_is_text_not_an_error()
    {
        Assert.Equal(DocumentFormat.Text, ThumbnailGenerator.DetectFormat(ReadOnlyMemory<byte>.Empty));
        Assert.Equal(DocumentFormat.Text, ThumbnailGenerator.DetectFormat(new byte[] { 0x25 }));
    }

    [Fact]
    public void Damaged_container_is_reported_as_unknown()
    {
        Assert.Equal(DocumentFormat.Unknown, ThumbnailGenerator.DetectFormat(HostileFiles.CompoundFileHugeFat()));
        var zipHeaderOnly = new byte[] { (byte)'P', (byte)'K', 3, 4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        Assert.Equal(DocumentFormat.Unknown, ThumbnailGenerator.DetectFormat(zipHeaderOnly));
    }

    [Fact]
    public void Supported_extensions_are_lower_case_with_a_dot()
    {
        var extensions = ThumbnailGenerator.SupportedExtensions;
        Assert.All(extensions, e => { Assert.StartsWith(".", e); Assert.Equal(e.ToLowerInvariant(), e); });
        foreach (var sample in Samples.All) Assert.Contains(Path.GetExtension(sample.FileName), extensions);
        Assert.Contains(".mp4", extensions);
        Assert.Contains(".webm", extensions);
    }
}
