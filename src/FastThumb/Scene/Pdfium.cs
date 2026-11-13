using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace FastThumb.Scene;

/// <summary>
/// Minimal P/Invoke over PDFium: pdfium.dll on Windows, libpdfium.so on Linux (bblanchon.PDFium.Win32 / .Linux;
/// "pdfium" resolves to either). PDFium is not thread-safe: one global lock.
/// </summary>
internal static class Pdfium
{
    private const string Dll = "pdfium";
    private static readonly Lock Gate = new();
    private static bool _initialised;

    [DllImport(Dll)] private static extern void FPDF_InitLibrary();
    [DllImport(Dll)] private static extern IntPtr FPDF_LoadMemDocument(IntPtr data, int size, [MarshalAs(UnmanagedType.LPStr)] string? password);
    [DllImport(Dll)] private static extern uint FPDF_GetLastError();
    [DllImport(Dll)] private static extern void FPDF_CloseDocument(IntPtr document);
    [DllImport(Dll)] private static extern IntPtr FPDF_LoadPage(IntPtr document, int index);
    [DllImport(Dll)] private static extern void FPDF_ClosePage(IntPtr page);
    [DllImport(Dll)] private static extern float FPDF_GetPageWidthF(IntPtr page);
    [DllImport(Dll)] private static extern float FPDF_GetPageHeightF(IntPtr page);
    [DllImport(Dll)] private static extern IntPtr FPDFBitmap_CreateEx(int width, int height, int format, IntPtr firstScan, int stride);
    [DllImport(Dll)] private static extern void FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);
    [DllImport(Dll)] private static extern unsafe int FPDF_RenderPageBitmap_Start(IntPtr bitmap, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags, Pause* pause);
    [DllImport(Dll)] private static extern unsafe int FPDF_RenderPage_Continue(IntPtr page, Pause* pause);
    [DllImport(Dll)] private static extern void FPDF_RenderPage_Close(IntPtr page);
    [DllImport(Dll)] private static extern void FPDFBitmap_Destroy(IntPtr bitmap);

    private const int FormatBgra = 4;
    private const int FlagAnnotations = 0x01;
    private const int RenderToBeContinued = 1;

    /// <summary>IFSDK_PAUSE: PDFium asks it, every so many page objects, whether to stop drawing for now.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct Pause
    {
        public int Version;
        public delegate* unmanaged[Cdecl]<Pause*, int> NeedToPauseNow;
        public IntPtr User;
    }

    /// <summary>
    /// Pauses only when the call is cancelled. PDFium calls back on the thread that is drawing, which is the thread whose
    /// cancellation <see cref="Guard"/> holds.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int NeedToPauseNow(Pause* pause) => Guard.Cancellation.IsCancellationRequested ? 1 : 0;

    public static SKBitmap RenderFirstPage(byte[] pdf, int longSide, out int fullWidth, out int fullHeight)
        => RenderPage(pdf, 0, longSide, out fullWidth, out fullHeight, out _);

    public static SKBitmap RenderPage(byte[] pdf, int pageIndex, int longSide, out int fullWidth, out int fullHeight, out int pageCount)
        => RenderPage(pdf, pageIndex, longSide, longSide > 0 ? 0 : 72, ResourceLimits.Default.MaxPixels, out fullWidth, out fullHeight, out pageCount);

    [DllImport(Dll)] private static extern int FPDF_GetPageCount(IntPtr document);

    public static int PageCount(byte[] pdf)
    {
        var pin = GCHandle.Alloc(pdf, GCHandleType.Pinned);
        try
        {
            lock (Gate)
            {
                var document = Open(pin, pdf.Length);
                try { return FPDF_GetPageCount(document); }
                finally { FPDF_CloseDocument(document); }
            }
        }
        finally { pin.Free(); }
    }

    [DllImport(Dll)] private static extern IntPtr FPDFText_LoadPage(IntPtr page);
    [DllImport(Dll)] private static extern void FPDFText_ClosePage(IntPtr textPage);
    [DllImport(Dll)] private static extern int FPDFText_CountChars(IntPtr textPage);
    [DllImport(Dll)] private static extern unsafe int FPDFText_GetText(IntPtr textPage, int start, int count, char* result);

    /// <summary>Most characters read from one page: a page of text holds a few thousand; more is a crafted file.</summary>
    private const int MaxPageChars = 1 << 20;

    /// <summary>The text layer of the first <paramref name="maxPages"/> pages (line breaks as "\n").</summary>
    public static unsafe List<string> PageTexts(byte[] pdf, int maxPages, out int pageCount)
    {
        var texts = new List<string>();
        var pin = GCHandle.Alloc(pdf, GCHandleType.Pinned);
        try
        {
            lock (Gate)
            {
                var document = Open(pin, pdf.Length);
                try
                {
                    pageCount = FPDF_GetPageCount(document);
                    for (var index = 0; index < Math.Min(pageCount, maxPages); index++)
                    {
                        Guard.Check();
                        var page = FPDF_LoadPage(document, index);
                        if (page == IntPtr.Zero) { texts.Add(""); continue; }
                        try
                        {
                            var textPage = FPDFText_LoadPage(page);
                            if (textPage == IntPtr.Zero) { texts.Add(""); continue; }
                            try
                            {
                                var count = Math.Min(FPDFText_CountChars(textPage), MaxPageChars);
                                if (count <= 0) { texts.Add(""); continue; }
                                var buffer = new char[count + 1];
                                int written;
                                fixed (char* chars = buffer) written = FPDFText_GetText(textPage, 0, count, chars);
                                // written counts the terminating zero
                                var text = new string(buffer, 0, Math.Clamp(written - 1, 0, count));
                                texts.Add(Normalise(text));
                            }
                            finally { FPDFText_ClosePage(textPage); }
                        }
                        finally { FPDF_ClosePage(page); }
                    }
                }
                finally { FPDF_CloseDocument(document); }
            }
        }
        finally { pin.Free(); }
        return texts;
    }

    /// <summary>PDFium ends lines with "\r\n" and keeps soft hyphens and NULs from some generators: plain lines, trailing "\n".</summary>
    private static string Normalise(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\0", "").Replace("­", "").Split('\n');
        var joined = string.Join('\n', lines.Select(line => line.TrimEnd())).Trim('\n');
        return joined.Length == 0 ? "" : joined + "\n";
    }

    [DllImport(Dll)] private static extern unsafe CULong FPDF_GetMetaText(IntPtr document, [MarshalAs(UnmanagedType.LPStr)] string tag, void* buffer, CULong length);

    private static readonly string[] MetaTags = ["Title", "Author", "Subject", "Keywords", "CreationDate", "ModDate"];

    /// <summary>Page count, size of page 1 in points and the document information entries, without rendering anything.</summary>
    public static unsafe (int PageCount, float Width, float Height, Dictionary<string, string> Meta) Inspect(byte[] pdf)
    {
        var pin = GCHandle.Alloc(pdf, GCHandleType.Pinned);
        try
        {
            lock (Gate)
            {
                var document = Open(pin, pdf.Length);
                try
                {
                    var count = FPDF_GetPageCount(document);
                    float width = 0, height = 0;
                    var page = FPDF_LoadPage(document, 0);
                    if (page != IntPtr.Zero)
                    {
                        try { (width, height) = (FPDF_GetPageWidthF(page), FPDF_GetPageHeightF(page)); }
                        finally { FPDF_ClosePage(page); }
                    }
                    if (!(width > 0 && height > 0)) throw new InvalidDataException("PDF page 1 has no size");
                    var meta = new Dictionary<string, string>();
                    foreach (var tag in MetaTags)
                    {
                        // UTF-16LE with a terminating zero; the length comes from the file, so it is capped
                        var length = (long)FPDF_GetMetaText(document, tag, null, new CULong(0)).Value;
                        if (length <= 2 || length > 1 << 16) continue;
                        var buffer = new byte[length];
                        fixed (byte* bytes = buffer) FPDF_GetMetaText(document, tag, bytes, new CULong((nuint)length));
                        meta[tag] = System.Text.Encoding.Unicode.GetString(buffer, 0, (int)length - 2);
                    }
                    return (count, width, height, meta);
                }
                finally { FPDF_CloseDocument(document); }
            }
        }
        finally { pin.Free(); }
    }

    /// <summary>Width and height of page 1 in points.</summary>
    public static (double Width, double Height) FirstPageSize(byte[] pdf)
    {
        var pin = GCHandle.Alloc(pdf, GCHandleType.Pinned);
        try
        {
            lock (Gate)
            {
                var document = Open(pin, pdf.Length);
                try
                {
                    var page = FPDF_LoadPage(document, 0);
                    if (page == IntPtr.Zero) throw new InvalidDataException("PDFium cannot load page 1");
                    try
                    {
                        float width = FPDF_GetPageWidthF(page), height = FPDF_GetPageHeightF(page);
                        if (!(width > 0 && height > 0)) throw new InvalidDataException("PDF page 1 has no size");
                        return (width, height);
                    }
                    finally { FPDF_ClosePage(page); }
                }
                finally { FPDF_CloseDocument(document); }
            }
        }
        finally { pin.Free(); }
    }

    /// <summary>Caller holds <see cref="Gate"/>.</summary>
    private static IntPtr Open(GCHandle pin, int length)
    {
        if (!_initialised) { FPDF_InitLibrary(); _initialised = true; }
        var document = FPDF_LoadMemDocument(pin.AddrOfPinnedObject(), length, null);
        if (document == IntPtr.Zero) throw new InvalidDataException($"PDFium cannot open document (error {FPDF_GetLastError()})");
        return document;
    }

    /// <param name="longSide">long side of the bitmap in pixels, at least</param>
    /// <param name="fullDpi">resolution of the full size: the bitmap is at least that large too, and <paramref name="fullWidth"/> and
    /// <paramref name="fullHeight"/> are the page at it; 0 = no full size (they are then the page at 72 DPI)</param>
    /// <param name="maxPixels">the bitmap is scaled down further to hold at most this many pixels (page sizes come from the file)</param>
    /// <exception cref="OperationCanceledException">The call was cancelled while the page was being drawn.</exception>
    public static unsafe SKBitmap RenderPage(byte[] pdf, int pageIndex, int longSide, double fullDpi, long maxPixels, out int fullWidth, out int fullHeight, out int pageCount)
    {
        var pin = GCHandle.Alloc(pdf, GCHandleType.Pinned);
        try
        {
            lock (Gate)
            {
                var document = Open(pin, pdf.Length);
                try
                {
                    pageCount = FPDF_GetPageCount(document);
                    var page = FPDF_LoadPage(document, pageIndex);
                    if (page == IntPtr.Zero) throw new InvalidDataException($"PDFium cannot load page {pageIndex + 1}");
                    try
                    {
                        float widthPt = FPDF_GetPageWidthF(page), heightPt = FPDF_GetPageHeightF(page);
                        if (!(widthPt > 0 && heightPt > 0)) throw new InvalidDataException($"PDF page {pageIndex + 1} has no size");
                        var fullScale = fullDpi > 0 ? fullDpi / 72 : 1;
                        fullWidth = (int)Math.Min(widthPt * fullScale, int.MaxValue);
                        fullHeight = (int)Math.Min(heightPt * fullScale, int.MaxValue);
                        var scale = Math.Max(longSide > 0 ? longSide / Math.Max(widthPt, heightPt) : 0f, fullDpi > 0 ? (float)fullScale : 0f);
                        if (scale <= 0) scale = 1f;
                        Func<double, double> round = Math.Round;
                        if ((double)widthPt * heightPt * scale * scale > maxPixels)
                        {
                            scale = (float)Math.Sqrt(maxPixels / ((double)widthPt * heightPt));
                            round = v => Math.Floor(v + 1e-3);   // so that the bitmap really holds at most maxPixels
                        }
                        // a very long, thin page still gets one pixel across
                        var bitmap = new SKBitmap(new SKImageInfo(Math.Max(1, (int)round(widthPt * scale)), Math.Max(1, (int)round(heightPt * scale)), SKColorType.Bgra8888, SKAlphaType.Premul));
                        // render straight into the Skia pixel buffer: no intermediate copy or encode
                        var target = FPDFBitmap_CreateEx(bitmap.Width, bitmap.Height, FormatBgra, bitmap.GetPixels(), bitmap.RowBytes);
                        try
                        {
                            FPDFBitmap_FillRect(target, 0, 0, bitmap.Width, bitmap.Height, 0xFFFFFFFF);
                            // drawn progressively so that a cancelled call stops mid-page and lets go of the lock
                            var pause = new Pause { Version = 1, NeedToPauseNow = &NeedToPauseNow };
                            try
                            {
                                var status = FPDF_RenderPageBitmap_Start(target, page, 0, 0, bitmap.Width, bitmap.Height, 0, FlagAnnotations, &pause);
                                while (status == RenderToBeContinued)
                                {
                                    Guard.Check();
                                    status = FPDF_RenderPage_Continue(page, &pause);
                                }
                            }
                            finally { FPDF_RenderPage_Close(page); }
                        }
                        catch { bitmap.Dispose(); throw; }
                        finally { FPDFBitmap_Destroy(target); }
                        return bitmap;
                    }
                    finally { FPDF_ClosePage(page); }
                }
                finally { FPDF_CloseDocument(document); }
            }
        }
        finally { pin.Free(); }
    }
}
