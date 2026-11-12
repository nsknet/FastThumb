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
| `Outputs` | `ThumbnailOutputs` | `All` | at least one flag | What to produce: `Images`, `PreviewPdf` or `All`. |
| `Sizes` | `IReadOnlyList<int>` | `DefaultSizes`: 2500, 1000, 500, 300, 100 | each 1 to 65500; may be empty if `IncludeFullSize` is set | Longest side, in pixels, of each thumbnail. |
| `IncludeFullSize` | `bool` | `true` | | Also produce the full-size image. |
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
- `All`: both.

For a PDF, image or video input with `Outputs = PreviewPdf`, the result has no images and no preview PDF.

### Sizes and IncludeFullSize

Each entry of `Sizes` is the longest side of one thumbnail; the aspect ratio is kept. The result lists the images in
this order: the full-size image first (if `IncludeFullSize`), then one image per entry of `Sizes`, in the order given.
Look one up with `ThumbnailResult.Find(size)` or `ThumbnailResult.FullSize`.

What "full size" means depends on the input:

| Input | Full-size image |
|---|---|
| Document, spreadsheet, presentation, XPS | Page 1 at 96 DPI |
| PDF | Page 1 at 72 DPI (one pixel per point) |
| Image | The image at its own size |
| Video | The frame at its own size (sample aspect ratio applied) |

Every full-size image is scaled down, if needed, to fit `ResourceLimits.MaxPixels`.

An image or a video frame is never enlarged: a size larger than the original yields the original size. A document
page is rendered at whatever size is asked for.

At least one image must be requested when `Outputs` includes `Images`: an empty `Sizes` with
`IncludeFullSize = false` is rejected.

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

A cancelled call throws `OperationCanceledException`. Rendering a PDF page in PDFium and encoding a single image are
not interrupted once started.
