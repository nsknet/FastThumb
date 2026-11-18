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
/// pixels where the format is simple (TGA, the PSD composite or its layers, DICOM), the preview a file carries where drawing
/// it ourselves would mean a whole engine (camera RAW, EPS, CorelDRAW), Illustrator's own path language, and a
/// reduced-resolution decode for JPEG 2000. Embedded previews are JPEG, TIFF, PNG or BMP, which libvips (or Skia, for BMP)
/// then decodes as any other image.
/// </summary>
internal static class ManagedImages
{
    private enum Kind { None, Tga, Psd, Eps, Raw, Jpeg2000, Cdr, Dicom }

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
        Kind.Raw => OrTiff(data, () => Run(() => RawPreview.Decode(data, request))),
        Kind.Jpeg2000 => Run(() => Jpeg2000.Jpeg2000Decoder.Decode(data, request)),
        Kind.Cdr => Run(() => CdrPreview.Decode(data, request)),
        Kind.Dicom => Run(() => DicomDecoder.Decode(data, request)),
        _ => null,
    };

    /// <summary>Width and height from the header alone, or null when the bytes are not a format read here.</summary>
    public static (int Width, int Height)? Size(byte[] data, string? extension) => Find(data, extension) switch
    {
        Kind.Tga => Run(() => TgaDecoder.Size(data)),
        Kind.Psd => Run(() => PsdDecoder.Size(data)),
        Kind.Eps => Run(() => EpsPreview.Size(data)),
        Kind.Raw => OrTiff<(int, int)?>(data, () => Run(() => RawPreview.Size(data))),
        Kind.Jpeg2000 => Run(() => Jpeg2000.Jpeg2000Decoder.Size(data)),
        Kind.Cdr => Run(() => CdrPreview.Size(data, DecodeRequest.Full(64L << 20))),
        Kind.Dicom => Run(() => DicomDecoder.Size(data)),
        _ => null,
    };

    private static Kind Find(ReadOnlySpan<byte> d, string? extension)
    {
        if (PsdDecoder.Claims(d)) return Kind.Psd;
        if (Jpeg2000.Jpeg2000Decoder.Claims(d)) return Kind.Jpeg2000;
        if (EpsPreview.Claims(d, extension)) return Kind.Eps;
        if (DicomDecoder.Claims(d)) return Kind.Dicom;
        if (CdrPreview.Claims(d, extension)) return Kind.Cdr;
        if (RawPreview.Claims(d, extension)) return Kind.Raw;
        if (TgaDecoder.Claims(d, extension)) return Kind.Tga;
        return Kind.None;
    }

    /// <summary>
    /// A RAW file that is a standard TIFF (CR2, NEF, ARW, DNG...) with no JPEG preview is still a TIFF: null, so libvips reads its
    /// first directory, often a small RGB thumbnail. A RAW with its own signature has nothing else to offer and is refused.
    /// </summary>
    private static T? OrTiff<T>(byte[] data, Func<T> read)
    {
        try { return read(); }
        catch (UnsupportedFormatException) when (TiffWalker.IsStandard(data)) { return default; }
    }

    /// <summary>A record running past the end, a count that overflows: a damaged file, reported as one.</summary>
    private static T Run<T>(Func<T> read)
    {
        try { return read(); }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or OverflowException or EndOfStreamException
            or FormatException or InvalidCastException or DivideByZeroException or InvalidOperationException or NullReferenceException)
        {
            throw new InvalidDocumentException($"The image is damaged ({e.GetType().Name}: {e.Message})", e);
        }
    }

    /// <summary>
    /// A gzip-compressed SVG (.svgz) inflated here, within <paramref name="maxBytes"/>, so the SVG loader gets plain XML and a
    /// small file cannot expand without bound; anything else is returned as it is.
    /// </summary>
    public static byte[] Inflated(byte[] data, long maxBytes)
    {
        if (data.Length < 2 || data[0] != 0x1F || data[1] != 0x8B) return data;
        try
        {
            using var gzip = new System.IO.Compression.GZipStream(new MemoryStream(data, false), System.IO.Compression.CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (output.Length + read > maxBytes)
                    throw new ResourceLimitExceededException($"The compressed SVG expands to more than {maxBytes:N0} bytes (ResourceLimits.MaxInputBytes).");
                output.Write(buffer, 0, read);
                Guard.Check();
            }
            return output.ToArray();
        }
        catch (InvalidDataException e) { throw new InvalidDocumentException($"The compressed SVG is damaged ({e.Message})", e); }
    }

    /// <summary>A preview a file carries in a format libvips reads (JPEG, TIFF), size-checked on its header.</summary>
    public static NetVips.Image Embedded(byte[] encoded, DecodeRequest request)
    {
        var image = NetVips.Image.NewFromBuffer(encoded);
        try { request.Check(image.Width, image.Height); }
        catch { image.Dispose(); throw; }
        return image;
    }

    /// <summary>
    /// An embedded picture in any format libvips reads, or Skia (BMP: what CorelDRAW stores), size-checked on its header before
    /// a pixel is decoded.
    /// </summary>
    public static NetVips.Image Encoded(byte[] encoded, DecodeRequest request)
    {
        try { return Embedded(encoded, request); }
        catch (NetVips.VipsException e)
        {
            using var codec = SkiaSharp.SKCodec.Create(SkiaSharp.SKData.CreateCopy(encoded))
                ?? throw new UnsupportedFormatException("The embedded picture cannot be decoded.", e);
            request.Check(codec.Info.Width, codec.Info.Height);
            using var bitmap = SkiaSharp.SKBitmap.Decode(codec, new SkiaSharp.SKImageInfo(codec.Info.Width, codec.Info.Height, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul))
                ?? throw new UnsupportedFormatException("The embedded picture cannot be decoded.", e);
            return FromPixels(bitmap.Bytes, bitmap.Width, bitmap.Height, 4);
        }
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
