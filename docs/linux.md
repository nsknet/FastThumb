# Linux, fonts and containers

FastThumb runs on Linux x64 and arm64 with the same managed code as on Windows and macOS. This page covers what a
Linux machine or container needs, how fonts are chosen, and what to expect when the same document is rendered on
different platforms.

## Requirements

- **glibc**. All native libraries are glibc builds, so use a glibc-based distribution (Debian, Ubuntu, RHEL, Fedora,
  ...). **musl/Alpine is not supported.** `thumbvideo` needs glibc 2.17 or later; the other native packages set
  their own minimum (at the time of writing, the SkiaSharp Linux build needs glibc 2.27, that is Ubuntu 18.04 or
  Debian 10 and later).
- **fontconfig**. Skia finds fonts through fontconfig (`libfontconfig.so.1`). Without it Skia cannot load.
- **Fonts**. A minimal container image has no fonts at all. Install at least the metric-compatible substitutes
  listed below; otherwise every text falls back to whatever fontconfig finds, with different widths, line breaks and
  page counts.

No other system package is required: Skia, libvips, PDFium and thumbvideo bring their own codecs and do not use the
system's libpng, libjpeg or FreeType for decoding input. (fontconfig itself uses the system FreeType to scan the
installed, trusted font files.)

On Debian or Ubuntu:

```bash
apt-get install -y --no-install-recommends \
  fontconfig fonts-liberation fonts-crosextra-carlito fonts-crosextra-caladea fonts-dejavu-core
```

Add `fonts-noto-cjk` (about 100 MB) for Chinese, Japanese and Korean text.

## Fonts

Office documents almost always ask for fonts such as Calibri, Cambria, Arial, Times New Roman or Aptos, which are
not freely redistributable and are usually absent on Linux. `Layout/FontCache.cs` resolves each requested family in
this order, on every platform:

1. **The family itself**, if it is installed.

2. **A metric-compatible clone**: a font with the same advance widths, so line breaks and page counts do not change.

   | Requested | Substitutes with identical metrics | Debian/Ubuntu package |
   |---|---|---|
   | Calibri | Carlito | `fonts-crosextra-carlito` |
   | Cambria | Caladea | `fonts-crosextra-caladea` |
   | Arial, Helvetica | Liberation Sans, Nimbus Sans | `fonts-liberation` |
   | Times New Roman, Times | Liberation Serif, Tinos, Nimbus Roman | `fonts-liberation` |
   | Courier New, Courier | Liberation Mono, Cousine, Nimbus Mono PS | `fonts-liberation` |
   | Arial Narrow | Liberation Sans Narrow | |
   | Georgia | Gelasio | |

   The mapping works in both directions: a document written with Liberation Sans is rendered with Arial on a machine
   that only has Arial.

3. **A look-alike**: a font of similar design, chosen to be as close as possible in width. Each look-alike is tried
   together with its own clones.

   | Requested | Look-alikes, in order |
   |---|---|
   | Aptos, Aptos Narrow, Calibri Light | Calibri (then Carlito) |
   | Aptos Display | Calibri Light, Calibri |
   | Cambria Math | Cambria (then Caladea) |
   | Segoe UI, Tahoma, Trebuchet MS, Lucida Grande, Lucida Sans Unicode | Arial (then Liberation Sans), DejaVu Sans |
   | Verdana, Century Gothic | DejaVu Sans, Arial |
   | Consolas, Lucida Console | DejaVu Sans Mono, Courier New |
   | Georgia | Times New Roman, DejaVu Serif |
   | Garamond, Book Antiqua, Palatino Linotype | Georgia, Times New Roman |
   | Arial Narrow, Impact | DejaVu Sans Condensed, Arial |
   | Comic Sans MS | Comic Neue, DejaVu Sans |
   | Helvetica Neue, Arial Unicode MS | Arial |
   | SimSun, FangSong, SimHei, Microsoft YaHei | Noto Serif CJK SC / Noto Sans CJK SC |
   | MS Mincho, MS Gothic | Noto Serif CJK JP, Noto Sans CJK JP |
   | Malgun Gothic, Batang | Noto Sans CJK KR, Noto Serif CJK KR |
   | DejaVu Sans, DejaVu Serif, DejaVu Sans Mono, Noto Sans | Verdana, Georgia, Consolas, Segoe UI (documents written on Linux, opened elsewhere) |

   A look-alike has different glyph widths, but **line spacing still follows the requested font**: FastThumb keeps
   the vertical metrics of the common families that are only ever replaced by a look-alike (Segoe UI, Tahoma,
   Verdana, Trebuchet MS, Georgia, Consolas, Century Gothic and others), so pages do not shrink or grow vertically.

4. **The platform default**: whatever fontconfig (or the operating system) returns for the name, usually DejaVu
   Sans on Linux.

Style names used as family names, such as "Calibri Light", "Arial Narrow" or "Segoe UI Semibold", are split into
the base family plus a weight or width, because both DirectWrite and fontconfig file them under the base family.

### Checking fonts on a machine

```bash
fastthumb info
```

lists, for a set of common office fonts, the face that is actually used and how it was chosen:

```text
fonts      requested -> used (= same metrics, ~ look-alike, ! platform default)
           Calibri            = Carlito (weight 400, width 5)
           Segoe UI           ~ Liberation Sans (weight 400, width 5)
           Symbol             ! DejaVu Sans (weight 400, width 5)
```

A `!` next to a common font means a font package is missing.

```bash
fastthumb fonts "Segoe UI" Arial "Liberation Sans" Carlito
```

prints the width of a test sentence and the line metrics (per em) of installed families, which is how look-alikes
are compared.

To use your own fonts (for example licensed copies of the original fonts), install them where fontconfig finds
them: `/usr/share/fonts`, `~/.fonts`, or a directory listed in a fontconfig configuration file selected with the
`FONTCONFIG_FILE` environment variable. FastThumb caches resolved typefaces for the lifetime of the process, so
install fonts before the process starts.

## Containers

The [Dockerfile](../Dockerfile) at the repository root is a complete example for the command-line tool:

```bash
docker build -t fastthumb .
docker build --build-arg CJK_FONTS=true -t fastthumb .     # with Chinese, Japanese and Korean fonts (about 100 MB)
docker run --rm fastthumb info
docker run --rm -v "$PWD/in:/in:ro" -v "$PWD/out:/out" fastthumb run --out /out /in
```

Build it from the repository root, after the thumbvideo library for the image's architecture has been placed in
`src/FastThumb/runtimes/` (see [native.md](native.md)).

What the image does, and what your own image should do for an application that references the FastThumb package:

- **Base image**: a Debian- or Ubuntu-based .NET runtime image (`mcr.microsoft.com/dotnet/runtime` or `aspnet`), not
  an Alpine one.
- **Packages**: `fontconfig` and the font packages above.
- **`MALLOC_ARENA_MAX=2`**: glibc gives every thread that allocates its own memory arena. With several sizes encoded
  in parallel this roughly doubles peak memory; two arenas keep about half of it for a few percent more time.
- **Only the natives of the image's architecture** are kept under `runtimes/` to save space.
- **A non-root user** (`USER $APP_UID`, the non-root user that the .NET images ship).

For untrusted input, also restrict the container from outside:

```bash
docker run --rm --network none --read-only --tmpfs /tmp:size=512m --memory 2g --pids-limit 256 \
  -v "$PWD/in:/in:ro" -v "$PWD/out:/out" fastthumb run --out /out /in
```

See [security.md](security.md#recommended-deployment).

## Differences between platforms

- **Images, PDFs and videos** are decoded by the same versions of libvips, PDFium and thumbvideo on every platform,
  so their thumbnails are expected to be identical.
- **Documents, spreadsheets and presentations** can differ slightly between platforms: the text rasteriser differs,
  and where the original fonts are missing, substitutes are used. With metric-compatible fonts, line breaks and page
  counts normally match; with look-alikes they can differ.
- **.NET 8 and .NET 10** give identical images. Preview PDFs have identical content but different compressed bytes
  (the runtimes use different zlib implementations).

## Known limitations

- **Symbol and Wingdings** (old-style bullets) and **emoji** fonts have no free substitute in the default set; such
  characters appear as boxes or as other glyphs.
- **Chinese, Japanese and Korean** text needs CJK fonts (`fonts-noto-cjk`). FastThumb does not switch fonts per
  character: text is drawn with the font resolved for its run, so characters that the font lacks (for example CJK
  text in a run formatted with a Latin font) appear as boxes, on every platform.
- **Legacy 8-bit fonts with custom encodings** (such as `.VnTime` or `VNI-Times`) are mapped to Times New Roman or
  Arial, but their text is not re-encoded, so it is rendered incorrectly, on every platform.
- **musl/Alpine** is not supported: it would need musl builds of all four native libraries.
