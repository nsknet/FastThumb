// FastThumb in an ASP.NET Core minimal API.
//
//   POST /files            multipart upload (field "file"): stores a 400 px WebP thumbnail and a square 160 px tile,
//                          returns the id, format, page count, placeholder and the start of the text
//   GET  /files/{id}/thumbnail   the thumbnail      GET /files/{id}/tile   the square tile
//   POST /inspect          multipart upload: format, pages, size and document properties, nothing rendered
//   GET  /                 a test page with an upload form
//
// Run:  dotnet run --project samples/FastThumb.AspNetCore     then open the URL it prints.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using FastThumb;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(args);

// Inputs can be large: allow 100 MB uploads (the library itself stops at ResourceLimits.MaxInputBytes).
const long MaxUpload = 100L << 20;
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = MaxUpload);
builder.Services.Configure<FormOptions>(form => form.MultipartBodyLengthLimit = MaxUpload);

// DocumentFormat and the other enums as names in JSON, and no null fields
builder.Services.ConfigureHttpJsonOptions(json =>
{
    json.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    json.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
});

// One generator for the whole application: it is thread-safe and holds no per-call state.
builder.Services.AddSingleton<IThumbnailGenerator>(new ThumbnailGenerator(ThumbnailOptions.Default with
{
    Sizes = [400, 160],
    IncludeFullSize = false,
    Format = ThumbnailFormat.WebP,
    Quality = 75,
    MaxPages = 20,                       // thumbnails need page 1; 20 pages of text are plenty for a preview
    EncodeInParallel = false,            // the server already runs several requests at once
    Outputs = ThumbnailOutputs.Images | ThumbnailOutputs.Text | ThumbnailOutputs.Placeholder,
    Limits = ResourceLimits.Default with { MaxInputBytes = MaxUpload },
}));

// The 160 px size is cut as a square tile; a second generator with Fit = Cover does it from the same upload.
builder.Services.AddKeyedSingleton<IThumbnailGenerator>("tile", new ThumbnailGenerator(ThumbnailOptions.Default with
{
    Sizes = [160],
    IncludeFullSize = false,
    Format = ThumbnailFormat.WebP,
    Fit = ThumbnailFit.Cover,
    MaxPages = 1,
    EncodeInParallel = false,
    Outputs = ThumbnailOutputs.Images,
    Limits = ResourceLimits.Default with { MaxInputBytes = MaxUpload },
}));

// Rendering is CPU-bound: run at most one document per core and let the other requests wait their turn.
builder.Services.AddSingleton(new SemaphoreSlim(Environment.ProcessorCount));

// A real application keeps the results in blob storage or a database; a dictionary keeps the sample self-contained.
builder.Services.AddSingleton<ConcurrentDictionary<string, StoredFile>>();

// Tracing and metrics: the library reports to ThumbnailGenerator.TelemetryName ("FastThumb"). With the
// OpenTelemetry.Extensions.Hosting package:
//   builder.Services.AddOpenTelemetry()
//       .WithTracing(t => t.AddSource(ThumbnailGenerator.TelemetryName).AddAspNetCoreInstrumentation())
//       .WithMetrics(m => m.AddMeter(ThumbnailGenerator.TelemetryName));
// Without any package: dotnet-counters monitor --counters FastThumb -n FastThumb.AspNetCore

var app = builder.Build();

app.MapGet("/", () => Results.Content(Page.Html, "text/html; charset=utf-8"));

app.MapPost("/files", async (IFormFile file, IThumbnailGenerator thumbnails, [FromKeyedServices("tile")] IThumbnailGenerator tiles,
    SemaphoreSlim gate, ConcurrentDictionary<string, StoredFile> store, CancellationToken cancellationToken) =>
{
    var data = await ReadAsync(file, cancellationToken);
    // the same bytes always give the same output: the content hash is the id, and a repeated upload costs nothing
    var id = Convert.ToHexStringLower(SHA256.HashData(data))[..32];
    if (!store.TryGetValue(id, out var stored))
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var result = await Task.Run(() => thumbnails.Generate(data, file.FileName, cancellationToken), cancellationToken);
            var tile = await Task.Run(() => tiles.Generate(data, file.FileName, cancellationToken), cancellationToken);
            stored = new StoredFile(result.Format, result.PageCount, result.HasMorePages, result.Find(400)?.Data, tile.Find(160)?.Data,
                result.Placeholder, result.Text ?? "");
        }
        finally { gate.Release(); }
        store[id] = stored;
    }
    return Results.Ok(new
    {
        id,
        format = stored.Format.ToString(),
        pages = stored.Pages,
        morePages = stored.MorePages,
        thumbnail = stored.Thumbnail is null ? null : $"/files/{id}/thumbnail",
        tile = stored.Tile is null ? null : $"/files/{id}/tile",
        thumbHash = stored.Placeholder?.ThumbHashBase64,
        color = stored.Placeholder?.AverageColor,
        text = stored.Text.Length > 500 ? stored.Text[..500] + "…" : stored.Text,
    });
}).DisableAntiforgery();

app.MapGet("/files/{id}/thumbnail", (string id, HttpContext http, ConcurrentDictionary<string, StoredFile> store) =>
    Image(http, id, store.TryGetValue(id, out var stored) ? stored.Thumbnail : null));

app.MapGet("/files/{id}/tile", (string id, HttpContext http, ConcurrentDictionary<string, StoredFile> store) =>
    Image(http, id, store.TryGetValue(id, out var stored) ? stored.Tile : null));

app.MapPost("/inspect", async (IFormFile file, IThumbnailGenerator thumbnails, CancellationToken cancellationToken) =>
{
    var data = await ReadAsync(file, cancellationToken);
    return Results.Ok(await Task.Run(() => thumbnails.Inspect(data, file.FileName, cancellationToken), cancellationToken));
}).DisableAntiforgery();

// The library reports bad input with its own exception types: map them to status codes once, here.
app.Use(async (http, next) =>
{
    try { await next(http); }
    catch (Exception e) when (StatusFor(e) is { } status && !http.Response.HasStarted)
    {
        // messages can quote native decoders: log them, but give the client only the kind of problem
        app.Logger.LogWarning(e, "Upload rejected");
        await Results.Problem(statusCode: status, title: e.GetType().Name).ExecuteAsync(http);
    }
});

app.Run();

static int? StatusFor(Exception e) => e switch
{
    UnsupportedFormatException => StatusCodes.Status415UnsupportedMediaType,
    ResourceLimitExceededException => StatusCodes.Status413PayloadTooLarge,
    InvalidDocumentException => StatusCodes.Status422UnprocessableEntity,
    TimeoutException => StatusCodes.Status422UnprocessableEntity,
    _ => null,
};

static async Task<byte[]> ReadAsync(IFormFile file, CancellationToken cancellationToken)
{
    using var buffer = new MemoryStream((int)Math.Min(file.Length, int.MaxValue));
    await file.CopyToAsync(buffer, cancellationToken);
    return buffer.ToArray();
}

// Thumbnails never change for an id (it is the content hash): the browser may keep them for good.
static IResult Image(HttpContext http, string id, byte[]? data)
{
    if (data is null) return Results.NotFound();
    http.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
    return Results.Bytes(data, "image/webp", entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{id}\""));
}

sealed record StoredFile(DocumentFormat Format, int Pages, bool MorePages, byte[]? Thumbnail, byte[]? Tile, ThumbnailPlaceholder? Placeholder, string Text);

static class Page
{
    // The placeholder colour shows at once; the thumbhash package would decode a blurred preview (see docs/options.md).
    public const string Html =
        """
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <title>FastThumb sample</title>
        <style>
          body { font: 15px system-ui, sans-serif; margin: 2rem auto; max-width: 52rem; padding: 0 1rem; }
          #out { display: grid; grid-template-columns: repeat(auto-fill, minmax(160px, 1fr)); gap: 1rem; margin-top: 1.5rem; }
          figure { margin: 0; } figure div { aspect-ratio: 1; border-radius: 6px; overflow: hidden; }
          figure img { width: 100%; height: 100%; display: block; } figcaption { font-size: 12px; margin-top: .3rem; overflow-wrap: anywhere; }
        </style></head>
        <body>
          <h1>FastThumb sample</h1>
          <p>Choose documents, PDFs, images or videos. Each one is uploaded to <code>POST /files</code>.</p>
          <input type="file" id="files" multiple>
          <div id="out"></div>
          <script>
            document.getElementById("files").addEventListener("change", async e => {
              for (const file of e.target.files) {
                const figure = document.createElement("figure");
                figure.innerHTML = "<div></div><figcaption></figcaption>";
                figure.querySelector("figcaption").textContent = file.name + " …";
                document.getElementById("out").append(figure);
                const form = new FormData();
                form.append("file", file);
                const response = await fetch("/files", { method: "POST", body: form });
                const body = await response.json();
                if (!response.ok) { figure.querySelector("figcaption").textContent = file.name + ": " + body.title; continue; }
                figure.querySelector("div").style.background = body.color ?? "#eee";
                if (body.tile) {
                  const img = new Image();
                  img.src = body.tile;
                  img.alt = file.name;
                  figure.querySelector("div").append(img);
                }
                figure.querySelector("figcaption").textContent = `${file.name}: ${body.format}, ${body.pages} page(s)`;
                figure.title = body.text;
              }
            });
          </script>
        </body></html>
        """;
}
