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

    /// <summary>Runs the static constructor (once): the libvips settings above, without processing anything.</summary>
    public static void ApplyProcessSettings() { }

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
        var want = new Wanted(options);
        var images = new List<Thumbnail>();
        if (format == DocumentFormat.Image)
        {
            if (!want.Picture) return Finish(new ThumbnailResult(format, 1, images, null, default), want, null);
            ThumbnailPlaceholder? placeholder;
            using (var original = Decode(Images.ManagedImages.Inflated(data, options.Limits.MaxInputBytes), extension, new(options.Limits.MaxPixels, BigEnough(options, want))))
                placeholder = EncodeImage(original, images, options, want);
            return Finish(new ThumbnailResult(format, 1, images, null, new(TimeSpan.Zero, TimeSpan.Zero, clock.Elapsed)), want, placeholder);
        }

        if (format == DocumentFormat.Pdf)
        {
            List<string>? texts = null;
            var count = 0;
            if (want.Text) texts = Pdfium.PageTexts(data, options.MaxPages ?? DefaultMaxTextPages, out count);
            bool More() => (options.MaxPages is { } max && count > max) || (texts != null && count > texts.Count);
            if (!want.Picture)
            {
                if (texts == null) count = Pdfium.PageCount(data);
                return Finish(new ThumbnailResult(format, count, images, null, new(Lap(clock), TimeSpan.Zero, TimeSpan.Zero)), want, null, texts, More());
            }
            // an Illustrator file saved without PDF content: its page is only a notice, its XMP thumbnail is the picture
            if (Images.IllustratorPdf.Placeholder(data) is { } thumbnail)
            {
                if (texts == null) count = Pdfium.PageCount(data);
                ThumbnailPlaceholder? fromThumbnail;
                using (var picture = Images.ManagedImages.Embedded(thumbnail, new(options.Limits.MaxPixels, BigEnough(options, want))))
                    fromThumbnail = EncodeImage(picture, images, options, want);
                return Finish(new ThumbnailResult(format, count, images, null, new(Lap(clock), TimeSpan.Zero, Lap(clock))), want, fromThumbnail, texts, More());
            }
            // page 1 is drawn once, large enough for the largest size and for the full size (PageDpi, 72 DPI by default)
            var longSide = !want.Images ? PlaceholderSide
                : options.Fit == ThumbnailFit.Cover && options.Sizes.Count > 0 ? MasterSide(Pdfium.FirstPageSize(data), options)
                : options.Sizes.Count > 0 ? options.Sizes.Max() : 0;
            var fullDpi = want.Images && options.IncludeFullSize ? options.PageDpi ?? 72 : 0;
            using var bitmap = Pdfium.RenderPage(data, 0, longSide, fullDpi, options.Limits.MaxPixels, out var width, out var height, out count);
            var render = Lap(clock);
            (width, height) = Fit(width, height, options.Limits.MaxPixels);
            var placeholder = EncodeBitmap(bitmap, width, height, images, options, want, cropTop: true);
            return Finish(new ThumbnailResult(format, count, images, null, new(render, TimeSpan.Zero, Lap(clock))), want, placeholder, texts, More());
        }

        try { return CreateDocument(data, format, options, images, clock, want); }
        // a damaged document surfaces from the readers as whatever the broken field tripped over (an index, a number, XML
        // syntax); callers get one type for "bad file", and ArgumentException keeps meaning "bad options"
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or System.Xml.XmlException or FormatException
            or OverflowException or KeyNotFoundException or InvalidCastException or NullReferenceException or DivideByZeroException)
        {
            throw new InvalidDocumentException($"The document is damaged ({e.GetType().Name}: {e.Message})", e);
        }
    }

    /// <summary>Pages of a PDF read for text when <see cref="ThumbnailOptions.MaxPages"/> is not set (the layout limit of documents).</summary>
    private const int DefaultMaxTextPages = 3000;

    /// <summary>Long side of the picture a placeholder is made from: ThumbHash takes at most 100 x 100 px.</summary>
    private const int PlaceholderSide = 100;

    /// <summary>The requested outputs of one call.</summary>
    private readonly struct Wanted(ThumbnailOptions options)
    {
        public bool Images { get; } = (options.Outputs & ThumbnailOutputs.Images) != 0;
        public bool Pdf { get; } = (options.Outputs & ThumbnailOutputs.PreviewPdf) != 0;
        public bool Text { get; } = (options.Outputs & ThumbnailOutputs.Text) != 0;
        public bool Placeholder { get; } = (options.Outputs & ThumbnailOutputs.Placeholder) != 0;

        /// <summary>Page 1, the image or the frame has to be decoded or drawn.</summary>
        public bool Picture => Images || Placeholder;
    }

    private static ThumbnailResult Finish(ThumbnailResult result, Wanted want, ThumbnailPlaceholder? placeholder,
        IReadOnlyList<string>? texts = null, bool hasMorePages = false) =>
        result.With(want.Text ? texts ?? [] : null, placeholder, hasMorePages);

    private static ThumbnailResult CreateDocument(byte[] data, DocumentFormat format, ThumbnailOptions options, List<Thumbnail> images,
        Stopwatch clock, Wanted want)
    {
        using var document = Layout(data, format, options.MaxPages, out var more);
        var first = document.Pages[0];
        TimeSpan render, pdfTime = TimeSpan.Zero, encode = TimeSpan.Zero;
        SKBitmap? master = null;
        ThumbnailPlaceholder? placeholder = null;
        try
        {
            if (want.Picture)
            {
                // the full size is the page at PageDpi, by default 96 DPI, the usual screen resolution for office documents
                // (page sizes come from the file: within MaxPixels, and the master too). The master is drawn once, large
                // enough for the largest size and for the full size, so nothing is enlarged from it.
                var maxPixels = options.Limits.MaxPixels;
                var dpi = options.PageDpi ?? 96;
                var (fullWidth, fullHeight) = Fit(first.Width * dpi / 72, first.Height * dpi / 72, maxPixels);
                var longSide = !want.Images ? PlaceholderSide
                    : Math.Max(options.Sizes.Count > 0 ? MasterSide(first.Width, first.Height, options) : 0,
                        options.IncludeFullSize ? Math.Max(fullWidth, fullHeight) : 0);
                var (masterWidth, masterHeight) = Fit((double)first.Width * longSide / Math.Max(first.Width, first.Height),
                    (double)first.Height * longSide / Math.Max(first.Width, first.Height), maxPixels);
                master = first.ToBitmap(Math.Max(masterWidth, masterHeight));
                render = Lap(clock);
                placeholder = EncodeBitmap(master, fullWidth, fullHeight, images, options, want, cropTop: true);
                encode = Lap(clock);
            }
            else render = Lap(clock);
            byte[]? pdf = null;
            if (want.Pdf)
            {
                Guard.Check();
                pdf = document.ToPdf();
                pdfTime = Lap(clock);
            }
            List<string>? texts = null;
            if (want.Text)
            {
                texts = new List<string>(document.Pages.Count);
                foreach (var page in document.Pages) texts.Add(PageText.Read(page));
                render += Lap(clock);
            }
            return Finish(new ThumbnailResult(format, document.Pages.Count, images, pdf, new(render, pdfTime, encode)), want, placeholder, texts, more);
        }
        finally { master?.Dispose(); }
    }

    /// <summary>
    /// Long side of page 1 when it is drawn: enough for the largest size, and for <see cref="ThumbnailFit.Cover"/> enough that
    /// the page covers the whole box (a portrait page in a square box is drawn taller than the size).
    /// </summary>
    private static int MasterSide((double Width, double Height) page, ThumbnailOptions options) => MasterSide(page.Width, page.Height, options);

    private static int MasterSide(double pageWidth, double pageHeight, ThumbnailOptions options)
    {
        if (options.Sizes.Count == 0) return 0;
        if (options.Fit != ThumbnailFit.Cover) return options.Sizes.Max();
        double longest = 0;
        foreach (var size in options.Sizes)
        {
            var (boxWidth, boxHeight) = CoverBox(size, options.AspectRatio);
            var scale = Math.Max(boxWidth / pageWidth, boxHeight / pageHeight);
            longest = Math.Max(longest, scale * Math.Max(pageWidth, pageHeight));
        }
        return (int)Math.Min(65500, Math.Ceiling(longest));
    }

    /// <summary>
    /// Whether a picture of a given size still gives every requested output as the full picture would: the full size needs
    /// every pixel; each size needs its long side (Fit) or its whole box (Cover); a placeholder needs 100 px.
    /// Only decoders that can decode at a reduced size use it (JPEG 2000).
    /// </summary>
    private static Func<int, int, bool> BigEnough(ThumbnailOptions options, Wanted want)
    {
        if (want.Images && options.IncludeFullSize) return static (_, _) => false;
        var sizes = want.Images ? options.Sizes.ToArray() : [];
        var placeholder = want.Placeholder;
        var cover = options.Fit == ThumbnailFit.Cover;
        var aspect = options.AspectRatio;
        return (width, height) =>
        {
            if (placeholder && Math.Max(width, height) < PlaceholderSide) return false;
            foreach (var size in sizes)
            {
                if (!cover) { if (Math.Max(width, height) < size) return false; continue; }
                var (boxWidth, boxHeight) = CoverBox(size, aspect);
                if (width < boxWidth || height < boxHeight) return false;
            }
            return true;
        };
    }

    /// <summary>The box of one size for <see cref="ThumbnailFit.Cover"/>: its longer side is the size.</summary>
    private static (int Width, int Height) CoverBox(int size, double aspectRatio) => aspectRatio >= 1
        ? (size, Math.Max(1, (int)Math.Round(size / aspectRatio)))
        : (Math.Max(1, (int)Math.Round(size * aspectRatio)), size);

    /// <summary>
    /// Same as the byte[] overload, from a stream. A seekable video is read in place (only its header and the frame that is
    /// taken); every other format is read whole first. The stream is read from its current position and is not disposed.
    /// </summary>
    public static ThumbnailResult Create(Stream stream, string? extension, ThumbnailOptions options) =>
        Read(stream, extension, options, video => CreateVideo(video, options), data => Create(data, extension, options));

    /// <summary>
    /// A seekable video goes to <paramref name="video"/> as a stream starting at the current position (read in place);
    /// any other input is read into memory, within <see cref="ResourceLimits.MaxInputBytes"/>, and goes to <paramref name="other"/>.
    /// </summary>
    public static T Read<T>(Stream stream, string? extension, ThumbnailOptions options, Func<Stream, T> video, Func<byte[], T> other)
    {
        if (stream.CanSeek)
        {
            var start = stream.Position;
            Span<byte> head = stackalloc byte[512];
            var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            stream.Position = start;
            if (FormatDetector.IsVideo(head[..read], extension))
                return start == 0 ? video(stream) : video(new SubStream(stream, start));
            // known length: one exact buffer instead of a growing MemoryStream and a copy of it
            var length = stream.Length - start;
            if (length > options.Limits.MaxInputBytes) throw TooLarge(options);
            var data = GC.AllocateUninitializedArray<byte>((int)length);
            stream.ReadExactly(data);
            return other(data);
        }
        return other(ReadAll(stream, options));
    }

    /// <summary>
    /// The rest of a non-seekable stream in one array of its exact length, within <see cref="ResourceLimits.MaxInputBytes"/>.
    /// </summary>
    public static byte[] ReadAll(Stream stream, ThumbnailOptions options)
    {
        var buffer = new ChunkedBuffer(options);
        for (int n; (n = stream.Read(buffer.Space().Span)) > 0;)
        {
            buffer.Advance(n);
            Guard.Check();
        }
        return buffer.ToArray();
    }

    /// <summary>Same as <see cref="ReadAll"/>, reading asynchronously.</summary>
    public static async Task<byte[]> ReadAllAsync(Stream stream, ThumbnailOptions options, CancellationToken cancellationToken)
    {
        var buffer = new ChunkedBuffer(options);
        for (int n; (n = await stream.ReadAsync(buffer.Space(), cancellationToken).ConfigureAwait(false)) > 0;) buffer.Advance(n);
        return buffer.ToArray();
    }

    /// <summary>
    /// Bytes of unknown length kept in chunks that grow to 4 MiB, then copied once into an array of the exact length. A
    /// growing MemoryStream would hold up to twice the input in spare capacity, and its buffer would still need a copy to
    /// become an exact array; here the chunks can be collected as soon as the array is made, so only the input itself stays
    /// in memory while it is processed.
    /// </summary>
    private sealed class ChunkedBuffer(ThumbnailOptions options)
    {
        private const int FirstChunk = 64 << 10, LargestChunk = 4 << 20;
        private readonly List<byte[]> _chunks = [];
        private int _used;      // bytes used in the last chunk
        private long _length;

        /// <summary>Free space at the end, at least one byte, never past the input limit plus one byte (to detect it).</summary>
        public Memory<byte> Space()
        {
            if (_chunks.Count == 0 || _used == _chunks[^1].Length)
            {
                var size = _chunks.Count == 0 ? FirstChunk : Math.Min(LargestChunk, _chunks[^1].Length * 2);
                _chunks.Add(GC.AllocateUninitializedArray<byte>(size));
                _used = 0;
            }
            var room = (int)Math.Min(_chunks[^1].Length - _used, options.Limits.MaxInputBytes - _length + 1);
            return _chunks[^1].AsMemory(_used, room);
        }

        public void Advance(int count)
        {
            _used += count;
            _length += count;
            if (_length > options.Limits.MaxInputBytes) throw TooLarge(options);
        }

        public byte[] ToArray()
        {
            var data = GC.AllocateUninitializedArray<byte>((int)_length);
            var at = 0;
            for (var i = 0; i < _chunks.Count; i++)
            {
                var count = i == _chunks.Count - 1 ? _used : _chunks[i].Length;
                _chunks[i].AsSpan(0, count).CopyTo(data.AsSpan(at));
                at += count;
            }
            _chunks.Clear();
            return data;
        }
    }

    public static ResourceLimitExceededException TooLarge(ThumbnailOptions options) =>
        new($"The input is larger than {options.Limits.MaxInputBytes:N0} bytes (ResourceLimits.MaxInputBytes).");

    /// <summary>Video: one frame (the middle by default), then the same size set as an image file.</summary>
    private static ThumbnailResult CreateVideo(Stream stream, ThumbnailOptions options)
    {
        var images = new List<Thumbnail>();
        var want = new Wanted(options);
        if (!want.Picture) return Finish(new ThumbnailResult(DocumentFormat.Video, 1, images, null, default), want, null);
        var clock = Stopwatch.StartNew();
        using var frame = Video.VideoFrame.Grab(stream, out _, options.Video.Position, (int)Math.Min(int.MaxValue, options.Video.Timeout.TotalMilliseconds));
        var grab = Lap(clock);
        var placeholder = EncodeImage(frame, images, options, want);
        return Finish(new ThumbnailResult(DocumentFormat.Video, 1, images, null, new(grab, TimeSpan.Zero, Lap(clock))), want, placeholder);
    }

    /// <summary>Lays a document out into pages: flow documents are paginated, slides and sheets have their own renderers.</summary>
    public static PagedDocument Layout(byte[] data, DocumentFormat format) => Layout(data, format, null, out _);

    /// <summary>
    /// Same, stopping after <paramref name="maxPages"/> pages. One page more is laid out to learn whether the document goes on
    /// (<paramref name="more"/>), then dropped. Without a limit only the built-in ones apply and <paramref name="more"/> is false.
    /// </summary>
    public static PagedDocument Layout(byte[] data, DocumentFormat format, int? maxPages, out bool more)
    {
        var limit = maxPages is { } max ? (int)Math.Min(int.MaxValue, max + 1L) : (int?)null;
        PagedDocument document;
        if (format == DocumentFormat.Xps) document = Fixed.XpsReader.Read(data, limit);
        else if (FormatDetector.IsSlides(format)) document = SlideRenderer.Render(FormatDetector.OpenDeck(data, format), limit);
        else if (FormatDetector.IsSheets(format))
            document = limit is { } pages ? SheetRenderer.Render(FormatDetector.OpenSheets(data, format), Math.Min(pages, SheetRenderer.MaxPages))
                : SheetRenderer.Render(FormatDetector.OpenSheets(data, format));
        else
        {
            var reader = FormatDetector.Open(data, format);
            try { document = limit is { } pages ? Paginator.Build(reader, Math.Min(pages, Paginator.MaxPages)) : Paginator.Build(reader); }
            finally { (reader as IDisposable)?.Dispose(); }
        }
        more = maxPages is { } kept && document.Pages.Count > kept;
        if (more) document.Pages.RemoveRange(maxPages!.Value, document.Pages.Count - maxPages.Value);
        return document;
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
            // rounded down, so that the result really holds at most maxPixels
            var scale = Math.Sqrt(maxPixels / (width * height));
            // (less a hair, so a square root that lands just under a whole number does not lose a pixel)
            width = Math.Floor(width * scale + 1e-6);
            height = Math.Floor(height * scale + 1e-6);
        }
        return ((int)Math.Clamp(Math.Round(width), 1, int.MaxValue), (int)Math.Clamp(Math.Round(height), 1, int.MaxValue));
    }

    private static void CheckPixels(int width, int height, long maxPixels)
    {
        if ((long)width * height > maxPixels)
            throw new ResourceLimitExceededException($"The image is {width} x {height} px, more than {maxPixels:N0} pixels (ResourceLimits.MaxPixels).");
    }

    /// <summary>
    /// The formats read in managed code first (PSD, TGA, EPS, RAW, JPEG 2000), then libvips; Skia covers the few formats this
    /// libvips build has no loader for (BMP, ICO). All read the header before any pixel, so a picture declaring more than
    /// MaxPixels is refused before it is decoded.
    /// </summary>
    private static NetVips.Image Decode(byte[] data, string? extension, Images.DecodeRequest request)
    {
        if (Images.ManagedImages.Decode(data, extension, request) is { } managed) return managed;
        var maxPixels = request.MaxPixels;
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

    /// <summary>
    /// Thumbnail set from a rendered page: the master itself, the "full" size and the smaller boxes; with
    /// <see cref="ThumbnailFit.Cover"/> each box is filled and cropped from the top of the page.
    /// </summary>
    /// <returns>The placeholder, if requested.</returns>
    private static ThumbnailPlaceholder? EncodeBitmap(SKBitmap master, int fullWidth, int fullHeight, List<Thumbnail> images, ThumbnailOptions options,
        Wanted want, bool cropTop)
    {
        // wrap the BGRA pixels without copying, reorder to RGB (alpha is always opaque here), keep them in memory once
        using var bgra = NetVips.Image.NewFromMemory(master.GetPixels(), (ulong)master.ByteCount, master.Width, master.Height, 4, NetVips.Enums.BandFormat.Uchar);
        using var rgb = bgra[2].Bandjoin(bgra[1], bgra[0]).Copy(interpretation: NetVips.Enums.Interpretation.Srgb);
        using var cached = rgb.CopyMemory();
        var placeholder = want.Placeholder ? Placeholder(cached) : null;
        if (!want.Images) return placeholder;

        var wanted = new List<(int?, object)>();
        if (options.IncludeFullSize)
        {
            // a master drawn at the full size can differ from it by a pixel of rounding: it is the full size then
            if (Math.Abs(fullWidth - master.Width) <= 1 && Math.Abs(fullHeight - master.Height) <= 1) (fullWidth, fullHeight) = (master.Width, master.Height);
            wanted.Add((null, (Math.Max(1, fullWidth), Math.Max(1, fullHeight))));
        }
        foreach (var box in options.Sizes)
        {
            if (options.Fit == ThumbnailFit.Cover) { wanted.Add((box, new Cover(CoverBox(box, options.AspectRatio)))); continue; }
            var ratio = (double)box / Math.Max(master.Width, master.Height);
            wanted.Add((box, (Math.Max(1, (int)Math.Round(master.Width * ratio)), Math.Max(1, (int)Math.Round(master.Height * ratio)))));
        }
        Encode(wanted, key =>
        {
            if (key is Cover cover)
            {
                using var cropped = cached.ThumbnailImage(cover.Box.Width, height: cover.Box.Height,
                    crop: cropTop ? NetVips.Enums.Interesting.Low : NetVips.Enums.Interesting.Centre);
                return Save(cropped, options);
            }
            var (width, height) = ((int, int))key;
            if (width == cached.Width && height == cached.Height) return Save(cached, options);
            using var resized = cached.ThumbnailImage(width, height: height, size: NetVips.Enums.Size.Force);
            return Save(resized, options);
        }, images, options.EncodeInParallel);
        return placeholder;
    }

    /// <summary>A <see cref="ThumbnailFit.Cover"/> box (a distinct key type, so it is never mistaken for a plain size).</summary>
    private readonly record struct Cover((int Width, int Height) Box);

    /// <summary>
    /// Image files (and video frames) never touch the document renderer: libvips decodes the file once, applies EXIF orientation,
    /// and each size is cut from that copy ("full" needs every pixel anyway). Nothing is enlarged beyond the original.
    /// </summary>
    /// <returns>The placeholder, if requested.</returns>
    private static ThumbnailPlaceholder? EncodeImage(NetVips.Image original, List<Thumbnail> images, ThumbnailOptions options, Wanted want)
    {
        CheckPixels(original.Width, original.Height, options.Limits.MaxPixels);   // video frames come here straight from the decoder
        using var upright = original.Autorot();
        using var cached = upright.CopyMemory();
        var placeholder = want.Placeholder ? Placeholder(cached) : null;
        if (!want.Images) return placeholder;

        var side = Math.Max(cached.Width, cached.Height);
        var wanted = new List<(int?, object)>();
        if (options.IncludeFullSize) wanted.Add((null, 0));
        foreach (var box in options.Sizes)
        {
            if (options.Fit != ThumbnailFit.Cover) { wanted.Add((box, side <= box ? 0 : box)); continue; }
            // never enlarged: a picture smaller than the box gives the largest box of the same shape that it fills
            var (boxWidth, boxHeight) = CoverBox(box, options.AspectRatio);
            var scale = Math.Max((double)boxWidth / cached.Width, (double)boxHeight / cached.Height);
            if (scale > 1) (boxWidth, boxHeight) = (Math.Max(1, (int)(boxWidth / scale)), Math.Max(1, (int)(boxHeight / scale)));
            wanted.Add((box, boxWidth == cached.Width && boxHeight == cached.Height ? 0 : new Cover((boxWidth, boxHeight))));
        }
        Encode(wanted, key =>
        {
            if (key is Cover cover)
            {
                using var cropped = cached.ThumbnailImage(cover.Box.Width, height: cover.Box.Height, crop: NetVips.Enums.Interesting.Centre);
                return Save(cropped, options);
            }
            var box = (int)key;
            if (box == 0) return Save(cached, options);
            using var thumbnail = cached.ThumbnailImage(box, height: box, size: NetVips.Enums.Size.Down);
            return Save(thumbnail, options);
        }, images, options.EncodeInParallel);
        return placeholder;
    }

    /// <summary>ThumbHash and average colour of a picture, from a copy of at most 100 x 100 px in 8-bit sRGB with alpha.</summary>
    private static ThumbnailPlaceholder Placeholder(NetVips.Image picture)
    {
        Guard.Check();
        using var small = picture.ThumbnailImage(PlaceholderSide, height: PlaceholderSide, size: NetVips.Enums.Size.Down);
        using var srgb = small.Interpretation == NetVips.Enums.Interpretation.Srgb ? small.Copy() : small.Colourspace(NetVips.Enums.Interpretation.Srgb);
        using var bytes = srgb.Format == NetVips.Enums.BandFormat.Uchar ? srgb.Copy() : srgb.Cast(NetVips.Enums.BandFormat.Uchar);
        using var rgba = bytes.Bands switch
        {
            1 => bytes.Bandjoin(bytes, bytes, 255),
            2 => bytes[0].Bandjoin(bytes[0], bytes[0], bytes[1]),
            3 => bytes.Bandjoin(255),
            4 => bytes.Copy(),
            _ => bytes.ExtractBand(0, n: 4),
        };
        return ThumbHash.Encode(rgba.Width, rgba.Height, rgba.WriteToMemory<byte>());
    }

    /// <summary>A read-only view of a seekable stream from a given offset on, so a video can start mid-stream.</summary>
    internal sealed class SubStream(Stream inner, long start) : Stream
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
