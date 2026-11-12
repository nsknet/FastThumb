using System.Collections.Concurrent;
using SkiaSharp;

namespace FastThumb.Layout;

/// <summary>Vertical metrics of a font, as Skia gives them: ascent is negative (above the baseline).</summary>
internal readonly record struct LineMetrics(float Ascent, float Descent, float Leading)
{
    public LineMetrics Scale(float size) => new(Ascent * size, Descent * size, Leading * size);
}

/// <summary>
/// Process-level immutable cache: typefaces are resolved once and shared by every document and thread.
/// The same documents are rendered on Windows (Microsoft's fonts installed) and on Linux (free fonts only), so a
/// family that is not installed goes, in order, to:
///   1. a metric-compatible clone (same advance widths, so line breaks and pagination do not move):
///      Calibri ⇄ Carlito, Cambria ⇄ Caladea, Arial ⇄ Liberation Sans, Times New Roman ⇄ Liberation Serif, ...;
///   2. a look-alike (similar design, different widths): Aptos → Calibri, Verdana → DejaVu Sans, ...;
///   3. whatever the platform's own matching returns (Windows: its default face; Linux: fontconfig's best match).
/// </summary>
internal static class FontCache
{
    private static readonly ConcurrentDictionary<(string Family, bool Bold, bool Italic), (SKTypeface Face, bool Faithful)> Typefaces = new();

    /// <summary>Groups of families with identical metrics: any member stands in for any other without reflowing text.</summary>
    private static readonly string[][] MetricClones =
    [
        ["Arial", "Liberation Sans", "Arimo", "Helvetica", "ArialMT", "Nimbus Sans", "Nimbus Sans L"],
        ["Times New Roman", "Liberation Serif", "Tinos", "Times", "TimesNewRomanPSMT", "Nimbus Roman", "Nimbus Roman No9 L"],
        ["Courier New", "Liberation Mono", "Cousine", "Courier", "CourierNewPSMT", "Nimbus Mono PS"],
        ["Arial Narrow", "Liberation Sans Narrow"],
        ["Calibri", "Carlito"],
        ["Cambria", "Caladea"],
        ["Georgia", "Gelasio"],
    ];

    /// <summary>
    /// Second choices when no clone is installed: close in design and as close as possible in width. Each is tried with
    /// its own clones. Widths of one test sentence, in em: Segoe UI 45.9, Tahoma 46.5, Trebuchet 47.5, Arial / Liberation
    /// Sans 46.1, Verdana 53.2, DejaVu Sans 52.6, Century Gothic 50.1, Georgia 47.6, Times / Liberation Serif 42.7,
    /// DejaVu Serif 53.6, Consolas 58.3, Courier New / Liberation Mono 63.6, DejaVu Sans Mono 63.8.
    /// </summary>
    private static readonly Dictionary<string, string[]> LookAlikes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Aptos Narrow (Excel's default since 2024) as Calibri, not Arial Narrow: measured on 69 real workbooks against their
        // printed output, Arial Narrow moved 65 of them further away
        ["Aptos"] = ["Calibri"],
        ["Aptos Display"] = ["Calibri Light", "Calibri"],
        ["Aptos Narrow"] = ["Calibri"],
        ["Calibri Light"] = ["Calibri"],
        ["Cambria Math"] = ["Cambria"],
        ["Helvetica Neue"] = ["Arial"],
        ["Arial Unicode MS"] = ["Arial"],
        ["Arial-BoldMT"] = ["Arial"],
        ["Segoe UI"] = ["Arial", "DejaVu Sans"],
        ["Segoe UI Light"] = ["Segoe UI"],
        ["Segoe UI Semibold"] = ["Segoe UI"],
        ["Verdana"] = ["DejaVu Sans", "Arial"],
        ["Tahoma"] = ["Arial", "DejaVu Sans"],
        ["Trebuchet MS"] = ["Arial", "DejaVu Sans"],
        ["Lucida Grande"] = ["Arial", "DejaVu Sans"],
        ["Lucida Sans Unicode"] = ["Arial", "DejaVu Sans"],
        ["Century Gothic"] = ["DejaVu Sans", "Arial"],
        ["Consolas"] = ["DejaVu Sans Mono", "Courier New"],
        ["Lucida Console"] = ["DejaVu Sans Mono", "Courier New"],
        ["Georgia"] = ["Times New Roman", "DejaVu Serif"],
        ["Arial Narrow"] = ["DejaVu Sans Condensed", "Arial"],
        ["Garamond"] = ["Georgia", "Times New Roman"],
        ["Book Antiqua"] = ["Georgia", "Times New Roman"],
        ["Palatino Linotype"] = ["Georgia", "Times New Roman"],
        ["Comic Sans MS"] = ["Comic Neue", "DejaVu Sans"],
        ["Impact"] = ["DejaVu Sans Condensed", "Arial"],
        // Vietnamese and Chinese defaults that Office documents ask for
        [".VnTime"] = ["Times New Roman"],
        ["VNI-Times"] = ["Times New Roman"],
        [".VnArial"] = ["Arial"],
        ["SimSun"] = ["Noto Serif CJK SC", "Noto Sans CJK SC"],
        ["FangSong"] = ["Noto Serif CJK SC"],
        ["SimHei"] = ["Noto Sans CJK SC"],
        ["Microsoft YaHei"] = ["Noto Sans CJK SC"],
        ["MS Mincho"] = ["Noto Serif CJK JP"],
        ["MS Gothic"] = ["Noto Sans CJK JP"],
        ["Malgun Gothic"] = ["Noto Sans CJK KR"],
        ["Batang"] = ["Noto Serif CJK KR"],
        // the free families, the other way round (documents written on Linux, opened on Windows)
        ["DejaVu Sans"] = ["Verdana"],
        ["DejaVu Sans Condensed"] = ["Arial Narrow"],
        ["DejaVu Serif"] = ["Georgia"],
        ["DejaVu Sans Mono"] = ["Consolas", "Courier New"],
        ["Noto Sans"] = ["Segoe UI", "Arial"],
    };

    private static readonly Dictionary<string, string[]> Clones = MetricClones
        .SelectMany(group => group.Select(family => (family, group)))
        .ToDictionary(pair => pair.family, pair => pair.group, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Line spacing (per em, Skia's sign convention: ascent negative) of families that are only ever replaced by a
    /// look-alike, measured from the real fonts on Windows. Metric clones need no entry: Carlito, Caladea and the
    /// Liberation fonts already report exactly Calibri's, Cambria's, Arial's, Times New Roman's and Courier New's.
    /// </summary>
    private static readonly Dictionary<string, LineMetrics> OriginalSpacing = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Segoe UI"] = new(-1.0791f, 0.251f, 0f),
        ["Tahoma"] = new(-1.0005f, 0.2065f, 0f),
        ["Verdana"] = new(-1.0054f, 0.21f, 0f),
        ["Trebuchet MS"] = new(-0.939f, 0.2222f, 0f),
        ["Georgia"] = new(-0.917f, 0.2192f, 0f),
        ["Consolas"] = new(-0.9199f, 0.251f, 0f),
        ["Century Gothic"] = new(-0.9712f, 0.2202f, 0.0347f),
        ["Lucida Console"] = new(-0.7891f, 0.2109f, 0f),
        ["Lucida Sans Unicode"] = new(-1.0967f, 0.4399f, 0f),
        ["Garamond"] = new(-0.8618f, 0.2632f, 0f),
        ["Book Antiqua"] = new(-0.9604f, 0.2822f, 0f),
        ["Palatino Linotype"] = new(-1.0498f, 0.2993f, 0f),
        ["Comic Sans MS"] = new(-1.1021f, 0.2915f, 0f),
        ["Impact"] = new(-1.0088f, 0.2109f, 0f),
    };

    /// <summary>The requested family's own line spacing per em when the face used is only a look-alike of it, else null.</summary>
    public static LineMetrics? LineSpacing(RunProps props)
    {
        if (IsFaithful(props)) return null;
        var family = StyleSuffix(props.Font)?.Family ?? props.Font;
        return OriginalSpacing.TryGetValue(family, out var spacing) ? spacing : null;
    }

    public static SKTypeface Get(RunProps props) => Resolve(props).Face;

    /// <summary>
    /// True when the face has the requested family's metrics: the family itself or a metric-compatible clone.
    /// False for a look-alike or the platform default, where measurements tuned to the original do not apply.
    /// </summary>
    public static bool IsFaithful(RunProps props) => Resolve(props).Faithful;

    // family names come from documents: past this many (or for an absurd name) a face is resolved but not kept
    private const int MaxCached = 1024;

    private static (SKTypeface Face, bool Faithful) Resolve(RunProps props)
    {
        var key = (props.Font, props.Bold, props.Italic);
        if (Typefaces.TryGetValue(key, out var hit)) return hit;
        return Typefaces.Count >= MaxCached || props.Font.Length > 256 ? Find(key) : Typefaces.GetOrAdd(key, Find);
    }

    private static (SKTypeface Face, bool Faithful) Find((string Family, bool Bold, bool Italic) key)
    {
        SKTypeface? Load(string family) => SKTypeface.FromFamilyName(family,
            key.Bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal, SKFontStyleWidth.Normal,
            key.Italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);

        // FromFamilyName silently returns another face when the family is missing, hence the name checks
        SKTypeface? Exact(string family)
        {
            if (Load(family) is { } face && string.Equals(face.FamilyName, family, StringComparison.OrdinalIgnoreCase)) return face;
            // "Calibri Light", "Arial Narrow", "Segoe UI Semibold": Office names a style as if it were a family; the
            // platform (DirectWrite, fontconfig) files it under the base family with a weight or width
            if (StyleSuffix(family) is not { } style) return null;
            var weight = key.Bold ? SKFontStyleWeight.Bold : style.Weight ?? SKFontStyleWeight.Normal;
            var width = style.Width ?? SKFontStyleWidth.Normal;
            var styled = SKTypeface.FromFamilyName(style.Family, weight, width, key.Italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
            return styled != null && string.Equals(styled.FamilyName, style.Family, StringComparison.OrdinalIgnoreCase)
                && Math.Abs(styled.FontWeight - (int)weight) <= 100 && (style.Width == null || styled.FontWidth < (int)SKFontStyleWidth.Normal)
                ? styled : null;
        }

        IEnumerable<string> WithClones(string family) =>
            Clones.TryGetValue(family, out var group) ? group.Where(g => !string.Equals(g, family, StringComparison.OrdinalIgnoreCase)).Prepend(family) : [family];

        foreach (var candidate in WithClones(key.Family))
            if (Exact(candidate) is { } face) return (face, true);

        // look-alikes, breadth first, each with its clones; guards against cycles (DejaVu Sans ⇄ Verdana)
        var tried = new HashSet<string>(WithClones(key.Family), StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(LookAlikes.GetValueOrDefault(key.Family) ?? []);
        while (queue.Count > 0)
        {
            var next = queue.Dequeue();
            foreach (var candidate in WithClones(next))
            {
                if (!tried.Add(candidate)) continue;
                if (Exact(candidate) is { } face) return (face, false);
            }
            foreach (var further in LookAlikes.GetValueOrDefault(next) ?? []) queue.Enqueue(further);
        }
        return (Load(key.Family) ?? SKTypeface.Default, false);
    }

    private static readonly (string Suffix, SKFontStyleWeight? Weight, SKFontStyleWidth? Width)[] Styles =
    [
        (" Thin", SKFontStyleWeight.Thin, null), (" ExtraLight", SKFontStyleWeight.ExtraLight, null), (" Light", SKFontStyleWeight.Light, null),
        (" SemiLight", (SKFontStyleWeight)350, null), (" Medium", SKFontStyleWeight.Medium, null), (" SemiBold", SKFontStyleWeight.SemiBold, null),
        (" Black", SKFontStyleWeight.Black, null), (" Heavy", SKFontStyleWeight.Black, null),
        (" Narrow", null, SKFontStyleWidth.Condensed), (" Condensed", null, SKFontStyleWidth.Condensed),
    ];

    private static (string Family, SKFontStyleWeight? Weight, SKFontStyleWidth? Width)? StyleSuffix(string family)
    {
        foreach (var (suffix, weight, width) in Styles)
            if (family.Length > suffix.Length && family.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return (family[..^suffix.Length], weight, width);
        return null;
    }
}
