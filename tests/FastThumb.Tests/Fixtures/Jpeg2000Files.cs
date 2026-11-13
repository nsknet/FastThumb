namespace FastThumb.Tests.Fixtures;

/// <summary>The committed JPEG 2000 files (tests/fixtures/jpeg2000, made by tests/fixtures/make-jpeg2000.py) and their references.</summary>
internal static class Jpeg2000Files
{
    public static string? Directory { get; } = Find();

    private static string? Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "fixtures", "jpeg2000");
            if (System.IO.Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>Every (file, reduction) with a reference picture.</summary>
    public static IReadOnlyList<(string File, int Reduce)> References { get; } = Directory == null ? [] :
        System.IO.Directory.GetFiles(Directory, "*.png")
            .Select(Path.GetFileName)
            .Select(name => (File: name![..name!.LastIndexOf(".r", StringComparison.Ordinal)], Reduce: int.Parse(name[(name.LastIndexOf(".r", StringComparison.Ordinal) + 2)..^4], System.Globalization.CultureInfo.InvariantCulture)))
            .OrderBy(r => r.File, StringComparer.Ordinal).ThenBy(r => r.Reduce)
            .ToList();

    public static byte[] Read(string file) => File.ReadAllBytes(Path.Combine(Directory ?? throw new InvalidOperationException("tests/fixtures/jpeg2000 not found"), file));

    /// <summary>
    /// Largest and mean difference allowed from the reference, per file: none for the reversible wavelet, float rounding for the
    /// irreversible one, a little more where the reference turns sYCC into RGB with its own rounding.
    /// </summary>
    public static (int Max, double Mean) Tolerance(string file) => file switch
    {
        _ when file.StartsWith("lossless", StringComparison.Ordinal) || file.StartsWith("tiled", StringComparison.Ordinal)
            || file.StartsWith("grey", StringComparison.Ordinal) || file.StartsWith("offset", StringComparison.Ordinal)
            || file.StartsWith("ffmpeg-sop", StringComparison.Ordinal) => (0, 0),
        _ when file.StartsWith("ffmpeg-yuv", StringComparison.Ordinal) => (2, 0.6),
        _ => (1, 0.01),
    };
}
