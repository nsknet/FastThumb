namespace FastThumb.Scene;

/// <summary>
/// ThumbHash encoder (https://github.com/evanw/thumbhash, MIT, by Evan Wallace): a picture of at most 100 x 100 px as about
/// 25 bytes of low-frequency DCT terms in a luminance / chroma space, plus the average colour.
/// </summary>
internal static class ThumbHash
{
    /// <param name="rgba">Unpremultiplied RGBA, 8 bits per channel, row by row.</param>
    public static ThumbnailPlaceholder Encode(int w, int h, ReadOnlySpan<byte> rgba)
    {
        if (w is < 1 or > 100 || h is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(w), "ThumbHash takes at most 100 x 100 px.");
        if (rgba.Length < w * h * 4) throw new ArgumentException("Too few pixels.", nameof(rgba));
        var n = w * h;

        double avgR = 0, avgG = 0, avgB = 0, avgA = 0;
        for (int i = 0, j = 0; i < n; i++, j += 4)
        {
            var alpha = rgba[j + 3] / 255.0;
            avgR += alpha / 255 * rgba[j];
            avgG += alpha / 255 * rgba[j + 1];
            avgB += alpha / 255 * rgba[j + 2];
            avgA += alpha;
        }
        if (avgA > 0) { avgR /= avgA; avgG /= avgA; avgB /= avgA; }

        var hasAlpha = avgA < n;
        var lLimit = hasAlpha ? 5 : 7;
        var lx = Math.Max(1, (int)Round(lLimit * w / (double)Math.Max(w, h)));
        var ly = Math.Max(1, (int)Round(lLimit * h / (double)Math.Max(w, h)));
        double[] l = new double[n], p = new double[n], q = new double[n], a = new double[n];
        for (int i = 0, j = 0; i < n; i++, j += 4)
        {
            var alpha = rgba[j + 3] / 255.0;
            var r = avgR * (1 - alpha) + alpha / 255 * rgba[j];
            var g = avgG * (1 - alpha) + alpha / 255 * rgba[j + 1];
            var b = avgB * (1 - alpha) + alpha / 255 * rgba[j + 2];
            l[i] = (r + g + b) / 3;
            p[i] = (r + g) / 2 - b;
            q[i] = r - g;
            a[i] = alpha;
        }

        var (lDc, lAc, lScale) = Channel(l, w, h, Math.Max(3, lx), Math.Max(3, ly));
        var (pDc, pAc, pScale) = Channel(p, w, h, 3, 3);
        var (qDc, qAc, qScale) = Channel(q, w, h, 3, 3);
        var (aDc, aAc, aScale) = hasAlpha ? Channel(a, w, h, 5, 5) : (0, [], 0);

        var isLandscape = w > h;
        var header24 = (int)Round(63 * lDc) | ((int)Round(31.5 + 31.5 * pDc) << 6) | ((int)Round(31.5 + 31.5 * qDc) << 12)
            | ((int)Round(31 * lScale) << 18) | ((hasAlpha ? 1 : 0) << 23);
        var header16 = (isLandscape ? ly : lx) | ((int)Round(63 * pScale) << 3) | ((int)Round(63 * qScale) << 9) | ((isLandscape ? 1 : 0) << 15);
        var acStart = hasAlpha ? 6 : 5;
        var acCount = lAc.Length + pAc.Length + qAc.Length + aAc.Length;
        var hash = new byte[acStart + (acCount + 1) / 2];
        hash[0] = (byte)header24;
        hash[1] = (byte)(header24 >> 8);
        hash[2] = (byte)(header24 >> 16);
        hash[3] = (byte)header16;
        hash[4] = (byte)(header16 >> 8);
        if (hasAlpha) hash[5] = (byte)((int)Round(15 * aDc) | ((int)Round(15 * aScale) << 4));
        var index = 0;
        foreach (var ac in hasAlpha ? [lAc, pAc, qAc, aAc] : new[] { lAc, pAc, qAc })
            foreach (var f in ac)
            {
                hash[acStart + (index >> 1)] |= (byte)((int)Round(15 * f) << ((index & 1) << 2));
                index++;
            }

        var color = $"#{Byte(avgR):x2}{Byte(avgG):x2}{Byte(avgB):x2}";
        return new ThumbnailPlaceholder(hash, color);
    }

    private static (double Dc, double[] Ac, double Scale) Channel(double[] channel, int w, int h, int nx, int ny)
    {
        double dc = 0, scale = 0;
        var ac = new List<double>();
        var fx = new double[w];
        for (var cy = 0; cy < ny; cy++)
            for (var cx = 0; cx * ny < nx * (ny - cy); cx++)
            {
                double f = 0;
                for (var x = 0; x < w; x++) fx[x] = Math.Cos(Math.PI / w * cx * (x + 0.5));
                for (var y = 0; y < h; y++)
                {
                    var fy = Math.Cos(Math.PI / h * cy * (y + 0.5));
                    for (var x = 0; x < w; x++) f += channel[x + y * w] * fx[x] * fy;
                }
                f /= w * h;
                if (cx > 0 || cy > 0) { ac.Add(f); scale = Math.Max(scale, Math.Abs(f)); }
                else dc = f;
            }
        var terms = ac.ToArray();
        if (scale > 0) for (var i = 0; i < terms.Length; i++) terms[i] = 0.5 + 0.5 / scale * terms[i];
        return (dc, terms, scale);
    }

    /// <summary>JavaScript's Math.round (halves go up), which the reference encoder uses.</summary>
    private static double Round(double value) => Math.Floor(value + 0.5);

    private static int Byte(double value) => (int)Math.Clamp(Round(value * 255), 0, 255);
}
