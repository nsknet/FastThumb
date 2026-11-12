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
    [DllImport(Dll)] private static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);
    [DllImport(Dll)] private static extern void FPDFBitmap_Destroy(IntPtr bitmap);

    private const int FormatBgra = 4;
    private const int FlagAnnotations = 0x01;

    public static SKBitmap RenderFirstPage(byte[] pdf, int longSide, out int fullWidth, out int fullHeight)
        => RenderPage(pdf, 0, longSide, out fullWidth, out fullHeight, out _);

    public static SKBitmap RenderPage(byte[] pdf, int pageIndex, int longSide, out int fullWidth, out int fullHeight, out int pageCount)
        => RenderPage(pdf, pageIndex, longSide, ResourceLimits.Default.MaxPixels, out fullWidth, out fullHeight, out pageCount);

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

    /// <summary>Caller holds <see cref="Gate"/>.</summary>
    private static IntPtr Open(GCHandle pin, int length)
    {
        if (!_initialised) { FPDF_InitLibrary(); _initialised = true; }
        var document = FPDF_LoadMemDocument(pin.AddrOfPinnedObject(), length, null);
        if (document == IntPtr.Zero) throw new InvalidDataException($"PDFium cannot open document (error {FPDF_GetLastError()})");
        return document;
    }

    /// <param name="longSide">long side of the bitmap in pixels; 0 = the page's own size in points (72 DPI)</param>
    /// <param name="maxPixels">the bitmap is scaled down further to hold at most this many pixels (page sizes come from the file)</param>
    public static SKBitmap RenderPage(byte[] pdf, int pageIndex, int longSide, long maxPixels, out int fullWidth, out int fullHeight, out int pageCount)
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
                        fullWidth = (int)Math.Min(widthPt, int.MaxValue);
                        fullHeight = (int)Math.Min(heightPt, int.MaxValue);
                        var scale = longSide > 0 ? longSide / Math.Max(widthPt, heightPt) : 1f;
                        if ((double)widthPt * heightPt * scale * scale > maxPixels) scale = (float)Math.Sqrt(maxPixels / ((double)widthPt * heightPt));
                        // a very long, thin page still gets one pixel across
                        var bitmap = new SKBitmap(new SKImageInfo(Math.Max(1, (int)Math.Round(widthPt * scale)), Math.Max(1, (int)Math.Round(heightPt * scale)), SKColorType.Bgra8888, SKAlphaType.Premul));
                        // render straight into the Skia pixel buffer: no intermediate copy or encode
                        var target = FPDFBitmap_CreateEx(bitmap.Width, bitmap.Height, FormatBgra, bitmap.GetPixels(), bitmap.RowBytes);
                        try
                        {
                            FPDFBitmap_FillRect(target, 0, 0, bitmap.Width, bitmap.Height, 0xFFFFFFFF);
                            FPDF_RenderPageBitmap(target, page, 0, 0, bitmap.Width, bitmap.Height, 0, FlagAnnotations);
                        }
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
