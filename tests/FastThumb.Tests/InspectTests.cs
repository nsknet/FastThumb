using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO.Compression;
using System.Text;
using FastThumb.Tests.Fixtures;

namespace FastThumb.Tests;

/// <summary>Inspect (properties without rendering) and the telemetry the library reports.</summary>
public class InspectTests
{
    private static readonly ThumbnailGenerator Generator = ThumbnailGenerator.Default;

    /// <summary>The package with extra parts added (docProps, meta.xml).</summary>
    private static byte[] With(byte[] package, params (string Name, string Content)[] parts)
    {
        using var buffer = new MemoryStream();
        buffer.Write(package);
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Update, leaveOpen: true))
            foreach (var (name, content) in parts)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(Encoding.UTF8.GetBytes(content));
            }
        return buffer.ToArray();
    }

    private const string Core =
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties"
            xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:dcterms="http://purl.org/dc/terms/"
            xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
          <dc:title>Quarterly report</dc:title>
          <dc:subject>Sales</dc:subject>
          <dc:creator>Ada Lovelace</dc:creator>
          <cp:keywords>q1, sales</cp:keywords>
          <dcterms:created xsi:type="dcterms:W3CDTF">2026-01-02T03:04:05Z</dcterms:created>
          <dcterms:modified xsi:type="dcterms:W3CDTF">2026-02-03T04:05:06Z</dcterms:modified>
        </cp:coreProperties>
        """;

    private static string App(string element, int count) =>
        $"""<?xml version="1.0" encoding="UTF-8"?><Properties xmlns="http://schemas.openxmlformats.org/officeDocument/2006/extended-properties"><{element}>{count}</{element}></Properties>""";

    [Fact]
    public void Docx_properties_come_from_docProps()
    {
        var docx = With(OfficeFiles.Docx(3), ("docProps/core.xml", Core), ("docProps/app.xml", App("Pages", 3)));
        var info = Generator.Inspect(docx, "a.docx");
        Assert.Equal(DocumentFormat.Docx, info.Format);
        Assert.Equal(3, info.PageCount);
        Assert.Equal("Quarterly report", info.Title);
        Assert.Equal("Ada Lovelace", info.Author);
        Assert.Equal("Sales", info.Subject);
        Assert.Equal("q1, sales", info.Keywords);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), info.Created);
        Assert.Equal(new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero), info.Modified);
        Assert.Null(info.Width);
        Assert.Null(info.Duration);
    }

    [Fact]
    public void Pptx_page_count_is_the_saved_slide_count()
    {
        var info = Generator.Inspect(With(OfficeFiles.Pptx(2), ("docProps/app.xml", App("Slides", 2))));
        Assert.Equal(DocumentFormat.Pptx, info.Format);
        Assert.Equal(2, info.PageCount);
    }

    [Fact]
    public void A_document_without_properties_has_only_its_format()
    {
        var info = Generator.Inspect(OfficeFiles.Docx(1));
        Assert.Equal(DocumentFormat.Docx, info.Format);
        Assert.Null(info.PageCount);
        Assert.Null(info.Title);
        Assert.Null(info.Created);
    }

    [Fact]
    public void A_broken_properties_part_is_ignored()
    {
        var info = Generator.Inspect(With(OfficeFiles.Docx(1), ("docProps/core.xml", "<cp:coreProperties><dc:title>never closed")));
        Assert.Equal(DocumentFormat.Docx, info.Format);
        Assert.Null(info.Title);
    }

    [Fact]
    public void OpenDocument_properties_come_from_meta_xml()
    {
        const string meta =
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <office:document-meta xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0"
                xmlns:meta="urn:oasis:names:tc:opendocument:xmlns:meta:1.0" xmlns:dc="http://purl.org/dc/elements/1.1/">
              <office:meta>
                <dc:title>Minutes</dc:title>
                <meta:initial-creator>Grace Hopper</meta:initial-creator>
                <dc:creator>Someone Else</dc:creator>
                <meta:keyword>board</meta:keyword><meta:keyword>2026</meta:keyword>
                <meta:creation-date>2026-03-04T05:06:07</meta:creation-date>
                <meta:document-statistic meta:page-count="4"/>
              </office:meta>
            </office:document-meta>
            """;
        var info = Generator.Inspect(With(OfficeFiles.Odt(), ("meta.xml", meta)));
        Assert.Equal(DocumentFormat.Odt, info.Format);
        Assert.Equal("Minutes", info.Title);
        Assert.Equal("Grace Hopper", info.Author);
        Assert.Equal("board, 2026", info.Keywords);
        Assert.Equal(new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero), info.Created);
        Assert.Equal(4, info.PageCount);
    }

    [Fact]
    public void Pdf_gives_its_page_count_size_and_information_dictionary()
    {
        var pdf = PdfFiles.Pdf(3, 595, 842, "/Title (Annual report) /Author (Alan Turing) /CreationDate (D:20250607080910+07'00')");
        var info = Generator.Inspect(pdf);
        Assert.Equal(DocumentFormat.Pdf, info.Format);
        Assert.Equal(3, info.PageCount);
        Assert.Equal((595, 842), (info.Width, info.Height));
        Assert.Equal("Annual report", info.Title);
        Assert.Equal("Alan Turing", info.Author);
        Assert.Equal(new DateTimeOffset(2025, 6, 7, 8, 9, 10, TimeSpan.FromHours(7)), info.Created);
        Assert.Null(info.Modified);
    }

    [Theory]
    [InlineData("D:2024", 2024, 1, 1, 0, 0, 0, 0)]
    [InlineData("D:20240229235959Z", 2024, 2, 29, 23, 59, 59, 0)]
    [InlineData("D:20240102030405-05'30'", 2024, 1, 2, 3, 4, 5, -330)]
    [InlineData("20240102", 2024, 1, 2, 0, 0, 0, 0)]
    public void Pdf_dates_are_parsed(string text, int year, int month, int day, int hour, int minute, int second, int offsetMinutes)
    {
        Assert.Equal(new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.FromMinutes(offsetMinutes)), Inspector.PdfDate(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("D:")]
    [InlineData("D:20241399")]
    [InlineData("garbage")]
    [InlineData("D:00010101000000Z")]
    public void Bad_pdf_dates_are_null(string text) => Assert.Null(Inspector.PdfDate(text));

    [Theory]
    [MemberData(nameof(Samples.RasterImageData), MemberType = typeof(Samples))]
    public void Image_size_is_read_from_the_header(string name)
    {
        var info = Generator.Inspect(Samples.Get(name).Bytes);
        Assert.Equal(DocumentFormat.Image, info.Format);
        Assert.Equal((320, 200), (info.Width, info.Height));
        Assert.Equal(1, info.PageCount);
    }

    [Theory]
    [MemberData(nameof(Samples.AllData), MemberType = typeof(Samples))]
    public void Every_sample_can_be_inspected_from_bytes_stream_and_file(string name)
    {
        var sample = Samples.Get(name);
        var fromBytes = Generator.Inspect(sample.Bytes, sample.FileName);
        Assert.Equal(sample.Format, fromBytes.Format);
        using var stream = new MemoryStream(sample.Bytes);
        Assert.Equal(sample.Format, Generator.Inspect(stream, sample.FileName).Format);
        var path = Path.Combine(Path.GetTempPath(), $"fastthumb-inspect-{Guid.NewGuid():N}{Path.GetExtension(sample.FileName)}");
        File.WriteAllBytes(path, sample.Bytes);
        try { Assert.Equal(sample.Format, Generator.InspectFile(path).Format); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Inspect_reports_damage_and_unknown_input_with_the_public_types()
    {
        Assert.Throws<UnsupportedFormatException>(() => Generator.Inspect(Zip.Build(("readme.txt", "just a file")), "archive.docx"));
        var truncated = OfficeFiles.Docx(1)[..200];
        Assert.ThrowsAny<ThumbnailException>(() => Generator.Inspect(truncated, "x.docx"));
        Assert.Throws<ResourceLimitExceededException>(() =>
            new ThumbnailGenerator(ThumbnailOptions.Default with { Limits = ResourceLimits.Default with { MaxInputBytes = 10 } }).Inspect(PdfFiles.Pdf()));
    }

    [Fact]
    public void Inspect_is_cancellable()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => Generator.Inspect(PdfFiles.Pdf(), cancellationToken: cancel.Token));
    }

    // ---- telemetry -------------------------------------------------------------------------------

    [Fact]
    public void Generate_and_inspect_are_traced()
    {
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ThumbnailGenerator.TelemetryName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => { lock (activities) activities.Add(activity); },
        };
        ActivitySource.AddActivityListener(listener);

        // a parent of our own, so this test finds its activities among those of tests running at the same time
        using var parentSource = new ActivitySource("FastThumb.Tests.Telemetry");
        using var parentListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "FastThumb.Tests.Telemetry",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(parentListener);
        string traceId;
        using (var parent = parentSource.StartActivity("test")!)
        {
            traceId = parent.TraceId.ToString();
            Generator.Generate(PdfFiles.Pdf(2), "a.pdf");
            Generator.Inspect(PdfFiles.Pdf(2));
            Assert.ThrowsAny<ThumbnailException>(() => Generator.Generate(Zip.Build(("readme.txt", "just a file")), "archive.docx"));
        }

        List<Activity> ours;
        lock (activities) ours = activities.Where(a => a.TraceId.ToString() == traceId).ToList();
        var generate = ours.Where(a => a.OperationName == "FastThumb.Generate").ToList();
        Assert.Equal(2, generate.Count);
        Assert.Equal("Pdf", generate[0].GetTagItem("fastthumb.format"));
        Assert.Equal(2, generate[0].GetTagItem("fastthumb.page_count"));
        Assert.Equal(6, generate[0].GetTagItem("fastthumb.image_count"));
        Assert.Equal(ActivityStatusCode.Error, generate[1].Status);
        Assert.Equal(nameof(UnsupportedFormatException), generate[1].GetTagItem("error.type"));
        var inspect = Assert.Single(ours, a => a.OperationName == "FastThumb.Inspect");
        Assert.Equal("Pdf", inspect.GetTagItem("fastthumb.format"));
    }

    [Fact]
    public void Generate_reports_metrics()
    {
        var durations = new List<(double Value, Dictionary<string, object?> Tags)>();
        var outputs = new List<long>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == ThumbnailGenerator.TelemetryName) l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            if (instrument.Name == "fastthumb.operation.duration")
                lock (durations) durations.Add((value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value)));
        });
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            if (instrument.Name == "fastthumb.output.size") lock (outputs) outputs.Add(value);
        });
        listener.Start();

        Generator.Generate(OfficeFiles.Docx(1), "a.docx");
        listener.Dispose();

        lock (durations)
            Assert.Contains(durations, d => d.Value > 0 && Equals(d.Tags.GetValueOrDefault("fastthumb.operation"), "generate") && Equals(d.Tags.GetValueOrDefault("fastthumb.format"), "Docx"));
        lock (outputs) Assert.Contains(outputs, bytes => bytes > 1000);
    }

    [Fact]
    public void Telemetry_name_is_stable()
    {
        Assert.Equal("FastThumb", ThumbnailGenerator.TelemetryName);
    }
}
