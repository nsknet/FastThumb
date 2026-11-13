using System.Globalization;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using FastThumb.Scene;
using SkiaSharp;

namespace FastThumb;

/// <summary>
/// <see cref="DocumentInfo"/> without layout or rendering: document properties from the package, the page count and
/// size of a PDF from PDFium, the size of an image from its header, the size and duration of a video from its first frame.
/// Properties are optional: a damaged properties part leaves them null instead of failing the call.
/// </summary>
internal static class Inspector
{
    /// <summary>Longest property value kept; a title is a line, not a document.</summary>
    private const int MaxValue = 1000;

    public static DocumentInfo Inspect(Stream stream, string? extension, ThumbnailOptions options) =>
        Thumbnailer.Read(stream, extension, options, video => Video(video, options), data => Inspect(data, extension, options));

    public static DocumentInfo Inspect(byte[] data, string? extension, ThumbnailOptions options)
    {
        var format = FormatDetector.Detect(data, extension);
        // the same answer as Generate gives for it; DetectFormat is there for a check that does not throw
        if (format == DocumentFormat.Unknown) throw new NotSupportedException("The format is not recognised.");
        switch (format)
        {
            case DocumentFormat.Video:
                using (var video = new MemoryStream(data, false)) return Video(video, options);
            case DocumentFormat.Image:
                return Image(data, extension);
            case DocumentFormat.Pdf:
                return Pdf(data);
        }
        var info = new DocumentInfo(format);
        try
        {
            switch (format)
            {
                case DocumentFormat.Docx or DocumentFormat.Xlsx or DocumentFormat.Pptx or DocumentFormat.Xlsb:
                    OfficeOpenXml(data, info);
                    break;
                case DocumentFormat.Odt or DocumentFormat.Ods or DocumentFormat.Odp:
                    using (var zip = new ZipArchive(new MemoryStream(data, false), ZipArchiveMode.Read))
                        if (Xml(zip, "meta.xml") is { } meta) OpenDocument(meta.Root, info);
                    break;
                case DocumentFormat.Fodp:
                    using (var stream = new MemoryStream(data, false)) OpenDocument(Guard.LoadXml(stream).Root, info);
                    break;
                case DocumentFormat.Epub:
                    Epub(data, info);
                    break;
            }
        }
        catch (XmlException) { }   // a broken properties part: the document may still render, so no error here
        // the input is in memory: an I/O error can only come from a damaged ZIP directory or entry
        catch (IOException e) { throw new InvalidDocumentException($"The file is damaged ({e.Message})", e); }
        return info;
    }

    private static DocumentInfo Video(Stream stream, ThumbnailOptions options)
    {
        // the first frame: the cheapest one to reach, and the size and duration are the same for every frame
        using var frame = global::FastThumb.Video.VideoFrame.Grab(stream, out var details, 0, (int)Math.Min(int.MaxValue, options.Video.Timeout.TotalMilliseconds));
        return new DocumentInfo(DocumentFormat.Video)
        {
            PageCount = 1,
            Width = frame.Width,
            Height = frame.Height,
            Duration = details.Duration > 0 && details.Duration < TimeSpan.MaxValue.TotalSeconds ? TimeSpan.FromSeconds(details.Duration) : null,
            VideoCodec = Text(details.Codec),
        };
    }

    /// <summary>
    /// Only the header is read: by the managed readers (PSD, TGA, EPS, RAW, JPEG 2000), else by libvips, which loads lazily,
    /// or Skia's codec, which reads the header on creation.
    /// </summary>
    private static DocumentInfo Image(byte[] data, string? extension)
    {
        if (Images.ManagedImages.Size(data, extension) is { } size)
            return new DocumentInfo(DocumentFormat.Image) { PageCount = 1, Width = size.Width, Height = size.Height };
        int width, height;
        try
        {
            using var image = NetVips.Image.NewFromBuffer(data);
            (width, height) = (image.Width, image.Height);
            // EXIF orientations 5 to 8 turn the picture a quarter: displayed, it is the other way round
            if (image.GetTypeOf("orientation") != 0 && image.Get("orientation") is int orientation && orientation is >= 5 and <= 8)
                (width, height) = (height, width);
        }
        catch (NetVips.VipsException e)
        {
            using var codec = SKCodec.Create(SKData.CreateCopy(data)) ?? throw new UnsupportedFormatException("Image cannot be decoded.", e);
            (width, height) = (codec.Info.Width, codec.Info.Height);
        }
        return new DocumentInfo(DocumentFormat.Image) { PageCount = 1, Width = width, Height = height };
    }

    private static DocumentInfo Pdf(byte[] data)
    {
        var (count, width, height, meta) = Pdfium.Inspect(data);
        return new DocumentInfo(DocumentFormat.Pdf)
        {
            PageCount = count,
            Width = (int)Math.Clamp(Math.Round(width), 1, int.MaxValue),
            Height = (int)Math.Clamp(Math.Round(height), 1, int.MaxValue),
            Title = Text(meta.GetValueOrDefault("Title")),
            Author = Text(meta.GetValueOrDefault("Author")),
            Subject = Text(meta.GetValueOrDefault("Subject")),
            Keywords = Text(meta.GetValueOrDefault("Keywords")),
            Created = PdfDate(meta.GetValueOrDefault("CreationDate")),
            Modified = PdfDate(meta.GetValueOrDefault("ModDate")),
        };
    }

    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace DcTerms = "http://purl.org/dc/terms/";
    private static readonly XNamespace CoreProperties = "http://schemas.openxmlformats.org/package/2006/metadata/core-properties";
    private static readonly XNamespace ExtendedProperties = "http://schemas.openxmlformats.org/officeDocument/2006/extended-properties";
    private static readonly XNamespace Office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private static readonly XNamespace Meta = "urn:oasis:names:tc:opendocument:xmlns:meta:1.0";

    /// <summary>docProps/core.xml (title, author, dates) and docProps/app.xml (pages or slides as last saved).</summary>
    private static void OfficeOpenXml(byte[] data, DocumentInfo info)
    {
        using var zip = new ZipArchive(new MemoryStream(data, false), ZipArchiveMode.Read);
        if (Xml(zip, "docProps/core.xml")?.Root is { } core)
        {
            info.Title = Text(core.Element(Dc + "title")?.Value);
            info.Author = Text(core.Element(Dc + "creator")?.Value);
            info.Subject = Text(core.Element(Dc + "subject")?.Value);
            info.Keywords = Text(core.Element(CoreProperties + "keywords")?.Value);
            info.Created = Date(core.Element(DcTerms + "created")?.Value);
            info.Modified = Date(core.Element(DcTerms + "modified")?.Value);
        }
        if (info.Format is DocumentFormat.Docx or DocumentFormat.Pptx && Xml(zip, "docProps/app.xml")?.Root is { } app)
            info.PageCount = Count(app.Element(ExtendedProperties + (info.Format == DocumentFormat.Docx ? "Pages" : "Slides"))?.Value);
    }

    /// <summary>The office:meta element of meta.xml or of a flat OpenDocument file.</summary>
    private static void OpenDocument(XElement? root, DocumentInfo info)
    {
        var meta = root?.Element(Office + "meta");
        if (meta == null) return;
        info.Title = Text(meta.Element(Dc + "title")?.Value);
        // dc:creator is who saved it last; meta:initial-creator is the author
        info.Author = Text(meta.Element(Meta + "initial-creator")?.Value) ?? Text(meta.Element(Dc + "creator")?.Value);
        info.Subject = Text(meta.Element(Dc + "subject")?.Value);
        info.Keywords = Text(string.Join(", ", meta.Elements(Meta + "keyword").Select(k => k.Value.Trim()).Where(k => k.Length > 0)));
        info.Created = Date(meta.Element(Meta + "creation-date")?.Value);
        info.Modified = Date(meta.Element(Dc + "date")?.Value);
        if (info.Format == DocumentFormat.Odt) info.PageCount = Count(meta.Element(Meta + "document-statistic")?.Attribute(Meta + "page-count")?.Value);
    }

    /// <summary>The Dublin Core metadata of the package document named by META-INF/container.xml.</summary>
    private static void Epub(byte[] data, DocumentInfo info)
    {
        using var zip = new ZipArchive(new MemoryStream(data, false), ZipArchiveMode.Read);
        var container = Xml(zip, "META-INF/container.xml")?.Root;
        var path = container?.Descendants().FirstOrDefault(e => e.Name.LocalName == "rootfile")?.Attribute("full-path")?.Value;
        if (path == null || Xml(zip, path)?.Root is not { } package) return;
        var metadata = package.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata");
        if (metadata == null) return;
        info.Title = Text(metadata.Element(Dc + "title")?.Value);
        info.Author = Text(metadata.Element(Dc + "creator")?.Value);
        info.Subject = Text(metadata.Element(Dc + "subject")?.Value);
        info.Created = Date(metadata.Element(Dc + "date")?.Value);
        // EPUB 3 keeps the modification time in <meta property="dcterms:modified">
        info.Modified = Date(metadata.Elements().FirstOrDefault(e => e.Name.LocalName == "meta" && (string?)e.Attribute("property") == "dcterms:modified")?.Value);
    }

    private static XDocument? Xml(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path) ?? zip.Entries.FirstOrDefault(e => string.Equals(e.FullName.TrimStart('/'), path, StringComparison.OrdinalIgnoreCase));
        if (entry == null) return null;
        Guard.Check();
        // properties are small: a part far larger than any real one is skipped rather than read
        if (entry.Length > 4 << 20) return null;
        using var stream = Guard.Open(entry, 4 << 20);
        return Guard.LoadXml(stream);
    }

    private static string? Text(string? value)
    {
        if (value == null) return null;
        value = value.Replace('\0', ' ').Trim();
        if (value.Length > MaxValue) value = value[..MaxValue];
        return value.Length == 0 ? null : value;
    }

    private static int? Count(string? value) =>
        int.TryParse(value?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count > 0 ? count : null;

    /// <summary>
    /// W3C date-time (OOXML, OpenDocument, EPUB); without a zone it is taken as UTC. Writers put 0001-01-01 or 1601-01-01
    /// for "not set", so a date before 1900 counts as none.
    /// </summary>
    private static DateTimeOffset? Date(string? value) =>
        DateTimeOffset.TryParse(value?.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? Plausible(date) : null;

    private static DateTimeOffset? Plausible(DateTimeOffset date) => date.Year >= 1900 ? date : null;

    /// <summary>PDF date: "D:YYYYMMDDHHmmSSOHH'mm'", every part after the year optional.</summary>
    internal static DateTimeOffset? PdfDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (text.StartsWith("D:", StringComparison.Ordinal)) text = text[2..];
        int Part(int start, int length, int fallback) =>
            text.Length >= start + length && int.TryParse(text.AsSpan(start, length), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : fallback;
        var year = Part(0, 4, -1);
        if (year < 1) return null;
        int month = Part(4, 2, 1), day = Part(6, 2, 1), hour = Part(8, 2, 0), minute = Part(10, 2, 0), second = Part(12, 2, 0);
        var offset = TimeSpan.Zero;
        if (text.Length > 14 && text[14] is '+' or '-')
        {
            var sign = text[14] == '-' ? -1 : 1;
            var zone = text[15..].Replace("'", "");
            int zoneHours = zone.Length >= 2 && int.TryParse(zone.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var h) ? h : 0;
            int zoneMinutes = zone.Length >= 4 && int.TryParse(zone.AsSpan(2, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var m) ? m : 0;
            offset = sign * new TimeSpan(Math.Min(zoneHours, 14), Math.Min(zoneMinutes, 59), 0);
        }
        try { return Plausible(new DateTimeOffset(year, month, day, hour, minute, second, offset)); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}
