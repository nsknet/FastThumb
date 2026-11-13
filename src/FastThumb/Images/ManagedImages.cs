namespace FastThumb.Images;

/// <summary>What a decoder is asked for: the pixel limit, and the smallest picture that still serves every requested output.</summary>
/// <param name="MaxPixels">Largest picture accepted, checked on the declared size before any pixel is decoded.</param>
/// <param name="BigEnough">
/// True when a picture of this width x height still gives every requested output at full quality. A decoder that can decode
/// at a reduced size (JPEG 2000) takes the smallest that is big enough; the others ignore it.
/// </param>
internal readonly record struct DecodeRequest(long MaxPixels, Func<int, int, bool> BigEnough)
{
    public static DecodeRequest Full(long maxPixels) => new(maxPixels, static (_, _) => false);

    public void Check(long width, long height)
    {
        if (width <= 0 || height <= 0) throw new InvalidDocumentException($"The image declares a size of {width} x {height} px.");
        if (width * height > MaxPixels)
            throw new ResourceLimitExceededException($"The image is {width} x {height} px, more than {MaxPixels:N0} pixels (ResourceLimits.MaxPixels).");
        // decoded here into one array of up to 4 bytes a pixel, whatever MaxPixels allows
        if (width * height > Array.MaxLength / 4)
            throw new ResourceLimitExceededException($"The image is {width} x {height} px, more than this decoder holds in memory.");
    }
}

/// <summary>
/// Image formats the bundled libvips and Skia have no loader for, read here in managed code with no dependency:
/// pixels where the format is simple (TGA, the PSD composite), the preview a file carries where drawing it ourselves would
/// mean a whole engine (camera RAW, EPS), and a reduced-resolution decode for JPEG 2000. Embedded previews are JPEG or TIFF,
/// which libvips then decodes as any other image.
/// </summary>
internal static class ManagedImages
{
    private enum Kind { None, Tga, Psd, Eps }

    /// <summary>
    /// A format read here, from its signature (TGA has none: its extension, or the TGA 2.0 footer). <paramref name="d"/> may be
    /// only the head of the file.
    /// </summary>
    public static bool Claims(ReadOnlySpan<byte> d, string? extension) => Find(d, extension) != Kind.None;

    /// <summary>The picture, or null when the bytes are not a format read here (libvips or Skia then try).</summary>
    public static NetVips.Image? Decode(byte[] data, string? extension, DecodeRequest request) => Find(data, extension) switch
    {
        Kind.Tga => Run(() => TgaDecoder.Decode(data, request)),
        Kind.Psd => Run(() => PsdDecoder.Decode(data, request)),
        Kind.Eps => Run(() => EpsPreview.Decode(data, request)),
        _ => null,
    };

    /// <summary>Width and height from the header alone, or null when the bytes are not a format read here.</summary>
    public static (int Width, int Height)? Size(byte[] data, string? extension) => Find(data, extension) switch
    {
        Kind.Tga => Run(() => TgaDecoder.Size(data)),
        Kind.Psd => Run(() => PsdDecoder.Size(data)),
        Kind.Eps => Run(() => EpsPreview.Size(data)),
        _ => null,
    };

    private static Kind Find(ReadOnlySpan<byte> d, string? extension)
    {
        if (PsdDecoder.Claims(d)) return Kind.Psd;
        if (EpsPreview.Claims(d, extension)) return Kind.Eps;
        if (TgaDecoder.Claims(d, extension)) return Kind.Tga;
        return Kind.None;
    }

    /// <summary>A record running past the end, a count that overflows: a damaged file, reported as one.</summary>
    private static T Run<T>(Func<T> read)
    {
        try { return read(); }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or OverflowException or EndOfStreamException
            or FormatException or InvalidCastException or DivideByZeroException)
        {
            throw new InvalidDocumentException($"The image is damaged ({e.GetType().Name}: {e.Message})", e);
        }
    }

    /// <summary>A preview a file carries in a format libvips reads (JPEG, TIFF), size-checked on its header.</summary>
    public static NetVips.Image Embedded(byte[] encoded, DecodeRequest request)
    {
        var image = NetVips.Image.NewFromBuffer(encoded);
        try { request.Check(image.Width, image.Height); }
        catch { image.Dispose(); throw; }
        return image;
    }

    /// <summary>Interleaved 8-bit pixels (1 grey, 2 grey + alpha, 3 RGB, 4 RGBA bands) as a libvips image.</summary>
    public static unsafe NetVips.Image FromPixels(byte[] pixels, int width, int height, int bands)
    {
        fixed (byte* p = pixels)
        {
            using var raw = NetVips.Image.NewFromMemoryCopy((nint)p, (ulong)width * (ulong)height * (ulong)bands, width, height, bands, NetVips.Enums.BandFormat.Uchar);
            return raw.Copy(interpretation: bands >= 3 ? NetVips.Enums.Interpretation.Srgb : NetVips.Enums.Interpretation.Bw);
        }
    }
}
