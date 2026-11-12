namespace FastThumb;

/// <summary>Input format, recognised from the content (the file name only breaks ties between plain-text formats).</summary>
public enum DocumentFormat
{
    /// <summary>Not recognised.</summary>
    Unknown,
    /// <summary>PDF.</summary>
    Pdf,
    /// <summary>Word 97-2003 binary document (.doc, .dot).</summary>
    Doc,
    /// <summary>Office Open XML word-processing document (.docx, .docm, .dotx, .dotm).</summary>
    Docx,
    /// <summary>Office Open XML document as a single XML file (Flat OPC).</summary>
    FlatOpc,
    /// <summary>Word 2003 XML document.</summary>
    WordMl,
    /// <summary>OpenDocument text (.odt, .ott).</summary>
    Odt,
    /// <summary>EPUB e-book.</summary>
    Epub,
    /// <summary>Rich Text Format.</summary>
    Rtf,
    /// <summary>HTML.</summary>
    Html,
    /// <summary>MIME HTML web archive (.mht, .mhtml).</summary>
    Mhtml,
    /// <summary>Markdown.</summary>
    Markdown,
    /// <summary>Plain text.</summary>
    Text,
    /// <summary>Mobipocket / Kindle e-book (.mobi, .azw, .azw3).</summary>
    Mobi,
    /// <summary>Compiled HTML Help.</summary>
    Chm,
    /// <summary>Hangul Word Processor 5.x document (text only).</summary>
    Hwp,
    /// <summary>WordPerfect 6+ document (text only).</summary>
    WordPerfect,
    /// <summary>Microsoft Works word-processor document (text only).</summary>
    Works,
    /// <summary>Office Open XML presentation (.pptx, .pptm, .potx, .ppsx).</summary>
    Pptx,
    /// <summary>PowerPoint 97-2003 binary presentation (.ppt, .pps, .pot).</summary>
    Ppt,
    /// <summary>OpenDocument presentation (.odp).</summary>
    Odp,
    /// <summary>OpenDocument presentation as a single XML file (.fodp).</summary>
    Fodp,
    /// <summary>Office Open XML spreadsheet (.xlsx, .xlsm, .xltx).</summary>
    Xlsx,
    /// <summary>Excel 97-2003 binary workbook (BIFF8).</summary>
    Xls,
    /// <summary>Comma-separated values.</summary>
    Csv,
    /// <summary>OpenDocument spreadsheet (.ods).</summary>
    Ods,
    /// <summary>Excel binary workbook (.xlsb).</summary>
    Xlsb,
    /// <summary>XML Paper Specification (.xps, .oxps).</summary>
    Xps,
    /// <summary>Raster or vector image (JPEG, PNG, GIF, WebP, TIFF, HEIF/AVIF, SVG, BMP, ...).</summary>
    Image,
    /// <summary>Video (MP4/MOV, Matroska/WebM, AVI, MPEG-PS/TS, FLV, ASF/WMV, Ogg, ...).</summary>
    Video,
}

/// <summary>One encoded thumbnail.</summary>
public sealed class Thumbnail
{
    internal Thumbnail(int? maxDimension, int width, int height, ThumbnailFormat format, byte[] data)
    {
        MaxDimension = maxDimension;
        Width = width;
        Height = height;
        Format = format;
        Data = data;
    }

    /// <summary>The requested longest side (one of <see cref="ThumbnailOptions.Sizes"/>), or <c>null</c> for the full-size image.</summary>
    public int? MaxDimension { get; }

    /// <summary>Whether this is the full-size image (<see cref="ThumbnailOptions.IncludeFullSize"/>).</summary>
    public bool IsFullSize => MaxDimension is null;

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>
    /// The encoding actually written: <see cref="ThumbnailOptions.Format"/>, or PNG for an image too large for WebP or JPEG.
    /// </summary>
    public ThumbnailFormat Format { get; }

    /// <summary>The encoded image.</summary>
    public byte[] Data { get; }

    /// <summary>A short name for the size: the number of pixels, or "full".</summary>
    public string Name => MaxDimension?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "full";

    /// <inheritdoc />
    public override string ToString() => $"{Name}: {Width} x {Height} {Format}, {Data.Length:N0} bytes";
}

/// <summary>Time spent in each stage of one call.</summary>
/// <param name="Render">Reading, layout and rendering of the first page; for a video, decoding the frame.</param>
/// <param name="PreviewPdf">Writing the preview PDF.</param>
/// <param name="Encode">Resizing and encoding the thumbnails.</param>
public readonly record struct ThumbnailTimings(TimeSpan Render, TimeSpan PreviewPdf, TimeSpan Encode)
{
    /// <summary>The sum of the stages.</summary>
    public TimeSpan Total => Render + PreviewPdf + Encode;
}

/// <summary>The thumbnails, the preview PDF and facts about one input.</summary>
public sealed class ThumbnailResult
{
    internal ThumbnailResult(DocumentFormat format, int pageCount, IReadOnlyList<Thumbnail> images, byte[]? previewPdf, ThumbnailTimings timings)
    {
        Format = format;
        PageCount = pageCount;
        Images = images;
        PreviewPdf = previewPdf;
        Timings = timings;
    }

    /// <summary>The format recognised from the content.</summary>
    public DocumentFormat Format { get; }

    /// <summary>Number of pages; an image or a video counts as one.</summary>
    public int PageCount { get; }

    /// <summary>
    /// The thumbnails: the full-size image first (if requested), then one per <see cref="ThumbnailOptions.Sizes"/>, in that order.
    /// Empty when <see cref="ThumbnailOutputs.Images"/> was not requested.
    /// </summary>
    public IReadOnlyList<Thumbnail> Images { get; }

    /// <summary>A PDF of every page, or <c>null</c> (not requested, or the input is a PDF, an image or a video).</summary>
    public byte[]? PreviewPdf { get; }

    /// <summary>Time spent in each stage.</summary>
    public ThumbnailTimings Timings { get; }

    /// <summary>The full-size image, or <c>null</c> if it was not requested.</summary>
    public Thumbnail? FullSize => Images.FirstOrDefault(image => image.IsFullSize);

    /// <summary>The thumbnail made for a requested size, or <c>null</c>.</summary>
    public Thumbnail? Find(int maxDimension) => Images.FirstOrDefault(image => image.MaxDimension == maxDimension);
}

/// <summary>Base type of the errors this library reports about an input.</summary>
public abstract class ThumbnailException : Exception
{
    /// <summary>Creates the exception.</summary>
    protected ThumbnailException(string message, Exception? innerException) : base(message, innerException) { }
}

/// <summary>The input is not a format, or uses a feature or codec, that this library can read.</summary>
public class UnsupportedFormatException : ThumbnailException
{
    /// <summary>Creates the exception.</summary>
    public UnsupportedFormatException(string message, Exception? innerException = null) : base(message, innerException) { }
}

/// <summary>The input is damaged or inconsistent. <see cref="Exception.InnerException"/> holds the original error, if any.</summary>
public class InvalidDocumentException : ThumbnailException
{
    /// <summary>Creates the exception.</summary>
    public InvalidDocumentException(string message, Exception? innerException = null) : base(message, innerException) { }
}

/// <summary>
/// The input exceeds a limit: <see cref="ResourceLimits"/>, a decompressed part larger than 256 MB, or nesting deeper than the
/// readers accept. The input is treated as hostile and is not processed further.
/// </summary>
public class ResourceLimitExceededException : InvalidDocumentException
{
    /// <summary>Creates the exception.</summary>
    public ResourceLimitExceededException(string message, Exception? innerException = null) : base(message, innerException) { }
}
