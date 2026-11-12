# Supported formats

FastThumb recognises the format from the content of the input, not from its name (see
[architecture.md](architecture.md#1-detect)). The file name or extension passed to `Generate` only breaks ties between
plain-text formats and identifies a few formats that are recognised by extension only. The recognised format is
reported as `ThumbnailResult.Format` (a `DocumentFormat` value), and `ThumbnailGenerator.SupportedExtensions` lists
the usual extensions.

Thumbnails are made from the first page. A preview PDF of every page is produced for every format in the
"Documents", "Spreadsheets", "Presentations" and XPS sections; PDF, image and video inputs have no preview PDF.

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

## Presentations

One page per slide, at the presentation's slide size, up to 1000 slides.

| Format | `DocumentFormat` | Extensions | Rendered | Not rendered / limitations |
|---|---|---|---|---|
| Office Open XML presentation | `Pptx` | `.pptx`, `.pptm`, `.potx`, `.ppsx` | Slide, layout, master and theme inheritance; shape geometry, solid fills, outlines, pictures and text with placeholder, list-style and theme inheritance | Charts, SmartArt, embedded OLE objects, effects, rotation, picture cropping; gradients use their first stop |
| PowerPoint 97-2003 | `Ppt` | `.ppt`, `.pps`, `.pot` | Shape anchors (including nested groups), fills, outlines, pictures and text with paragraph and character runs on top of the master's text styles | Master shapes and backgrounds. PowerPoint 95 files are rejected |
| OpenDocument presentation | `Odp` | `.odp`, `.otp` | Pages, frames, shapes, pictures, text with ODF styles | Master-page decoration |
| Flat OpenDocument presentation | `Fodp` | `.fodp` | Same as ODP, from a single XML file | Same as ODP |

## Fixed-layout documents

| Format | `DocumentFormat` | Extensions | Rendered | Not rendered / limitations |
|---|---|---|---|---|
| PDF | `Pdf` | `.pdf` | Page 1 rendered by PDFium, including annotations. The full-size image is the page at 72 DPI. `PageCount` is the document's page count | No preview PDF (the input already is one). Password-protected PDFs fail with `InvalidDocumentException`. No JavaScript or XFA forms |
| XPS / OpenXPS | `Xps` | `.xps`, `.oxps` | Nested canvases with transforms and opacity, glyph runs with embedded (including obfuscated) fonts, explicit glyph indices, advances and offsets, paths, solid and image brushes, resource dictionaries. Up to 500 pages | Gradients use their first stop; clipping and tiling are ignored |

## Images

Images are decoded by libvips (BMP and ICO by Skia), EXIF orientation is applied, and they are never enlarged. A
picture larger than `ResourceLimits.MaxPixels` is rejected with `ResourceLimitExceededException` before it is decoded.

| Format | Extensions | Notes |
|---|---|---|
| JPEG | `.jpg`, `.jpeg` | |
| PNG | `.png` | |
| GIF | `.gif` | First frame |
| WebP | `.webp` | |
| TIFF | `.tif`, `.tiff` | First page |
| HEIF / AVIF | `.heic`, `.heif`, `.avif` | Recognised by the still-image brands of the ISO media container. Decoding depends on the codecs included in the libvips build shipped by NetVips.Native; an image whose codec is missing fails with `UnsupportedFormatException` |
| SVG | `.svg` | Recognised by extension only. Rendered by librsvg from memory, with no base URI (no external files or URLs) |
| BMP | `.bmp` | Recognised by extension only |
| ICO | `.ico` | Recognised by extension only |

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
