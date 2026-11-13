using System.Buffers.Binary;

namespace FastThumb.Images.Jpeg2000;

/// <summary>
/// JPEG 2000 (ITU-T T.800, Part 1): a JP2 file or a raw codestream (J2K), decoded at the smallest resolution that still serves
/// what was asked: each halving skips a wavelet level, and with it most of the tier-1 work. A picture larger than MaxPixels is
/// reduced until it fits rather than refused, as long as the file has the levels for it.
/// </summary>
internal static class Jpeg2000Decoder
{
    private static ReadOnlySpan<byte> Signature => [0x00, 0x00, 0x00, 0x0C, 0x6A, 0x50, 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A];

    public static bool Claims(ReadOnlySpan<byte> d) =>
        d.StartsWith(Signature) || (d.Length >= 4 && d[0] == 0xFF && d[1] == 0x4F && d[2] == 0xFF && d[3] == 0x51);

    /// <summary>Where the codestream is, and the JP2 enumerated colour space (16 sRGB, 17 grey, 18 sYCC; 0 unknown).</summary>
    private static (int Start, int End, int ColourSpace) Locate(byte[] data)
    {
        if (!data.AsSpan().StartsWith(Signature)) return (0, data.Length, 0);
        var colourSpace = 0;
        var at = 0;
        var end = data.Length;
        var boxes = 0;
        while (true)
        {
            if (at + 8 > end && end < data.Length) end = data.Length;    // past the header super-box: back to the top level
            if (at + 8 > end || ++boxes > 4096) break;
            long length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at));
            var type = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at + 4));
            var header = 8;
            if (length == 1)
            {
                if (at + 16 > end) break;
                length = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(at + 8));
                header = 16;
            }
            else if (length == 0) length = end - at;
            if (at + length > end && type == 0x6A703263) length = end - at;   // a cut codestream: decoded as far as it goes
            if (length < header || at + length > end) throw new InvalidDocumentException("A JP2 box runs past the end of the file.");
            var content = (Start: at + header, End: (int)(at + length));
            switch (type)
            {
                case 0x6A703268:                                             // jp2h: a super-box, read its boxes in place
                    end = content.End;
                    at = content.Start;
                    continue;
                case 0x636F6C72:                                             // colr
                    if (content.End - content.Start >= 7 && data[content.Start] == 1)
                        colourSpace = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(content.Start + 3));
                    break;
                case 0x70636C72:                                             // pclr
                    throw new UnsupportedFormatException("Palette JPEG 2000 images are not supported.");
                case 0x6A703263:                                             // jp2c
                    return (content.Start, content.End, colourSpace);
            }
            at = content.End;
        }
        throw new InvalidDocumentException("The JP2 file has no codestream.");
    }

    public static (int Width, int Height) Size(byte[] data)
    {
        var (start, end, _) = Locate(data);
        var stream = Codestream.Header(data, start, end);
        return (stream.Width - stream.X0, stream.Height - stream.Y0);
    }

    public static NetVips.Image Decode(byte[] data, DecodeRequest request)
    {
        var (start, end, colourSpace) = Locate(data);
        var stream = Codestream.Parse(data, start, end);
        if (stream.Tiles.Count == 0) throw new InvalidDocumentException("The JPEG 2000 codestream has no tile data.");
        var levels = stream.Tiles.Values.SelectMany(t => t.Parameters.Styles).Min(s => s.Levels);

        (int Width, int Height) At(int reduce) =>
            (Geometry.CeilShift(stream.Width, reduce) - Geometry.CeilShift(stream.X0, reduce), Geometry.CeilShift(stream.Height, reduce) - Geometry.CeilShift(stream.Y0, reduce));

        // the smallest picture that is still big enough, then as small as MaxPixels needs
        var reduce = 0;
        for (var r = levels; r > 0; r--)
            if (At(r) is var (w, h) && request.BigEnough(w, h)) { reduce = r; break; }
        while (reduce < levels && (long)At(reduce).Width * At(reduce).Height > request.MaxPixels) reduce++;
        var (width, height) = At(reduce);
        request.Check(width, height);

        // each component at the reduced resolution
        var count = stream.Components.Length;
        var planes = new float[count][];
        var bounds = new (int X0, int Y0, int Width, int Height)[count];
        for (var c = 0; c < count; c++)
        {
            var component = stream.Components[c];
            int cx0 = Geometry.CeilShift(Geometry.CeilDiv(stream.X0, component.Dx), reduce), cy0 = Geometry.CeilShift(Geometry.CeilDiv(stream.Y0, component.Dy), reduce);
            int cx1 = Geometry.CeilShift(Geometry.CeilDiv(stream.Width, component.Dx), reduce), cy1 = Geometry.CeilShift(Geometry.CeilDiv(stream.Height, component.Dy), reduce);
            bounds[c] = (cx0, cy0, cx1 - cx0, cy1 - cy0);
            request.Check(Math.Max(1, cx1 - cx0), Math.Max(1, cy1 - cy0));
            planes[c] = new float[(long)(cx1 - cx0) * (cy1 - cy0)];
        }

        var budget = new TileDecoder.Budget();
        foreach (var (tile, (parameters, parts)) in stream.Tiles.OrderBy(t => t.Key))
        {
            Guard.Check();
            if (parts.Count == 0) continue;
            byte[] tileData;
            int tileStart, tileEnd;
            if (parts.Count == 1) (tileData, tileStart, tileEnd) = (data, parts[0].Start, parts[0].Start + parts[0].Length);
            else
            {
                tileData = new byte[parts.Sum(p => (long)p.Length)];
                var at = 0;
                foreach (var (partStart, length) in parts) { Buffer.BlockCopy(data, partStart, tileData, at, length); at += length; }
                (tileStart, tileEnd) = (0, tileData.Length);
            }
            var samples = new TileDecoder(stream, tile, parameters, tileData, tileStart, tileEnd, reduce, budget).Decode();
            if (parameters.Transform) InverseTransform(samples, parameters.Styles[0].Reversible);
            for (var c = 0; c < count; c++)
            {
                var (source, sx0, sy0, sw, sh) = samples[c];
                var (px0, py0, pw, ph) = bounds[c];
                for (var y = 0; y < sh; y++)
                {
                    var ty = sy0 + y - py0;
                    if ((uint)ty >= (uint)ph) continue;
                    for (var x = 0; x < sw; x++)
                    {
                        var tx = sx0 + x - px0;
                        if ((uint)tx < (uint)pw) planes[c][ty * pw + tx] = source[y * sw + x];
                    }
                }
            }
        }
        return Compose(stream, planes, bounds, width, height, colourSpace);
    }

    /// <summary>The inverse component transform of the first three components: RCT (reversible) or ICT (G.2, G.3).</summary>
    private static void InverseTransform((float[] Samples, int X0, int Y0, int Width, int Height)[] samples, bool reversible)
    {
        if (samples.Length < 3 || samples[1].Samples.Length != samples[0].Samples.Length || samples[2].Samples.Length != samples[0].Samples.Length) return;
        float[] y = samples[0].Samples, cb = samples[1].Samples, cr = samples[2].Samples;
        for (var i = 0; i < y.Length; i++)
        {
            if (reversible)
            {
                var g = y[i] - MathF.Floor((cb[i] + cr[i]) / 4);
                (y[i], cb[i], cr[i]) = (cr[i] + g, g, cb[i] + g);
            }
            else
                (y[i], cb[i], cr[i]) = (y[i] + 1.402f * cr[i], y[i] - 0.344136f * cb[i] - 0.714136f * cr[i], y[i] + 1.772f * cb[i]);
        }
    }

    /// <summary>
    /// 8-bit pixels: level shift, rounding and scaling of each component, components sampled more coarsely spread over the
    /// picture, sYCC turned into RGB. One component is grey, two grey and alpha, three RGB, four or more RGBA.
    /// </summary>
    private static NetVips.Image Compose(Codestream stream, float[][] planes, (int X0, int Y0, int Width, int Height)[] bounds, int width, int height, int colourSpace)
    {
        var count = Math.Min(planes.Length, 4);
        var bands = count switch { 1 => 1, 2 => 2, 3 => 3, _ => 4 };
        var pixels = new byte[(long)width * height * bands];
        for (var c = 0; c < count; c++)
        {
            Guard.Check();
            var component = stream.Components[c];
            var max = (1L << component.Precision) - 1;
            var shift = 1L << (component.Precision - 1);
            var (_, _, cw, ch) = bounds[c];
            if (cw <= 0 || ch <= 0) continue;
            var plane = planes[c];
            for (var y = 0; y < height; y++)
            {
                var sy = ch == height ? y : Math.Min(ch - 1, (int)((long)y * ch / height));
                for (var x = 0; x < width; x++)
                {
                    var sx = cw == width ? x : Math.Min(cw - 1, (int)((long)x * cw / width));
                    var v = Math.Clamp((long)MathF.Round(plane[sy * cw + sx]) + shift, 0, max);
                    pixels[((long)y * width + x) * bands + c] = (byte)(component.Precision > 8 ? v >> (component.Precision - 8) : component.Precision == 8 ? v : v * 255 / max);
                }
            }
        }
        if (colourSpace == 18 && count >= 3)
            for (long i = 0; i < pixels.Length; i += bands)
            {
                float y = pixels[i], cb = pixels[i + 1] - 128f, cr = pixels[i + 2] - 128f;
                pixels[i] = (byte)Math.Clamp(MathF.Round(y + 1.402f * cr), 0, 255);
                pixels[i + 1] = (byte)Math.Clamp(MathF.Round(y - 0.344136f * cb - 0.714136f * cr), 0, 255);
                pixels[i + 2] = (byte)Math.Clamp(MathF.Round(y + 1.772f * cb), 0, 255);
            }
        return ManagedImages.FromPixels(pixels, width, height, bands);
    }
}
