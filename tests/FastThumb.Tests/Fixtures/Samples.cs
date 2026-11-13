namespace FastThumb.Tests.Fixtures;

/// <summary>One generated input with what the library should report about it.</summary>
/// <param name="Name">Test case name.</param>
/// <param name="FileName">File name passed to the generator (plain-text formats need the extension).</param>
/// <param name="Format">Expected detected format.</param>
/// <param name="Pages">Expected page count, or null when it depends on layout (at least 1).</param>
/// <param name="Paginated">Whether a preview PDF is produced (documents laid out into pages).</param>
public sealed record Sample(string Name, string FileName, DocumentFormat Format, int? Pages, bool Paginated, Func<byte[]> Create)
{
    private readonly Lazy<byte[]> _bytes = new(Create);

    /// <summary>The bytes, generated once per test run (callers must not modify them).</summary>
    public byte[] Bytes => _bytes.Value;

    public override string ToString() => Name;
}

internal static class Samples
{
    public static readonly Sample Docx = new("docx", "test.docx", DocumentFormat.Docx, 1, true, () => OfficeFiles.Docx(1));
    public static readonly Sample Docx3 = new("docx-3-pages", "test3.docx", DocumentFormat.Docx, 3, true, () => OfficeFiles.Docx(3));
    public static readonly Sample Xlsx = new("xlsx", "test.xlsx", DocumentFormat.Xlsx, null, true, () => OfficeFiles.Xlsx());
    public static readonly Sample Pptx = new("pptx", "test.pptx", DocumentFormat.Pptx, 2, true, () => OfficeFiles.Pptx(2));
    public static readonly Sample Odt = new("odt", "test.odt", DocumentFormat.Odt, 1, true, OfficeFiles.Odt);
    public static readonly Sample Ods = new("ods", "test.ods", DocumentFormat.Ods, null, true, OfficeFiles.Ods);
    public static readonly Sample Rtf = new("rtf", "test.rtf", DocumentFormat.Rtf, 1, true, TextFiles.Rtf);
    public static readonly Sample Html = new("html", "test.html", DocumentFormat.Html, 1, true, TextFiles.Html);
    public static readonly Sample Csv = new("csv", "test.csv", DocumentFormat.Csv, null, true, TextFiles.Csv);
    public static readonly Sample Markdown = new("markdown", "test.md", DocumentFormat.Markdown, 1, true, TextFiles.Markdown);
    public static readonly Sample Text = new("text", "test.txt", DocumentFormat.Text, 1, true, TextFiles.PlainText);
    public static readonly Sample Pdf = new("pdf", "test.pdf", DocumentFormat.Pdf, 1, false, () => PdfFiles.Pdf(1));
    public static readonly Sample Pdf3 = new("pdf-3-pages", "test3.pdf", DocumentFormat.Pdf, 3, false, () => PdfFiles.Pdf(3));
    public static readonly Sample Png = new("png", "test.png", DocumentFormat.Image, 1, false, () => ImageFiles.Png());
    public static readonly Sample Jpeg = new("jpeg", "test.jpg", DocumentFormat.Image, 1, false, () => ImageFiles.Jpeg());
    public static readonly Sample WebP = new("webp", "test.webp", DocumentFormat.Image, 1, false, () => ImageFiles.WebP());
    public static readonly Sample Tga = new("tga", "test.tga", DocumentFormat.Image, 1, false, () => RasterFiles.Tga());
    public static readonly Sample Svg = new("svg", "test.svg", DocumentFormat.Image, 1, false, () => System.Text.Encoding.UTF8.GetBytes(TextFiles.Svg()));

    public static IReadOnlyList<Sample> All { get; } =
        [Docx, Docx3, Xlsx, Pptx, Odt, Ods, Rtf, Html, Csv, Markdown, Text, Pdf, Pdf3, Png, Jpeg, WebP, Svg, Tga];

    public static IReadOnlyList<Sample> Documents { get; } = All.Where(s => s.Paginated).ToList();

    public static IEnumerable<object[]> AllData => All.Select(s => new object[] { s.Name });

    public static IEnumerable<object[]> DocumentData => Documents.Select(s => new object[] { s.Name });

    public static IEnumerable<object[]> RasterImageData => new[] { Png, Jpeg, WebP, Tga }.Select(s => new object[] { s.Name });

    /// <summary>Theories take the name (a stable, serialisable test case id) and look the sample up.</summary>
    public static Sample Get(string name) => All.Single(s => s.Name == name);
}
