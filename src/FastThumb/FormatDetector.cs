using System.Globalization;
using System.IO.Compression;
using System.Text;
using FastThumb.Cells;
using FastThumb.Layout;
using FastThumb.Slides;
using FastThumb.Words;
using SkiaSharp;

namespace FastThumb;

/// <summary>
/// Format detection by content, not by file name: signature first (PDF, compound file, ZIP and what is
/// inside it), then a look at the leading text. The extension only breaks ties between plain-text formats.
/// </summary>
internal static class FormatDetector
{
    static FormatDetector() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>The extensions of every format read here; the content decides the format, these are what the files are usually called.</summary>
    public static readonly IReadOnlyCollection<string> Extensions = new SortedSet<string>(StringComparer.Ordinal)
    {
        ".pdf",
        ".doc", ".dot", ".docx", ".docm", ".dotx", ".dotm", ".xml", ".wml", ".odt", ".ott", ".rtf", ".txt", ".md", ".markdown",
        ".htm", ".html", ".xhtml", ".mht", ".mhtml", ".epub", ".mobi", ".azw", ".azw3", ".chm", ".hwp", ".wpd", ".wps",
        ".ppt", ".pps", ".pot", ".pptx", ".pptm", ".potx", ".ppsx", ".odp", ".fodp",
        ".xls", ".xlsx", ".xlsm", ".xltx", ".xlsb", ".csv", ".tsv", ".ods",
        ".xps", ".oxps",
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".tif", ".tiff", ".heic", ".heif", ".avif", ".bmp", ".svg", ".ico",
        ".psd", ".psb", ".tga", ".eps", ".epsf", ".epsi",
        ".cr2", ".cr3", ".nef", ".nrw", ".arw", ".srf", ".sr2", ".dng", ".orf", ".rw2", ".raf", ".pef", ".srw", ".erf", ".3fr",
        ".mp4", ".m4v", ".mov", ".3gp", ".3g2", ".f4v", ".mkv", ".webm", ".avi", ".flv", ".asf", ".wmv", ".mpg", ".mpeg", ".mpe", ".m2v",
        ".vob", ".m2ts", ".ogv", ".ogg", ".rm", ".rmvb", ".mxf", ".vivo", ".viv", ".roq",
    };

    public static DocumentFormat Detect(byte[] data, string? extension)
    {
        if (data.Length >= 5 && data.AsSpan(0, 5).SequenceEqual("%PDF-"u8)) return DocumentFormat.Pdf;
        if (data.Length >= 8 && data.AsSpan(0, 8).SequenceEqual(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }))
        {
            // compound file: which application wrote it is told by its streams
            var container = new CompoundFile(data);
            if (container.Has("WordDocument")) return DocumentFormat.Doc;
            if (container.Has("PowerPoint Document")) return DocumentFormat.Ppt;
            if (container.Has("Workbook") || container.Has("Book")) return DocumentFormat.Xls;
            if (container.Has("FileHeader") && (container.Has("Section0") || container.Has("PrvText"))) return DocumentFormat.Hwp;
            if (container.Has("CONTENTS") || container.Has("MN0") || container.Has("MatOST")) return DocumentFormat.Works;
            return DocumentFormat.Unknown;
        }
        if (data.Length >= 4 && data.AsSpan(0, 4).SequenceEqual("ITSF"u8)) return DocumentFormat.Chm;
        if (data.Length >= 4 && data[0] == 0xFF && data[1] == (byte)'W' && data[2] == (byte)'P' && data[3] == (byte)'C') return DocumentFormat.WordPerfect;
        if (data.Length >= 68 && data.AsSpan(60, 8).SequenceEqual("BOOKMOBI"u8)) return DocumentFormat.Mobi;
        if (IsImage(data, extension)) return DocumentFormat.Image;
        if (IsVideo(data, extension)) return DocumentFormat.Video;
        if (data.Length >= 4 && data[0] == 'P' && data[1] == 'K' && data[2] == 3 && data[3] == 4)
        {
            using var zip = new ZipArchive(new MemoryStream(data, false), ZipArchiveMode.Read);
            if (zip.GetEntry("word/document.xml") != null) return DocumentFormat.Docx;
            if (zip.GetEntry("ppt/presentation.xml") != null) return DocumentFormat.Pptx;
            if (zip.GetEntry("xl/workbook.xml") != null) return DocumentFormat.Xlsx;
            if (zip.GetEntry("xl/workbook.bin") != null) return DocumentFormat.Xlsb;
            if (zip.GetEntry("mimetype") is { } mime)
            {
                // a few hundred bytes are all a media type needs; the entry itself may be a bomb
                using var stream = mime.Open();
                var buffer = new byte[512];
                var length = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
                var mimeType = Encoding.UTF8.GetString(buffer, 0, length);
                if (mimeType.Contains("presentation", StringComparison.Ordinal)) return DocumentFormat.Odp;
                if (mimeType.Contains("spreadsheet", StringComparison.Ordinal)) return DocumentFormat.Ods;
            }
            if (zip.Entries.Any(e => e.FullName.EndsWith(".fpage", StringComparison.OrdinalIgnoreCase) || e.FullName.EndsWith(".fdseq", StringComparison.OrdinalIgnoreCase))) return DocumentFormat.Xps;
            if (zip.GetEntry("META-INF/container.xml") != null) return DocumentFormat.Epub;
            if (zip.GetEntry("content.xml") != null) return DocumentFormat.Odt;
            return DocumentFormat.Unknown;
        }

        var head = Decode(data.AsSpan(0, Math.Min(data.Length, 4096)), null).TrimStart('﻿', ' ', '\t', '\r', '\n');
        if (head.StartsWith("{\\rtf", StringComparison.Ordinal)) return DocumentFormat.Rtf;
        if (head.Contains("<pkg:package", StringComparison.Ordinal)) return DocumentFormat.FlatOpc;
        if (head.Contains("office:document", StringComparison.Ordinal) && head.Contains("opendocument.presentation", StringComparison.Ordinal)) return DocumentFormat.Fodp;
        if (head.Contains("<w:wordDocument", StringComparison.Ordinal)) return DocumentFormat.WordMl;
        if (head.StartsWith("MIME-Version:", StringComparison.OrdinalIgnoreCase) || (head.Contains("MIME-Version:", StringComparison.OrdinalIgnoreCase) && head.Contains("multipart/", StringComparison.OrdinalIgnoreCase))) return DocumentFormat.Mhtml;
        if (head.Contains("<html", StringComparison.OrdinalIgnoreCase) || head.StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase)) return DocumentFormat.Html;
        return (extension ?? "").ToLowerInvariant() switch
        {
            ".csv" or ".tsv" => DocumentFormat.Csv,
            ".htm" or ".html" or ".xhtml" => DocumentFormat.Html,
            ".md" or ".markdown" => DocumentFormat.Markdown,
            ".mht" or ".mhtml" => DocumentFormat.Mhtml,
            ".rtf" => DocumentFormat.Rtf,
            _ => DocumentFormat.Text,
        };
    }

    public static bool IsSlides(DocumentFormat format) => format is DocumentFormat.Pptx or DocumentFormat.Ppt or DocumentFormat.Odp or DocumentFormat.Fodp;

    public static bool IsSheets(DocumentFormat format) => format is DocumentFormat.Xlsx or DocumentFormat.Xls or DocumentFormat.Csv or DocumentFormat.Ods or DocumentFormat.Xlsb;

    /// <summary>Raster / vector image files, by signature (and by extension for the text-based or rarer ones).</summary>
    public static bool IsImage(ReadOnlySpan<byte> d, string? extension)
    {
        if (d.Length < 12) return false;
        if (d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return true;                                           // JPEG
        if (d[0] == 0x89 && d[1] == 'P' && d[2] == 'N' && d[3] == 'G') return true;                               // PNG
        if (d[0] == 'G' && d[1] == 'I' && d[2] == 'F' && d[3] == '8') return true;                               // GIF
        if ((d[0] == 'I' && d[1] == 'I' && d[2] == 42 && d[3] == 0) || (d[0] == 'M' && d[1] == 'M' && d[2] == 0 && d[3] == 42)) return true; // TIFF
        if (d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F' && d[8] == 'W' && d[9] == 'E' && d[10] == 'B' && d[11] == 'P') return true; // WebP
        if (d[4] == 'f' && d[5] == 't' && d[6] == 'y' && d[7] == 'p'                                            // ISO media: only the still-image brands
            && Encoding.ASCII.GetString(d.Slice(8, 4)) is "heic" or "heix" or "hevc" or "mif1" or "msf1" or "avif" or "avis") return true;
        if (Images.ManagedImages.Claims(d, extension)) return true;                                              // PSD, TGA, EPS, RAW, JPEG 2000
        return (extension ?? "").ToLowerInvariant() is ".bmp" or ".svg" or ".ico";   // only what libvips (svg) or Skia (bmp, ico) can actually decode
    }

    /// <summary>
    /// Video (and audio-only media, which then fails with "no video stream") from the first bytes of a file:
    /// container signature first; the extension only counts when the bytes are not some other known format.
    /// Enough to decide before the rest of a stream is read: 512 bytes cover every signature.
    /// </summary>
    public static bool IsVideo(ReadOnlySpan<byte> d, string? extension)
    {
        if (IsImage(d, null)) return false;                                                                      // HEIC / AVIF share the MP4 container
        if (d.Length >= 12)
        {
            if (Encoding.ASCII.GetString(d.Slice(4, 4)) is "ftyp" or "moov" or "mdat" or "wide" or "free" or "skip" or "pnot") return true;   // MP4 / MOV / 3GP
            if (d[0] == 0x1A && d[1] == 0x45 && d[2] == 0xDF && d[3] == 0xA3) return true;                        // Matroska / WebM
            if (d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F' && d[8] == 'A' && d[9] == 'V' && d[10] == 'I') return true;   // AVI
            if (d[0] == 'F' && d[1] == 'L' && d[2] == 'V' && d[3] == 1) return true;                             // FLV
            if (d[0] == 0x30 && d[1] == 0x26 && d[2] == 0xB2 && d[3] == 0x75 && d[4] == 0x8E && d[5] == 0x66 && d[6] == 0xCF && d[7] == 0x11) return true;   // ASF / WMV
            if (d[0] == 0 && d[1] == 0 && d[2] == 1 && (d[3] == 0xBA || d[3] == 0xB3)) return true;               // MPEG program stream / video
            if (d[0] == 0x47 && d.Length > 376 && d[188] == 0x47 && d[376] == 0x47) return true;                  // MPEG transport stream
            if (d[0] == 'O' && d[1] == 'g' && d[2] == 'g' && d[3] == 'S') return true;                           // Ogg
            if (d[0] == '.' && d[1] == 'R' && d[2] == 'M' && d[3] == 'F') return true;                           // RealMedia
            if (d[0] == 0x06 && d[1] == 0x0E && d[2] == 0x2B && d[3] == 0x34) return true;                        // MXF
            // bytes of another known format win over a misleading file name
            if (d[0] == '%' && d[1] == 'P' && d[2] == 'D' && d[3] == 'F') return false;
            if (d[0] == 'P' && d[1] == 'K' && d[2] == 3 && d[3] == 4) return false;
            if (d[0] == 0xD0 && d[1] == 0xCF && d[2] == 0x11 && d[3] == 0xE0) return false;
        }
        // media extensions, audio-only ones included: those are reported as "no video" instead of being laid out as text
        return (extension ?? "").ToLowerInvariant() is ".mkv" or ".mp4" or ".avi" or ".mov" or ".flv" or ".wav" or ".webm" or ".vob" or ".ogv" or ".ogg"
            or ".rm" or ".rmvb" or ".vivo" or ".viv" or ".asf" or ".wmv" or ".m4v" or ".mp2" or ".mpeg" or ".mpg" or ".mpe" or ".m2v" or ".3gp" or ".3g2"
            or ".mxf" or ".roq" or ".f4v" or ".m2ts";   // not .ts / .mts: those are TypeScript far more often; transport streams are caught by signature
    }

    /// <summary>Opens a spreadsheet format as sheet snapshots.</summary>
    public static List<SheetSnapshot> OpenSheets(byte[] data, DocumentFormat format) => format switch
    {
        DocumentFormat.Xlsx => XlsxReader.Read(data),
        DocumentFormat.Xls => XlsReader.Read(data),
        DocumentFormat.Csv => CsvReader.Read(Decode(data, null)),
        DocumentFormat.Ods => OdsReader.Read(data),
        DocumentFormat.Xlsb => XlsbReader.Read(data),
        _ => throw new NotSupportedException("Unsupported spreadsheet format"),
    };

    /// <summary>Opens a presentation format as a normalised slide deck.</summary>
    public static SlideDeck OpenDeck(byte[] data, DocumentFormat format) => format switch
    {
        DocumentFormat.Pptx => PptxReader.Read(data),
        DocumentFormat.Ppt => PptReader.Read(data),
        DocumentFormat.Odp => OdpReader.Read(data, flat: false),
        DocumentFormat.Fodp => OdpReader.Read(data, flat: true),
        _ => throw new NotSupportedException("Unsupported presentation format"),
    };

    /// <summary>Opens any supported non-PDF format as a block source.</summary>
    public static IPageSource Open(byte[] data, DocumentFormat format) => format switch
    {
        DocumentFormat.Docx => new DocxReader(new MemoryStream(data, false)),
        DocumentFormat.FlatOpc => DocxReader.FromFlatOpc(Decode(data, null)),
        DocumentFormat.Doc => new DocReader(new MemoryStream(data, false)),
        DocumentFormat.Odt => OdtReader.Read(data),
        DocumentFormat.Epub => HtmlReader.ReadEpub(data),
        DocumentFormat.Html => HtmlReader.Read(DecodeHtml(data)),
        DocumentFormat.Mhtml => HtmlReader.ReadMhtml(data),
        DocumentFormat.Rtf => RtfReader.Read(data),
        DocumentFormat.Markdown => MarkdownReader.Read(Decode(data, null)),
        DocumentFormat.Text => TextReaders.ReadText(Decode(data, null)),
        DocumentFormat.WordMl => DocxReader.FromWordMl(Decode(data, null)),
        DocumentFormat.Mobi => MobiReader.Read(data),
        DocumentFormat.Chm => ChmReader.Read(data),
        DocumentFormat.Hwp => LegacyTextReaders.ReadHwp(data),
        DocumentFormat.WordPerfect => LegacyTextReaders.ReadWordPerfect(data),
        DocumentFormat.Works => LegacyTextReaders.ReadWorks(data),
        _ => throw new NotSupportedException("Unsupported document format"),
    };

    /// <summary>BOM-aware decode; without a BOM the given encoding (or UTF-8) is used.</summary>
    public static string Decode(ReadOnlySpan<byte> data, Encoding? fallback)
    {
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) return Encoding.UTF8.GetString(data[3..]);
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE) return Encoding.Unicode.GetString(data[2..]);
        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(data[2..]);
        return (fallback ?? Encoding.UTF8).GetString(data);
    }

    /// <summary>HTML declares its own charset in a meta tag near the top.</summary>
    public static string DecodeHtml(ReadOnlySpan<byte> data)
    {
        var head = Encoding.Latin1.GetString(data[..Math.Min(data.Length, 2048)]);
        var at = head.IndexOf("charset=", StringComparison.OrdinalIgnoreCase);
        Encoding? encoding = null;
        if (at >= 0)
        {
            var name = new string(head[(at + 8)..].TrimStart('"', '\'', ' ').TakeWhile(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
            try { encoding = Encoding.GetEncoding(name); } catch (ArgumentException) { }
        }
        return Decode(data, encoding);
    }

    /// <summary>CSS / ODF length ("12pt", "2.5cm", "1in", "16px", "10mm") to points.</summary>
    public static float? Length(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        var digits = 0;
        while (digits < value.Length && (char.IsDigit(value[digits]) || value[digits] is '.' or '-' or '+')) digits++;
        if (digits == 0 || !float.TryParse(value[..digits], NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return null;
        return value[digits..].Trim().ToLowerInvariant() switch
        {
            "pt" => number,
            "in" => number * 72,
            "cm" => number * 72 / 2.54f,
            "mm" => number * 72 / 25.4f,
            "px" or "" => number * 0.75f,
            "pc" => number * 12,
            _ => null,
        };
    }

    public static SKColor? Color(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim().ToLowerInvariant();
        if (value.StartsWith('#'))
        {
            var hex = value[1..];
            if (hex.Length == 3) hex = string.Concat(hex.Select(c => $"{c}{c}"));
            return hex.Length == 6 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb) ? new SKColor(0xFF000000 | rgb) : null;
        }
        return value switch
        {
            "black" => SKColors.Black,
            "red" => SKColors.Red,
            "blue" => SKColors.Blue,
            "green" => SKColors.Green,
            "white" => SKColors.White,
            "gray" or "grey" => SKColors.Gray,
            "navy" => SKColors.Navy,
            "maroon" => SKColors.Maroon,
            "purple" => SKColors.Purple,
            _ => null,
        };
    }
}
