using System.Text;

namespace FastThumb.Images;

/// <summary>
/// A PDF-based Illustrator file (.ai, Illustrator 9 and later) saved with "Create PDF Compatible File" off. Its artwork is
/// only in Illustrator's private data; its one PDF page is a notice that the file was saved without PDF content. The picture
/// shown instead is the thumbnail Illustrator puts in the file's XMP metadata (xmpGImg:image, a JPEG of about 256 px).
/// </summary>
internal static class IllustratorPdf
{
    /// <summary>Largest XMP thumbnail read, as base64 text.</summary>
    private const int MaxThumbnailText = 16 << 20;

    /// <summary>
    /// The XMP thumbnail when this PDF is such a file; null for any other PDF, which then renders as usual. Such a file without
    /// a thumbnail fails: drawing the notice would only show the notice.
    /// </summary>
    public static byte[]? Placeholder(byte[] pdf)
    {
        // Illustrator's private data marks the file; only then is the page text read
        if (pdf.AsSpan().IndexOf("AIPrivateData"u8) < 0) return null;
        var texts = Scene.Pdfium.PageTexts(pdf, 1, out _);
        if (texts is not [var first, ..] || !first.Contains("without PDF Content", StringComparison.OrdinalIgnoreCase)) return null;
        return Thumbnail(pdf) ?? throw new UnsupportedFormatException(
            "The Illustrator file was saved without PDF content and has no thumbnail; re-save it with \"Create PDF Compatible File\" on.");
    }

    /// <summary>The JPEG in the first xmpGImg:image of an uncompressed XMP packet (element or attribute form), or null.</summary>
    internal static byte[]? Thumbnail(byte[] pdf)
    {
        var d = pdf.AsSpan();
        var at = d.IndexOf("xmpGImg:image"u8);
        while (at >= 0)
        {
            var rest = d[(at + "xmpGImg:image"u8.Length)..];
            ReadOnlySpan<byte> text = default;
            if (rest.Length > 0 && rest[0] == '>')
            {
                var end = rest.IndexOf("</xmpGImg:image"u8);
                if (end > 0) text = rest[1..end];
            }
            else if (rest.Length > 1 && rest[0] == '=' && rest[1] is (byte)'"' or (byte)'\'')
            {
                var end = rest[2..].IndexOf(rest[1]);
                if (end > 0) text = rest.Slice(2, end);
            }
            if (text.Length is > 0 and <= MaxThumbnailText && Base64(text) is { } jpeg) return jpeg;
            var next = rest.IndexOf("xmpGImg:image"u8);
            at = next < 0 ? -1 : at + "xmpGImg:image"u8.Length + next;
        }
        return null;
    }

    /// <summary>Base64 with the line breaks XMP writes as "&amp;#xA;" (and plain white space) left out; null when it is not base64.</summary>
    private static byte[]? Base64(ReadOnlySpan<byte> text)
    {
        var clean = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '&')
            {
                var semicolon = text[i..].IndexOf((byte)';');
                if (semicolon < 0) return null;
                i += semicolon;
                continue;
            }
            if (text[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') continue;
            clean.Append((char)text[i]);
        }
        try
        {
            var bytes = Convert.FromBase64String(clean.ToString());
            return bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 ? bytes : null;
        }
        catch (FormatException) { return null; }
    }
}
