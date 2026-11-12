# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Changed

- **Markdown**: a new reader that follows CommonMark and the GitHub extensions (simplified, no new dependency): setext
  headings, block quotes, nested and task lists, indented code, table column alignment, strikethrough, links,
  autolinks, escapes and entities. Code blocks are drawn in a shaded box and only page 1 of a long file is parsed.

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

[1.0.0]: https://github.com/nsknet/FastThumb/releases/tag/v1.0.0
