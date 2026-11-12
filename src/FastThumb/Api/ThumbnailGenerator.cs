namespace FastThumb;

/// <summary>Creates thumbnails and preview PDFs. Implemented by <see cref="ThumbnailGenerator"/>; use the interface to inject or mock it.</summary>
public interface IThumbnailGenerator
{
    /// <summary>The options every call uses.</summary>
    ThumbnailOptions Options { get; }

    /// <summary>Thumbnails for an input held in memory.</summary>
    /// <param name="data">The whole file.</param>
    /// <param name="fileName">The file name or extension, if known. Only used to tell plain-text formats apart (CSV, Markdown, ...).</param>
    /// <param name="cancellationToken">Stops the work between pages, images and reads.</param>
    ThumbnailResult Generate(ReadOnlyMemory<byte> data, string? fileName = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Thumbnails for an input read from a stream, from its current position. A seekable video stream is read in place (only
    /// the container header and the frame that is taken); any other input is read into memory first. The stream is not disposed.
    /// </summary>
    ThumbnailResult Generate(Stream stream, string? fileName = null, CancellationToken cancellationToken = default);

    /// <summary>Thumbnails for a file on disk.</summary>
    ThumbnailResult GenerateFromFile(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Same as <see cref="Generate(Stream, string?, CancellationToken)"/>, for a stream that should be read asynchronously
    /// (network, cloud storage). The stream is read asynchronously; decoding and rendering then run on the thread pool.
    /// </summary>
    Task<ThumbnailResult> GenerateAsync(Stream stream, string? fileName = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Creates thumbnails and preview PDFs for documents, spreadsheets, presentations, PDF, images and video.
/// <para>
/// Thread-safe: one instance can serve any number of concurrent calls. Every input is treated as untrusted
/// (see <see cref="ResourceLimits"/>).
/// </para>
/// <code>
/// var generator = new ThumbnailGenerator(ThumbnailOptions.Default with { Sizes = [512], Format = ThumbnailFormat.WebP });
/// var result = generator.GenerateFromFile("report.docx");
/// File.WriteAllBytes("report.webp", result.Images[0].Data);
/// </code>
/// </summary>
/// <remarks>
/// Errors: <see cref="UnsupportedFormatException"/> (format or codec not supported), <see cref="InvalidDocumentException"/>
/// (damaged input), <see cref="ResourceLimitExceededException"/> (a limit was exceeded), <see cref="TimeoutException"/>
/// (<see cref="VideoOptions.Timeout"/>), <see cref="OperationCanceledException"/>, and <see cref="IOException"/> from the stream.
/// </remarks>
public sealed class ThumbnailGenerator : IThumbnailGenerator
{
    /// <summary>A generator with <see cref="ThumbnailOptions.Default"/>.</summary>
    public static ThumbnailGenerator Default { get; } = new();

    /// <summary>Creates a generator.</summary>
    /// <param name="options">The options; <see cref="ThumbnailOptions.Default"/> if <c>null</c>.</param>
    /// <exception cref="ArgumentException">An option is out of range.</exception>
    public ThumbnailGenerator(ThumbnailOptions? options = null)
    {
        options ??= ThumbnailOptions.Default;
        options.Validate();
        Options = options;
    }

    /// <inheritdoc />
    public ThumbnailOptions Options { get; }

    /// <summary>The file extensions this library reads, lower case with the dot (".docx", ".pdf", ".mp4", ...).</summary>
    public static IReadOnlyCollection<string> SupportedExtensions => FormatDetector.Extensions;

    /// <summary>Recognises the format of a file from its content.</summary>
    /// <param name="data">The whole file (ZIP-based formats keep their directory at the end).</param>
    /// <param name="fileName">The file name or extension, if known; only breaks ties between plain-text formats.</param>
    public static DocumentFormat DetectFormat(ReadOnlyMemory<byte> data, string? fileName = null)
    {
        try { return FormatDetector.Detect(AsArray(data), Extension(fileName)); }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException or IOException) { return DocumentFormat.Unknown; }
    }

    /// <inheritdoc />
    public ThumbnailResult Generate(ReadOnlyMemory<byte> data, string? fileName = null, CancellationToken cancellationToken = default)
    {
        if (data.Length > Options.Limits.MaxInputBytes) throw Thumbnailer.TooLarge(Options);
        var array = AsArray(data);
        return Run(() => Thumbnailer.Create(array, Extension(fileName), Options), cancellationToken);
    }

    /// <inheritdoc />
    public ThumbnailResult Generate(Stream stream, string? fileName = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return Run(() => Thumbnailer.Create(stream, Extension(fileName), Options), cancellationToken);
    }

    /// <inheritdoc />
    public ThumbnailResult GenerateFromFile(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, FileOptions.SequentialScan);
        return Generate(file, Path.GetFileName(path), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ThumbnailResult> GenerateAsync(Stream stream, string? fileName = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        // a seekable stream is handed over as it is (a video is then read in place); any other one is buffered asynchronously
        if (stream.CanSeek) return await Task.Run(() => Generate(stream, fileName, cancellationToken), cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        for (int n; (n = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0;)
        {
            if (buffer.Length + n > Options.Limits.MaxInputBytes) throw Thumbnailer.TooLarge(Options);
            buffer.Write(chunk, 0, n);
        }
        var data = buffer.GetBuffer().AsMemory(0, (int)buffer.Length);
        return await Task.Run(() => Generate(data, fileName, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The readers index arrays: the caller's array is used as it is when the memory is a whole one.</summary>
    private static byte[] AsArray(ReadOnlyMemory<byte> data) =>
        System.Runtime.InteropServices.MemoryMarshal.TryGetArray(data, out var segment)
            && segment.Offset == 0 && segment.Count == segment.Array!.Length ? segment.Array : data.ToArray();

    private static string? Extension(string? fileName) =>
        string.IsNullOrEmpty(fileName) ? null : fileName.StartsWith('.') ? fileName : Path.GetExtension(fileName);

    /// <summary>Runs one call with its cancellation in scope and the public exception types at the boundary.</summary>
    private static ThumbnailResult Run(Func<ThumbnailResult> work, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var previous = Guard.Cancellation;
        Guard.Cancellation = cancellationToken;
        try { return work(); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception e) when (Translate(e) is { } translated) { throw translated; }
        finally { Guard.Cancellation = previous; }
    }

    /// <summary>Internal errors as the documented public types; null keeps the exception as it is.</summary>
    private static Exception? Translate(Exception e) => e switch
    {
        ThumbnailException => null,
        InsufficientExecutionStackException => new ResourceLimitExceededException("The document is nested too deeply.", e),
        NotSupportedException => new UnsupportedFormatException(e.Message, e),
        InvalidDataException => new InvalidDocumentException(e.Message, e),
        // libvips decodes lazily: damage past the header only shows when the pixels are read
        NetVips.VipsException => new InvalidDocumentException($"The image is damaged ({LastLine(e.Message)})", e),
        _ => null,
    };

    /// <summary>libvips reports a chain of errors, one per line, the cause last.</summary>
    private static string LastLine(string message) =>
        message.Split((char)10, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? message;
}
