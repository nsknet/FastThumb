# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.5.0] - 2026-11-18

### Added

- **Adobe Illustrator**: PostScript-based `.ai` files (Illustrator 8 and older) and EPS files Illustrator wrote without a
  preview are drawn from Illustrator's own drawing operators (paths, fills, strokes, compound paths, clipping groups,
  colours, dashes; hidden layers left out; no text, gradients or images); a file with an Illustrator thumbnail shows it.
  Such files were laid out as text before. A PDF-based `.ai` saved without PDF content shows its XMP thumbnail instead of
  Illustrator's notice page. `.ai` is in `SupportedExtensions`.
- **Layered Photoshop files**: a PSD or PSB saved with "Maximize Compatibility" off (image resource 1057) no longer shows
  its white placeholder composite: its layers are composited (stored pixels, transparency, masks, opacity, fill,
  visibility, clipping, groups, six common blend modes), with the embedded thumbnail as fallback.
- **OpenDocument drawings**: `DocumentFormat.Odg` (`.odg`, `.otg`) and `DocumentFormat.Fodg` (`.fodg`), drawn page by page
  with a preview PDF. They were read as text documents before.
- **SVGZ**: gzip-compressed SVG, inflated within `ResourceLimits.MaxInputBytes`.
- **CorelDRAW** (`.cdr`): the preview picture of RIFF drawings and of X4+ packages.
- **DICOM** (`.dcm`, `.dicom`): the first frame, uncompressed, RLE, JPEG baseline/extended or JPEG 2000, with the
  modality rescale, window and MONOCHROME1 inversion.

### Changed

- **BMP is recognised by content**: its headers are checked, so a BMP without a `.bmp` name is an image instead of being
  laid out as text.

## [1.4.0] - 2026-11-13

### Added

- **Page resolution**: `ThumbnailOptions.PageDpi` sets the resolution of the full-size image of a page (documents,
  spreadsheets, presentations, XPS and PDF), for example 300 for print quality. Page 1 is drawn once, at the larger of
  the full size and the largest size, so the full-size image is never enlarged. Without it the full size stays 96 DPI
  for documents and 72 DPI for PDF. Command-line tool: `--dpi`.
- `ThumbnailGenerator.ApplyProcessSettings()` applies the process-wide libvips settings at a chosen time (for a process
  that also uses libvips itself), instead of at the first call that needs them. Nothing else is loaded early.

### Changed

- **A PDF page stops drawing when the call is cancelled**: PDFium draws progressively and checks the
  `CancellationToken`, so a slow page no longer keeps the process-wide PDFium lock until it is finished.
- **Less memory for non-seekable streams**: `GenerateAsync` held such a stream's bytes about twice while the file was
  processed (a growing buffer and a copy of it); it now reads them into one array of the exact length and holds them
  once. `Generate` reads them the same way, without a growing buffer.
- The package carries a short, Markdown-only README, so its page on nuget.org no longer shows raw HTML or broken
  images.

### Fixed

- A page scaled down to fit `ResourceLimits.MaxPixels` could come out a few pixels over the limit after rounding.

## [1.3.0] - 2026-11-13

### Added

- **More image formats**, read in managed code with no new dependency (the bundled libvips and Skia have no loader for
  them):
  - **PSD / PSB**: the composite image, in every common colour mode, 1 to 16 bits.
  - **TGA**: colour-mapped, true-colour and greyscale, plain or run-length encoded.
  - **EPS**: the preview the file carries (the TIFF of a DOS EPS, EPSI, Illustrator's thumbnail).
  - **Camera RAW** (CR2, CR3, NEF, ARW, DNG, ORF, RW2, RAF, PEF and more): the largest JPEG preview, with the RAW file's
    orientation.
  - **JPEG 2000** (JP2, J2K): a Part 1 decoder (progression order changes and packed packet headers included) that
    decodes only the wavelet levels the requested sizes need.

### Fixed

- CR3 files are no longer taken for videos, and ORF, RW2 and RAF files are no longer laid out as text.

## [1.2.0] - 2026-11-13

### Added

- **Text**: `ThumbnailOutputs.Text` returns the plain text of every page (`ThumbnailResult.PageTexts` and `Text`), for
  search indexing. Documents, spreadsheets, presentations and XPS give the text of the laid-out pages; a PDF gives its
  text layer through PDFium.
- **Cover thumbnails**: `ThumbnailOptions.Fit = ThumbnailFit.Cover` fills a box of `AspectRatio` (square by default)
  and crops it, keeping the top of a page or the centre of a picture. Images are still never enlarged.
- **Placeholders**: `ThumbnailOutputs.Placeholder` returns a ThumbHash of about 25 bytes and the average colour
  (`ThumbnailResult.Placeholder`), to show while the thumbnail loads.
- **Page limit**: `ThumbnailOptions.MaxPages` stops the layout, the preview PDF and the text after that many pages;
  `ThumbnailResult.HasMorePages` tells whether pages were left out. `MaxPages = 1` makes thumbnails of long documents
  cheaper.
- **Inspect**: `ThumbnailGenerator.Inspect` and `InspectFile` return a `DocumentInfo` (format, page count, size, title,
  author, subject, keywords, dates, video duration and codec) without laying out or rendering.
- **Telemetry**: an `ActivitySource` and a `Meter` named `FastThumb` (`ThumbnailGenerator.TelemetryName`) trace every
  call and measure duration, input and output size and pages. No dependency; nothing is recorded without a listener.
- **ASP.NET Core sample** in `samples/FastThumb.AspNetCore`.
- Command-line tool: `--text`, `--placeholder`, `--max-pages`, `--fit`, `--aspect`, `--mode none` and the `inspect`
  command.

### Changed

- `ThumbnailOutputs.All` still means images and preview PDF; the new flags are added to it with `|`. Code that used
  the value 4 as an invalid flag now gets `Text`.
- `IThumbnailGenerator` has three new members (`Inspect` twice and `InspectFile`). A class of your own that implements
  the interface must add them; mocking libraries are not affected.

## [1.1.0] - 2026-11-13

### Changed

- **Markdown**: a new reader that follows CommonMark and the GitHub extensions (simplified, no new dependency): setext
  headings, block quotes, nested and task lists, indented code, table column alignment, strikethrough, links,
  autolinks, escapes and entities. Code blocks are drawn in a shaded box and only page 1 of a long file is parsed.
- **Markdown**: fenced code gets simple GitHub-style syntax colours for C#, Python, JavaScript/TypeScript/JSON,
  HTML/XML, CSS, shell and a few C-like languages (Java, C/C++, Go, Rust, Kotlin, Swift).

## [1.0.0] - 2026-11-12

First public release.

### Added

- **Library** (`FastThumb` package, targets .NET 8 and .NET 10)
  - `ThumbnailGenerator` and `IThumbnailGenerator`: thumbnails and preview PDFs from bytes
    (`Generate(ReadOnlyMemory<byte>)`), streams (`Generate(Stream)`, `GenerateAsync(Stream)`) and files
    (`GenerateFromFile`). Thread-safe; `ThumbnailGenerator.Default` uses the default options.
  - `ThumbnailGenerator.DetectFormat` and `ThumbnailGenerator.SupportedExtensions`.
  - `ThumbnailOptions`, `VideoOptions` and `ResourceLimits`: immutable records with `Validate()`.
    Defaults: sizes 2500, 1000, 500, 300 and 100 px plus the full-size image, PNG at compression level 3, preview
    PDF, parallel encoding, video frame at the middle with a 60-second time limit, at most 16384 x 16384 pixels and
    1 GiB of input.
  - `ThumbnailOutputs` (`Images`, `PreviewPdf`, `All`) and `ThumbnailFormat` (`Png`, `WebP`, `Jpeg`).
  - `ThumbnailResult` with `Format`, `PageCount`, `Images`, `FullSize`, `Find`, `PreviewPdf` and `Timings`;
    `Thumbnail` with `MaxDimension`, `IsFullSize`, `Width`, `Height`, `Format`, `Data` and `Name`.
  - Exceptions: `ThumbnailException` (base), `UnsupportedFormatException`, `InvalidDocumentException` and
    `ResourceLimitExceededException`.
  - Cancellation through `CancellationToken`: checked between pages, during reads, between encoded sizes and while
    reading a video.
- **Formats**
  - Word processing: DOCX/DOCM/DOTX/DOTM, Flat OPC, Word 2003 XML, DOC, RTF, ODT.
  - Web and text: HTML, MHTML, Markdown, plain text.
  - E-books and help: EPUB, MOBI/AZW3 (PalmDOC compression), CHM.
  - Text-only readers: HWP 5.x, WordPerfect 6+, Microsoft Works.
  - Spreadsheets: XLSX/XLSM/XLTX, XLSB, XLS (BIFF8), ODS, CSV/TSV, rendered as printed pages.
  - Presentations: PPTX, PPT, ODP, FODP.
  - Fixed layout: PDF (PDFium), XPS/OXPS.
  - Images: JPEG, PNG, GIF, WebP, TIFF, HEIF/AVIF, SVG, BMP, ICO.
  - Video: one frame from MP4/MOV, Matroska/WebM, AVI, FLV, ASF/WMV, MPEG-PS/TS, Ogg, RealMedia, MXF and others,
    with H.264, HEVC, AV1, VP8, VP9, MPEG-1/2/4 and many older codecs.
  - Content-based format detection; the file name only breaks ties between plain-text formats.
- **Rendering**
  - Own layout engine and paginator for flow documents, slide renderer and spreadsheet page planner.
  - Preview PDF of every page written by FastThumb's own PDF writer, with subset fonts and searchable text.
  - Font resolution with metric-compatible substitutes (Carlito, Caladea, Liberation) and look-alikes, so documents
    keep their line breaks where the original fonts are not installed.
- **Security**
  - Limits for untrusted input: pixels, input size, 256 MB per decompressed part, XML depth 256, stack-checked
    recursion, 3000 pages, 1000 slides, 20000 rows x 500 columns per sheet, video frame size and decoding time.
  - No DTD processing, no external references, no macros or scripts, no network or file access from input.
  - Decode-only FFmpeg build without network, protocols, encoders, filters or audio.
- **Platforms**: `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64` (glibc), `osx-x64`, `osx-arm64`.
- **Native video library** `thumbvideo` (FFmpeg n9.0.2 and dav1d 1.5.4, LGPL-2.1-or-later), built from
  `native/thumbvideo` with zig for all six runtime identifiers.
- **Command-line tool** (`src/FastThumb.Cli`, built from source, not published as a package): `run`, `info` and `fonts` commands, parallel
  processing, `--skip-existing`, JSON-lines output and documented exit codes.
- **Dockerfile** for the command-line tool with fontconfig and metric-compatible fonts.
- Documentation: architecture, formats, options, security, native components, Linux and fonts, command-line tool.

[1.5.0]: https://github.com/nsknet/FastThumb/releases/tag/v1.5.0
[1.4.0]: https://github.com/nsknet/FastThumb/releases/tag/v1.4.0
[1.3.0]: https://github.com/nsknet/FastThumb/releases/tag/v1.3.0
[1.2.0]: https://github.com/nsknet/FastThumb/releases/tag/v1.2.0
[1.1.0]: https://github.com/nsknet/FastThumb/releases/tag/v1.1.0
[1.0.0]: https://github.com/nsknet/FastThumb/releases/tag/v1.0.0
