# The `fastthumb` command-line tool

`fastthumb` is a thin front end over the FastThumb library: it makes thumbnails and preview PDFs for single files
or whole directory trees. It behaves the same on Windows, Linux and macOS (see [linux.md](linux.md) for fonts and
containers).

## Build and run

The tool lives in the source tree (`src/FastThumb.Cli`) as a worked example of the API and a way to try a format or
a platform. It is not published as a package: build it from a checkout of the repository.

```bash
dotnet run --project src/FastThumb.Cli -c Release -- --help
```

To get an executable in a folder of your choice:

```bash
dotnet publish src/FastThumb.Cli -c Release -o ./fastthumb-cli
```

The executable is `fastthumb-cli/FastThumb.Cli` (`FastThumb.Cli.exe` on Windows). The tool targets .NET 8 and rolls
forward to any newer .NET that is installed. The examples below write `fastthumb` for that executable; with
`dotnet run`, put the same arguments after `--`.
Video needs the thumbvideo library for your platform (see [native.md](native.md)).

## Commands

| Command | What it does |
|---|---|
| `fastthumb run <input>... -o <dir> [options]` | Makes thumbnails and/or preview PDFs. |
| `fastthumb inspect <file>...` | Prints one JSON line per file with the format, page count, size and document properties, without rendering anything. |
| `fastthumb info` | Prints the tool and library versions, the operating system, runtime identifier, .NET version and CPU count, the versions of libvips, Skia and thumbvideo, whether PDFium renders a page, and how common office fonts resolve on this machine. |
| `fastthumb fonts <family>...` | Prints, for each installed family, the width of a test sentence and the ascent, descent and leading per em; `not installed` for a family that is missing. |
| `fastthumb --version` (`-V`, `version`) | Prints the tool version, library version, .NET version and runtime identifier. |
| `fastthumb --help` (`-h`, `help`, or no arguments) | Prints the usage text. |

## `run`

```text
fastthumb run <input>... -o <dir> [options]
```

`<input>` is a file or a directory. A directory is read recursively; its files are processed in ordinal order of
their paths. Several inputs can be given. An input that does not exist is a command-line error.

### Options

| Option | Default | Meaning |
|---|---|---|
| `-o`, `--out <dir>` | required | Output directory; created if missing. |
| `-m`, `--mode <what>` | `both` | `images`, `pdf`, `both` or `none`. `pdf` is the preview PDF of every page; only documents have one (a PDF, an image or a video does not). `none` is for `--text` or `--placeholder` alone. |
| `--text` | | Also write the text of every page to `<dir>/<name>.txt`, pages separated by a form feed (`\f`). |
| `--placeholder` | | Also make a ThumbHash and the average colour; shown with `--json` and `--verbose`. |
| `--max-pages <n>` | all | Lay out, write and read the text of at most `n` pages. |
| `-s`, `--sizes <list>` | `full,2500,1000,500,300,100` | Longest side of each image in pixels (1 to 65500), comma separated. `full` is the page at 96 DPI, a PDF page at 72 DPI, an image or a video frame at its own size. Images are never enlarged. |
| `--fit <how>` | `contain` | `contain`: the whole page, longest side = size. `cover`: fill the box and crop, keeping the top of a page or the centre of a picture. |
| `--aspect <w:h>` | `1:1` | Shape of the box for `--fit cover`: `16:9`, `3:4`, or a number such as `0.75` (0.01 to 100). |
| `-f`, `--format <format>` | `png` | `png`, `webp` or `jpeg` (`jpg`). |
| `-q`, `--quality <1-100>` | `80` | WebP and JPEG quality. |
| `--png-compression <0-9>` | `3` | zlib level for PNG; lower is faster and larger. |
| `--video-position <0-1>` | `0.5` | Where the video frame is taken (0.5 is the middle). |
| `--video-timeout <seconds>` | `60` | Give up on a video after this long (1 to 86400). |
| `-j`, `--parallel <n>` | `1` | Files processed at the same time (1 to 256). |
| `--no-parallel-encode` | | Encode the sizes of one file one after another: less CPU, more time per file. Worth using with a high `--parallel`. |
| `-i`, `--include <exts>` | all files | Only process files with these extensions when reading a directory, for example `docx,xlsx,pdf`. Files given directly are always processed. |
| `--skip-existing` | | Skip a file whose outputs are all there already (to resume an interrupted run). |
| `--json` | | Print one JSON object per file on standard output; the summary goes to standard error. |
| `--quiet` | | Print only failures and the summary. |
| `-v`, `--verbose` | | Add stage timings per file: render, pdf, images. |
| `--no-color` | | No ANSI colours. Colours are also off when the output is redirected, when `NO_COLOR` is set, and with `--json`. |

Options can be written as `--name value` or `--name=value`. Everything after `--` is treated as an input, so file
names that start with `-` can be passed. The sizes are processed largest first, and a size listed twice is used once.

### Output files

```text
<dir>/<name>.<size>.<png|webp|jpg>
<dir>/<name>.preview.pdf
<dir>/<name>.txt                      with --text
```

- `<name>` is the path of the file below the input directory with `/` replaced by `__` (so
  `in/reports/q1.docx` becomes `reports__q1.docx`); for a file given directly it is the file name.
- `<size>` is the number of pixels, or `full`.
- The extension is that of the requested format, `png`, `webp` or `jpg`. An image too large for WebP or JPEG is
  encoded as PNG by the library and then gets the `png` extension.
- All outputs go directly into `<dir>`; no sub-directories are created.

If two inputs would produce the same `<name>` (for example `a/b.docx` and `a__b.docx`, or the same file name under
two inputs), the command fails before processing anything, instead of letting one silently overwrite the other. The
comparison ignores case.

Each output is written to a temporary file next to its target and then renamed over it, so an interrupted run never
leaves a truncated output that `--skip-existing` would take for a finished one.

With `--skip-existing`, a file is skipped when every requested image exists, the text file exists if `--text` is
given, and, if a preview PDF is requested and the format has one, the preview PDF exists.

### Console output

```text
fastthumb 1.0.0 · .NET 10.0.0 · linux-x64
70 file(s) → /data/out
images + preview PDF: full,2500,1000,500,300,100 as png (compression 3), 1 at a time

  ok    001_report.doc     Doc      4 pages   352 ms   6 img   1.3 MB   pdf   173 KB
  ok    038_api.docx       Docx   120 pages   299 ms   6 img    93 KB   pdf   600 KB
  FAIL  064_scan.tif       UnsupportedFormatException: Image cannot be decoded: ...

70 of 70 file(s) in 7.81 s (9.0 files/s): 69 ok, 1 failed, 0 skipped
written: 414 images (52.1 MB), 40 preview PDFs (12.3 MB)
by format: Pdf 30 (median 66 ms) · Docx 15 (median 120 ms) · ...
slowest: 056_notes.txt (633 ms)
```

A file is reported as `ok`, `skip` (outputs already there, or nothing to produce, such as `--mode pdf` for an image)
or `FAIL` (with the exception type and message).

### JSON output

With `--json`, each processed file produces one line on standard output:

```json
{"file":"reports__q1.docx","status":"ok","format":"Docx","pages":12,"ms":143,"bytes":1532144,"images":6,"pdfBytes":88211,"timings":{"render":61.2,"pdf":20.4,"images":58.9}}
```

| Field | Type | Meaning |
|---|---|---|
| `file` | string | The output name `<name>` (not the full input path). |
| `status` | string | `ok`, `error` or `skipped`. |
| `format` | string | The `DocumentFormat` recognised from the content. Absent for errors and for files skipped by `--skip-existing`. |
| `pages` | number | Page count. Absent when `format` is absent. |
| `ms` | number | Wall-clock time for the file, in whole milliseconds, including writing the outputs. |
| `bytes` | number | Total size of the images written. |
| `images` | number | Number of images written. |
| `pdfBytes` | number | Size of the preview PDF, 0 if none. |
| `textBytes` | number | Size of the text file, with `--text`. Absent when there is no text. |
| `morePages` | boolean | `true` when `--max-pages` left pages out. Absent otherwise. |
| `placeholder` | object | With `--placeholder`: `thumbHash` (Base64) and `color` (`"#rrggbb"`). |
| `timings` | object | `render`, `pdf` and `images` in milliseconds (one decimal): the library's `ThumbnailTimings`. Absent for errors and for files skipped by `--skip-existing`. |
| `error` | string | Exception type and message, only when `status` is `error`. |
| `note` | string | Why the file was skipped, only when `status` is `skipped`. |

Fields with no value are omitted. Lines appear in completion order, which with `--parallel` above 1 is not the input
order. The summary is printed on standard error, so standard output contains only JSON lines.

### Exit status

| Code | Meaning |
|---|---|
| 0 | Every file was processed (or skipped). |
| 1 | At least one file failed. |
| 2 | Bad command line: unknown command or option, missing or invalid value, missing `--out`, no input, input not found, clashing output names. A message is printed on standard error. |
| 130 | Interrupted with Ctrl+C. Files already in progress are finished; files not yet started are left out. |

### Examples

```bash
fastthumb run report.docx -o out
```

```bash
fastthumb run in/ -o out -m images -s 1000,300 -f webp -q 75 -j 4
```

```bash
fastthumb run in/ -o out -m pdf -i docx,doc,xlsx,pptx
```

```bash
fastthumb run in/ -o out --json > run.jsonl
```

```bash
fastthumb run in/ -o out --skip-existing -j 8 --no-parallel-encode --quiet
```

Text of every document for a search index, with no images:

```bash
fastthumb run in/ -o out -m none --text -j 4
```

Square 300 px tiles of page 1 only, with placeholders in the JSON lines:

```bash
fastthumb run in/ -o out -m images -s 300 --fit cover --max-pages 1 --placeholder --json > tiles.jsonl
```

## `inspect`

```bash
fastthumb inspect report.pdf clip.mp4
```

```json
{"file":"report.pdf","format":"Pdf","pages":13,"width":417,"height":583,"title":"Annual report"}
{"file":"clip.mp4","format":"Video","pages":1,"width":1920,"height":1080,"durationSeconds":12.5,"videoCodec":"h264"}
```

Fields are those of `DocumentInfo` (see the main README); a field with no value is omitted. A file that cannot be
read gives `{"file":...,"error":"<type>: <message>"}` and the exit status 1.

## `info`

```bash
fastthumb info
```

Use it to check an installation, especially a new container image: it loads every native library and lists the font
that is used for each common office font, marked `=` (the font itself or one with the same metrics), `~` (a
look-alike) or `!` (the platform default, which usually means a font package is missing). See
[linux.md](linux.md#fonts).

## `fonts`

```bash
fastthumb fonts "Segoe UI" Arial "Liberation Sans" Carlito
```

At least one family name is required.

## Environment variables

| Variable | Effect |
|---|---|
| `NO_COLOR` | Any non-empty value turns ANSI colours off. |
| `THUMBVIDEO_LOG` | When set, the video decoder writes its diagnostics to standard error; useful for a video that will not open. |

## Docker

The repository's [Dockerfile](../Dockerfile) builds an image whose entry point is the tool:

```bash
docker build -t fastthumb .
docker run --rm -v "$PWD/in:/in:ro" -v "$PWD/out:/out" fastthumb run --out /out /in
```
