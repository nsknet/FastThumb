namespace FastThumb;

/// <summary>
/// Facts about an input, read without laying it out or rendering it (<see cref="ThumbnailGenerator.Inspect(ReadOnlyMemory{byte}, string?, CancellationToken)"/>).
/// Every property except <see cref="Format"/> is <c>null</c> when the file does not say or the format does not have it.
/// </summary>
public sealed class DocumentInfo
{
    internal DocumentInfo(DocumentFormat format) => Format = format;

    /// <summary>The format recognised from the content.</summary>
    public DocumentFormat Format { get; }

    /// <summary>
    /// Number of pages: counted for a PDF, 1 for an image or a video, and the count the authoring application saved for a
    /// DOCX (pages), a PPTX (slides) or an ODT (pages). A saved count can be out of date and differ from
    /// <see cref="ThumbnailResult.PageCount"/>, which counts the pages laid out. <c>null</c> for other formats.
    /// </summary>
    public int? PageCount { get; internal set; }

    /// <summary>Width in pixels of an image or a video frame (as displayed, after rotation), or of PDF page 1 at 72 DPI.</summary>
    public int? Width { get; internal set; }

    /// <summary>Height in pixels of an image or a video frame (as displayed, after rotation), or of PDF page 1 at 72 DPI.</summary>
    public int? Height { get; internal set; }

    /// <summary>The document title from its properties.</summary>
    public string? Title { get; internal set; }

    /// <summary>The author (creator) from the document properties.</summary>
    public string? Author { get; internal set; }

    /// <summary>The subject from the document properties.</summary>
    public string? Subject { get; internal set; }

    /// <summary>The keywords from the document properties, as written.</summary>
    public string? Keywords { get; internal set; }

    /// <summary>When the document says it was created.</summary>
    public DateTimeOffset? Created { get; internal set; }

    /// <summary>When the document says it was last modified.</summary>
    public DateTimeOffset? Modified { get; internal set; }

    /// <summary>Duration of a video, when the container tells.</summary>
    public TimeSpan? Duration { get; internal set; }

    /// <summary>Video codec name (for example "h264", "hevc", "av1").</summary>
    public string? VideoCodec { get; internal set; }

    /// <inheritdoc />
    public override string ToString() =>
        $"{Format}{(PageCount is { } p ? $", {p} page(s)" : "")}{(Width is { } w ? $", {w} x {Height} px" : "")}{(Title is { } t ? $", \"{t}\"" : "")}";
}
