using System.Globalization;
using System.Text;

namespace FastThumb.Tests.Fixtures;

/// <summary>Minimal PDFs written by hand, with a correct cross-reference table.</summary>
internal static class PdfFiles
{
    /// <summary>A PDF of <paramref name="pages"/> pages of the given size in points, each with a filled box and a line of text.</summary>
    /// <param name="info">Body of the document information dictionary (for example "/Title (Report)"), or null for none.</param>
    public static byte[] Pdf(int pages = 1, double width = 612, double height = 792, string? info = null)
    {
        // objects: 1 catalog, 2 pages, 3 font, then per page: page object and its content stream
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "", // pages, filled in below
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        var kids = new List<string>();
        var w = width.ToString(CultureInfo.InvariantCulture);
        var h = height.ToString(CultureInfo.InvariantCulture);
        for (var i = 0; i < pages; i++)
        {
            var pageId = objects.Count + 1;
            var content = $"0.2 0.4 0.8 rg 36 36 200 100 re f\nBT /F1 24 Tf 50 {(height - 80).ToString(CultureInfo.InvariantCulture)} Td (Page {i + 1}) Tj ET\n";
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {w} {h}] /Resources << /Font << /F1 3 0 R >> >> /Contents {pageId + 1} 0 R >>");
            objects.Add($"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream");
            kids.Add($"{pageId} 0 R");
        }
        objects[1] = $"<< /Type /Pages /Kids [{string.Join(' ', kids)}] /Count {pages} >>";
        if (info == null) return Write(objects);
        objects.Add($"<< {info} >>");
        return Write(objects, objects.Count);
    }

    /// <summary>A one-page Letter PDF whose page is slow to draw: <paramref name="strokes"/> stroked lines across it.</summary>
    public static byte[] SlowPdf(int strokes)
    {
        var content = new StringBuilder("0.5 w\n");
        var random = new Random(1);
        for (var i = 0; i < strokes; i++)
            content.Append(CultureInfo.InvariantCulture, $"{random.Next(612)} {random.Next(792)} m {random.Next(612)} {random.Next(792)} l S\n");
        var text = content.ToString();
        return Write([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(text)} >>\nstream\n{text}endstream",
        ]);
    }

    internal static byte[] Write(List<string> objects, int? infoId = null)
    {
        var output = new StringBuilder("%PDF-1.4\n%âãÏÓ\n");
        var offsets = new List<int>();
        foreach (var (body, index) in objects.Select((b, i) => (b, i)))
        {
            offsets.Add(Encoding.Latin1.GetByteCount(output.ToString()));
            output.Append($"{index + 1} 0 obj\n{body}\nendobj\n");
        }
        var xref = Encoding.Latin1.GetByteCount(output.ToString());
        output.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) output.Append($"{offset:D10} 00000 n \n");
        output.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R{(infoId is { } id ? $" /Info {id} 0 R" : "")} >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(output.ToString());
    }
}
