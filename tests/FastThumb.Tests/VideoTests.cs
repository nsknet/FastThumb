using FastThumb.Tests.Fixtures;

namespace FastThumb.Tests;

/// <summary>
/// Video needs the native thumbvideo library and the fixture videos (tests/fixtures/video, 64 x 48, 1 s, made by
/// tests/fixtures/make-videos.sh); the tests are skipped where either is missing.
/// </summary>
public class VideoTests
{
    private const int Width = 64, Height = 48;
    private static readonly ThumbnailOptions Options = ThumbnailOptions.Default with { Sizes = [] };
    private static readonly ThumbnailGenerator Generator = new(Options);

    public static IEnumerable<object[]> Videos => VideoFixtures.FileData;

    [VideoFact]
    public void Native_library_reports_its_version()
    {
        Assert.NotEmpty(FastThumb.Video.VideoFrame.Version);
    }

    [VideoTheory]
    [MemberData(nameof(Videos))]
    public void Video_is_detected_by_content(string file)
    {
        var data = File.ReadAllBytes(VideoFixtures.PathOf(file));
        Assert.Equal(DocumentFormat.Video, ThumbnailGenerator.DetectFormat(data));
        Assert.Equal(DocumentFormat.Video, ThumbnailGenerator.DetectFormat(data, "misleading.docx"));
    }

    [VideoTheory]
    [MemberData(nameof(Videos))]
    public void Video_gives_one_frame_at_its_own_size(string file)
    {
        var result = Generator.GenerateFromFile(VideoFixtures.PathOf(file));
        Assert.Equal(DocumentFormat.Video, result.Format);
        Assert.Equal(1, result.PageCount);
        Assert.Null(result.PreviewPdf);
        var image = Assert.Single(result.Images);
        Assert.True(image.IsFullSize);
        Assert.Equal((Width, Height), (image.Width, image.Height));
        Assert.Equal(ThumbnailFormat.Png, image.Format);
        Assert.Equal((Width, Height), ImageFiles.Size(image.Data));
        var (min, max) = ImageFiles.Range(image.Data);
        Assert.True(max - min > 50, "the frame is blank");
    }

    [VideoTheory]
    [MemberData(nameof(Videos))]
    public void Video_frame_gives_cover_boxes_a_placeholder_and_no_text(string file)
    {
        var generator = new ThumbnailGenerator(Options with
        {
            Sizes = [100, 32],
            IncludeFullSize = false,
            Fit = ThumbnailFit.Cover,
            Outputs = ThumbnailOutputs.Images | ThumbnailOutputs.Text | ThumbnailOutputs.Placeholder,
        });
        var result = generator.GenerateFromFile(VideoFixtures.PathOf(file));
        // 64 x 48 cannot fill 100 x 100: the largest square it fills is 48 x 48
        Assert.Equal((48, 48), (result.Find(100)!.Width, result.Find(100)!.Height));
        Assert.Equal((32, 32), (result.Find(32)!.Width, result.Find(32)!.Height));
        Assert.Empty(result.PageTexts!);
        Assert.NotNull(result.Placeholder);
        Assert.True((result.Placeholder.ThumbHash[4] & 0x80) != 0, "a 64 x 48 frame is landscape");
    }

    [VideoTheory]
    [MemberData(nameof(Videos))]
    public void Video_frame_is_scaled_down_but_never_enlarged(string file)
    {
        var generator = new ThumbnailGenerator(Options with { Sizes = [500, 32], Format = ThumbnailFormat.Jpeg });
        var result = generator.GenerateFromFile(VideoFixtures.PathOf(file));
        Assert.Equal(new int?[] { null, 500, 32 }, result.Images.Select(i => i.MaxDimension));
        Assert.Equal((Width, Height), (result.FullSize!.Width, result.FullSize.Height));
        Assert.Equal((Width, Height), (result.Find(500)!.Width, result.Find(500)!.Height));
        Assert.Equal((32, 24), (result.Find(32)!.Width, result.Find(32)!.Height));
        Assert.All(result.Images, i => Assert.Equal(ThumbnailFormat.Jpeg, i.Format));
    }

    [VideoTheory]
    [MemberData(nameof(Videos))]
    public void Position_changes_the_frame(string file)
    {
        var path = VideoFixtures.PathOf(file);
        byte[] FrameAt(double position) =>
            new ThumbnailGenerator(Options with { Video = new VideoOptions { Position = position } }).GenerateFromFile(path).Images[0].Data;

        var start = FrameAt(0);
        var end = FrameAt(1);
        Assert.NotEqual(start, end);
        Assert.Equal(start, FrameAt(0));          // the same position gives the same frame
        Assert.Equal(FrameAt(0.5), Generator.GenerateFromFile(path).Images[0].Data);   // the middle is the default
    }

    [VideoTheory]
    [MemberData(nameof(Videos))]
    public async Task Every_input_path_gives_the_same_frame(string file)
    {
        var path = VideoFixtures.PathOf(file);
        var data = await File.ReadAllBytesAsync(path);
        var reference = Generator.GenerateFromFile(path).Images[0].Data;

        Assert.Equal(reference, Generator.Generate(data).Images[0].Data);
        Assert.Equal(reference, Generator.Generate(data, file).Images[0].Data);
        using (var stream = new MemoryStream(data, false))
            Assert.Equal(reference, Generator.Generate(stream).Images[0].Data);
        using (var stream = new NonSeekableStream(new MemoryStream(data, false)))
            Assert.Equal(reference, Generator.Generate(stream).Images[0].Data);
        using (var stream = new MemoryStream(data, false))
            Assert.Equal(reference, (await Generator.GenerateAsync(stream)).Images[0].Data);
        using (var stream = new NonSeekableStream(new MemoryStream(data, false)))
            Assert.Equal(reference, (await Generator.GenerateAsync(stream)).Images[0].Data);

        // a video that starts in the middle of a seekable stream
        var padded = new byte[data.Length + 1000];
        data.CopyTo(padded, 1000);
        using (var stream = new MemoryStream(padded, false) { Position = 1000 })
            Assert.Equal(reference, Generator.Generate(stream).Images[0].Data);
    }

    [VideoTheory]
    [MemberData(nameof(Videos))]
    public void Preview_pdf_only_gives_nothing_for_a_video(string file)
    {
        var generator = new ThumbnailGenerator(ThumbnailOptions.Default with { Outputs = ThumbnailOutputs.PreviewPdf });
        var result = generator.GenerateFromFile(VideoFixtures.PathOf(file));
        Assert.Equal(DocumentFormat.Video, result.Format);
        Assert.Empty(result.Images);
        Assert.Null(result.PreviewPdf);
    }

    [VideoFact]
    public void Timeout_must_be_positive()
    {
        var path = VideoFixtures.Files[0];
        Assert.ThrowsAny<ArgumentException>(() => new ThumbnailGenerator(Options with { Video = new VideoOptions { Timeout = TimeSpan.Zero } }));
        Assert.ThrowsAny<ArgumentException>(() => new ThumbnailGenerator(Options with { Video = new VideoOptions { Timeout = TimeSpan.FromSeconds(-5) } }));
        // a positive one is used
        var generator = new ThumbnailGenerator(Options with { Video = new VideoOptions { Timeout = TimeSpan.FromSeconds(30) } });
        Assert.Single(generator.GenerateFromFile(path).Images);
    }

    [VideoTheory]
    [MemberData(nameof(Videos))]
    public void Frame_larger_than_max_pixels_is_refused(string file)
    {
        var generator = new ThumbnailGenerator(Options with { Limits = new ResourceLimits { MaxPixels = Width * Height - 1 } });
        Assert.Throws<ResourceLimitExceededException>(() => generator.GenerateFromFile(VideoFixtures.PathOf(file)));
    }

    [VideoFact]
    public void Cancelled_token_stops_a_video()
    {
        var path = VideoFixtures.Files[0];
        Assert.ThrowsAny<OperationCanceledException>(() => Generator.GenerateFromFile(path, new CancellationToken(canceled: true)));
    }

    [VideoFact]
    public void Garbage_behind_a_video_signature_is_reported_as_unsupported_or_damaged()
    {
        byte[][] signatures =
        [
            [0, 0, 0, 0x20, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m'],
            [0x1A, 0x45, 0xDF, 0xA3, 0x9F, 0x42, 0x86, 0x81, 0x01, 0x42, 0xF7, 0x81],
            [(byte)'R', (byte)'I', (byte)'F', (byte)'F', 0, 0, 1, 0, (byte)'A', (byte)'V', (byte)'I', (byte)' '],
            [(byte)'F', (byte)'L', (byte)'V', 1, 1, 0, 0, 0, 9, 0, 0, 0],
            [(byte)'O', (byte)'g', (byte)'g', (byte)'S', 0, 2, 0, 0, 0, 0, 0, 0],
        ];
        var generator = new ThumbnailGenerator(Options with { Video = new VideoOptions { Timeout = TimeSpan.FromSeconds(10) } });
        foreach (var signature in signatures)
            for (var seed = 1; seed <= 3; seed++)
            {
                var data = HostileFiles.Garbage(seed, 20_000);
                signature.CopyTo(data, 0);
                var error = TestUtil.Within(TimeSpan.FromSeconds(30), () => generator.Generate(data));
                Assert.True(error is UnsupportedFormatException or InvalidDocumentException or TimeoutException,
                    $"signature {Convert.ToHexString(signature)}, seed {seed}: {error?.GetType().Name}: {error?.Message}");
            }
    }

    [VideoTheory]
    [MemberData(nameof(Videos))]
    public void Fuzz_flipped_bytes_only_give_documented_errors(string file)
    {
        var original = File.ReadAllBytes(VideoFixtures.PathOf(file));
        var generator = new ThumbnailGenerator(Options with { Video = new VideoOptions { Timeout = TimeSpan.FromSeconds(10) } });
        var failures = new List<string>();
        var hang = TestUtil.Within(TimeSpan.FromSeconds(60), () =>
        {
            for (var seed = 0; seed < 50; seed++)
            {
                var random = new Random(seed);
                var data = original.ToArray();
                for (var i = 0; i < 4; i++) data[random.Next(data.Length)] ^= (byte)(1 << random.Next(8));
                try { generator.Generate(data, file); }
                catch (Exception e) when (e is ThumbnailException or TimeoutException or OperationCanceledException) { }
                catch (Exception e) { failures.Add($"seed {seed}: {e.GetType().FullName}: {e.Message}"); }
            }
        });
        Assert.Null(hang);
        Assert.True(failures.Count == 0, $"{file}: {failures.Count} mutant(s) escaped with an undocumented exception\n{string.Join('\n', failures)}");
    }

    [Fact]
    public void Media_extension_only_counts_when_the_bytes_are_no_other_format()
    {
        // detection needs no native library: a media extension on unrecognised bytes is "video", known bytes win
        Assert.Equal(DocumentFormat.Video, ThumbnailGenerator.DetectFormat("just some text, long enough"u8.ToArray(), "song.wav"));
        Assert.Equal(DocumentFormat.Pdf, ThumbnailGenerator.DetectFormat(Samples.Pdf.Bytes, "movie.mp4"));
        Assert.Equal(DocumentFormat.Docx, ThumbnailGenerator.DetectFormat(Samples.Docx.Bytes, "movie.mkv"));
    }
}
