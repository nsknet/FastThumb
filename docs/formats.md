# Supported formats

FastThumb recognises the format from the content of the input, not from its name (see
[architecture.md](architecture.md#1-detect)). The file name or extension passed to `Generate` only breaks ties between
plain-text formats and identifies a few formats that are recognised by extension only. The recognised format is
reported as `ThumbnailResult.Format` (a `DocumentFormat` value), and `ThumbnailGenerator.SupportedExtensions` lists
the usual extensions.

Thumbnails are made from the first page. A preview PDF of every page is produced for every format in the
"Documents", "Spreadsheets", "Presentations and drawings" and XPS sections; PDF, image and video inputs have no preview PDF.

Fidelity is aimed at thumbnails and previews: layout, fonts, colours, tables and pictures are reproduced closely
enough to recognise a document, but FastThumb is not a full office suite. The limitations below are the known gaps.

## Documents

| Format | `DocumentFormat` | Extensions | Rendered | Not rendered / limitations |
|---|---|---|---|---|
| Office Open XML document | `Docx` | `.docx`, `.docm`, `.dotx`, `.dotm` | Styles and theme fonts/colours, paragraph and character formatting, numbered and bulleted lists, tables, hyperlinks, content controls, tracked insertions, inline and floating pictures (DrawingML and legacy VML), text boxes, shapes, headers and footers (including a separate first-page header), page size and margins of the first section | Charts, SmartArt, footnotes, comments, equations; macros are never read |
| Flat OPC document | `FlatOpc` | `.xml` | Same as DOCX (the package as one XML file) | Same as DOCX |
| Word 2003 XML | `WordMl` | `.xml`, `.wml` | Read as DOCX parts | Same as DOCX |
| Word 97-2003 binary | `Doc` | `.doc`, `.dot` | Fonts, styles, character, paragraph and table properties, first section page setup | Lists numbering, pictures, headers and footers, field codes, macros. Encrypted files are rejected. Word 95 and older are rejected |
| OpenDocument text | `Odt` | `.odt`, `.ott` | Paragraphs, headings, lists, tables, spans, styles from `content.xml` and `styles.xml` | Pictures and drawing frames, headers and footers. Flat XML text documents (`.fodt`) are not recognised (see below) |
| Rich Text Format | `Rtf` | `.rtf` | Font and colour tables, character and paragraph formatting, Unicode and code-page escapes, simple tables, page size and margins | Pictures, shapes, headers, footers, footnotes, field instructions |
| HTML | `Html` | `.htm`, `.html`, `.xhtml` | Block structure, headings, inline formatting from tags and `style` attributes, lists, tables (with `colspan` and width hints); the charset from the `<meta>` tag | No CSS style sheets or cascade, no scripts, no images, no external resources |
| MHTML web archive | `Mhtml` | `.mht`, `.mhtml` | The first `text/html` part, as HTML | Same as HTML |
| Markdown | `Markdown` | `.md`, `.markdown` | CommonMark and GitHub (GFM) extensions, simplified: ATX and setext headings, paragraphs with hard breaks, block quotes, nested bulleted, numbered and task lists, fenced and indented code (fenced code in C#, Python, JavaScript/TypeScript/JSON, HTML/XML, CSS, shell and a few C-like languages gets GitHub-style colours: keywords, strings, comments, numbers, names, tags), pipe tables with column alignment, rules; `**strong**`, `*emphasis*`, `~~strikethrough~~`, `` `code` ``, links, autolinks and bare URLs (drawn as link-coloured text), escapes and HTML entities. Only page 1 is parsed | Images are shown as their alt text; HTML tags are removed (their text is kept, `<br>` breaks the line, `align="center"` on a block is honoured); no footnotes or math; highlighting is word-based, not a parser, so unusual code can be coloured wrongly; emoji need a font that has them |
| Plain text | `Text` | `.txt` and anything unrecognised | Every line in a monospaced font (Courier New or a metric-compatible substitute); tabs as four spaces | UTF-8 unless a byte-order mark says otherwise |
| EPUB | `Epub` | `.epub` | The spine chapters in order, through the HTML reader | Same as HTML; cover images are not drawn |
| Mobipocket / Kindle | `Mobi` | `.mobi`, `.azw`, `.azw3` | PalmDOC-compressed and uncompressed text records, through the HTML reader | HUFF/CDIC-compressed books and encrypted (DRM) books are rejected |
| Compiled HTML Help | `Chm` | `.chm` | HTML topics from the LZX-compressed content section, through the HTML reader | Same as HTML |
| Hangul Word Processor 5.x | `Hwp` | `.hwp` | Text only: paragraphs | No formatting or layout. Password-protected files are rejected |
| WordPerfect 6 and later | `WordPerfect` | `.wpd` | Text only: paragraphs with bold, italic and underline | No layout. Encrypted files and WordPerfect 5.x are rejected |
| Microsoft Works word processor | `Works` | `.wps` | Text only: paragraphs | No formatting or layout |

The HWP, WordPerfect and Works readers recover text only and have been tested with less real-world material than the
other readers.

Flow documents are paginated up to 3000 pages; content beyond that is not laid out.

## Spreadsheets

Spreadsheets are rendered as printed pages: visible sheets in order, the print area if one is set (otherwise every
cell that has content, a fill or a border), with the sheet's paper size, orientation, margins, scale, fit-to-page and
manual page breaks. At most 200 pages are produced, and each sheet is read up to 20000 rows and 500 columns.

| Format | `DocumentFormat` | Extensions | Rendered | Not rendered / limitations |
|---|---|---|---|---|
| Office Open XML workbook | `Xlsx` | `.xlsx`, `.xlsm`, `.xltx` | Shared strings, fonts, fills, borders, alignment and wrapping, number formats, theme and indexed colours, column widths, row heights, hidden rows and columns, merged cells, page setup, print area, manual breaks. Formula cells show the value cached in the file | Charts, images, conditional formatting, rich text inside a cell (shown as plain text). Formulas are not recalculated |
| Excel binary workbook | `Xlsb` | `.xlsb` | Same model as XLSX, from the binary records | Table styles, charts, images, conditional formatting |
| Excel 97-2003 (BIFF8) | `Xls` | `.xls` | Shared strings, fonts, number formats, cell formats, palette, column and row geometry, numbers, strings, cached formula results, booleans and errors, merges, page setup, margins, manual breaks | Encrypted workbooks and BIFF5 (Excel 95) are rejected |
| OpenDocument spreadsheet | `Ods` | `.ods`, `.ots` | Displayed cell text, fonts, fills, borders, alignment, wrapping, column widths, row heights, merges, page layout | Flat XML spreadsheets (`.fods`) are not recognised (see below) |
| Comma- or tab-separated values | `Csv` | `.csv`, `.tsv` | Every value as text; numbers right-aligned. The delimiter is detected from the first line | Recognised only by extension (otherwise read as plain text) |

Number formats cover General, fixed decimals, thousands separators, percent, scientific, currency and literal
prefixes and suffixes, negative and zero sections, and date and time patterns, with en-US symbols. Fractions and
conditional sections fall back to General.

## Presentations and drawings

One page per slide or drawing page, at the document's page size, up to 1000 pages.

| Format | `DocumentFormat` | Extensions | Rendered | Not rendered / limitations |
|---|---|---|---|---|
| Office Open XML presentation | `Pptx` | `.pptx`, `.pptm`, `.potx`, `.ppsx` | Slide, layout, master and theme inheritance; shape geometry, solid fills, outlines, pictures and text with placeholder, list-style and theme inheritance | Charts, SmartArt, embedded OLE objects, effects, rotation, picture cropping; gradients use their first stop |
| PowerPoint 97-2003 | `Ppt` | `.ppt`, `.pps`, `.pot` | Shape anchors (including nested groups), fills, outlines, pictures and text with paragraph and character runs on top of the master's text styles | Master shapes and backgrounds. PowerPoint 95 files are rejected |
| OpenDocument presentation | `Odp` | `.odp`, `.otp` | Pages, frames, shapes, pictures, text with ODF styles | Master-page decoration |
| Flat OpenDocument presentation | `Fodp` | `.fodp` | Same as ODP, from a single XML file | Same as ODP |
| OpenDocument drawing | `Odg` | `.odg`, `.otg` | Same as ODP: every drawing page with its shapes, pictures and text, at the drawing's page size | Same as ODP |
| Flat OpenDocument drawing | `Fodg` | `.fodg` | Same as ODG, from a single XML file | Same as ODG |

## Fixed-layout documents

| Format | `DocumentFormat` | Extensions | Rendered | Not rendered / limitations |
|---|---|---|---|---|
| PDF | `Pdf` | `.pdf`; also `.ai` from Illustrator 9 on | Page 1 rendered by PDFium, including annotations. The full-size image is the page at 72 DPI. `PageCount` is the document's page count. An Illustrator file saved with "Create PDF Compatible File" off (its only page is a notice) shows the thumbnail Illustrator keeps in its XMP metadata instead (about 256 px) | No preview PDF (the input already is one). Password-protected PDFs fail with `InvalidDocumentException`. No JavaScript or XFA forms. An Illustrator file without PDF content and without an XMP thumbnail fails with `UnsupportedFormatException` |
| XPS / OpenXPS | `Xps` | `.xps`, `.oxps` | Nested canvases with transforms and opacity, glyph runs with embedded (including obfuscated) fonts, explicit glyph indices, advances and offsets, paths, solid and image brushes, resource dictionaries. Up to 500 pages | Gradients use their first stop; clipping and tiling are ignored |

## Images

Images are decoded by libvips (BMP and ICO by Skia), EXIF orientation is applied, and they are never enlarged. A
picture larger than `ResourceLimits.MaxPixels` is rejected with `ResourceLimitExceededException` before it is decoded.

PSD, TGA, EPS, PostScript-based Illustrator, camera RAW, JPEG 2000, CorelDRAW and DICOM have no loader in the bundled
libvips or in Skia. FastThumb reads them in managed code, with no added dependency. Where drawing the file would take a
whole engine, it uses the picture the file already carries: the composite image of a PSD, the preview of an EPS, the
JPEG preview of a RAW file, the preview of a CorelDRAW drawing. Embedded JPEG, TIFF, PNG and BMP previews are then
decoded by libvips (BMP by Skia) like any other image.

| Format | Extensions | Notes |
|---|---|---|
| JPEG | `.jpg`, `.jpeg` | |
| PNG | `.png` | |
| GIF | `.gif` | First frame |
| WebP | `.webp` | |
| TIFF | `.tif`, `.tiff` | First page |
| HEIF / AVIF | `.heic`, `.heif`, `.avif` | Recognised by the still-image brands of the ISO media container. Decoding depends on the codecs included in the libvips build shipped by NetVips.Native; an image whose codec is missing fails with `UnsupportedFormatException` |
| SVG | `.svg` | Recognised by extension only. Rendered by librsvg from memory, with no base URI (no external files or URLs) |
| Compressed SVG | `.svgz` | Recognised by the gzip signature and the extension. Inflated in memory within `ResourceLimits.MaxInputBytes` (a larger result fails with `ResourceLimitExceededException`), then drawn as SVG |
| BMP | `.bmp` | Recognised by its headers (the "BM" signature, a known header size, one plane, a BMP bit depth) whatever the name, and by extension otherwise. Windows and OS/2 bitmaps, 1 to 32 bits, RLE4/RLE8, bit fields, top-down |
| ICO | `.ico` | Recognised by extension only |
| Photoshop PSD / PSB | `.psd`, `.psb` | The composite image Photoshop stores after the layers. A file saved with "Maximize Compatibility" off stores only a placeholder there (image resource 1057 says so): its layers are composited instead, from the pixels each layer stores, with transparency, user masks, opacity and fill opacity, visibility, clipping, groups (blended as one, or passed through) and the normal, multiply, screen, overlay, darken and lighten blend modes (other modes are drawn as normal). Layer effects, adjustment and fill layers, vector masks and smart-object settings are not applied; layers of 32-bit files are not composited, and a file whose layers cannot be read shows its thumbnail. Bitmap, greyscale, duotone (as grey), indexed, RGB, CMYK (converted without a colour profile) and Lab, at 1, 8 or 16 bits, raw, PackBits or ZIP. Transparency only when the file marks the composite as transparent. 32-bit and multichannel files use the JPEG thumbnail in the image resources (about 160 px), or fail with `UnsupportedFormatException` without one |
| TGA | `.tga` | Recognised by extension, or by the TGA 2.0 footer. Colour-mapped, true-colour and greyscale, plain or run-length encoded, 8 to 32 bits |
| EPS | `.eps`, `.epsf`, `.epsi` | The preview the file carries: the TIFF of a DOS EPS, an EPSI preview, a Photoshop or an Illustrator thumbnail. PostScript itself is not drawn (that needs a PostScript interpreter): an EPS without a preview fails with `UnsupportedFormatException`, except one Illustrator wrote, whose artwork is drawn from its paths (see the next row). `Inspect` reads the size from `%%BoundingBox`, in points. Plain PostScript (`.ps`) is not an image |
| Illustrator, PostScript-based | `.ai` (Illustrator 8 and older) | Recognised by the extension, or by an Illustrator `%%Creator` or `%AIn_` comment. Illustrator's thumbnail if the file has one; otherwise the artwork drawn from Illustrator's own drawing operators: paths, fills and strokes, compound paths, clipping groups, grey, CMYK, custom and RGB colours, line width, joins, caps and dashes; hidden layers and guides are left out. Text, gradients, patterns and placed or raster images are not drawn. The page is `%%HiResBoundingBox` or `%%BoundingBox`, on white. Illustrator 9 and later write PDF-based files, which are read as PDF |
| Camera RAW | `.cr2`, `.cr3`, `.nef`, `.nrw`, `.arw`, `.srf`, `.sr2`, `.dng`, `.orf`, `.rw2`, `.raf`, `.pef`, `.srw`, `.erf`, `.3fr` and more | The largest JPEG preview in the file, turned by the RAW file's orientation; the sensor data is not developed. ORF, RW2, RAF, CR3, CR2 and DNG are recognised by content; other TIFF-based RAW files (NEF, ARW, PEF...) need their extension, and without it are read as TIFF. A RAW file without a JPEG preview fails with `UnsupportedFormatException`, or, if it is a standard TIFF, shows its first TIFF image |
| CorelDRAW | `.cdr` | The preview the drawing carries; the vector content is not drawn. A RIFF drawing (up to X3) shows the bitmap of its DISP chunk; a package (X4 and later, a ZIP) the image under `previews/`, else the DISP chunk of the document inside it. Previews are small (typically 128 to 256 px). A drawing without one fails with `UnsupportedFormatException` |
| DICOM | `.dcm`, `.dicom` | Part 10 files (with the "DICM" preamble). The first frame: uncompressed (implicit or explicit VR, little or big endian), RLE Lossless, JPEG baseline/extended and JPEG 2000. Monochrome images go through the modality rescale and the file's first window (else the frame's own range) to 8 bits; MONOCHROME1 is inverted; RGB and YBR_FULL are shown in colour. JPEG lossless, JPEG-LS, palette colour and deflated data sets fail with `UnsupportedFormatException` |
| JPEG 2000 | `.jp2`, `.j2k`, `.j2c`, `.jpf`, `.jpx` | JP2 files and raw codestreams, Part 1: both wavelets, every progression order, quality layers, tiles, precincts, subsampled components, sYCC, alpha. Decoded at the smallest resolution the requested sizes need, so a thumbnail of a large picture skips most of the work; with `IncludeFullSize` the whole picture is decoded. A picture larger than `MaxPixels` is decoded at a reduced resolution that fits instead of being refused. Progression order changes (POC) and packed packet headers (PPM, PPT) are read. Palette images fail with `UnsupportedFormatException` |

All image formats report `DocumentFormat.Image`.

## Video

One frame is taken at `VideoOptions.Position` of the duration (the middle by default), then handled like an image
file. All video formats report `DocumentFormat.Video`. See [native/thumbvideo/README.md](../native/thumbvideo/README.md)
for how the frame is chosen.

**Containers**: MP4, MOV, M4V, 3GP, 3G2, F4V (ISO media), Matroska and WebM, AVI, FLV, ASF and WMV, MPEG program
stream (`.mpg`, `.mpeg`, `.mpe`, `.vob`), MPEG elementary video (`.m2v`), MPEG transport stream (`.m2ts`, and `.ts` by
signature), Ogg (`.ogv`, `.ogg`), RealMedia (`.rm`, `.rmvb`), Vivo (`.vivo`, `.viv`), MXF, RoQ.

**Video codecs**: H.264, HEVC (8 and 10 bit), AV1 (dav1d), VP8, VP9, VP3/VP5/VP6/VP7, Theora, MPEG-1, MPEG-2,
MPEG-4 Part 2, MS-MPEG4 v1-v3, H.261, H.263, FLV1 (Sorenson), WMV1/2/3, VC-1, RealVideo 1-4, Motion JPEG, ProRes,
DNxHD, DV, CineForm, JPEG 2000, SVQ1, SVQ3, QuickTime RLE, Cinepak, raw video, RoQ, Indeo 3/4/5, MS Video 1, MS RLE,
TSCC2, HuffYUV, FFV1, Ut Video.

Limitations:

- A file with no video stream (audio only: `.wav`, `.mp2`, or an MP4/Ogg with only audio) fails with
  `UnsupportedFormatException`, as do containers and codecs that are not in the list.
- Files named `.ts` or `.mts` are not treated as video by name (they are usually TypeScript); MPEG transport streams
  are still recognised by their signature.
- A video with cover art only yields that picture.
- HDR video (HEVC 10-bit PQ or HLG) is not tone-mapped, so colours look washed out.
- Frames with more pixels than 8192 x 4352 (about 35.7 million; 8K DCI fits), or wider or taller than 16384 pixels,
  are rejected.

## Encryption and damaged files

FastThumb never decrypts anything. Encrypted or password-protected files are rejected: with
`UnsupportedFormatException` where the format marks encryption explicitly (DOC, XLS, HWP, WordPerfect, MOBI, and
encrypted Office Open XML files, which are not ZIP packages), or with `InvalidDocumentException` where the encrypted
content simply cannot be parsed (PDF, ODF). A damaged file fails with `InvalidDocumentException`; the original error
is available as `InnerException`.

## Not recognised

- **Flat XML OpenDocument text and spreadsheets** (`.fodt`, `.fods`): only flat presentations (`.fodp`) are detected;
  a `.fodt` or `.fods` file is currently rendered as plain text.
- Anything else that is not recognised is rendered as plain text (if it has no known binary signature) or fails with
  `UnsupportedFormatException`.
