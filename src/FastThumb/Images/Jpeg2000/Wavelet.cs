namespace FastThumb.Images.Jpeg2000;

/// <summary>
/// Inverse discrete wavelet transform (ITU-T T.800 Annex F): the reversible 5/3 filter (integer lifting) or the irreversible
/// 9/7 filter, by lifting on a line extended symmetrically at both ends, rows first and then columns at each level.
/// </summary>
internal static class Wavelet
{
    private const float Alpha = -1.586134342059924f, Beta = -0.052980118572961f, Gamma = 0.882911075530934f, Delta = 0.443506852043971f;
    private const float K = 1.230174104914001f;

    /// <summary>
    /// The samples of the highest resolution given, from the LL band of resolution 0 and the HL, LH, HH bands of each
    /// resolution after it. Bounds are in each resolution's (or band's) own coordinates.
    /// </summary>
    public static float[] Reconstruct((int X0, int Y0, int X1, int Y1, (int X0, int Y0, int X1, int Y1, float[] Samples)[] Bands)[] resolutions, bool reversible)
    {
        var current = resolutions[0].Bands[0].Samples;
        float[] line = [];
        for (var r = 1; r < resolutions.Length; r++)
        {
            Guard.Check();
            var (x0, y0, x1, y1, bands) = resolutions[r];
            var previous = resolutions[r - 1];
            int width = x1 - x0, height = y1 - y0;
            if (width <= 0 || height <= 0) { current = []; continue; }
            var next = new float[width * height];

            // interleave: even columns and rows from the low-pass bands
            var (llx, lly, llw) = (previous.X0, previous.Y0, previous.X1 - previous.X0);
            var hl = bands[0];
            var lh = bands[1];
            var hh = bands[2];
            for (var y = y0; y < y1; y++)
                for (var x = x0; x < x1; x++)
                {
                    float value;
                    if ((y & 1) == 0)
                        value = (x & 1) == 0 ? current[(y / 2 - lly) * llw + x / 2 - llx] : hl.Samples[(y / 2 - hl.Y0) * (hl.X1 - hl.X0) + x / 2 - hl.X0];
                    else
                        value = (x & 1) == 0 ? lh.Samples[(y / 2 - lh.Y0) * (lh.X1 - lh.X0) + x / 2 - lh.X0] : hh.Samples[(y / 2 - hh.Y0) * (hh.X1 - hh.X0) + x / 2 - hh.X0];
                    next[(y - y0) * width + x - x0] = value;
                }

            if (line.Length < width + 8) line = new float[width + 8];
            for (var y = 0; y < height; y++)
                Inverse(next.AsSpan(y * width, width), x0, reversible, line);
            Columns(next, width, height, y0, reversible);
            current = next;
        }
        return current;
    }

    private const int Batch = 32;

    /// <summary>
    /// Every column in place, <see cref="Batch"/> at a time: the lifting steps then run along rows of the batch, which keeps the
    /// memory access sequential instead of one sample per row of the picture.
    /// </summary>
    private static void Columns(float[] samples, int width, int height, int y0, bool reversible)
    {
        if (height == 1)
        {
            if ((y0 & 1) != 0) for (var i = 0; i < width; i++) samples[i] /= 2;
            return;
        }
        var pad = reversible ? 2 : 4;
        var length = height + 2 * pad;
        var period = 2 * (height - 1);
        var even = (y0 - pad) & 1;
        var x = new float[length * Batch];
        for (var start = 0; start < width; start += Batch)
        {
            var count = Math.Min(Batch, width - start);
            for (var j = 0; j < length; j++)
            {
                var k = (j - pad) % period;
                if (k < 0) k += period;
                if (k >= height) k = period - k;
                samples.AsSpan(k * width + start, count).CopyTo(x.AsSpan(j * Batch, count));
            }
            if (reversible)
            {
                for (var j = 1; j < length - 1; j++)
                    if (((j + even) & 1) == 0)
                        for (int c = 0, at = j * Batch; c < count; c++, at++) x[at] -= MathF.Floor((x[at - Batch] + x[at + Batch] + 2) / 4);
                for (var j = 2; j < length - 2; j++)
                    if (((j + even) & 1) == 1)
                        for (int c = 0, at = j * Batch; c < count; c++, at++) x[at] += MathF.Floor((x[at - Batch] + x[at + Batch]) / 2);
            }
            else
            {
                for (var j = 0; j < length; j++)
                {
                    var scale = ((j + even) & 1) == 0 ? K : 1 / K;
                    for (int c = 0, at = j * Batch; c < count; c++, at++) x[at] *= scale;
                }
                Lift(x, count, length, even, 1, 0, Delta);
                Lift(x, count, length, even, 2, 1, Gamma);
                Lift(x, count, length, even, 3, 0, Beta);
                Lift(x, count, length, even, 4, 1, Alpha);
            }
            for (var j = 0; j < height; j++) x.AsSpan((j + pad) * Batch, count).CopyTo(samples.AsSpan(j * width + start, count));
        }
    }

    /// <summary>One 9/7 lifting step over a batch of columns: rows of parity <paramref name="parity"/> lose <paramref name="factor"/> times their neighbours.</summary>
    private static void Lift(float[] x, int count, int length, int even, int margin, int parity, float factor)
    {
        for (var j = margin; j < length - margin; j++)
            if (((j + even) & 1) == parity)
                for (int c = 0, at = j * Batch; c < count; c++, at++) x[at] -= factor * (x[at - Batch] + x[at + Batch]);
    }

    /// <summary>One line in place (1D_SR): <paramref name="i0"/> is the coordinate of its first sample, whose parity says low or high pass.</summary>
    private static void Inverse(Span<float> samples, int i0, bool reversible, float[] buffer)
    {
        var n = samples.Length;
        if (n == 1)
        {
            if ((i0 & 1) != 0) samples[0] /= 2;
            return;
        }
        var pad = reversible ? 2 : 4;
        var length = n + 2 * pad;
        var x = buffer.AsSpan(0, length);
        var period = 2 * (n - 1);
        for (var j = 0; j < length; j++)
        {
            // whole-sample symmetric extension
            var k = (j - pad) % period;
            if (k < 0) k += period;
            if (k >= n) k = period - k;
            x[j] = samples[k];
        }
        // parity of buffer index j is that of coordinate i0 - pad + j
        var even = (i0 - pad) & 1;                  // j is a low-pass (even) sample when (j + i0 - pad) is even
        if (reversible)
        {
            for (var j = 1; j < length - 1; j++)
                if (((j + even) & 1) == 0) x[j] -= MathF.Floor((x[j - 1] + x[j + 1] + 2) / 4);
            for (var j = 2; j < length - 2; j++)
                if (((j + even) & 1) == 1) x[j] += MathF.Floor((x[j - 1] + x[j + 1]) / 2);
        }
        else
        {
            for (var j = 0; j < length; j++) x[j] *= ((j + even) & 1) == 0 ? K : 1 / K;
            for (var j = 1; j < length - 1; j++)
                if (((j + even) & 1) == 0) x[j] -= Delta * (x[j - 1] + x[j + 1]);
            for (var j = 2; j < length - 2; j++)
                if (((j + even) & 1) == 1) x[j] -= Gamma * (x[j - 1] + x[j + 1]);
            for (var j = 3; j < length - 3; j++)
                if (((j + even) & 1) == 0) x[j] -= Beta * (x[j - 1] + x[j + 1]);
            for (var j = 4; j < length - 4; j++)
                if (((j + even) & 1) == 1) x[j] -= Alpha * (x[j - 1] + x[j + 1]);
        }
        x.Slice(pad, n).CopyTo(samples);
    }
}
