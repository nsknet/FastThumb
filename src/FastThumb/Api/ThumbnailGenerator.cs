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

    /// <summary>
    /// Format, page count, size and document properties of an input held in memory, without laying it out or rendering it.
    /// A video is opened and its first frame decoded, to learn its displayed size.
    /// </summary>
    /// <param name="data">The whole file.</param>
    /// <param name="fileName">The file name or extension, if known. Only used to tell plain-text formats apart.</param>
    /// <param name="cancellationToken">Stops the work between reads.</param>
    DocumentInfo Inspect(ReadOnlyMemory<byte> data, string? fileName = null, CancellationToken cancellationToken = default);

    /// <summary>Same as <see cref="Inspect(ReadOnlyMemory{byte}, string?, CancellationToken)"/>, from a stream (a seekable video is read in place).</summary>
    DocumentInfo Inspect(Stream stream, string? fileName = null, CancellationToken cancellationToken = default);

    /// <summary>Same as <see cref="Inspect(ReadOnlyMemory{byte}, string?, CancellationToken)"/>, for a file on disk.</summary>
    DocumentInfo InspectFile(string path, CancellationToken cancellationToken = default);
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
    /// <summary>
    /// Name of the <see cref="System.Diagnostics.ActivitySource"/> and the <see cref="System.Diagnostics.Metrics.Meter"/> this
    /// library reports to: "FastThumb". With OpenTelemetry, <c>.AddSource(ThumbnailGenerator.TelemetryName)</c> and
    /// <c>.AddMeter(ThumbnailGenerator.TelemetryName)</c>.
    /// </summary>
    public const string TelemetryName = "FastThumb";

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

    /// <summary>
    /// Applies now the process-wide libvips settings that are otherwise applied by the first call that needs them: the
    /// operation cache is turned off, and loaders libvips marks as unfit for untrusted input are refused (see docs/security.md).
    /// Call it at startup when the process also uses libvips itself, so that libvips behaves the same from the start instead
    /// of changing at the first thumbnail. Only libvips is loaded: PDFium, Skia and the video decoder still load when a file
    /// needs them. Calling it again does nothing.
    /// </summary>
    public static void ApplyProcessSettings() => Thumbnailer.ApplyProcessSettings();

    /// <summary>Recognises the format of a file from its content.</summary>
    /// <param name="data">The whole file (ZIP-based formats keep their directory at the end).</param>
    /// <param name="fileName">The file name or extension, if known; only breaks ties between plain-text formats.</param>
    public static DocumentFormat DetectFormat(ReadOnlyMemory<byte> data, string? fileName = null)
    {
        try { return FormatDetector.Detect(AsArray(data), Extension(fileName)); }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException or IOException || Guard.IsDamagedZipName(e)) { return DocumentFormat.Unknown; }
    }

    /// <inheritdoc />
    public ThumbnailResult Generate(ReadOnlyMemory<byte> data, string? fileName = null, CancellationToken cancellationToken = default)
    {
        if (data.Length > Options.Limits.MaxInputBytes) throw Thumbnailer.TooLarge(Options);
        var array = AsArray(data);
        return Run("generate", array.Length, () => Thumbnailer.Create(array, Extension(fileName), Options), cancellationToken);
    }

    /// <inheritdoc />
    public ThumbnailResult Generate(Stream stream, string? fileName = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return Run("generate", null, () => Thumbnailer.Create(stream, Extension(fileName), Options), cancellationToken);
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
        // one array of the exact length, which Generate uses as it is: the input is held once while it is processed
        var data = await Thumbnailer.ReadAllAsync(stream, Options, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => Generate(data, fileName, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public DocumentInfo Inspect(ReadOnlyMemory<byte> data, string? fileName = null, CancellationToken cancellationToken = default)
    {
        if (data.Length > Options.Limits.MaxInputBytes) throw Thumbnailer.TooLarge(Options);
        var array = AsArray(data);
        return Run("inspect", array.Length, () => Inspector.Inspect(array, Extension(fileName), Options), cancellationToken);
    }

    /// <inheritdoc />
    public DocumentInfo Inspect(Stream stream, string? fileName = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return Run("inspect", null, () => Inspector.Inspect(stream, Extension(fileName), Options), cancellationToken);
    }

    /// <inheritdoc />
    public DocumentInfo InspectFile(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, FileOptions.SequentialScan);
        return Inspect(file, Path.GetFileName(path), cancellationToken);
    }

    /// <summary>The readers index arrays: the caller's array is used as it is when the memory is a whole one.</summary>
    private static byte[] AsArray(ReadOnlyMemory<byte> data) =>
        System.Runtime.InteropServices.MemoryMarshal.TryGetArray(data, out var segment)
            && segment.Offset == 0 && segment.Count == segment.Array!.Length ? segment.Array : data.ToArray();

    private static string? Extension(string? fileName) =>
        string.IsNullOrEmpty(fileName) ? null : fileName.StartsWith('.') ? fileName : Path.GetExtension(fileName);

    /// <summary>
    /// Runs one call with its cancellation in scope, the public exception types at the boundary, and a trace and metrics.
    /// </summary>
    private static T Run<T>(string operation, long? inputBytes, Func<T> work, CancellationToken cancellationToken) where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        using var activity = Telemetry.Start(operation, inputBytes);
        var previous = Guard.Cancellation;
        Guard.Cancellation = cancellationToken;
        try
        {
            var result = work();
            Telemetry.Succeeded(activity, operation, started, result);
            return result;
        }
        catch (Exception e)
        {
            var thrown = e is OperationCanceledException && cancellationToken.IsCancellationRequested ? e : Translate(e) ?? e;
            Telemetry.Failed(activity, operation, started, thrown);
            if (ReferenceEquals(thrown, e)) throw;
            throw thrown;
        }
        finally { Guard.Cancellation = previous; }
    }

    /// <summary>Internal errors as the documented public types; null keeps the exception as it is.</summary>
    private static Exception? Translate(Exception e) => e switch
    {
        ThumbnailException => null,
        InsufficientExecutionStackException => new ResourceLimitExceededException("The document is nested too deeply.", e),
        NotSupportedException => new UnsupportedFormatException(e.Message, e),
        InvalidDataException => new InvalidDocumentException(e.Message, e),
        ArgumentException when Guard.IsDamagedZipName(e) => new InvalidDocumentException("The package is damaged: an entry name is not valid.", e),
        // libvips decodes lazily: damage past the header only shows when the pixels are read
        NetVips.VipsException => new InvalidDocumentException($"The image is damaged ({LastLine(e.Message)})", e),
        _ => null,
    };

    /// <summary>libvips reports a chain of errors, one per line, the cause last.</summary>
    private static string LastLine(string message) => LastErrorLine(message) ?? message;

    /// <summary>The last line of <paramref name="message"/> that is not blank, trimmed; null when there is none.</summary>
    internal static string? LastErrorLine(string message) =>
        message.Split('\n').Select(line => line.Trim()).LastOrDefault(line => line.Length > 0);
}
