using System.Diagnostics;
using FastThumb.Tests.Fixtures;

namespace FastThumb.Tests;

public class CancellationTests
{
    private static readonly ThumbnailGenerator Generator = new(ThumbnailOptions.Default with { Sizes = [300] });
    private static readonly CancellationToken Cancelled = new(canceled: true);

    [Theory]
    [InlineData("docx")]
    [InlineData("xlsx")]
    [InlineData("pdf")]
    [InlineData("png")]
    public void A_cancelled_token_stops_every_entry_point_before_any_work(string name)
    {
        var sample = Samples.Get(name);
        Assert.ThrowsAny<OperationCanceledException>(() => Generator.Generate(sample.Bytes, sample.FileName, Cancelled));
        Assert.ThrowsAny<OperationCanceledException>(() => Generator.Generate(sample.Bytes.AsMemory(), sample.FileName, Cancelled));
        using var stream = new MemoryStream(sample.Bytes, false);
        Assert.ThrowsAny<OperationCanceledException>(() => Generator.Generate(stream, sample.FileName, Cancelled));
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task A_cancelled_token_stops_the_async_entry_point()
    {
        using var seekable = new MemoryStream(Samples.Docx.Bytes, false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Generator.GenerateAsync(seekable, "a.docx", Cancelled));
        using var forwardOnly = new NonSeekableStream(new MemoryStream(Samples.Docx.Bytes, false));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Generator.GenerateAsync(forwardOnly, "a.docx", Cancelled));
    }

    [Fact]
    public void A_cancelled_token_stops_a_file_before_it_is_rendered()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fastthumb-{Guid.NewGuid():N}.docx");
        File.WriteAllBytes(path, Samples.Docx.Bytes);
        try { Assert.ThrowsAny<OperationCanceledException>(() => Generator.GenerateFromFile(path, Cancelled)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void The_exception_carries_the_callers_token()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var error = Assert.ThrowsAny<OperationCanceledException>(() => Generator.Generate(Samples.Docx.Bytes, null, source.Token));
        Assert.Equal(source.Token, error.CancellationToken);
    }

    /// <summary>
    /// Cancels a long document while it is being laid out. The document grows until the cancellation lands mid-way (a fast
    /// machine may finish a small one first), so the test does not depend on how fast the machine is.
    /// </summary>
    [Fact]
    public void Cancelling_a_large_document_mid_way_stops_it_quickly()
    {
        for (var paragraphs = 3000; ; paragraphs *= 4)
        {
            var docx = OfficeFiles.LargeDocx(paragraphs);
            using var source = new CancellationTokenSource();
            var sinceCancel = new Stopwatch();
            using var registration = source.Token.Register(sinceCancel.Start);
            source.CancelAfter(TimeSpan.FromMilliseconds(30));

            var error = TestUtil.Within(TestUtil.Seconds(30), () => Generator.Generate(docx, "large.docx", source.Token));
            var stoppedAfter = sinceCancel.Elapsed;

            if (error == null)
            {
                // finished before the 30 ms were over: try a longer document
                Assert.True(paragraphs < 200_000, "the document was never slow enough to be cancelled mid-way");
                continue;
            }
            var cancelled = Assert.IsAssignableFrom<OperationCanceledException>(error);
            Assert.Equal(source.Token, cancelled.CancellationToken);
            Assert.True(stoppedAfter < TimeSpan.FromSeconds(3), $"stopped {stoppedAfter.TotalMilliseconds:0} ms after the cancellation");
            return;
        }
    }

    [Fact]
    public async Task Cancelling_an_async_call_mid_way_stops_it_quickly()
    {
        for (var paragraphs = 3000; ; paragraphs *= 4)
        {
            var docx = OfficeFiles.LargeDocx(paragraphs);
            using var source = new CancellationTokenSource();
            using var stream = new MemoryStream(docx, false);
            // measured from the cancellation, not from the start: a loaded CI machine may take seconds to get there
            var sinceCancel = new Stopwatch();
            using var registration = source.Token.Register(sinceCancel.Start);
            source.CancelAfter(TimeSpan.FromMilliseconds(30));
            try
            {
                await Generator.GenerateAsync(stream, "large.docx", source.Token);
            }
            catch (OperationCanceledException)
            {
                Assert.True(sinceCancel.Elapsed < TimeSpan.FromSeconds(3), $"stopped {sinceCancel.ElapsedMilliseconds} ms after the cancellation");
                return;
            }
            Assert.True(paragraphs < 200_000, "the document was never slow enough to be cancelled mid-way");
        }
    }

    [Fact]
    public void A_cancelled_call_does_not_affect_the_next_one_on_the_same_thread()
    {
        Assert.ThrowsAny<OperationCanceledException>(() => Generator.Generate(Samples.Docx.Bytes, null, Cancelled));
        var result = Generator.Generate(Samples.Docx.Bytes);
        Assert.Equal(1, result.PageCount);
        Assert.NotEmpty(result.Images);
    }

    [Fact]
    public void A_token_that_is_never_cancelled_changes_nothing()
    {
        using var source = new CancellationTokenSource();
        var with = Generator.Generate(Samples.Pptx.Bytes, null, source.Token);
        var without = Generator.Generate(Samples.Pptx.Bytes);
        Assert.Equal(without.Images.Select(i => i.Data), with.Images.Select(i => i.Data));
    }
}
