# Security

FastThumb is meant to process files uploaded by users, inside your own process. This page describes what it
defends against, the limits it enforces, what it cannot defend against, and how to deploy it for untrusted input.
To report a vulnerability, see [SECURITY.md](../SECURITY.md).

## Threat model

Every byte of the input is assumed to be written by an attacker. The file name, the extension, every size, count,
offset and nesting level declared in the file, and every embedded font and picture are untrusted.

FastThumb aims to prevent three kinds of harm:

- **Resource exhaustion**: a file of a few kilobytes that declares huge sizes and exhausts memory or disk, or runs
  forever.
- **Process termination**: stack overflow or other failures that `try`/`catch` cannot handle and that would take
  down every request in the process.
- **Reaching outside the input**: a document that points at a URL, a network share or another file on the machine.

Out of the threat model: the options and the caller are trusted, and FastThumb does not sanitise its output for a
specific consumer. Thumbnails are freshly encoded images. The preview PDF is written by FastThumb's own writer and
contains only page content (text, vector shapes, fonts and images; no scripts, links, forms or attachments), but two
kinds of data derived from the input do end up in it: JPEG pictures embedded in the document are copied through
without re-encoding, and fonts embedded in the document (XPS, DOCX) are written as subsets. Whatever displays the
preview PDF therefore still decodes JPEG and font data that originated in the upload.

## What is never done

- **No code from the input is executed.** Macros, scripts, OLE objects, field codes and formulas are never run. VBA
  parts are not read. Formula cells show the value cached in the file.
- **No network and no file access.** Readers resolve relationships and references (`r:id`, `href`, `src`) only inside
  the package or compound file being read. External targets are ignored. HTML is laid out without loading any
  resource.
- **No DTD processing.** Every XML reader runs with `DtdProcessing.Ignore` and no resolver: no external entities and
  no entity expansion ("billion laughs").
- **Nothing is extracted to disk.** Archives are read into memory with `System.IO.Compression`; no entry name is ever
  used as a path. The library writes no files at all; the command-line tool writes only into its output directory,
  with output names that contain no directory separators.
- **No child processes** and no temporary files.

## Limits

A file that exceeds a limit fails with `ResourceLimitExceededException` (which derives from
`InvalidDocumentException`), except where the table says it is scaled down or truncated instead. The process keeps
running.

| Limit | Value | Applies to | Configurable |
|---|---|---|---|
| Pixels decoded or produced | 16384 x 16384 | Image files and video frames: rejected. Document pages, PDF pages and the full-size image: scaled down to fit | `ResourceLimits.MaxPixels` |
| Input read into memory | 1 GiB (at most 2 GiB) | Every input except a seekable video stream, which is read in place | `ResourceLimits.MaxInputBytes` |
| One decompressed part | 256 MB | ZIP entries (DOCX, XLSX, PPTX, ODF, EPUB, XPS, ...), OLE streams, deflate and LZX sections. Both the declared size and the bytes that actually come out are checked | No |
| XML nesting | 256 levels | Every XML part | No |
| Recursion in readers and layout | While enough stack remains | Every recursive function checks the remaining stack before going deeper | No |
| Embedded pictures | 64 megapixels | Larger: a JPEG is decoded at 1/2, 1/4 or 1/8 scale; other formats are left out | No |
| Pages | 3000 pages (flow documents), 1000 slides, 500 XPS pages, 200 spreadsheet pages | Content beyond the limit is left out | No |
| Spreadsheet cells | 20000 rows x 500 columns per sheet | Cells beyond are not read | No |
| Page size | 1 to 14400 points per side | Sizes from the file are clamped (14400 pt is the PDF limit) | No |
| Video frame | 8192 x 4352 pixels (about 35.7 megapixels), at most 16384 per side | Checked in the stream header, during stream probing and by the decoder before it allocates. Reported as `InvalidDocumentException` | No |
| Video reading time | 60 seconds | Checked for every packet and at every point where FFmpeg polls for interruption. Exceeding it throws `TimeoutException` | `VideoOptions.Timeout` (must be positive) |
| Video packets | 4000 packets per attempt | Bounds the work for damaged files | No |
| Process-wide caches | 1024 typefaces | Cache keys come from documents (font names), so the caches stop growing | No |

Other loops whose length comes from the file (glyph clusters in XPS, HTML tag stacks, RTF groups, nested field
codes, merged cell ranges, table column spans) are bounded as well, and regular expressions applied to input use the
non-backtracking engine.

Damaged input that trips a reader in some other way (an index out of range, a number that does not parse, malformed
XML) is reported as `InvalidDocumentException`, with the original error as `InnerException`.

## What is not compiled in

Attack surface is reduced by leaving code out:

- **FFmpeg** (inside `thumbvideo`) is built with `--disable-everything --disable-network`: no protocols, no
  encoders, no muxers, no filters, no audio decoders, no hardware acceleration and no devices. Only the demuxers,
  video decoders and parsers listed in [native/thumbvideo/build.sh](../native/thumbvideo/build.sh) exist in the
  binary. Input bytes reach it only through read and seek callbacks on the .NET stream.
- **libvips**: loaders that libvips itself marks as unsuitable for untrusted input are blocked at start-up. SVG is
  the one exception: it is read from memory with no base URI, so it cannot reach other files, and its declared size
  goes through the `MaxPixels` check like any other image. libvips' operation cache is turned off.
- **Managed image readers** for PSD, TGA, EPS, camera RAW and JPEG 2000 replace native loaders the bundled libvips
  does not have (no ImageMagick, LibRaw or OpenJPEG is shipped). They are bounds-checked .NET code: every offset and
  length is checked against the input, the declared size against `MaxPixels` before pixels are allocated, TIFF
  directories are read once each, and a JPEG 2000 file is limited in tiles, precincts, packets and code-blocks.
  Embedded JPEG and TIFF previews then go through libvips like any image.
- **PDFium**: the build used has no V8 and no XFA, and FastThumb does not initialise form filling, so JavaScript in a
  PDF never runs. No password is ever supplied.
- **thumbvideo** exports three functions only; all FFmpeg symbols are hidden. The Linux builds are compiled with
  `-fstack-protector-strong -D_FORTIFY_SOURCE=2` and the macOS builds with `-fstack-protector-strong` (the toolchain
  does not support these flags for the Windows targets).

## Residual risks

The limits stop the cheap attacks. Three things cannot be solved from inside the process:

1. **Memory-safety bugs in native code can crash the process, or worse.** FFmpeg, dav1d, PDFium, libvips and the
   image codecs it bundles, FreeType (embedded fonts in XPS and DOCX) and Skia are native code. A crash there cannot
   be caught, and a memory-corruption bug may be exploitable. Keep FastThumb up to date: releases update these
   components when security fixes are published. The FFmpeg and dav1d versions are pinned in `build.sh`.
2. **Documents and PDFs have no time limit.** Only video has a deadline. Loops are bounded, but a deliberately heavy
   document can still keep one thread busy for seconds to tens of seconds. Use the `CancellationToken` to give up
   on documents (it is checked between pages and during reads); rendering a PDF page cannot be interrupted, and
   because PDFium is serialised by one process-wide lock, one slow PDF makes other PDFs wait.
3. **There is no overall memory ceiling per input.** Each limit protects one allocation; several large parts in one
   file still add up.

## Recommended deployment

For a service that accepts files from the public, **run thumbnail generation in a separate worker process or
container with resource limits, and kill it when it runs too long**:

- One worker process (or container) per input or per small batch. The parent sets a deadline (for example two
  minutes); when it passes, the parent kills the worker and reports the input as failed.
- Limit memory, CPU and the number of processes: cgroups or container limits on Linux, a Job Object on Windows.
- No network access, a non-root user, a read-only file system, and a size-limited tmpfs for `/tmp`:

  ```bash
  docker run --rm --network none --read-only --tmpfs /tmp:size=512m --memory 2g --pids-limit 256 \
    -v "$PWD/in:/in:ro" -v "$PWD/out:/out" fastthumb run /in --out /out
  ```

  The image built from the repository's [Dockerfile](../Dockerfile) already runs as a non-root user.
- Set `ResourceLimits.MaxInputBytes` and `ResourceLimits.MaxPixels` to what your service actually needs; the
  defaults are generous.
- Consider `ThumbnailOptions.EncodeInParallel = false` when many inputs are processed concurrently, to bound CPU
  use per input.
- Exception messages can contain text from native decoders and names from inside the file. Log them, but do not
  return them verbatim to end users.
- Treat the outputs as data derived from untrusted input: serve thumbnails and preview PDFs with the correct content
  type and, ideally, from a separate origin.

## Testing

The test suite ([tests/FastThumb.Tests](../tests/FastThumb.Tests)) generates hostile inputs in code, including ZIP
bombs, XML nested 100,000 levels deep, entity-expansion and external-entity documents, compound files whose header
claims more sectors than the file holds, RTF with a million unclosed groups, and random garbage, and requires each
of them to fail cleanly or finish quickly. Contributions of new hostile cases are welcome; see
[CONTRIBUTING.md](../CONTRIBUTING.md).
