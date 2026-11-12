# FastThumb

[![CI](https://github.com/nsknet/FastThumb/actions/workflows/ci.yml/badge.svg)](https://github.com/nsknet/FastThumb/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/FastThumb.svg)](https://www.nuget.org/packages/FastThumb)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

FastThumb is a .NET library that turns office documents, spreadsheets, presentations, PDF files, e-books, images and
videos into thumbnail images and, for paginated documents, a preview PDF of every page. Everything runs in-process:
no Office installation, no LibreOffice, no headless browser and no child processes. Documents are read and laid out
by FastThumb's own managed readers, pages are drawn with Skia, PDFs are rendered with PDFium, images are resized and
encoded with libvips, and a single frame is taken from a video with a small decode-only FFmpeg build. Every input is
treated as untrusted and is processed within explicit resource limits.

## Features

- **One call, many outputs**: a full-size image plus any number of thumbnail sizes (by longest side), and a preview
  PDF of all pages, from one read of the input.
- **Wide format coverage**: DOCX, DOC, RTF, ODT, HTML, MHTML, Markdown, text, EPUB, MOBI/AZW3, CHM, XLSX, XLSB, XLS,
  ODS, CSV, PPTX, PPT, ODP, XPS, PDF, common image formats, and common video containers and codecs.
  See [docs/formats.md](docs/formats.md).
- **Content-based detection**: the format is recognised from the bytes; the file name only breaks ties between
  plain-text formats.
- **PNG, WebP or JPEG output** with configurable quality and PNG compression level.
- **Bytes, streams or files**: a seekable video stream is read in place (only the container header and one group of
  pictures), so thumbnails of remote videos are cheap.
- **Thread-safe**: one `ThumbnailGenerator` instance can serve any number of concurrent calls.
- **Cancellable**: a `CancellationToken` is checked between pages, during reads, between encoded sizes and while
  reading a video.
- **Hardened for untrusted input**: limits on pixels, input size, decompressed parts, XML depth, nesting, pages and
  video decoding time. See [docs/security.md](docs/security.md).
- **Cross-platform**: Windows, Linux (glibc) and macOS, x64 and arm64.
- **Command-line tool**: `fastthumb` processes single files or whole directory trees, with JSON-lines output for
  scripting.

## Supported formats

| Category | Formats |
|---|---|
| Word processing | DOCX/DOCM/DOTX/DOTM, Flat OPC XML, Word 2003 XML, DOC/DOT (Word 97-2003), RTF, ODT/OTT |
| Web and text | HTML/XHTML, MHTML, Markdown, plain text |
| E-books and help | EPUB, MOBI/AZW/AZW3, CHM |
| Legacy (text only) | HWP 5.x, WordPerfect 6+, Microsoft Works |
| Spreadsheets | XLSX/XLSM/XLTX, XLSB, XLS (BIFF8), ODS/OTS, CSV/TSV |
| Presentations | PPTX/PPTM/POTX/PPSX, PPT/PPS/POT, ODP/OTP, FODP |
| Fixed layout | PDF, XPS/OXPS |
| Images | JPEG, PNG, GIF, WebP, TIFF, HEIF/AVIF, SVG, BMP, ICO |
| Video | MP4/MOV/M4V/3GP, Matroska/WebM, AVI, FLV, ASF/WMV, MPEG-PS/TS, Ogg, RealMedia, MXF and more |

The full list, with what is rendered and the known limitations of each reader, is in [docs/formats.md](docs/formats.md).

## Installation

```bash
dotnet add package FastThumb
```

The package targets **.NET 8** and **.NET 10** and carries the native libraries for all supported platforms.

The repository also contains a command-line tool, `fastthumb`, as a worked example and for trying formats and
platforms. It is built from source and is not published as a package: see [Command-line tool](#command-line-tool).

## Quick start

### From a file

```csharp
using FastThumb;

var generator = new ThumbnailGenerator();               // ThumbnailOptions.Default
ThumbnailResult result = generator.GenerateFromFile("report.docx");

foreach (Thumbnail image in result.Images)              // "full" first, then 2500, 1000, 500, 300, 100
    File.WriteAllBytes($"report.{image.Name}.png", image.Data);

if (result.PreviewPdf is not null)
    File.WriteAllBytes("report.preview.pdf", result.PreviewPdf);

Console.WriteLine($"{result.Format}, {result.PageCount} pages, {result.Timings.Total.TotalMilliseconds:0} ms");
```

`Thumbnail.Name` is the requested size (`"1000"`) or `"full"`. `Thumbnail.Format` is the encoding that was actually
written (an image too large for WebP or JPEG is written as PNG, see [docs/options.md](docs/options.md));
`ThumbnailOptions.FileExtension` gives the usual extension of the requested format (`"png"`, `"webp"` or `"jpg"`).

### From bytes

```csharp
byte[] data = await File.ReadAllBytesAsync("budget.xlsx");
ThumbnailResult result = ThumbnailGenerator.Default.Generate(data, "budget.xlsx");
```

The file name (or just an extension such as `".csv"`) is optional. It is only used to tell plain-text formats apart
(CSV, Markdown, HTML, plain text) and to recognise a few formats that have no signature (BMP, ICO, SVG, some video
containers).

### From a stream

```csharp
using var stream = File.OpenRead("clip.mp4");
ThumbnailResult result = ThumbnailGenerator.Default.Generate(stream, "clip.mp4");
```

The stream is read from its current position and is not disposed. A seekable video stream is read in place; any
other input is read into memory first (within `ResourceLimits.MaxInputBytes`).

### Asynchronously

```csharp
await using var download = await httpClient.GetStreamAsync(url, cancellationToken);
ThumbnailResult result = await ThumbnailGenerator.Default.GenerateAsync(download, "slides.pptx", cancellationToken);
```

A non-seekable stream is buffered asynchronously; decoding and rendering then run on the thread pool.

### Options

`ThumbnailOptions` is an immutable record: derive variants with `with`.

```csharp
var options = ThumbnailOptions.Default with
{
    Sizes = [1024, 256],
    IncludeFullSize = false,
    Format = ThumbnailFormat.WebP,
    Quality = 75,
    Outputs = ThumbnailOutputs.Images,
    Video = VideoOptions.Default with { Position = 0.25, Timeout = TimeSpan.FromSeconds(20) },
    Limits = ResourceLimits.Default with { MaxInputBytes = 200L << 20 },
};
var generator = new ThumbnailGenerator(options);        // throws ArgumentException if an option is out of range
```

Every option, its default and its range are listed in [docs/options.md](docs/options.md).

### Dependency injection

`IThumbnailGenerator` is the interface to inject or mock. The generator is thread-safe, so register one instance:

```csharp
builder.Services.AddSingleton<IThumbnailGenerator>(
    new ThumbnailGenerator(ThumbnailOptions.Default with { Format = ThumbnailFormat.WebP }));

public sealed class PreviewService(IThumbnailGenerator thumbnails)
{
    public Task<ThumbnailResult> CreateAsync(Stream upload, string fileName, CancellationToken ct) =>
        thumbnails.GenerateAsync(upload, fileName, ct);
}
```

### Format detection

```csharp
DocumentFormat format = ThumbnailGenerator.DetectFormat(data, "file.bin");        // Unknown if not recognised
IReadOnlyCollection<string> extensions = ThumbnailGenerator.SupportedExtensions;  // ".docx", ".pdf", ".mp4", ...
```

## Results and errors

`ThumbnailResult` contains:

| Member | Meaning |
|---|---|
| `Format` | The `DocumentFormat` recognised from the content. |
| `PageCount` | Number of pages; an image or a video counts as one. |
| `Images` | The thumbnails: the full-size image first (if requested), then one per entry of `Sizes`, in that order. |
| `FullSize`, `Find(int)` | The full-size image, or the image made for a given size (`null` if absent). |
| `PreviewPdf` | A PDF of every page, or `null` (not requested, or the input is a PDF, an image or a video). |
| `Timings` | Time spent rendering, writing the preview PDF and encoding (`ThumbnailTimings`). |

Thumbnails are always made from the first page (or slide, or printed page of a spreadsheet).

Errors are reported with these exception types:

| Exception | When |
|---|---|
| `UnsupportedFormatException` | The format, a feature (for example encryption) or the video codec is not supported. |
| `InvalidDocumentException` | The input is damaged or inconsistent. `InnerException` holds the original error. |
| `ResourceLimitExceededException` | A limit was exceeded. Derives from `InvalidDocumentException`. |
| `TimeoutException` | Reading a video took longer than `VideoOptions.Timeout`. |
| `OperationCanceledException` | The `CancellationToken` was cancelled. |
| `IOException` | Reading the stream failed. |
| `ArgumentException` | An option is out of range (thrown by the constructor or by `ThumbnailOptions.Validate()`). |

`UnsupportedFormatException` and `InvalidDocumentException` derive from `ThumbnailException`. Exception messages can
contain details from native decoders: log them, but do not show them verbatim to end users.

## Command-line tool

The command-line tool is part of the source tree (`src/FastThumb.Cli`), not a published package. Build it once:

```bash
dotnet publish src/FastThumb.Cli -c Release -o ./fastthumb-cli
```

Then run `./fastthumb-cli/FastThumb.Cli` (written `fastthumb` below):

```bash
fastthumb run report.docx -o out
fastthumb run in/ -o out -m images -s 1000,300 -f webp -q 75 -j 4
fastthumb run in/ -o out --json > run.jsonl
fastthumb info
```

Outputs are named `<out>/<name>.<size>.<png|webp|jpg>` and `<out>/<name>.preview.pdf`. All commands, options, exit
codes and JSON fields are described in [docs/cli.md](docs/cli.md).

## Docker

The [Dockerfile](Dockerfile) at the repository root builds an image with the command-line tool, fontconfig and the
metric-compatible fonts that Linux needs:

```bash
docker build -t fastthumb .
docker run --rm fastthumb info
docker run --rm --network none --read-only --tmpfs /tmp:size=512m --memory 2g --pids-limit 256 \
  -v "$PWD/in:/in:ro" -v "$PWD/out:/out" fastthumb run /in --out /out
```

See [docs/linux.md](docs/linux.md) for fonts, base images and container advice.

## Platform requirements

| Platform | Runtime identifiers | Notes |
|---|---|---|
| Windows | `win-x64`, `win-arm64` | |
| Linux | `linux-x64`, `linux-arm64` | glibc-based distributions (Debian, Ubuntu, RHEL, ...). musl/Alpine is not supported. Needs fontconfig and fonts, see [docs/linux.md](docs/linux.md). |
| macOS | `osx-x64`, `osx-arm64` | macOS 11 or later. |

`win-x64` and `linux-x64` are the most tested; the other platforms are built and tested in CI.

Images are identical on .NET 8 and .NET 10. Preview PDFs have identical content, but their compressed bytes differ
(.NET 10 compresses with zlib-ng, .NET 8 with the runtime's zlib).

## Security

FastThumb is designed for untrusted uploads, but it parses complex formats and calls native decoders inside your
process, and a memory-safety bug in a native component can still crash that process. For a public service, run
thumbnail generation in an isolated, resource-limited worker process or container. Read
[docs/security.md](docs/security.md) for the threat model, the limits and the recommended deployment, and
[SECURITY.md](SECURITY.md) to report a vulnerability.

## Building from source

Requirements: the .NET 10 SDK (see [global.json](global.json)), plus the .NET 8 runtime to run the `net8.0` tests.

```bash
dotnet build FastThumb.slnx -c Release
dotnet test FastThumb.slnx -c Release
```

The video library `thumbvideo` is a native build output and is not committed (`src/FastThumb/runtimes/` is
git-ignored). CI builds it for all six runtime identifiers; locally, build it for your platform with

```bash
sh native/thumbvideo/build.sh linux-x64     # or win-x64, win-arm64, linux-arm64, osx-x64, osx-arm64
```

which needs zig, make, git, meson, ninja and, for the x64 targets, nasm; see [docs/native.md](docs/native.md).
Without the library everything except video works; a video input then fails with `UnsupportedFormatException`.

Versions come from git tags (`v1.2.3`) through MinVer; an untagged build gets a pre-release version.

Documentation:

- [docs/architecture.md](docs/architecture.md): how an input becomes thumbnails
- [docs/formats.md](docs/formats.md): formats and limitations
- [docs/options.md](docs/options.md): every option
- [docs/security.md](docs/security.md): threat model and limits
- [docs/native.md](docs/native.md): native components and how they are built
- [docs/linux.md](docs/linux.md): Linux, fonts and containers
- [docs/cli.md](docs/cli.md): the `fastthumb` command

## License

FastThumb is licensed under the [MIT License](LICENSE).

The `thumbvideo` native library (`runtimes/<rid>/native/`) statically links a decode-only LGPL build of FFmpeg and the
BSD-licensed dav1d, so that binary is distributed under the **GNU LGPL 2.1 or later**. Its complete source and build
script are in [native/thumbvideo](native/thumbvideo), so you can rebuild it and replace the binary that ships with
your application. The other third-party components (SkiaSharp/Skia, PDFium, libvips and their dependencies) are
listed with their licenses in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Contributing

Contributions are welcome. Please read [CONTRIBUTING.md](CONTRIBUTING.md) before opening a pull request. Release
history is in [CHANGELOG.md](CHANGELOG.md).
