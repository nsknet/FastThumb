# Options

All options are immutable records. Start from a default and derive variants with `with`:

```csharp
var options = ThumbnailOptions.Default with
{
    Sizes = [1024, 256],
    Format = ThumbnailFormat.WebP,
    Video = VideoOptions.Default with { Position = 0.1 },
    Limits = ResourceLimits.Default with { MaxPixels = 8192L * 8192 },
};
var generator = new ThumbnailGenerator(options);
```

The `ThumbnailGenerator` constructor calls `ThumbnailOptions.Validate()`, which throws `ArgumentException` when a
value is out of range. Call `Validate()` yourself to check options before creating a generator. The options of a
generator are available as `ThumbnailGenerator.Options` and apply to every call.

## ThumbnailOptions

| Property | Type | Default | Range | Meaning |
|---|---|---|---|---|
| `Outputs` | `ThumbnailOutputs` | `All` | at least one flag | What to produce: any of `Images`, `PreviewPdf`, `Text`, `Placeholder`. |
| `Sizes` | `IReadOnlyList<int>` | `DefaultSizes`: 2500, 1000, 500, 300, 100 | each 1 to 65500; may be empty if `IncludeFullSize` is set | Longest side, in pixels, of each thumbnail. |
| `IncludeFullSize` | `bool` | `true` | | Also produce the full-size image. |
| `PageDpi` | `double?` | `null` (96, a PDF 72) | 1 to 1200, or `null` | Resolution of the full-size image of a page. |
| `Fit` | `ThumbnailFit` | `Contain` | `Contain`, `Cover` | Whole page in the size, or a filled and cropped box. |
| `AspectRatio` | `double` | 1 | 0.01 to 100 | Width / height of the box for `Fit = Cover`. |
| `MaxPages` | `int?` | `null` | at least 1, or `null` | Pages laid out, written to the preview PDF and read for text. |
| `Format` | `ThumbnailFormat` | `Png` | `Png`, `WebP`, `Jpeg` | Encoding of the images. |
| `Quality` | `int` | 80 | 1 to 100 | WebP and JPEG quality. |
| `PngCompressionLevel` | `int` | 3 | 0 to 9 | zlib level for PNG. |
| `EncodeInParallel` | `bool` | `true` | | Encode the sizes of one input concurrently. |
| `Video` | `VideoOptions` | `VideoOptions.Default` | not null | How a frame is taken from a video. |
| `Limits` | `ResourceLimits` | `ResourceLimits.Default` | not null | Limits applied to untrusted input. |
| `FileExtension` | `string` (read-only) | `"png"` | | The usual extension of `Format`, without the dot: `"png"`, `"webp"` or `"jpg"`. |

### Outputs

`ThumbnailOutputs` is a flags enum:

- `Images`: the thumbnails (`ThumbnailResult.Images`).
- `PreviewPdf`: a PDF of every page (`ThumbnailResult.PreviewPdf`). Only paginated documents have one: a PDF, an image
  or a video does not. Requesting only `PreviewPdf` skips rendering and encoding the images.
- `All`: both. `All` does not include the two flags below, so existing code keeps doing the same work.
- `Text`: the plain text of each page (`ThumbnailResult.PageTexts` and `ThumbnailResult.Text`). See [Text](#text).
- `Placeholder`: a ThumbHash and the average colour (`ThumbnailResult.Placeholder`). See [Placeholder](#placeholder).

Combine flags with `|`, for example `ThumbnailOutputs.All | ThumbnailOutputs.Text`. Requesting only `Text` lays the
document out but draws and encodes nothing; requesting only `Placeholder` draws page 1 at 100 px.

For a PDF, image or video input with `Outputs = PreviewPdf`, the result has no images and no preview PDF.

### Text

With `ThumbnailOutputs.Text`, `ThumbnailResult.PageTexts` holds one string per page, page 1 first, and
`ThumbnailResult.Text` joins them with a blank line. It is meant for search indexing, previews in a list, or feeding a
language model, not for round-tripping the document.

```csharp
var generator = new ThumbnailGenerator(ThumbnailOptions.Default with { Outputs = ThumbnailOutputs.Text });
var result = generator.GenerateFromFile("contract.docx");
index.Add(result.Text);
```

| Input | Where the text comes from |
|---|---|
| Document, spreadsheet, presentation, XPS | The laid-out pages: what the preview PDF shows, in reading order. |
| PDF | PDFium's text layer. A scanned page without one gives an empty string. |
| Image, video | Nothing: `PageTexts` is empty. |

Lines end with `\n`. A gap of more than a line becomes a blank line. Text far apart on one line, such as spreadsheet
cells or a tab stop, is separated by a tab. Headers and footers are included, because they are on the page. Text
clipped by a spreadsheet cell is clipped in the text too. A PDF's text is read from at most 3000 pages unless
`MaxPages` says otherwise. `PageTexts` is `null` when `Text` was not requested.

### Placeholder

With `ThumbnailOutputs.Placeholder`, `ThumbnailResult.Placeholder` describes page 1, the image or the video frame in a
few bytes, for a web or mobile UI to show while the real thumbnail loads:

- `ThumbHash` (and `ThumbHashBase64`): about 25 bytes in the [ThumbHash](https://evanw.github.io/thumbhash/) format.
  Decoders exist for JavaScript, Swift, Kotlin, Rust, Go and others. They return a blurred picture of about 32 px
  with the right aspect ratio and colours, and transparency when the picture has some.
- `AverageColor`: the average colour as CSS hex, `"#rrggbb"`, for a plain background.

The placeholder does not depend on `Sizes` or `Format`. It is computed from a copy of at most 100 x 100 px, so it adds
little time.

```csharp
var options = ThumbnailOptions.Default with { Sizes = [300], Outputs = ThumbnailOutputs.Images | ThumbnailOutputs.Placeholder };
var result = new ThumbnailGenerator(options).Generate(upload, fileName);
await db.SaveAsync(id, result.Find(300)!.Data, result.Placeholder!.ThumbHashBase64, result.Placeholder.AverageColor);
```

In the browser, with the `thumbhash` npm package:

```js
import { thumbHashToDataURL } from "thumbhash";
img.src = thumbHashToDataURL(Uint8Array.from(atob(row.thumbHash), c => c.charCodeAt(0)));
```

### Sizes and IncludeFullSize

Each entry of `Sizes` is the longest side of one thumbnail; the aspect ratio is kept. The result lists the images in
this order: the full-size image first (if `IncludeFullSize`), then one image per entry of `Sizes`, in the order given.
Look one up with `ThumbnailResult.Find(size)` or `ThumbnailResult.FullSize`.

What "full size" means depends on the input:

| Input | Full-size image |
|---|---|
| Document, spreadsheet, presentation, XPS | Page 1 at `PageDpi`, by default 96 DPI |
| PDF | Page 1 at `PageDpi`, by default 72 DPI (one pixel per point) |
| Image | The image at its own size |
| Video | The frame at its own size (sample aspect ratio applied) |

Every full-size image is scaled down, if needed, to fit `ResourceLimits.MaxPixels`.

`PageDpi = 300` gives a print-quality full-size page (an A4 page is 2480 x 3508 px, a Letter page 2550 x 3300 px). Page 1
is drawn once, at the larger of the full size and the largest entry of `Sizes`, and every image is cut from that one
drawing, so the full-size image is never enlarged from a smaller one. Memory and encoding time grow with it: a Letter
page at 300 DPI is drawn at 2550 x 3300 px (34 MB of pixels, plus a copy while the images are made), where the default
sizes alone need 1932 x 2500 px.

```csharp
var printQuality = ThumbnailOptions.Default with { PageDpi = 300 };
```

An image or a video frame is never enlarged: a size larger than the original yields the original size. A document
page is rendered at whatever size is asked for.

At least one image must be requested when `Outputs` includes `Images`: an empty `Sizes` with
`IncludeFullSize = false` is rejected.

### Fit and AspectRatio

`ThumbnailFit` says how each entry of `Sizes` fills its box:

- `Contain` (default): the whole page or picture; its longest side is the size.
- `Cover`: exactly a box of `AspectRatio`, filled and cropped, for grids of equal tiles and avatars. The longer side of
  the box is the size: with `AspectRatio = 16.0 / 9`, size 320 gives 320 x 180; with `0.75`, size 400 gives 300 x 400.

| Input | What `Cover` keeps |
|---|---|
| Document, spreadsheet, presentation, XPS, PDF | The top of page 1 (the left edge if the page is wider than the box), where the title is. The page is drawn large enough to fill the box, so the crop is sharp. |
| Image, video | The centre. |

An image or a video frame is never enlarged: if it cannot fill the box, the result is the largest box of the same
shape it can fill (a 320 x 200 image with a square size of 300 gives 200 x 200). The full-size image is never cropped.

### MaxPages

`MaxPages` stops a document after that many pages: they are laid out, written to the preview PDF and read for text,
and the rest is skipped. Thumbnails only need page 1, so `MaxPages = 1` with `Outputs = Images` makes a long document
much cheaper. The thumbnail of page 1 is identical with or without the limit.

```csharp
var thumbnailsOnly = ThumbnailOptions.Default with { Outputs = ThumbnailOutputs.Images, MaxPages = 1 };
```

`ThumbnailResult.HasMorePages` is `true` when pages were left out. For a document, `PageCount` is then the number of
pages laid out, since the rest was never laid out. A PDF still reports all its pages: PDFium knows the count without
reading them, and `MaxPages` only limits how many pages are read for text. The built-in limits (3000 pages for a flow
document, 200 printed pages for a spreadsheet, the slide limit for a presentation, 500 pages for XPS) apply either way.

### Format

`ThumbnailFormat`:

- `Png`: lossless. Uses `PngCompressionLevel`.
- `WebP`: lossy, uses `Quality`. Usually several times smaller than PNG.
- `Jpeg`: baseline JPEG, uses `Quality`. JPEG has no transparency: transparent areas are flattened onto white.

An image larger than the format allows (longest side above 16383 px for WebP, above 65500 px for JPEG) is written as
PNG. `Thumbnail.Format` always reports the encoding that was actually written.

### Quality

WebP and JPEG quality, 1 (smallest) to 100 (best). Ignored for PNG.

### PngCompressionLevel

The zlib level used for PNG, 0 (no compression, fastest) to 9 (smallest, slowest). The default, 3, is noticeably
faster than level 6 for files that are only slightly larger.

### EncodeInParallel

The PNG, WebP and JPEG encoders are single-threaded. With `EncodeInParallel = true`, the sizes of one input are
encoded at the same time, which shortens each call at the cost of more CPU. A service that already processes many
inputs concurrently can turn it off to reduce CPU contention.

## VideoOptions

| Property | Type | Default | Range | Meaning |
|---|---|---|---|---|
| `Position` | `double` | 0.5 | 0 to 1 | Where the frame is taken, as a fraction of the duration (0 = start, 0.5 = middle, 1 = end). |
| `Timeout` | `TimeSpan` | 60 seconds | greater than zero, at most `int.MaxValue` milliseconds (about 24 days) | Upper bound for reading one video. |

Notes:

- The native library caps the position at 0.98 of the duration, so a position of 1 takes a frame near the end.
- The decoder seeks to the keyframe before the position and decodes forward for at most 1.5 seconds; a heavy video
  with long groups of pictures can therefore yield a frame somewhat before the requested position. If nothing can be
  decoded there, the first frame is used.
- A damaged file that keeps the decoder busy fails with `TimeoutException` once `Timeout` has elapsed. Zero or a
  negative value is rejected, because it would mean no deadline at all.

## ResourceLimits

| Property | Type | Default | Range | Meaning |
|---|---|---|---|---|
| `MaxPixels` | `long` | 16384 x 16384 (268,435,456) | at least 1 | Largest picture, in pixels, that is decoded or produced: an image file, a video frame, a rendered page, the full-size image. |
| `MaxInputBytes` | `long` | 1 GiB | 1 byte to 2 GiB (`int.MaxValue`) | Largest input read into memory. |

- An **image file** or **video frame** above `MaxPixels` fails with `ResourceLimitExceededException`. The pixel count
  is checked from the header, before decoding: a few hundred bytes can declare a 60000 x 60000 image.
- A **page** (document, PDF, XPS) above `MaxPixels` is rendered scaled down to fit instead.
- `MaxInputBytes` applies to every input except a seekable video stream, which is read in place and never buffered.
  An input above it fails with `ResourceLimitExceededException`, checked while reading (a non-seekable stream is not
  read past the limit).

Other limits are fixed and cannot be configured; see [security.md](security.md#limits).

## CancellationToken

Every `Generate*` method takes an optional `CancellationToken`. It is checked:

- before the call starts;
- between pages and in recursive readers while a document is read and laid out;
- on every read of a decompressed part (ZIP entry, OLE stream, deflate or LZX section);
- between encoded sizes;
- on every read the video decoder makes from the stream.

- while PDFium draws a PDF page (it stops between drawing operations and lets go of its lock).

A cancelled call throws `OperationCanceledException`. Encoding a single image, and drawing a page of a document other
than a PDF, are not interrupted once started.
