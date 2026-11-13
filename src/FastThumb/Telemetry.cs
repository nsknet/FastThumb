using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace FastThumb;

/// <summary>
/// Traces and metrics through System.Diagnostics, which OpenTelemetry and dotnet-counters read: no dependency, and close to
/// no cost while nobody listens (StartActivity returns null, Record returns at once).
/// </summary>
internal static class Telemetry
{
    private static readonly string? Version = typeof(Telemetry).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0];

    public static readonly ActivitySource Source = new(ThumbnailGenerator.TelemetryName, Version);
    private static readonly Meter Meter = new(ThumbnailGenerator.TelemetryName, Version);

    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>(
        "fastthumb.operation.duration", "s", "Duration of one call, from the start of reading to the result.");
    private static readonly Histogram<long> InputSize = Meter.CreateHistogram<long>(
        "fastthumb.input.size", "By", "Size of the inputs read into memory (a video stream read in place is not counted).");
    private static readonly Counter<long> OutputSize = Meter.CreateCounter<long>(
        "fastthumb.output.size", "By", "Bytes of thumbnails and preview PDFs produced.");
    private static readonly Counter<long> Pages = Meter.CreateCounter<long>(
        "fastthumb.pages", "{page}", "Pages of the inputs (laid out, or counted for a PDF).");

    public static Activity? Start(string operation, long? inputBytes)
    {
        var activity = Source.StartActivity(operation == "generate" ? "FastThumb.Generate" : "FastThumb.Inspect");
        if (inputBytes is { } bytes)
        {
            activity?.SetTag("fastthumb.input.size", bytes);
            InputSize.Record(bytes, new KeyValuePair<string, object?>("fastthumb.operation", operation));
        }
        return activity;
    }

    public static void Succeeded(Activity? activity, string operation, long started, object result)
    {
        var (format, pages) = result switch
        {
            ThumbnailResult r => (r.Format, r.PageCount),
            DocumentInfo i => (i.Format, i.PageCount ?? 0),
            _ => (DocumentFormat.Unknown, 0),
        };
        var operationTag = new KeyValuePair<string, object?>("fastthumb.operation", operation);
        var formatTag = new KeyValuePair<string, object?>("fastthumb.format", format.ToString());
        Duration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, operationTag, formatTag);
        if (pages > 0) Pages.Add(pages, operationTag, formatTag);
        activity?.SetTag("fastthumb.format", format.ToString());
        activity?.SetTag("fastthumb.page_count", pages);
        if (result is not ThumbnailResult thumbnails) return;

        long images = 0;
        foreach (var image in thumbnails.Images) images += image.Data.Length;
        var pdf = thumbnails.PreviewPdf?.Length ?? 0;
        if (images > 0) OutputSize.Add(images, formatTag, new KeyValuePair<string, object?>("fastthumb.output", "image"));
        if (pdf > 0) OutputSize.Add(pdf, formatTag, new KeyValuePair<string, object?>("fastthumb.output", "pdf"));
        if (activity is null) return;
        activity.SetTag("fastthumb.image_count", thumbnails.Images.Count);
        activity.SetTag("fastthumb.output.size", images + pdf);
        activity.SetTag("fastthumb.render.duration", thumbnails.Timings.Render.TotalSeconds);
        activity.SetTag("fastthumb.pdf.duration", thumbnails.Timings.PreviewPdf.TotalSeconds);
        activity.SetTag("fastthumb.encode.duration", thumbnails.Timings.Encode.TotalSeconds);
    }

    public static void Failed(Activity? activity, string operation, long started, Exception error)
    {
        var type = error.GetType().Name;
        Duration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds,
            new KeyValuePair<string, object?>("fastthumb.operation", operation), new KeyValuePair<string, object?>("error.type", type));
        if (activity is null) return;
        activity.SetTag("error.type", type);
        // exception messages can quote native decoders: the type is enough to group failures
        activity.SetStatus(ActivityStatusCode.Error, type);
    }
}
