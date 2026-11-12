using System.IO.Compression;
using System.Security;
using System.Text;

namespace FastThumb.Tests.Fixtures;

/// <summary>Minimal, hand-written OOXML and OpenDocument packages: just the parts the readers need.</summary>
internal static class OfficeFiles
{
    private const string PackageRels = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string OfficeDocumentRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument";
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    // ---- DOCX ------------------------------------------------------------------------------------

    private const string DocxContentTypes =
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
          <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
          <Default Extension="xml" ContentType="application/xml"/>
          <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
        </Types>
        """;

    private static readonly string DocxRootRels =
        $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="{PackageRels}">
          <Relationship Id="rId1" Type="{OfficeDocumentRel}" Target="word/document.xml"/>
        </Relationships>
        """;

    private static readonly string DocxDocumentRels =
        $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="{PackageRels}"></Relationships>
        """;

    private static IEnumerable<(string, string)> DocxParts(string documentXml) =>
    [
        ("[Content_Types].xml", DocxContentTypes),
        ("_rels/.rels", DocxRootRels),
        ("word/_rels/document.xml.rels", DocxDocumentRels),
        ("word/document.xml", documentXml),
    ];

    private const string SectionLetter = """<w:sectPr><w:pgSz w:w="12240" w:h="15840"/><w:pgMar w:top="1440" w:right="1440" w:bottom="1440" w:left="1440"/></w:sectPr>""";

    public static string Paragraph(string text, bool bold = false, int halfPoints = 24) =>
        $"""<w:p><w:r><w:rPr>{(bold ? "<w:b/>" : "")}<w:sz w:val="{halfPoints}"/></w:rPr><w:t xml:space="preserve">{SecurityElement.Escape(text)}</w:t></w:r></w:p>""";

    private const string PageBreak = """<w:p><w:r><w:br w:type="page"/></w:r></w:p>""";

    public static string DocumentXml(string bodyContent, string? prolog = null) =>
        $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>{prolog}<w:document xmlns:w="{W}"><w:body>{bodyContent}{SectionLetter}</w:body></w:document>""";

    /// <summary>A US Letter document of <paramref name="pages"/> pages (explicit page breaks), each with a heading and text.</summary>
    public static byte[] Docx(int pages = 1)
    {
        var body = new StringBuilder();
        for (var page = 1; page <= pages; page++)
        {
            if (page > 1) body.Append(PageBreak);
            body.Append(Paragraph($"Page {page} heading", bold: true, halfPoints: 48));
            body.Append(Paragraph("The quick brown fox jumps over the lazy dog. 0123456789"));
        }
        return DocxFromDocumentXml(DocumentXml(body.ToString()));
    }

    /// <summary>A long document: <paramref name="paragraphs"/> paragraphs of text, many pages.</summary>
    public static byte[] LargeDocx(int paragraphs)
    {
        var body = new StringBuilder();
        for (var i = 0; i < paragraphs; i++)
            body.Append(Paragraph($"Paragraph {i}: the quick brown fox jumps over the lazy dog, again and again, to fill the line and wrap onto the next one."));
        return DocxFromDocumentXml(DocumentXml(body.ToString()));
    }

    public static byte[] DocxFromDocumentXml(string documentXml) => Zip.Build(DocxParts(documentXml).ToArray());

    /// <summary>A DOCX whose word/document.xml is written by <paramref name="write"/> (streamed: may decompress to gigabytes).</summary>
    public static byte[] DocxStreamed(Action<Stream> write) =>
        Zip.BuildStreamed(DocxParts("").Where(p => p.Item1 != "word/document.xml"), "word/document.xml", write);

    // ---- XLSX ------------------------------------------------------------------------------------

    /// <param name="workbookDoctype">A DOCTYPE declaration placed in front of the workbook's root element (hostile-input tests).</param>
    public static byte[] Xlsx(string? workbookDoctype = null)
    {
        const string S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        var rows = new StringBuilder();
        rows.Append("""<row r="1"><c r="A1" t="inlineStr"><is><t>Item</t></is></c><c r="B1" t="inlineStr"><is><t>Amount</t></is></c><c r="C1" t="s"><v>0</v></c></row>""");
        for (var r = 2; r <= 30; r++)
            rows.Append($"""<row r="{r}"><c r="A{r}" t="inlineStr"><is><t>Row {r}</t></is></c><c r="B{r}"><v>{r * 12.5}</v></c><c r="C{r}" t="s"><v>1</v></c></row>""");
        return Zip.Build(
            ("[Content_Types].xml",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Default Extension="xml" ContentType="application/xml"/>
                  <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
                  <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
                  <Override PartName="/xl/sharedStrings.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml"/>
                </Types>
                """),
            ("_rels/.rels",
                $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="{PackageRels}"><Relationship Id="rId1" Type="{OfficeDocumentRel}" Target="xl/workbook.xml"/></Relationships>
                """),
            ("xl/workbook.xml",
                $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                {workbookDoctype}<workbook xmlns="{S}" xmlns:r="{R}"><sheets><sheet name="Sheet1" sheetId="1" r:id="rId1"/></sheets></workbook>
                """),
            ("xl/_rels/workbook.xml.rels",
                $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="{PackageRels}">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                  <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings" Target="sharedStrings.xml"/>
                </Relationships>
                """),
            ("xl/sharedStrings.xml",
                $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <sst xmlns="{S}" count="2" uniqueCount="2"><si><t>Note</t></si><si><t>shared text</t></si></sst>
                """),
            ("xl/worksheets/sheet1.xml",
                $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="{S}"><sheetData>{rows}</sheetData></worksheet>"""));
    }

    // ---- PPTX ------------------------------------------------------------------------------------

    /// <summary>A 16:9 deck of <paramref name="slides"/> slides, each with a coloured background and a text box.</summary>
    public static byte[] Pptx(int slides = 2)
    {
        const string P = "http://schemas.openxmlformats.org/presentationml/2006/main";
        const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";
        const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        var types = new StringBuilder();
        var ids = new StringBuilder();
        var rels = new StringBuilder();
        var parts = new List<(string, string)>();
        for (var i = 1; i <= slides; i++)
        {
            types.Append($"""<Override PartName="/ppt/slides/slide{i}.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slide+xml"/>""");
            ids.Append($"""<p:sldId id="{255 + i}" r:id="rId{i}"/>""");
            rels.Append($"""<Relationship Id="rId{i}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide" Target="slides/slide{i}.xml"/>""");
            parts.Add(($"ppt/slides/slide{i}.xml",
                $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <p:sld xmlns:p="{P}" xmlns:a="{A}" xmlns:r="{R}">
                  <p:cSld>
                    <p:bg><p:bgPr><a:solidFill><a:srgbClr val="1F4E79"/></a:solidFill></p:bgPr></p:bg>
                    <p:spTree>
                      <p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>
                      <p:sp>
                        <p:nvSpPr><p:cNvPr id="2" name="Title"/><p:cNvSpPr txBox="1"/><p:nvPr/></p:nvSpPr>
                        <p:spPr><a:xfrm><a:off x="838200" y="1500000"/><a:ext cx="10515600" cy="2000000"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom>
                          <a:solidFill><a:srgbClr val="FFC000"/></a:solidFill></p:spPr>
                        <p:txBody><a:bodyPr/><a:p><a:r><a:rPr lang="en-US" sz="4400"><a:solidFill><a:srgbClr val="000000"/></a:solidFill></a:rPr><a:t>Slide {i}</a:t></a:r></a:p></p:txBody>
                      </p:sp>
                    </p:spTree>
                  </p:cSld>
                </p:sld>
                """));
        }
        parts.InsertRange(0,
        [
            ("[Content_Types].xml",
                $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Default Extension="xml" ContentType="application/xml"/>
                  <Override PartName="/ppt/presentation.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml"/>
                  {types}
                </Types>
                """),
            ("_rels/.rels",
                $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="{PackageRels}"><Relationship Id="rId1" Type="{OfficeDocumentRel}" Target="ppt/presentation.xml"/></Relationships>
                """),
            ("ppt/presentation.xml",
                $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <p:presentation xmlns:p="{P}" xmlns:a="{A}" xmlns:r="{R}">
                  <p:sldIdLst>{ids}</p:sldIdLst>
                  <p:sldSz cx="12192000" cy="6858000"/>
                  <p:notesSz cx="6858000" cy="9144000"/>
                </p:presentation>
                """),
            ("ppt/_rels/presentation.xml.rels",
                $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="{PackageRels}">{rels}</Relationships>"""),
        ]);
        return Zip.Build(parts.ToArray());
    }

    // ---- OpenDocument ----------------------------------------------------------------------------

    private const string OfficeNs =
        """xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0" xmlns:table="urn:oasis:names:tc:opendocument:xmlns:table:1.0" xmlns:style="urn:oasis:names:tc:opendocument:xmlns:style:1.0" xmlns:fo="urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0" office:version="1.3" """;

    private static byte[] OpenDocument(string mimeType, string content) =>
        Zip.Build(
            ("mimetype", Encoding.ASCII.GetBytes(mimeType), CompressionLevel.NoCompression),   // first and stored, as ODF requires
            ("META-INF/manifest.xml", Encoding.UTF8.GetBytes(
                $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <manifest:manifest xmlns:manifest="urn:oasis:names:tc:opendocument:xmlns:manifest:1.0" manifest:version="1.3">
                  <manifest:file-entry manifest:full-path="/" manifest:media-type="{mimeType}"/>
                  <manifest:file-entry manifest:full-path="content.xml" manifest:media-type="text/xml"/>
                </manifest:manifest>
                """), CompressionLevel.Optimal),
            ("content.xml", Encoding.UTF8.GetBytes(content), CompressionLevel.Optimal));

    public static byte[] Odt() => OpenDocument("application/vnd.oasis.opendocument.text",
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <office:document-content {OfficeNs}>
          <office:body><office:text>
            <text:h text:outline-level="1">OpenDocument heading</text:h>
            <text:p>The quick brown fox jumps over the lazy dog.</text:p>
            <text:p>Second paragraph with more text 0123456789.</text:p>
          </office:text></office:body>
        </office:document-content>
        """);

    public static byte[] Ods()
    {
        var rows = new StringBuilder();
        for (var r = 1; r <= 20; r++)
            rows.Append($"""<table:table-row><table:table-cell office:value-type="string"><text:p>Row {r}</text:p></table:table-cell><table:table-cell office:value-type="float" office:value="{r * 3}"><text:p>{r * 3}</text:p></table:table-cell></table:table-row>""");
        return OpenDocument("application/vnd.oasis.opendocument.spreadsheet",
            $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <office:document-content {OfficeNs}>
              <office:body><office:spreadsheet><table:table table:name="Sheet1">{rows}</table:table></office:spreadsheet></office:body>
            </office:document-content>
            """);
    }
}
