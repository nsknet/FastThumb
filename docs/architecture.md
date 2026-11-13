# Architecture

This page describes how FastThumb turns one input into thumbnails and a preview PDF, and where each step lives in
the source tree. All paths are relative to `src/FastThumb/`.

## Overview

```text
                    bytes / stream / file
                             |
                    FormatDetector (content first, extension only for ties)
                             |
   +------------+------------+-------------+--------------------------------------+
   |            |            |             |                                      |
  PDF         Image        Video      XPS (fixed)          Flow documents / slides / sheets
   |            |            |             |                                      |
 PDFium      libvips     thumbvideo    XpsReader            reader -> Paginator / SlideRenderer / SheetRenderer
 page 1      decode      one frame         |                                      |
   |            |            |             +------------------+-------------------+
   |            |            |                                |
   |            +-----+------+                       PageScene per page (display list)
   |                  |                                |                 |
   |                  |                         Skia raster of        PdfSink (own PDF writer)
   |                  |                         page 1                -> preview PDF of every page
   |                  |                                |
   +------------------+-------------------------------+
                      |
          libvips: resize + encode PNG / WebP / JPEG (sizes in parallel)
                      |
               ThumbnailResult
```

## Entry point

`Api/ThumbnailGenerator.cs` is the public surface (`ThumbnailGenerator`, `IThumbnailGenerator`). It validates the
options once in the constructor, enforces `ResourceLimits.MaxInputBytes` while reading, sets the call's
`CancellationToken` for the current thread, and translates internal errors into the public exception types:

| Internal error | Public exception |
|---|---|
| `NotSupportedException` | `UnsupportedFormatException` |
| `InvalidDataException` | `InvalidDocumentException` |
| `InsufficientExecutionStackException` | `ResourceLimitExceededException` |
| Index, argument, XML, format, overflow, cast and null errors raised by a damaged document | `InvalidDocumentException` (with the original as `InnerException`) |

`Thumbnailer.cs` is the internal engine. Every format goes through it; adding a format means adding a reader and one
line in `FormatDetector`.

## 1. Detect

`FormatDetector.cs` recognises the format from the content:

1. Signatures: `%PDF-`, OLE compound file (`.doc`, `.xls`, `.ppt`, HWP and Works are told apart by their stream
   names), CHM (`ITSF`), WordPerfect, MOBI (`BOOKMOBI`).
2. Image signatures (JPEG, PNG, GIF, TIFF, WebP, HEIF/AVIF brands), then video signatures (ISO media, Matroska, AVI,
   FLV, ASF, MPEG-PS/TS, Ogg, RealMedia, MXF).
3. ZIP packages, by the parts they contain (`word/document.xml`, `ppt/presentation.xml`, `xl/workbook.xml`,
   `xl/workbook.bin`, ODF `mimetype`, XPS pages, EPUB `META-INF/container.xml`, ODF `content.xml`).
4. The leading text: RTF, Flat OPC, flat ODF presentation, Word 2003 XML, MHTML, HTML.
5. Otherwise the extension decides between CSV/TSV, HTML, Markdown, MHTML, RTF and plain text.

BMP, ICO, SVG and some video containers are recognised by extension when no signature matches.

For a seekable stream, only the first 512 bytes are inspected before deciding whether it is a video. A video is then
read in place; every other input is read into one buffer.

## 2. Read

Each format has its own managed reader that produces a format-neutral model:

| Folder | Readers | Model |
|---|---|---|
| `Words/` | DOCX (also Flat OPC and Word 2003 XML), DOC, ODT, RTF, HTML (also MHTML and EPUB), MOBI, CHM, Markdown, text, HWP, WordPerfect, Works | flow blocks (`Layout/FlowModel.cs`): paragraphs, runs, tables, floats |
| `Slides/` | PPTX, PPT, ODP/FODP | `SlideDeck` of positioned shapes (`Slides/SlideModel.cs`) |
| `Cells/` | XLSX, XLSB, XLS, ODS, CSV | `SheetSnapshot` cells, styles and page setup (`Cells/SheetModel.cs`) |
| `Fixed/` | XPS/OXPS | page scenes directly (no layout needed) |

Readers use only `System.IO.Compression`, `System.Xml` and their own binary parsers (`CompoundFile.cs` for OLE
files). They never execute macros, scripts or fields, never follow external links and never touch the network or
the file system. Every size, count and nesting level read from a file goes through `Guard.cs` (see
[Limits](#limits)).

## 3. Lay out and paginate

- **Flow documents**: `Layout/Paginator.cs` pulls blocks from the reader and lays out lines and table rows page after
  page (at most 3000 pages), using fonts resolved by `Layout/FontCache.cs`.
- **Slides**: `Slides/SlideRenderer.cs` produces one page per slide (at most 1000 slides); shapes are drawn back to
  front: fill, outline, picture, then text.
- **Spreadsheets**: `Cells/SheetRenderer.cs` plans printed pages sheet after sheet, using the print area (or the used
  range), page size, orientation, margins, scale and fit-to-page settings (at most 200 pages).
- **XPS**: `Fixed/XpsReader.cs` replays the page markup straight into page scenes (at most 500 pages).

## 4. Page scene

`Scene/PageScene.cs` is a retained display list for one page: glyph runs with glyph ids and absolute positions,
rectangles and other shapes, paths and images. Text is shaped and measured once, during layout; the same scene is
replayed onto a raster canvas (thumbnail) and written to PDF (preview), so the image and the PDF always agree.

## 5. Raster and preview PDF

- **Raster**: page 1 is drawn with Skia into one bitmap, at the largest requested size (or at the full size when no
  size is requested). The full-size image is the page at 96 DPI, scaled down if it would exceed
  `ResourceLimits.MaxPixels`.
- **Preview PDF**: `Scene/PdfSink.cs` is FastThumb's own PDF writer. It writes every page with one Type0
  (Identity-H) font per typeface, with a subset TrueType program, an optional ToUnicode map (so text in the preview
  can be searched and copied) and image XObjects (JPEG pictures are passed through without re-encoding; other
  pictures are written as RGB with transparency flattened onto white). Text is written with the glyph ids and positions computed during
  layout. Streams are compressed with `System.IO.Compression.ZLibStream`. For a font without TrueType outlines, the
  document falls back to Skia's PDF backend.

## 6. PDF input

`Scene/Pdfium.cs` is a minimal P/Invoke layer over PDFium. Page 1 is rendered straight into a Skia bitmap at the
largest requested size, within `MaxPixels`; the full-size image is the page at 72 DPI. PDFium is not thread-safe, so
all PDFium calls in the process share one lock. A PDF input has no preview PDF.

## 7. Image input

libvips decodes the header first; the pixel count is checked against `MaxPixels` before any pixel is decoded. BMP and
ICO, for which the bundled libvips has no loader, are decoded with Skia after the same check. EXIF orientation is
applied, the image is decoded once into memory, and each size is cut from that copy. Images are never enlarged: a
size larger than the original yields the original.

libvips' operation cache is turned off (every input is different, so it would only hold memory), and loaders that
libvips marks as unsafe for untrusted input are blocked, except SVG, which is read from memory with no base URI.

Formats the bundled libvips and Skia cannot read are tried first, in `Images/ManagedImages.cs`, from their signature
(TGA from its extension or footer):

- `TgaDecoder` and `PsdDecoder` decode pixels (the PSD composite image, not its layers).
- `EpsPreview` and `RawPreview` find the preview the file carries: the TIFF of a DOS EPS, EPSI or Illustrator's
  thumbnail; the largest baseline JPEG of a RAW file, from its TIFF tags (`TiffWalker`), the RAF header or a bounded
  scan of the file. libvips decodes that preview, and a RAW file's orientation is set on it for `Autorot`.
- `Jpeg2000/` is a JPEG 2000 Part 1 decoder: codestream and tile-part headers (`Codestream`), packet headers with tag
  trees in the five progression orders (`TileDecoder`), the MQ coder and the three coding passes (`MqDecoder`,
  `Tier1`), and the inverse 5/3 and 9/7 wavelets (`Wavelet`). `Thumbnailer` passes a `DecodeRequest` that says how small
  a picture still serves every requested size, so only those wavelet levels are decoded, and tier 1 runs only for the
  code-blocks of those levels.

Each checks the declared size against `MaxPixels` before allocating pixels, and reports a damaged file as
`InvalidDocumentException`.

## 8. Video input

`Video/VideoFrame.cs` calls the `thumbvideo` native library (see [native.md](native.md) and
[native/thumbvideo/README.md](../native/thumbvideo/README.md)) through read and seek callbacks on the .NET stream: no
temporary file and no child process. The library returns one RGB frame at `VideoOptions.Position` of the duration,
plus the rotation to apply; the frame then goes through the same path as an image file.

## 9. Encode

All outputs go through libvips: the rendered page (or decoded image) is kept in memory once, each distinct size is
resized from it and encoded as PNG, WebP or JPEG. Identical outputs are encoded once. With
`ThumbnailOptions.EncodeInParallel` (the default), the sizes are encoded concurrently, because each encoder is
single-threaded.

## Limits

`Guard.cs` holds the limits shared by every reader:

- `Guard.ReadAll` / `Guard.Open` / `Guard.Limit`: every ZIP entry, OLE stream and decompression stream fails once more
  than 256 MB come out of it, whatever its header claims.
- `Guard.LoadXml` / `Guard.ParseXml` / `Guard.Depth`: DTD processing is off (no external entities, no entity
  expansion) and element nesting is limited to 256 levels.
- `Guard.Deeper()`: called at the top of every recursive function; it fails the file with
  `ResourceLimitExceededException` while there is still stack left, instead of letting a stack overflow end the
  process.
- `Guard.Image`: embedded pictures larger than 64 megapixels are decoded scaled down (JPEG) or left out.
- `Guard.Cancellation` / `Guard.Check()`: the per-thread cancellation token, checked between pages, in recursive
  readers, in every limited stream, between encoded sizes and in the video read callback.

The complete list of limits is in [security.md](security.md).

## Threading

`ThumbnailGenerator` holds no mutable state; one instance can serve any number of concurrent calls. Shared caches
(typefaces in `FontCache`) are process-wide and immutable once filled. The exceptions are PDFium, whose calls are
serialised by one lock, and libvips' global settings, which are set once when the engine is first used.
