# FastThumb

[![NuGet](https://img.shields.io/nuget/v/FastThumb.svg)](https://www.nuget.org/packages/FastThumb)
[![License: MIT](https://img.shields.io/badge/license-MIT-brightgreen.svg)](https://github.com/nsknet/FastThumb/blob/main/LICENSE)
![.NET 8 | 10](https://img.shields.io/badge/.NET-8%20%7C%2010-512BD4.svg)
![Windows | Linux | macOS](https://img.shields.io/badge/platforms-Windows%20%7C%20Linux%20%7C%20macOS-0ea5e9.svg)

**Thumbnails for 40+ file formats with one method call.** Office documents, spreadsheets, presentations, PDF, e-books,
images and videos become thumbnail images and, for paginated documents, a preview PDF of every page. Everything runs
in-process: no Office, no LibreOffice, no headless browser, no child processes. Free and open source, MIT licensed.

```csharp
var result = ThumbnailGenerator.Default.GenerateFromFile("deck.pptx");   // thumbnails + preview PDF
```

## Why FastThumb?

- ⚡ **Fast**: one read, one layout, one raster; every thumbnail size is encoded in parallel.
- 🪶 **Lightweight**: just this package, no office suite to install and babysit, no browser, no daemon.
- 🧩 **Modern**: async, cancellable, thread-safe, DI-friendly, immutable options, .NET 8 and .NET 10, x64 and arm64.
- 🛡️ **Safe by default**: built for untrusted uploads, with no macros, no scripts, no network and hard resource limits.
- 💸 **Free**: MIT license, usable in closed-source products, no per-server or per-document fees.

## Supported formats

| Category | Formats |
|---|---|
| Word processing | DOCX/DOCM/DOTX/DOTM, Flat OPC XML, Word 2003 XML, DOC/DOT, RTF, ODT/OTT |
| Web and text | HTML/XHTML, MHTML, Markdown, plain text |
| E-books and help | EPUB, MOBI/AZW/AZW3, CHM |
| Legacy (text only) | HWP 5.x, WordPerfect 6+, Microsoft Works |
| Spreadsheets | XLSX/XLSM/XLTX, XLSB, XLS, ODS/OTS, CSV/TSV |
| Presentations and drawings | PPTX/PPTM/POTX/PPSX, PPT/PPS/POT, ODP/OTP, FODP, ODG/OTG, FODG |
| Fixed layout | PDF, XPS/OXPS |
| Images | JPEG, PNG, GIF, WebP, TIFF, HEIF/AVIF, SVG/SVGZ, BMP, ICO, PSD/PSB (layers composited when the file has no real composite), TGA, EPS (its preview), Illustrator AI (PDF-based, or PostScript-based: its thumbnail or paths), camera RAW (its JPEG preview), JPEG 2000, CorelDRAW CDR (its preview), DICOM |
| Video | MP4/MOV/M4V/3GP, Matroska/WebM, AVI, FLV, ASF/WMV, MPEG-PS/TS, Ogg, RealMedia, MXF and more |

Details and limitations: [docs/formats.md](https://github.com/nsknet/FastThumb/blob/main/docs/formats.md).

## Quick start

```bash
dotnet add package FastThumb
```

```csharp
using FastThumb;

var generator = new ThumbnailGenerator();
ThumbnailResult result = generator.GenerateFromFile("report.docx");

foreach (Thumbnail image in result.Images)              // "full" first, then 2500, 1000, 500, 300, 100
    File.WriteAllBytes($"report.{image.Name}.png", image.Data);

if (result.PreviewPdf is not null)
    File.WriteAllBytes("report.preview.pdf", result.PreviewPdf);
```

Bytes, streams (`GenerateAsync`) and files are all accepted. Options are an immutable record:

```csharp
var options = ThumbnailOptions.Default with
{
    Sizes = [300],
    IncludeFullSize = false,
    Format = ThumbnailFormat.WebP,
    Fit = ThumbnailFit.Cover,      // exact 300 x 300 tiles
    MaxPages = 1,                  // thumbnails only need page 1
};
var result = new ThumbnailGenerator(options).GenerateFromFile("report.docx");
```

## Platforms

Windows, Linux (glibc) and macOS, x64 and arm64; the native libraries for all of them are in the package. Linux needs
fontconfig and fonts: see [docs/linux.md](https://github.com/nsknet/FastThumb/blob/main/docs/linux.md).

## Security

Every input is treated as untrusted and processed within explicit limits. Native decoders still run in your process,
so for a public service run thumbnail generation in an isolated, resource-limited worker. See
[docs/security.md](https://github.com/nsknet/FastThumb/blob/main/docs/security.md).

## Documentation

- [Full README](https://github.com/nsknet/FastThumb#readme): text output, placeholders, inspect, telemetry, DI, errors
- [Options](https://github.com/nsknet/FastThumb/blob/main/docs/options.md)
- [Architecture](https://github.com/nsknet/FastThumb/blob/main/docs/architecture.md)
- [Changelog](https://github.com/nsknet/FastThumb/blob/main/CHANGELOG.md)

## License

MIT. The `thumbvideo` native library (`runtimes/<rid>/native/`) statically links a decode-only LGPL build of FFmpeg
and the BSD-licensed dav1d, so that binary is distributed under the GNU LGPL 2.1 or later; its source and build script
are in [native/thumbvideo](https://github.com/nsknet/FastThumb/tree/main/native/thumbvideo). Other third-party
components are listed in
[THIRD-PARTY-NOTICES.md](https://github.com/nsknet/FastThumb/blob/main/THIRD-PARTY-NOTICES.md).
