using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace FastThumb.Tests.Fixtures;

/// <summary>Drawing formats written by hand: Illustrator (PostScript and PDF based), OpenDocument drawings, CorelDRAW, SVGZ.</summary>
internal static class DrawingFiles
{
    /// <summary>
    /// A PostScript-based Illustrator file of a 200 x 100 pt page, with no preview. Drawn: the left half red (CMYK fill), a
    /// blue square from (120, 20) to (180, 80) pt with a square hole from (140, 40) to (160, 60) (compound path, RGB fill),
    /// and a stroked green line. Not drawn: a hidden layer filled black, the prolog, and a guide.
    /// </summary>
    public static byte[] IllustratorPaths(bool eps = false) => Encoding.ASCII.GetBytes(
        $"%!PS-Adobe-3.0{(eps ? " EPSF-3.0" : "")}\n%%Creator: Adobe Illustrator(R) 8.0\n%%BoundingBox: 0 0 200 100\n%%EndComments\n" +
        "%%BeginProlog\n0 0 0 1 k 0 0 m 200 0 l 200 100 l 0 100 l f\n%%EndProlog\n" +
        "%%BeginSetup\n0 0 0 1 k 0 0 m 200 0 l 200 100 l 0 100 l f\n%%EndSetup\n" +
        "%AI5_BeginLayer\n1 1 1 1 0 0 1 255 79 79 0 50 Lb\n(Layer 1) Ln\n" +
        "0 1 1 0 k\n0 0 m\n100 0 l\n100 100 l\n0 100 l\nf\n" +
        "0 0 1 Xa\n*u\n120 20 m\n180 20 l\n180 80 l\n120 80 l\nf\n140 40 m\n160 40 l\n160 60 l\n140 60 l\nf\n*U\n" +
        "0 1 0 XA 4 w\n100 95 m\n200 95 L\nS\n" +
        "0 0 0 1 k 190 0 m 200 0 l 200 10 l *f\n" +
        "LB\n%AI5_EndLayer--\n" +
        "%AI5_BeginLayer\n0 1 1 1 0 0 1 255 79 79 0 50 Lb\n(Hidden) Ln\n0 0 0 1 k\n0 0 m\n200 0 l\n200 100 l\n0 100 l\nf\nLB\n%AI5_EndLayer--\n" +
        "%%PageTrailer\n%%Trailer\n%%EOF\n");

    /// <summary>A PostScript-based Illustrator file whose only content is <paramref name="points"/> line segments.</summary>
    public static byte[] IllustratorManyPoints(int points)
    {
        var text = new StringBuilder("%!PS-Adobe-3.0\n%%Creator: Adobe Illustrator(R) 8.0\n%%BoundingBox: 0 0 100 100\n%%EndComments\n0 0 m\n");
        for (var i = 0; i < points; i++) text.Append("1 1 l\n");
        text.Append("S\n%%EOF\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }

    /// <summary>
    /// A PDF-based Illustrator file saved without PDF content: Illustrator's private data, its notice as the page text, and
    /// (unless <paramref name="thumbnail"/> is false) a 64 x 40 JPEG thumbnail in the XMP metadata.
    /// </summary>
    public static byte[] IllustratorWithoutPdfContent(bool thumbnail = true)
    {
        var jpeg = Convert.ToBase64String(ImageFiles.Jpeg(64, 40));
        // XMP writes line breaks inside the base64 as character references
        var wrapped = string.Join("&#xA;", Enumerable.Range(0, (jpeg.Length + 75) / 76).Select(i => jpeg.Substring(i * 76, Math.Min(76, jpeg.Length - i * 76))));
        var xmp = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
            "<rdf:Description xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmlns:xmpGImg=\"http://ns.adobe.com/xap/1.0/g/img/\"><xmp:Thumbnails><rdf:Alt><rdf:li>" +
            (thumbnail ? $"<xmpGImg:width>64</xmpGImg:width><xmpGImg:height>40</xmpGImg:height><xmpGImg:format>JPEG</xmpGImg:format><xmpGImg:image>{wrapped}</xmpGImg:image>" : "") +
            "</rdf:li></rdf:Alt></xmp:Thumbnails></rdf:Description></rdf:RDF></x:xmpmeta>";
        const string notice = "This is an Adobe Illustrator File that was saved without PDF Content.";
        var content = $"BT /F1 12 Tf 20 700 Td ({notice}) Tj ET\n";
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Metadata 6 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Contents 5 0 R " +
                "/PieceInfo << /Illustrator << /Private << /AIPrivateData1 7 0 R >> >> >> >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream",
            $"<< /Type /Metadata /Subtype /XML /Length {Encoding.ASCII.GetByteCount(xmp)} >>\nstream\n{xmp}\nendstream",
            "<< /Length 8 >>\nstream\n%AI12_ x\nendstream",
        };
        return PdfFiles.Write(objects);
    }

    /// <summary>An OpenDocument drawing (or its flat XML form) of a 200 x 100 mm page with one blue rectangle and a line of text.</summary>
    public static byte[] Odg(bool flat = false)
    {
        const string ns = "xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:style=\"urn:oasis:names:tc:opendocument:xmlns:style:1.0\" " +
            "xmlns:draw=\"urn:oasis:names:tc:opendocument:xmlns:drawing:1.0\" xmlns:svg=\"urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0\" " +
            "xmlns:fo=\"urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0\" xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\"";
        const string styles = "<office:automatic-styles><style:page-layout style:name=\"PM1\"><style:page-layout-properties fo:page-width=\"200mm\" fo:page-height=\"100mm\"/></style:page-layout>" +
            "<style:style style:name=\"gr1\" style:family=\"graphic\"><style:graphic-properties draw:fill=\"solid\" draw:fill-color=\"#0000ff\"/></style:style></office:automatic-styles>";
        const string body = "<office:body><office:drawing><draw:page draw:name=\"page1\">" +
            "<draw:rect draw:style-name=\"gr1\" svg:x=\"10mm\" svg:y=\"10mm\" svg:width=\"80mm\" svg:height=\"50mm\"/>" +
            "<draw:frame svg:x=\"100mm\" svg:y=\"10mm\" svg:width=\"90mm\" svg:height=\"20mm\"><draw:text-box><text:p>Drawing</text:p></draw:text-box></draw:frame>" +
            "</draw:page></office:drawing></office:body>";
        if (flat)
            return Encoding.UTF8.GetBytes($"<?xml version=\"1.0\"?><office:document {ns} office:mimetype=\"application/vnd.oasis.opendocument.graphics\">{styles}{body}</office:document>");
        return Zip.Build(
            ("mimetype", "application/vnd.oasis.opendocument.graphics"),
            ("styles.xml", $"<?xml version=\"1.0\"?><office:document-styles {ns}>{styles}</office:document-styles>"),
            ("content.xml", $"<?xml version=\"1.0\"?><office:document-content {ns}>{body}</office:document-content>"));
    }

    /// <summary>A gzip-compressed SVG of a 120 x 80 px red rectangle.</summary>
    public static byte[] Svgz()
    {
        var svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"120\" height=\"80\"><rect width=\"120\" height=\"80\" fill=\"#ff0000\"/></svg>"u8.ToArray();
        return Gzip(svg);
    }

    /// <summary>A gzip stream of <paramref name="size"/> zero bytes, a few kilobytes on disk.</summary>
    public static byte[] GzipOfZeros(long size)
    {
        var output = new MemoryStream();
#if NETFRAMEWORK
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
#else
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
#endif
        {
            var block = new byte[1 << 20];
            for (long written = 0; written < size; written += block.Length) gzip.Write(block, 0, (int)Math.Min(block.Length, size - written));
        }
        return output.ToArray();
    }

    private static byte[] Gzip(byte[] data)
    {
        var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true)) gzip.Write(data);
        return output.ToArray();
    }

    /// <summary>
    /// A RIFF CorelDRAW file whose DISP chunk (inside a LIST, after 4 bytes of its own) holds the test picture as a 24-bit
    /// device-independent bitmap; without <paramref name="preview"/> the chunk is left out.
    /// </summary>
    public static byte[] CdrRiff(int width = 64, int height = 40, bool preview = true)
    {
        var bmp = BmpFiles.Bmp(BmpKind.Rgb24, width, height);
        var disp = new byte[4 + bmp.Length - 14];
        bmp.AsSpan(14).CopyTo(disp.AsSpan(4));
        var chunks = new MemoryStream();
        void Chunk(MemoryStream to, string id, byte[] body)
        {
            to.Write(Encoding.ASCII.GetBytes(id));
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)body.Length);
            to.Write(b);
            to.Write(body);
            if (body.Length % 2 == 1) to.WriteByte(0);
        }
        Chunk(chunks, "vrsn", [0x4C, 0x04]);
        var list = new MemoryStream();
        list.Write("stlt"u8);
        if (preview) Chunk(list, "DISP", disp);
        Chunk(list, "mcfg", new byte[16]);
        Chunk(chunks, "LIST", list.ToArray());
        var body = chunks.ToArray();
        var file = new byte[12 + body.Length];
        "RIFF"u8.CopyTo(file);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), (uint)(4 + body.Length));
        "CDRC"u8.CopyTo(file.AsSpan(8));
        body.CopyTo(file, 12);
        return file;
    }

    /// <summary>A CorelDRAW X4+ package: the RIFF document inside, and (unless left out) a PNG preview of the test picture.</summary>
    public static byte[] CdrPackage(bool thumbnail = true)
    {
        var parts = new List<(string, byte[], CompressionLevel)> { ("content/riffData.cdr", CdrRiff(32, 20), CompressionLevel.Optimal) };
        if (thumbnail) parts.Add(("previews/thumbnail.png", ImageFiles.Png(96, 60), CompressionLevel.NoCompression));
        return Zip.Build([.. parts]);
    }
}
