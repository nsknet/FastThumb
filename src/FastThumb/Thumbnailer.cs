using System.Diagnostics;
using FastThumb.Cells;
using FastThumb.Layout;
using FastThumb.Scene;
using FastThumb.Slides;
using SkiaSharp;

namespace FastThumb;

/// <summary>
/// The single entry point: bytes, a stream or a file in; thumbnails (+ preview.pdf) out. Every format goes through here:
///   detect → PDF: PDFium page 1 → image: libvips directly → video: one frame (thumbvideo), then as an image
///   → anything else: layout to pages (flow documents, slides or sheets) → page 1 bitmap + PDF of all pages
///   → libvips image set.
/// Adding a format means adding a reader and one line in FormatDetector; nothing here changes.
/// Thread-safe: independent files can be processed in parallel.
/// </summary>
internal static class Thumbnailer
{
    /// <summary>
    /// libvips keeps the last 100 operations and their pixels for reuse. Every file here is different, so the cache
    /// never hits and only holds old images in memory: it is turned off (process-wide, for this libvips).
    /// </summary>
    static Thumbnailer()
    {
        NetVips.Cache.Max = 0;
        // loaders libvips itself marks as unfit for untrusted input are refused (defence in depth: of those the current build
        // has only SVG). SVG is wanted and stays: librsvg reads a buffer with no base URI, so it cannot reach other files,
        // and its declared size goes through the MaxPixels check like any image.
        NetVips.NetVips.BlockUntrusted = true;
        NetVips.Operation.Block("VipsForeignLoadSvg", false);
    }

    /// <summary>Thumbnails for an input held in memory. The caller has validated <paramref name="options"/>.</summary>
    public static ThumbnailResult Create(byte[] data, string? extension, ThumbnailOptions options)
    {
        try { return CreateFromMemory(data, extension, options); }
        // the input is in memory by now, so an I/O error can only come from parsing it (a ZIP directory or a record that
        // runs past the end): that is a damaged file, not a failing device
        catch (IOException e)
        {
            throw new InvalidDocumentException($"The file is damaged ({e.Message})", e);
        }
    }

    private static ThumbnailResult CreateFromMemory(byte[] data, string? extension, ThumbnailOptions options)
    {
        var format = FormatDetector.Detect(data, extension);
        if (format == DocumentFormat.Video)
        {
            using var video = new MemoryStream(data, false);
            return CreateVideo(video, options);
        }

        var clock = Stopwatch.StartNew();
        var wantImages = (options.Outputs & ThumbnailOutputs.Images) != 0;
        var wantPdf = (options.Outputs & ThumbnailOutputs.PreviewPdf) != 0;
        var images = new List<Thumbnail>();
        if (format == DocumentFormat.Image)
        {
            if (!wantImages) return new ThumbnailResult(format, 1, images, null, default);
            using (var original = Decode(data, options.Limits.MaxPixels)) EncodeImage(original, images, options);
            return new ThumbnailResult(format, 1, images, null, new(TimeSpan.Zero, TimeSpan.Zero, clock.Elapsed));
        }

        if (format == DocumentFormat.Pdf)
        {
            if (!wantImages) return new ThumbnailResult(format, Pdfium.PageCount(data), images, null, default);
            // with no size, page 1 is rendered at its own size (72 DPI), which is the full size
            using var page = Pdfium.RenderPage(data, 0, options.Sizes.Count > 0 ? options.Sizes.Max() : 0, options.Limits.MaxPixels,
                out var width, out var height, out var count);
            var render = Lap(clock);
            (width, height) = Fit(width, height, options.Limits.MaxPixels);
            EncodeBitmap(page, width, height, images, options);
            return new ThumbnailResult(format, count, images, null, new(render, TimeSpan.Zero, Lap(clock)));
        }

        try { return CreateDocument(data, format, options, images, clock, wantImages, wantPdf); }
        // a damaged document surfaces from the readers as whatever the broken field tripped over (an index, a number, XML
        // syntax); callers get one type for "bad file", and ArgumentException keeps meaning "bad options"
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or System.Xml.XmlException or FormatException
            or OverflowException or KeyNotFoundException or InvalidCastException or NullReferenceException or DivideByZeroException)
        {
            throw new InvalidDocumentException($"The document is damaged ({e.GetType().Name}: {e.Message})", e);
        }
    }

    private static ThumbnailResult CreateDocument(byte[] data, DocumentFormat format, ThumbnailOptions options, List<Thumbnail> images,
        Stopwatch clock, bool wantImages, bool wantPdf)
    {
        using var document = Layout(data, format);
        var first = document.Pages[0];
        TimeSpan render, pdfTime = TimeSpan.Zero, encode = TimeSpan.Zero;
        SKBitmap? master = null;
        try
        {
            if (wantImages)
            {
                // the full size is the page at 96 DPI, the usual screen resolution for office documents
                // (page sizes come from the file: within MaxPixels, and the master too)
                var maxPixels = options.Limits.MaxPixels;
                var (fullWidth, fullHeight) = Fit(first.Width * 96.0 / 72, first.Height * 96.0 / 72, maxPixels);
                var longSide = options.Sizes.Count > 0 ? options.Sizes.Max() : Math.Max(fullWidth, fullHeight);
                var (masterWidth, masterHeight) = Fit((double)first.Width * longSide / Math.Max(first.Width, first.Height),
                    (double)first.Height * longSide / Math.Max(first.Width, first.Height), maxPixels);
                master = first.ToBitmap(Math.Max(masterWidth, masterHeight));
                render = Lap(clock);
                EncodeBitmap(master, fullWidth, fullHeight, images, options);
                encode = Lap(clock);
            }
            else render = Lap(clock);
            byte[]? pdf = null;
            if (wantPdf)
            {
                Guard.Check();
                pdf = document.ToPdf();
                pdfTime = Lap(clock);
            }
            return new ThumbnailResult(format, document.Pages.Count, images, pdf, new(render, pdfTime, encode));
        }
        finally { master?.Dispose(); }
    }

    /// <summary>
    /// Same as the byte[] overload, from a stream. A seekable video is read in place (only its header and the frame that is
    /// taken); every other format is read whole first. The stream is read from its current position and is not disposed.
    /// </summary>
    public static ThumbnailResult Create(Stream stream, string? extension, ThumbnailOptions options)
    {
        if (stream.CanSeek)
        {
            var start = stream.Position;
            Span<byte> head = stackalloc byte[512];
            var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            stream.Position = start;
            if (FormatDetector.IsVideo(head[..read], extension))
                return start == 0 ? CreateVideo(stream, options) : CreateVideo(new SubStream(stream, start), options);
            // known length: one exact buffer instead of a growing MemoryStream and a copy of it
            var length = stream.Length - start;
            if (length > options.Limits.MaxInputBytes) throw TooLarge(options);
            var data = GC.AllocateUninitializedArray<byte>((int)length);
            stream.ReadExactly(data);
            return Create(data, extension, options);
        }
        return Create(ReadAll(stream, options), extension, options);
    }

    /// <summary>The rest of a non-seekable stream, within <see cref="ResourceLimits.MaxInputBytes"/>.</summary>
    public static byte[] ReadAll(Stream stream, ThumbnailOptions options)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        for (int n; (n = stream.Read(chunk)) > 0;)
        {
            if (buffer.Length + n > options.Limits.MaxInputBytes) throw TooLarge(options);
            buffer.Write(chunk, 0, n);
            Guard.Check();
        }
        return buffer.ToArray();
    }

    public static ResourceLimitExceededException TooLarge(ThumbnailOptions options) =>
        new($"The input is larger than {options.Limits.MaxInputBytes:N0} bytes (ResourceLimits.MaxInputBytes).");

    /// <summary>Video: one frame (the middle by default), then the same size set as an image file.</summary>
    private static ThumbnailResult CreateVideo(Stream stream, ThumbnailOptions options)
    {
        var images = new List<Thumbnail>();
        if ((options.Outputs & ThumbnailOutputs.Images) == 0) return new ThumbnailResult(DocumentFormat.Video, 1, images, null, default);
        var clock = Stopwatch.StartNew();
        using var frame = Video.VideoFrame.Grab(stream, out _, options.Video.Position, (int)Math.Min(int.MaxValue, options.Video.Timeout.TotalMilliseconds));
        var grab = Lap(clock);
        EncodeImage(frame, images, options);
        return new ThumbnailResult(DocumentFormat.Video, 1, images, null, new(grab, TimeSpan.Zero, Lap(clock)));
    }

    /// <summary>Lays a document out into pages: flow documents are paginated, slides and sheets have their own renderers.</summary>
    public static PagedDocument Layout(byte[] data, DocumentFormat format)
    {
        if (format == DocumentFormat.Xps) return Fixed.XpsReader.Read(data);
        if (FormatDetector.IsSlides(format)) return SlideRenderer.Render(FormatDetector.OpenDeck(data, format));
        if (FormatDetector.IsSheets(format)) return SheetRenderer.Render(FormatDetector.OpenSheets(data, format));
        var reader = FormatDetector.Open(data, format);
        try { return Paginator.Build(reader); }
        finally { (reader as IDisposable)?.Dispose(); }
    }

    private static TimeSpan Lap(Stopwatch clock)
    {
        var elapsed = clock.Elapsed;
        clock.Restart();
        return elapsed;
    }

    /// <summary>
    /// Width and height scaled down (aspect kept) until they hold at most <paramref name="maxPixels"/>, each at least 1.
    /// Sizes here come from the file (page size, image header), so they are never trusted as they are.
    /// </summary>
    private static (int Width, int Height) Fit(double width, double height, long maxPixels)
    {
        if (!(width > 0 && height > 0)) throw new InvalidDocumentException("The page or image has no size.");
        if (width * height > maxPixels)
        {
            var scale = Math.Sqrt(maxPixels / (width * height));
            width *= scale;
            height *= scale;
        }
        return ((int)Math.Clamp(Math.Round(width), 1, int.MaxValue), (int)Math.Clamp(Math.Round(height), 1, int.MaxValue));
    }

    private static void CheckPixels(int width, int height, long maxPixels)
    {
        if ((long)width * height > maxPixels)
            throw new ResourceLimitExceededException($"The image is {width} x {height} px, more than {maxPixels:N0} pixels (ResourceLimits.MaxPixels).");
    }

    /// <summary>
    /// libvips first; Skia covers the few formats this libvips build has no loader for (BMP, ICO). Both read the header before
    /// any pixel, so a picture declaring more than <paramref name="maxPixels"/> is refused before it is decoded.
    /// </summary>
    private static NetVips.Image Decode(byte[] data, long maxPixels)
    {
        NetVips.Image? image = null;
        try { image = NetVips.Image.NewFromBuffer(data); }        // lazy: only the header so far
        catch (NetVips.VipsException e)
        {
            using var codec = SKCodec.Create(SKData.CreateCopy(data))
                ?? throw new UnsupportedFormatException($"Image cannot be decoded: {e.Message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault()}", e);
            CheckPixels(codec.Info.Width, codec.Info.Height, maxPixels);
            using var decoded = SKBitmap.Decode(codec, new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul))
                ?? throw new UnsupportedFormatException("Image cannot be decoded.", e);
            return NetVips.Image.NewFromMemoryCopy(decoded.GetPixels(), (ulong)decoded.ByteCount, decoded.Width, decoded.Height, 4, NetVips.Enums.BandFormat.Uchar)
                .Copy(interpretation: NetVips.Enums.Interpretation.Srgb);
        }
        try { CheckPixels(image.Width, image.Height, maxPixels); }
        catch { image.Dispose(); throw; }
        return image;
    }

    private static Encoded Save(NetVips.Image image, ThumbnailOptions options)
    {
        Guard.Check();
        var side = Math.Max(image.Width, image.Height);
        if (options.Format == ThumbnailFormat.WebP && side <= 16383)
            return new(image.WebpsaveBuffer(q: options.Quality, effort: 2), image.Width, image.Height, ThumbnailFormat.WebP);
        if (options.Format == ThumbnailFormat.Jpeg && side <= 65500)
        {
            if (!image.HasAlpha()) return new(image.JpegsaveBuffer(q: options.Quality), image.Width, image.Height, ThumbnailFormat.Jpeg);
            using var flat = image.Flatten(background: [255]);
            return new(flat.JpegsaveBuffer(q: options.Quality), image.Width, image.Height, ThumbnailFormat.Jpeg);
        }
        return new(image.PngsaveBuffer(compression: options.PngCompressionLevel), image.Width, image.Height, ThumbnailFormat.Png);
    }

    private readonly record struct Encoded(byte[] Data, int Width, int Height, ThumbnailFormat Format);

    /// <summary>
    /// Encodes each distinct output once and gives it every name that asked for it. The jobs run in parallel unless
    /// turned off: PNG/WebP/JPEG encoders are single-threaded, so the large and the small sizes overlap instead of queueing.
    /// </summary>
    private static void Encode(List<(int? Size, object Key)> wanted, Func<object, Encoded> encode, List<Thumbnail> images, bool parallel)
    {
        var keys = wanted.Select(w => w.Key).Distinct().ToArray();
        var results = new Encoded[keys.Length];
        if (keys.Length == 1 || !parallel) for (var i = 0; i < keys.Length; i++) results[i] = encode(keys[i]);
        else
        {
            // the worker threads get the caller's cancellation, which Guard keeps per thread
            var cancellation = Guard.Cancellation;
            Parallel.For(0, keys.Length, new ParallelOptions { CancellationToken = cancellation }, i =>
            {
                var previous = Guard.Cancellation;
                Guard.Cancellation = cancellation;
                try { results[i] = encode(keys[i]); }
                finally { Guard.Cancellation = previous; }
            });
        }
        foreach (var (size, key) in wanted)
        {
            var result = results[Array.IndexOf(keys, key)];
            images.Add(new Thumbnail(size, result.Width, result.Height, result.Format, result.Data));
        }
    }

    /// <summary>Thumbnail set from a rendered page: the master itself, the "full" size and the smaller boxes.</summary>
    private static void EncodeBitmap(SKBitmap master, int fullWidth, int fullHeight, List<Thumbnail> images, ThumbnailOptions options)
    {
        // wrap the BGRA pixels without copying, reorder to RGB (alpha is always opaque here), keep them in memory once
        using var bgra = NetVips.Image.NewFromMemory(master.GetPixels(), (ulong)master.ByteCount, master.Width, master.Height, 4, NetVips.Enums.BandFormat.Uchar);
        using var rgb = bgra[2].Bandjoin(bgra[1], bgra[0]).Copy(interpretation: NetVips.Enums.Interpretation.Srgb);
        using var cached = rgb.CopyMemory();

        var wanted = new List<(int?, object)>();
        if (options.IncludeFullSize) wanted.Add((null, (Math.Max(1, fullWidth), Math.Max(1, fullHeight))));
        foreach (var box in options.Sizes)
        {
            var ratio = (double)box / Math.Max(master.Width, master.Height);
            wanted.Add((box, (Math.Max(1, (int)Math.Round(master.Width * ratio)), Math.Max(1, (int)Math.Round(master.Height * ratio)))));
        }
        Encode(wanted, key =>
        {
            var (width, height) = ((int, int))key;
            if (width == cached.Width && height == cached.Height) return Save(cached, options);
            using var resized = cached.ThumbnailImage(width, height: height, size: NetVips.Enums.Size.Force);
            return Save(resized, options);
        }, images, options.EncodeInParallel);
    }

    /// <summary>
    /// Image files (and video frames) never touch the document renderer: libvips decodes the file once, applies EXIF orientation,
    /// and each size is cut from that copy ("full" needs every pixel anyway). Nothing is enlarged beyond the original.
    /// </summary>
    private static void EncodeImage(NetVips.Image original, List<Thumbnail> images, ThumbnailOptions options)
    {
        CheckPixels(original.Width, original.Height, options.Limits.MaxPixels);   // video frames come here straight from the decoder
        using var upright = original.Autorot();
        using var cached = upright.CopyMemory();
        var side = Math.Max(cached.Width, cached.Height);
        var wanted = new List<(int?, object)>();
        if (options.IncludeFullSize) wanted.Add((null, 0));
        foreach (var box in options.Sizes) wanted.Add((box, side <= box ? 0 : box));   // 0 = the original
        Encode(wanted, key =>
        {
            var box = (int)key;
            if (box == 0) return Save(cached, options);
            using var thumbnail = cached.ThumbnailImage(box, height: box, size: NetVips.Enums.Size.Down);
            return Save(thumbnail, options);
        }, images, options.EncodeInParallel);
    }

    /// <summary>A read-only view of a seekable stream from a given offset on, so a video can start mid-stream.</summary>
    private sealed class SubStream(Stream inner, long start) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => inner.Length - start;
        public override long Position { get => inner.Position - start; set => inner.Position = start + value; }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override long Seek(long offset, SeekOrigin origin) => origin switch
        {
            SeekOrigin.Begin => inner.Seek(start + offset, SeekOrigin.Begin) - start,
            SeekOrigin.Current => inner.Seek(offset, SeekOrigin.Current) - start,
            _ => inner.Seek(offset, SeekOrigin.End) - start,
        };
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
