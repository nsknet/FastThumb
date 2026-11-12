using System.Net;
using System.Net.Sockets;
using System.Text;
using FastThumb.Tests.Fixtures;

namespace FastThumb.Tests;

/// <summary>
/// Hostile inputs: each must end in the documented exception type (or succeed within the limits) inside a time bound,
/// never exhaust memory, hang or surface an internal error.
/// </summary>
public class SecurityTests
{
    private static readonly ThumbnailOptions Options = ThumbnailOptions.Default with { Sizes = [200] };
    private static readonly ThumbnailGenerator Generator = new(Options);
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);   // generous: a loaded CI machine must not make these flaky

    // ---- decompression and nesting ---------------------------------------------------------------

    [Fact]
    public void Zip_bomb_is_refused()
    {
        var bomb = HostileFiles.ZipBombDocx(megabytes: 300);
        Assert.True(bomb.Length < 2 << 20, $"the bomb itself is {bomb.Length:N0} bytes");
        Assert.Equal(DocumentFormat.Docx, ThumbnailGenerator.DetectFormat(bomb));
        TestUtil.Throws<ResourceLimitExceededException>(Limit, () => Generator.Generate(bomb, "bomb.docx"));
    }

    [Fact]
    public void Zip_bomb_whose_headers_declare_a_small_size_is_refused()
    {
        const int megabytes = 300;
        var bomb = HostileFiles.ZipBombDocx(megabytes);
        // the uncompressed size in the local and central headers: 300 MB + the XML around it, rewritten to 1 KB
        var realSize = Zip.Read(bomb).Single(p => p.Name == "word/document.xml").Content.Length;
        Assert.True(realSize > megabytes << 20);
        Assert.InRange(Zip.Patch(bomb, (uint)realSize, 1024), 1, 2);
        // refused by the limit, or by the ZIP reader noticing the mismatch: in both cases as a damaged document
        TestUtil.Throws<InvalidDocumentException>(Limit, () => Generator.Generate(bomb, "bomb.docx"));
    }

    [Fact]
    public void Deeply_nested_xml_is_refused()
    {
        var deep = HostileFiles.DeepXmlDocx(levels: 100_000);
        TestUtil.Throws<ResourceLimitExceededException>(Limit, () => Generator.Generate(deep, "deep.docx"));
    }

    [Fact]
    public void Deeply_nested_tables_are_refused_or_rendered()
    {
        // 60 tables in cells in tables: 180 elements deep, below the XML depth limit, but reader and layout recurse per table
        var body = new StringBuilder();
        for (var i = 0; i < 60; i++) body.Append("<w:tbl><w:tr><w:tc>");
        body.Append(OfficeFiles.Paragraph("deep"));
        for (var i = 0; i < 60; i++) body.Append("<w:p/></w:tc></w:tr></w:tbl>");
        var docx = OfficeFiles.DocxFromDocumentXml(OfficeFiles.DocumentXml(body.ToString()));
        var error = TestUtil.Within(Limit, () => Generator.Generate(docx, "tables.docx"));
        if (error != null) Assert.IsAssignableFrom<ThumbnailException>(error);
    }

    // ---- XML entities ----------------------------------------------------------------------------

    [Fact]
    public void Billion_laughs_is_refused()
    {
        var docx = HostileFiles.BillionLaughsDocx();
        var error = TestUtil.Throws<InvalidDocumentException>(Limit, () => Generator.Generate(docx, "laughs.docx"));
        Assert.True(error.Message.Length < 10_000, "the entity was expanded into the message");
    }

    [Fact]
    public void External_entity_is_refused_and_the_file_is_not_read()
    {
        const string secret = "FASTTHUMB-SECRET-7f3a9c";
        var path = Path.Combine(Path.GetTempPath(), $"fastthumb-secret-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, secret);
        try
        {
            var docx = HostileFiles.ExternalEntityDocx(path);
            // had the entity been resolved, the secret would be drawn on the page and written into the preview PDF as text
            var error = TestUtil.Throws<InvalidDocumentException>(Limit, () => Generator.Generate(docx, "xxe.docx"));
            for (Exception? e = error; e != null; e = e.InnerException) Assert.DoesNotContain(secret, e.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void External_entity_is_never_fetched_over_the_network()
    {
        // a listener on the loopback interface: resolving the entity or the external DTD would connect to it
        using var listener = new LoopbackListener();
        var entity = $"<!DOCTYPE w:document [<!ENTITY xxe SYSTEM \"{listener.Url}secret.txt\">]>";
        var externalDtd = $"<!DOCTYPE w:document SYSTEM \"{listener.Url}evil.dtd\">";
        var parameterEntity = $"<!DOCTYPE w:document [<!ENTITY % remote SYSTEM \"{listener.Url}evil.dtd\"> %remote;]>";

        foreach (var doctype in new[] { entity, externalDtd, parameterEntity })
        {
            var text = doctype == entity ? OfficeFiles.Paragraph("&xxe;").Replace("&amp;xxe;", "&xxe;") : OfficeFiles.Paragraph("text");
            var docx = OfficeFiles.DocxFromDocumentXml(OfficeFiles.DocumentXml(text, doctype));
            TestUtil.Throws<InvalidDocumentException>(Limit, () => Generator.Generate(docx, "xxe.docx"));
        }

        // a part read with the DTD ignored: the document is rendered, and still nothing is fetched
        var xlsx = OfficeFiles.Xlsx(workbookDoctype: $"<!DOCTYPE workbook SYSTEM \"{listener.Url}evil.dtd\">");
        var error = TestUtil.Within(Limit, () => Generator.Generate(xlsx, "xxe.xlsx"));
        if (error != null) Assert.IsAssignableFrom<InvalidDocumentException>(error);

        Assert.False(listener.WasContacted, "the library connected to the URL of an external entity");
    }

    private sealed class LoopbackListener : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        public LoopbackListener() => _listener.Start();

        public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";

        /// <summary>Whether anything connected (a connection waits in the backlog until it is accepted).</summary>
        public bool WasContacted => _listener.Pending();

        public void Dispose() => _listener.Stop();
    }

    // ---- declared sizes --------------------------------------------------------------------------

    [Fact]
    public void Svg_declaring_a_huge_size_is_refused()
    {
        var svg = Encoding.UTF8.GetBytes(TextFiles.Svg(60000, 60000));
        TestUtil.Throws<ResourceLimitExceededException>(Limit, () => Generator.Generate(svg, "huge.svg"));
    }

    [Fact]
    public void Png_header_declaring_a_huge_size_is_refused()
    {
        var png = ImageFiles.PngHeaderOnly(30000, 30000);
        Assert.True(png.Length < 200);
        Assert.Equal(DocumentFormat.Image, ThumbnailGenerator.DetectFormat(png));
        TestUtil.Throws<ResourceLimitExceededException>(Limit, () => Generator.Generate(png, "huge.png"));
    }

    [Fact]
    public void Image_larger_than_max_pixels_is_refused_and_one_within_it_is_not()
    {
        var png = Samples.Png.Bytes;   // 320 x 200 = 64000 px
        var tight = new ThumbnailGenerator(Options with { Limits = new ResourceLimits { MaxPixels = 63_999 } });
        Assert.Throws<ResourceLimitExceededException>(() => tight.Generate(png));
        var exact = new ThumbnailGenerator(Options with { Limits = new ResourceLimits { MaxPixels = 64_000 } });
        Assert.Equal(2, exact.Generate(png).Images.Count);
    }

    [Fact]
    public void Pdf_with_a_huge_page_is_scaled_down_to_max_pixels()
    {
        const long maxPixels = 1_000_000;
        var pdf = PdfFiles.Pdf(pages: 1, width: 200_000, height: 200_000);
        var generator = new ThumbnailGenerator(Options with { Limits = new ResourceLimits { MaxPixels = maxPixels } });
        ThumbnailResult? result = null;
        Assert.Null(TestUtil.Within(Limit, () => result = generator.Generate(pdf, "huge.pdf")));
        Assert.Equal(1, result!.PageCount);
        var full = result.FullSize!;
        Assert.True((long)full.Width * full.Height <= maxPixels, $"full size is {full.Width} x {full.Height}");
        Assert.Equal(full.Width, full.Height);
        Assert.Equal((full.Width, full.Height), ImageFiles.Size(full.Data));
        Assert.Equal(200, Math.Max(result.Find(200)!.Width, result.Find(200)!.Height));
    }

    [Fact]
    public void Document_with_a_huge_page_is_scaled_down_to_max_pixels()
    {
        const long maxPixels = 1_000_000;
        // 31680 twips = 22 in, the largest page Word allows; at 96 DPI that is 2112 px a side
        var xml = OfficeFiles.DocumentXml(OfficeFiles.Paragraph("big page")).Replace("w:w=\"12240\" w:h=\"15840\"", "w:w=\"31680\" w:h=\"31680\"");
        var generator = new ThumbnailGenerator(Options with { Limits = new ResourceLimits { MaxPixels = maxPixels } });
        var result = generator.Generate(OfficeFiles.DocxFromDocumentXml(xml), "big.docx");
        var full = result.FullSize!;
        Assert.True((long)full.Width * full.Height <= maxPixels, $"full size is {full.Width} x {full.Height}");
        Assert.Equal(1000, full.Width);
    }

    // ---- input size ------------------------------------------------------------------------------

    [Fact]
    public async Task Input_larger_than_max_input_bytes_is_refused_on_every_path()
    {
        var data = Samples.Docx.Bytes;
        var generator = new ThumbnailGenerator(Options with { Limits = new ResourceLimits { MaxInputBytes = data.Length - 1 } });

        Assert.Throws<ResourceLimitExceededException>(() => generator.Generate(data, "a.docx"));
        Assert.Throws<ResourceLimitExceededException>(() => generator.Generate(new MemoryStream(data, false), "a.docx"));
        Assert.Throws<ResourceLimitExceededException>(() => generator.Generate(new NonSeekableStream(new MemoryStream(data, false)), "a.docx"));
        await Assert.ThrowsAsync<ResourceLimitExceededException>(() => generator.GenerateAsync(new MemoryStream(data, false), "a.docx"));
        await Assert.ThrowsAsync<ResourceLimitExceededException>(() => generator.GenerateAsync(new NonSeekableStream(new MemoryStream(data, false)), "a.docx"));

        var path = Path.Combine(Path.GetTempPath(), $"fastthumb-{Guid.NewGuid():N}.docx");
        File.WriteAllBytes(path, data);
        try { Assert.Throws<ResourceLimitExceededException>(() => generator.GenerateFromFile(path)); }
        finally { File.Delete(path); }

        // exactly at the limit is accepted
        var exact = new ThumbnailGenerator(Options with { Limits = new ResourceLimits { MaxInputBytes = data.Length } });
        Assert.Equal(1, exact.Generate(data, "a.docx").PageCount);
        Assert.Equal(1, exact.Generate(new NonSeekableStream(new MemoryStream(data, false)), "a.docx").PageCount);
    }

    // ---- garbage ---------------------------------------------------------------------------------

    public static IEnumerable<object[]> Signatures()
    {
        object[] Case(string name, params byte[] signature) => [name, signature];
        yield return Case("pdf", "%PDF-1.7\n"u8.ToArray());
        yield return Case("zip", (byte)'P', (byte)'K', 3, 4);
        yield return Case("compound file", 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1);
        yield return Case("png", 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A);
        yield return Case("jpeg", 0xFF, 0xD8, 0xFF, 0xE0);
        yield return Case("gif", "GIF89a"u8.ToArray());
        yield return Case("tiff", (byte)'I', (byte)'I', 42, 0);
        yield return Case("webp", "RIFF\0\0\u0001\0WEBPVP8 "u8.ToArray());
        yield return Case("chm", "ITSF"u8.ToArray());
        yield return Case("wordperfect", 0xFF, (byte)'W', (byte)'P', (byte)'C');
    }

    [Theory]
    [MemberData(nameof(Signatures))]
    public void Garbage_behind_a_known_signature_is_reported_as_unsupported_or_damaged(string name, byte[] signature)
    {
        for (var seed = 1; seed <= 8; seed++)
        {
            var data = HostileFiles.Garbage(seed, 200 + seed * 3000);
            signature.CopyTo(data, 0);
            var error = TestUtil.Within(Limit, () => Generator.Generate(data));
            // a text-only legacy format may still yield some text; anything thrown must be one of the two documented types
            if (error == null) Assert.True(name is "chm" or "wordperfect", $"{name}, seed {seed}: garbage was accepted");
            else Assert.True(error is UnsupportedFormatException or InvalidDocumentException, $"{name}, seed {seed}: {error.GetType().Name}: {error.Message}");
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("garbage.bin")]
    [InlineData("garbage.docx")]
    [InlineData("garbage.xlsx")]
    [InlineData("garbage.pdf")]
    [InlineData("garbage.csv")]
    [InlineData("garbage.html")]
    [InlineData("garbage.md")]
    [InlineData("garbage.rtf")]
    [InlineData("garbage.svg")]
    [InlineData("garbage.bmp")]
    [InlineData("garbage.ico")]
    public void Random_bytes_never_surface_an_internal_error(string? fileName)
    {
        foreach (var length in new[] { 0, 1, 11, 12, 100, 4096, 20_000 })
            for (var seed = 1; seed <= 2; seed++)
            {
                var data = HostileFiles.Garbage(seed * 31 + length, length);
                // without a signature the bytes are text unless the name says otherwise: rendered as such, or refused
                var error = TestUtil.Within(Limit, () => Generator.Generate(data, fileName));
                Assert.True(error is null or UnsupportedFormatException or InvalidDocumentException,
                    $"{fileName ?? "(no name)"}, {length} bytes, seed {seed * 31 + length}: {error?.GetType().Name}: {error?.Message}");
            }
    }

    [Theory]
    [InlineData("garbage.svg")]
    [InlineData("garbage.bmp")]
    [InlineData("garbage.ico")]
    public void Random_bytes_named_as_an_image_are_refused(string fileName)
    {
        var data = HostileFiles.Garbage(7, 5000);
        var error = TestUtil.Within(Limit, () => Generator.Generate(data, fileName));
        Assert.True(error is UnsupportedFormatException or InvalidDocumentException, $"{error?.GetType().Name}: {error?.Message}");
    }

    [Fact]
    public void Truncated_documents_are_reported_as_damaged_or_rendered()
    {
        foreach (var sample in Samples.All)
            foreach (var keep in new[] { 0.1, 0.5, 0.9, 0.99 })
            {
                var data = sample.Bytes[..(int)(sample.Bytes.Length * keep)];
                var error = TestUtil.Within(Limit, () => Generator.Generate(data, sample.FileName));
                Assert.True(error is null or ThumbnailException, $"{sample.Name} cut to {keep:P0}: {error?.GetType().Name}: {error?.Message}");
            }
    }

    // ---- pathological structure ------------------------------------------------------------------

    [Fact]
    public void Rtf_with_a_million_open_groups_ends_in_time()
    {
        var rtf = HostileFiles.DeepRtf(1_000_000);
        Assert.Equal(DocumentFormat.Rtf, ThumbnailGenerator.DetectFormat(rtf));
        var error = TestUtil.Within(Limit, () => Generator.Generate(rtf, "deep.rtf"));
        if (error != null) Assert.IsAssignableFrom<ThumbnailException>(error);
    }

    [Fact]
    public void Html_with_deep_nesting_ends_in_time()
    {
        var html = new StringBuilder("<html><body>");
        for (var i = 0; i < 100_000; i++) html.Append("<div>");
        html.Append("text");
        var error = TestUtil.Within(Limit, () => Generator.Generate(Encoding.UTF8.GetBytes(html.ToString()), "deep.html"));
        if (error != null) Assert.IsAssignableFrom<ThumbnailException>(error);
    }

    [Fact]
    public void Compound_file_claiming_a_huge_fat_is_refused()
    {
        var doc = HostileFiles.CompoundFileHugeFat();
        TestUtil.Throws<InvalidDocumentException>(Limit, () => Generator.Generate(doc, "huge.doc"));
    }

    [Fact]
    public void Compound_file_with_a_looping_sector_chain_is_refused()
    {
        // one FAT sector (sector 0) whose entry for the directory sector (1) points back at itself
        var doc = HostileFiles.CompoundFileHugeFat();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(doc.AsSpan(44), 1);          // one FAT sector
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(doc.AsSpan(512), 0xFFFFFFFD);  // sector 0: the FAT itself
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(doc.AsSpan(512 + 4), 1);       // sector 1 -> sector 1
        TestUtil.Throws<InvalidDocumentException>(Limit, () => Generator.Generate(doc, "loop.doc"));
    }

    [Fact]
    public void Zip_that_is_not_a_document_is_unsupported()
    {
        var zip = Zip.Build(("readme.txt", "just a file"));
        Assert.Equal(DocumentFormat.Unknown, ThumbnailGenerator.DetectFormat(zip));
        TestUtil.Throws<UnsupportedFormatException>(Limit, () => Generator.Generate(zip, "archive.docx"));
    }

    [Fact]
    public void Package_without_its_main_part_content_is_damaged()
    {
        var empty = OfficeFiles.DocxFromDocumentXml("");
        TestUtil.Throws<InvalidDocumentException>(Limit, () => Generator.Generate(empty, "empty.docx"));
        var notXml = OfficeFiles.DocxFromDocumentXml("this is not XML <<<");
        TestUtil.Throws<InvalidDocumentException>(Limit, () => Generator.Generate(notXml, "bad.docx"));
    }

    [Fact]
    public void Spreadsheet_claiming_a_cell_at_the_last_row_and_column_ends_in_time()
    {
        var xlsx = Zip.Read(OfficeFiles.Xlsx());
        var parts = xlsx.Select(p => p.Name == "xl/worksheets/sheet1.xml"
            ? (p.Name, Encoding.UTF8.GetString(p.Content).Replace("</sheetData>", "<row r=\"1048576\"><c r=\"XFD1048576\"><v>1</v></c></row></sheetData>"))
            : (p.Name, Encoding.UTF8.GetString(p.Content))).ToArray();
        var error = TestUtil.Within(Limit, () => Generator.Generate(Zip.Build(parts), "far.xlsx"));
        if (error != null) Assert.IsAssignableFrom<ThumbnailException>(error);
    }

    // ---- fuzz ------------------------------------------------------------------------------------

    private static readonly ThumbnailGenerator FuzzGenerator = new(ThumbnailOptions.Default with
    {
        Sizes = [64],
        IncludeFullSize = false,
        EncodeInParallel = false,
        PngCompressionLevel = 0,
        Limits = new ResourceLimits { MaxPixels = 4_000_000 },
    });

    private const int Mutants = 50, FlipsPerMutant = 4;   // 200 seeded positions per document

    private static bool Documented(Exception? e) => e is null or ThumbnailException or OperationCanceledException or TimeoutException;

    /// <summary>The mutants of one document whose error is not a documented one, as "seed N: Type: message".</summary>
    private static List<string> Fuzz(string name, Func<Random, byte[]> mutate)
    {
        var sample = Samples.Get(name);
        var failures = new List<string>();
        var error = TestUtil.Within(TimeSpan.FromSeconds(60), () =>
        {
            for (var seed = 0; seed < Mutants; seed++)
            {
                var data = mutate(new Random(unchecked(StableHash(name) * 397 + seed)));
                try { FuzzGenerator.Generate(data, sample.FileName); }
                catch (Exception e) when (!Documented(e)) { failures.Add($"seed {seed}: {e.GetType().FullName}: {e.Message.Split('\n')[0]}"); }
                catch (Exception) { }
            }
        });
        Assert.Null(error);
        return failures;
    }

    /// <summary>string.GetHashCode is randomised per process; the seeds must be the same on every run.</summary>
    private static int StableHash(string text) => text.Aggregate(17, (hash, c) => unchecked(hash * 31 + c));

    /// <summary>libvips decodes lazily: damage in the pixel data only shows when the pixels are read, after the header was accepted.</summary>
    [Fact]
    public void Damaged_image_pixel_data_is_reported_as_damaged()
    {
        var webp = ImageFiles.WebP();
        webp[webp.Length / 2] ^= 0xFF;
        var error = TestUtil.Within(Limit, () => Generator.Generate(webp, "damaged.webp"));
        Assert.True(error is UnsupportedFormatException or InvalidDocumentException, $"{error?.GetType().FullName}: {error?.Message}");
    }

    /// <summary>
    /// An extra-field length in the ZIP central directory that runs past the end of the file: .NET 8 reports it as an
    /// IOException, later runtimes as InvalidDataException. Both must surface as a damaged document.
    /// </summary>
    [Fact]
    public void Damaged_zip_directory_is_reported_as_damaged()
    {
        var odt = OfficeFiles.Odt();
        var lastHeader = odt.AsSpan().LastIndexOf(new byte[] { (byte)'P', (byte)'K', 1, 2 });
        odt[lastHeader + 31] ^= 1;   // extra field length of the last central directory header: + 256 bytes
        TestUtil.Throws<InvalidDocumentException>(Limit, () => Generator.Generate(odt, "damaged.odt"));
        Assert.Equal(DocumentFormat.Unknown, ThumbnailGenerator.DetectFormat(odt));
    }

    [Theory]
    [MemberData(nameof(Samples.AllData), MemberType = typeof(Samples))]
    public void Fuzz_flipped_bytes_only_give_documented_errors(string name)
    {
        var original = Samples.Get(name).Bytes;
        var failures = Fuzz(name, random =>
        {
            var data = original.ToArray();
            for (var i = 0; i < FlipsPerMutant; i++) data[random.Next(data.Length)] ^= (byte)(1 << random.Next(8));
            return data;
        });
        Assert.True(failures.Count == 0, $"{name}: {failures.Count} mutant(s) escaped with an undocumented exception\n{string.Join('\n', failures)}");
    }

    public static IEnumerable<object[]> PackageData => new[] { "docx", "docx-3-pages", "xlsx", "pptx", "odt", "ods" }.Select(n => new object[] { n });

    /// <summary>
    /// A flipped byte in a compressed entry mostly breaks the deflate stream; here the parts themselves are changed and the
    /// package is written again, so the damage reaches the XML readers and the layout.
    /// </summary>
    [Theory]
    [MemberData(nameof(PackageData))]
    public void Fuzz_changed_package_parts_only_give_documented_errors(string name)
    {
        var parts = Zip.Read(Samples.Get(name).Bytes);
        var failures = Fuzz(name, random =>
        {
            var mutated = parts.Select(p => (p.Name, Content: p.Content.ToArray(), System.IO.Compression.CompressionLevel.Fastest)).ToArray();
            for (var i = 0; i < FlipsPerMutant; i++)
            {
                // the larger parts are hit more often; structure characters are swapped in to keep the XML close to valid
                var content = mutated.Where(p => p.Content.Length > 0).OrderByDescending(p => p.Content.Length).ElementAt(random.Next(Math.Min(3, mutated.Length))).Content;
                var at = random.Next(content.Length);
                content[at] = random.Next(3) switch
                {
                    0 => (byte)"<>/\"= 0-9"[random.Next(9)],
                    1 => (byte)random.Next(256),
                    _ => (byte)(content[at] ^ (1 << random.Next(8))),
                };
            }
            return Zip.Build(mutated);
        });
        Assert.True(failures.Count == 0, $"{name}: {failures.Count} mutant(s) escaped with an undocumented exception\n{string.Join('\n', failures)}");
    }
}
