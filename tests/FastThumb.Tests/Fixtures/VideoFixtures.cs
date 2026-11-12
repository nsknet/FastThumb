namespace FastThumb.Tests.Fixtures;

/// <summary>
/// The committed test videos (tests/fixtures/video, made by tests/fixtures/make-videos.sh) and whether the native video
/// library loads on this platform. Video tests skip, not fail, when either is missing.
/// </summary>
internal static class VideoFixtures
{
    private static readonly Lazy<string?> s_unavailable = new(() =>
    {
        try
        {
            _ = FastThumb.Video.VideoFrame.Version;
            return null;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or TypeInitializationException)
        {
            return $"native video library not available on this platform ({e.GetType().Name})";
        }
    });

    /// <summary>Why video tests cannot run here, or null when they can.</summary>
    public static string? Unavailable => s_unavailable.Value;

    public static string? Directory { get; } = Find();

    private static string? Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "fixtures", "video");
            if (System.IO.Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static IReadOnlyList<string> Files { get; } = Directory == null ? [] :
        System.IO.Directory.GetFiles(Directory).Where(f => Path.GetExtension(f) is ".mp4" or ".webm" or ".mkv" or ".mov").Order(StringComparer.Ordinal).ToList();

    public static IEnumerable<object[]> FileData => Files.Select(f => new object[] { Path.GetFileName(f) });

    public static string PathOf(string name) => Path.Combine(Directory!, name);
}

/// <summary>A fact that skips when the native video library is not available.</summary>
public sealed class VideoFactAttribute : FactAttribute
{
    public VideoFactAttribute()
    {
        if (VideoFixtures.Unavailable is { } reason) Skip = reason;
        else if (VideoFixtures.Files.Count == 0) Skip = "no test videos in tests/fixtures/video (run tests/fixtures/make-videos.sh)";
    }
}

/// <summary>A theory over the fixture videos that skips when the native video library or the videos are missing.</summary>
public sealed class VideoTheoryAttribute : TheoryAttribute
{
    public VideoTheoryAttribute()
    {
        if (VideoFixtures.Unavailable is { } reason) Skip = reason;
        else if (VideoFixtures.Files.Count == 0) Skip = "no test videos in tests/fixtures/video (run tests/fixtures/make-videos.sh)";
    }
}
