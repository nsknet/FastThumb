using FastThumb.Tests.Fixtures;

namespace FastThumb.Tests;

/// <summary>Every way of handing over the same input gives the same thumbnails.</summary>
public class InputPathTests
{
    private static readonly ThumbnailGenerator Generator = new(ThumbnailOptions.Default with { Sizes = [300, 64] });

    public static IEnumerable<object[]> Inputs => new[] { "docx", "xlsx", "pdf", "png", "csv" }.Select(n => new object[] { n });

    private static void AssertSame(ThumbnailResult expected, ThumbnailResult actual)
    {
        Assert.Equal(expected.Format, actual.Format);
        Assert.Equal(expected.PageCount, actual.PageCount);
        Assert.Equal(expected.Images.Count, actual.Images.Count);
        for (var i = 0; i < expected.Images.Count; i++)
        {
            Assert.Equal(expected.Images[i].MaxDimension, actual.Images[i].MaxDimension);
            Assert.Equal(expected.Images[i].Data, actual.Images[i].Data);
        }
        Assert.Equal(expected.PreviewPdf is null, actual.PreviewPdf is null);
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public async Task All_input_paths_give_identical_images(string name)
    {
        var sample = Samples.Get(name);
        var reference = Generator.Generate(sample.Bytes, sample.FileName);

        // a slice in the middle of a larger array
        var padded = new byte[sample.Bytes.Length + 100];
        sample.Bytes.CopyTo(padded, 37);
        AssertSame(reference, Generator.Generate(padded.AsMemory(37, sample.Bytes.Length), sample.FileName));

        // a copy (not the cached array) as ReadOnlyMemory
        AssertSame(reference, Generator.Generate(new ReadOnlyMemory<byte>(sample.Bytes.ToArray()), sample.FileName));

        // seekable stream
        using (var stream = new MemoryStream(sample.Bytes, false))
            AssertSame(reference, Generator.Generate(stream, sample.FileName));

        // non-seekable stream with short reads
        using (var stream = new NonSeekableStream(new MemoryStream(sample.Bytes, false)))
            AssertSame(reference, Generator.Generate(stream, sample.FileName));

        // a stream positioned after a prefix: read from the current position
        using (var stream = new MemoryStream(padded, 0, 37 + sample.Bytes.Length, false) { Position = 37 })
            AssertSame(reference, Generator.Generate(stream, sample.FileName));
        using (var stream = new NonSeekableStream(new MemoryStream(padded, 37, sample.Bytes.Length, false)))
            AssertSame(reference, Generator.Generate(stream, sample.FileName));

        // file on disk (the name gives the extension)
        var directory = Directory.CreateTempSubdirectory("fastthumb-tests-");
        try
        {
            var path = Path.Combine(directory.FullName, sample.FileName);
            await File.WriteAllBytesAsync(path, sample.Bytes);
            AssertSame(reference, Generator.GenerateFromFile(path));
        }
        finally { directory.Delete(recursive: true); }

        // asynchronous, seekable and not
        using (var stream = new MemoryStream(sample.Bytes, false))
            AssertSame(reference, await Generator.GenerateAsync(stream, sample.FileName));
        using (var stream = new NonSeekableStream(new MemoryStream(sample.Bytes, false)))
            AssertSame(reference, await Generator.GenerateAsync(stream, sample.FileName));
    }

    [Fact]
    public void The_stream_is_not_disposed()
    {
        using var stream = new MemoryStream(Samples.Png.Bytes, false);
        Generator.Generate(stream);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public void The_file_name_may_be_a_bare_extension()
    {
        var csv = Samples.Csv.Bytes;
        Assert.Equal(DocumentFormat.Csv, Generator.Generate(csv, ".csv").Format);
        Assert.Equal(DocumentFormat.Csv, Generator.Generate(csv, "some/dir/report.CSV").Format);
        Assert.Equal(DocumentFormat.Text, Generator.Generate(csv).Format);
    }

    [Fact]
    public async Task Missing_arguments_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => Generator.Generate((Stream)null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Generator.GenerateAsync(null!));
        Assert.ThrowsAny<ArgumentException>(() => Generator.GenerateFromFile(""));
        Assert.Throws<FileNotFoundException>(() => Generator.GenerateFromFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".docx")));
    }

    [Fact]
    public async Task One_generator_serves_concurrent_calls()
    {
        var samples = new[] { Samples.Docx, Samples.Pptx, Samples.Pdf, Samples.Png, Samples.Xlsx, Samples.Odt };
        var expected = samples.Select(s => Generator.Generate(s.Bytes, s.FileName)).ToArray();
        var results = await Task.WhenAll(samples.Concat(samples).Select(s => Task.Run(() => Generator.Generate(s.Bytes, s.FileName))));
        for (var i = 0; i < results.Length; i++) AssertSame(expected[i % samples.Length], results[i]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(65536)]
    [InlineData(65537)]
    [InlineData(9_000_123)]
    public async Task A_non_seekable_stream_is_read_into_one_exact_array(int length)
    {
        var data = new byte[length];
        new Random(length).NextBytes(data);
        var options = ThumbnailOptions.Default with { Limits = new ResourceLimits { MaxInputBytes = Math.Max(1, length) } };

        Assert.Equal(data, Thumbnailer.ReadAll(new NonSeekableStream(new MemoryStream(data, false)), options));
        Assert.Equal(data, await Thumbnailer.ReadAllAsync(new NonSeekableStream(new MemoryStream(data, false)), options, default));

        if (length == 0) return;
        var tooSmall = ThumbnailOptions.Default with { Limits = new ResourceLimits { MaxInputBytes = length - 1 } };
        if (length > 1)
        {
            Assert.Throws<ResourceLimitExceededException>(() => Thumbnailer.ReadAll(new NonSeekableStream(new MemoryStream(data, false)), tooSmall));
            await Assert.ThrowsAsync<ResourceLimitExceededException>(() => Thumbnailer.ReadAllAsync(new NonSeekableStream(new MemoryStream(data, false)), tooSmall, default));
        }
    }

}
