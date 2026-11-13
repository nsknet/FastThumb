namespace FastThumb;

/// <summary>What a <see cref="ThumbnailGenerator"/> produces for one input.</summary>
[Flags]
public enum ThumbnailOutputs
{
    /// <summary>The thumbnail images (<see cref="ThumbnailResult.Images"/>).</summary>
    Images = 1,

    /// <summary>
    /// A PDF of every page (<see cref="ThumbnailResult.PreviewPdf"/>). Only paginated documents have one; a PDF, an image or a
    /// video does not.
    /// </summary>
    PreviewPdf = 2,

    /// <summary>Both the images and the preview PDF. Does not include <see cref="Text"/> or <see cref="Placeholder"/>.</summary>
    All = Images | PreviewPdf,

    /// <summary>
    /// The plain text of each page (<see cref="ThumbnailResult.PageTexts"/>), for search indexing. For a document, the text
    /// that is laid out; for a PDF, PDFium's text layer (a scanned page has none). An image or a video has no text.
    /// </summary>
    Text = 4,

    /// <summary>
    /// A tiny blurred preview to show while the real thumbnail loads (<see cref="ThumbnailResult.Placeholder"/>): a ThumbHash
    /// of about 25 bytes and the average colour, taken from page 1, the image or the video frame.
    /// </summary>
    Placeholder = 8,
}

/// <summary>How each thumbnail fills its box.</summary>
public enum ThumbnailFit
{
    /// <summary>The whole page or picture, with its longest side equal to the size. The other side follows the aspect ratio.</summary>
    Contain,

    /// <summary>
    /// Exactly the box of <see cref="ThumbnailOptions.AspectRatio"/> (square by default), filled and cropped: a document page
    /// keeps its top, an image or a video frame its centre. A picture smaller than the box gives a smaller box of the same shape.
    /// </summary>
    Cover,
}

/// <summary>
/// Encoding of the thumbnail images. WebP and JPEG are lossy (<see cref="ThumbnailOptions.Quality"/>) and several times smaller
/// than PNG. JPEG has no transparency: transparent areas are flattened onto white. An image larger than the format allows
/// (16383 px for WebP, 65500 px for JPEG) is written as PNG; <see cref="Thumbnail.Format"/> reports what was written.
/// </summary>
public enum ThumbnailFormat
{
    /// <summary>Lossless PNG.</summary>
    Png,
    /// <summary>Lossy WebP.</summary>
    WebP,
    /// <summary>Baseline JPEG.</summary>
    Jpeg,
}

/// <summary>How a frame is taken from a video.</summary>
public sealed record VideoOptions
{
    /// <summary>Default video options: the middle frame, 60 s time limit.</summary>
    public static VideoOptions Default { get; } = new();

    /// <summary>Where the frame is taken, as a fraction of the duration (0 = start, 0.5 = middle, 1 = end).</summary>
    public double Position { get; init; } = 0.5;

    /// <summary>
    /// Upper bound for reading one video. A damaged file that keeps the decoder busy fails with a
    /// <see cref="TimeoutException"/>. Must be positive.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// Limits applied to untrusted input before any large allocation. A file that exceeds one fails with
/// <see cref="ResourceLimitExceededException"/>, except for pages, which are scaled down to fit <see cref="MaxPixels"/>.
/// </summary>
public sealed record ResourceLimits
{
    /// <summary>The default limits.</summary>
    public static ResourceLimits Default { get; } = new();

    /// <summary>
    /// Largest picture, in pixels, that is decoded or produced: an image file, a video frame, a rendered page, the full-size image.
    /// A few hundred bytes can declare a 60000 x 60000 px image, so a larger image file is refused, and a larger page is rendered
    /// scaled down. Default 16384 x 16384.
    /// </summary>
    public long MaxPixels { get; init; } = 16384L * 16384;

    /// <summary>
    /// Largest input read into memory (every format except a seekable video stream, which is read in place). Default 1 GiB,
    /// at most 2 GiB.
    /// </summary>
    public long MaxInputBytes { get; init; } = 1L << 30;
}

/// <summary>
/// Options for <see cref="ThumbnailGenerator"/>. Immutable: derive variants with <c>with</c>.
/// <code>
/// var options = ThumbnailOptions.Default with { Sizes = [1024, 256], Format = ThumbnailFormat.WebP, Outputs = ThumbnailOutputs.Images };
/// </code>
/// </summary>
public sealed record ThumbnailOptions
{
    /// <summary>The default sizes: 2500, 1000, 500, 300 and 100 px.</summary>
    public static IReadOnlyList<int> DefaultSizes { get; } = [2500, 1000, 500, 300, 100];

    /// <summary>
    /// The default options: <see cref="DefaultSizes"/> plus the full-size image, as PNG, and the preview PDF.
    /// </summary>
    public static ThumbnailOptions Default { get; } = new();

    /// <summary>What to produce. Default: <see cref="ThumbnailOutputs.All"/>.</summary>
    public ThumbnailOutputs Outputs { get; init; } = ThumbnailOutputs.All;

    /// <summary>
    /// Longest side, in pixels, of each thumbnail (1 to 65500). An image or a video frame is never enlarged: a size above the
    /// original gives the original.
    /// </summary>
    public IReadOnlyList<int> Sizes { get; init; } = DefaultSizes;

    /// <summary>
    /// Also produce the full-size image: a page at <see cref="PageDpi"/> (by default 96 DPI for a document, 72 DPI for a PDF),
    /// an image or a video frame at its own size (within <see cref="ResourceLimits.MaxPixels"/>).
    /// </summary>
    public bool IncludeFullSize { get; init; } = true;

    /// <summary>
    /// Resolution of the full-size image of a page (documents, spreadsheets, presentations, XPS and PDF), 1 to 1200 DPI, or
    /// <c>null</c> for 96 DPI for a document and 72 DPI for a PDF. 300 gives a print-quality page: an A4 page is then
    /// 2480 x 3508 px. The page is drawn once at the larger of this and the largest size, and every size is cut from it.
    /// A page is still scaled down to fit <see cref="ResourceLimits.MaxPixels"/>.
    /// </summary>
    public double? PageDpi { get; init; }

    /// <summary>
    /// How each size fills its box. Default: <see cref="ThumbnailFit.Contain"/>. The full-size image is never cropped.
    /// </summary>
    public ThumbnailFit Fit { get; init; } = ThumbnailFit.Contain;

    /// <summary>
    /// Width divided by height of the box for <see cref="ThumbnailFit.Cover"/>, 0.01 to 100: 1 is square, 16.0 / 9 is wide.
    /// The longer side of the box is the size. Default 1.
    /// </summary>
    public double AspectRatio { get; init; } = 1;

    /// <summary>
    /// Lay out, write to the preview PDF and read the text of at most this many pages, or <c>null</c> for no limit beyond the
    /// built-in ones. A thumbnail only needs page 1, so <c>1</c> makes long documents much faster when no preview PDF is needed.
    /// <see cref="ThumbnailResult.HasMorePages"/> tells whether pages were left out.
    /// </summary>
    public int? MaxPages { get; init; }

    /// <summary>Encoding of the images. Default: <see cref="ThumbnailFormat.Png"/>.</summary>
    public ThumbnailFormat Format { get; init; } = ThumbnailFormat.Png;

    /// <summary>WebP and JPEG quality, 1 to 100. Default 80.</summary>
    public int Quality { get; init; } = 80;

    /// <summary>
    /// zlib level for PNG, 0 to 9. Default 3: about 25% faster than level 6 for files about 6% larger.
    /// </summary>
    public int PngCompressionLevel { get; init; } = 3;

    /// <summary>
    /// Encode the sizes of one input at the same time (the encoders are single-threaded): about a quarter less time per input
    /// for some more CPU. A service that already processes many inputs at once can turn it off.
    /// </summary>
    public bool EncodeInParallel { get; init; } = true;

    /// <summary>How a frame is taken from a video.</summary>
    public VideoOptions Video { get; init; } = VideoOptions.Default;

    /// <summary>Limits applied to untrusted input.</summary>
    public ResourceLimits Limits { get; init; } = ResourceLimits.Default;

    /// <summary>The usual file extension of <see cref="Format"/>, without the dot: "png", "webp" or "jpg".</summary>
    public string FileExtension => Format.FileExtension();

    private const ThumbnailOutputs AllOutputs = ThumbnailOutputs.All | ThumbnailOutputs.Text | ThumbnailOutputs.Placeholder;

    /// <summary>Throws <see cref="ArgumentException"/> if a value is out of range.</summary>
    public void Validate()
    {
        if (Sizes is null) throw new ArgumentException("Sizes must not be null.", nameof(Sizes));
        if (Video is null) throw new ArgumentException("Video must not be null.", nameof(Video));
        if (Limits is null) throw new ArgumentException("Limits must not be null.", nameof(Limits));
        if (Sizes.Any(size => size is < 1 or > 65500)) throw new ArgumentException("Every size must be between 1 and 65500 px.", nameof(Sizes));
        if ((Outputs & AllOutputs) == 0) throw new ArgumentException("Nothing to produce.", nameof(Outputs));
        if (!Enum.IsDefined(Fit)) throw new ArgumentException("Unknown fit.", nameof(Fit));
        if (!(AspectRatio is >= 0.01 and <= 100)) throw new ArgumentException("AspectRatio must be between 0.01 and 100.", nameof(AspectRatio));
        if (PageDpi is { } dpi && !(dpi is >= 1 and <= 1200)) throw new ArgumentException("PageDpi must be between 1 and 1200.", nameof(PageDpi));
        if (MaxPages < 1) throw new ArgumentException("MaxPages must be at least 1.", nameof(MaxPages));
        if ((Outputs & ThumbnailOutputs.Images) != 0 && Sizes.Count == 0 && !IncludeFullSize)
            throw new ArgumentException("No image requested: give a size or set IncludeFullSize.", nameof(Sizes));
        if (!Enum.IsDefined(Format)) throw new ArgumentException("Unknown format.", nameof(Format));
        if (Quality is < 1 or > 100) throw new ArgumentException("Quality must be between 1 and 100.", nameof(Quality));
        if (PngCompressionLevel is < 0 or > 9) throw new ArgumentException("PNG compression level must be between 0 and 9.", nameof(PngCompressionLevel));
        if (!(Video.Position is >= 0 and <= 1)) throw new ArgumentException("Video position must be between 0 and 1.", nameof(Video));
        // zero or negative would mean no deadline at all in the native decoder
        if (Video.Timeout <= TimeSpan.Zero || Video.Timeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentException("Video timeout must be positive (at most about 24 days).", nameof(Video));
        if (Limits.MaxPixels < 1) throw new ArgumentException("MaxPixels must be positive.", nameof(Limits));
        if (Limits.MaxInputBytes is < 1 or > int.MaxValue) throw new ArgumentException("MaxInputBytes must be between 1 byte and 2 GiB.", nameof(Limits));
    }
}

internal static class ThumbnailFormatExtensions
{
    public static string FileExtension(this ThumbnailFormat format) =>
        format switch { ThumbnailFormat.WebP => "webp", ThumbnailFormat.Jpeg => "jpg", _ => "png" };
}
