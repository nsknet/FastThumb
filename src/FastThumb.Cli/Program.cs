using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using FastThumb;
using FastThumb.Layout;
using SkiaSharp;

// Command-line front end over FastThumb. `fastthumb --help` lists everything; README.md has examples.
//
// Output names: <out>/<name>.<size>.<png|webp|jpg> and <out>/<name>.preview.pdf, where <name> is the path relative to
// the input directory with '/' replaced by '__' (the file name for a file given directly), so runs on different
// machines can be compared file by file (--json prints one line per file).

Console.OutputEncoding = System.Text.Encoding.UTF8;   // file names and the · separators, also on a Windows console
try
{
    return args switch
    {
        [] or ["-h" or "--help" or "help"] => Cli.Help(),
        ["-V" or "--version" or "version"] => Cli.PrintVersion(),
        ["info", ..] => Cli.Info(),
        ["fonts", .. var families] when families.Length > 0 => Cli.Fonts(families),
        ["fonts"] => throw new UsageException("fonts: give at least one font family"),
        ["inspect", .. var files] when files.Length > 0 => Cli.Inspect(files),
        ["inspect"] => throw new UsageException("inspect: give at least one file"),
        ["run", .. var rest] when rest.Any(a => a is "-h" or "--help") => Cli.Help(),
        ["run", .. var rest] => Cli.Run(RunSettings.Parse(rest)),
        _ => throw new UsageException($"unknown command '{args[0]}'"),
    };
}
catch (UsageException e)
{
    Console.Error.WriteLine($"fastthumb: {e.Message}");
    Console.Error.WriteLine("Try 'fastthumb --help'.");
    return 2;
}

sealed class UsageException(string message) : Exception(message);

/// <summary>Everything `fastthumb run` was asked to do.</summary>
sealed record RunSettings(
    List<(string Path, string Name)> Files, string OutDir, ThumbnailOptions Options, int Parallel,
    bool Json, bool Quiet, bool Verbose, bool Color, bool SkipExisting)
{
    public ThumbnailGenerator Generator { get; } = new(Options);

    public static RunSettings Parse(string[] args)
    {
        var inputs = new List<string>();
        string? outDir = null;
        var options = ThumbnailOptions.Default;
        var parallel = 1;
        bool json = false, quiet = false, verbose = false, skipExisting = false;
        var color = !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null or "";
        HashSet<string>? include = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--") { inputs.AddRange(args[(i + 1)..]); break; }
            if (!arg.StartsWith('-') || arg == "-") { inputs.Add(arg); continue; }

            // --name=value is the same as --name value
            string? inline = null;
            var eq = arg.IndexOf('=');
            if (arg.StartsWith("--") && eq > 0) (arg, inline) = (arg[..eq], arg[(eq + 1)..]);
            var name = arg;
            string Value() => inline ?? (i + 1 < args.Length ? args[++i] : throw new UsageException($"option {name} needs a value"));
            int Int(int min, int max)
            {
                var text = Value();
                return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= min && n <= max
                    ? n : throw new UsageException($"option {name}: '{text}' is not a whole number in {min}–{max}");
            }
            double Number(double min, double max)
            {
                var text = Value();
                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n >= min && n <= max
                    ? n : throw new UsageException($"option {name}: '{text}' is not a number in {min}–{max}");
            }

            switch (arg)
            {
                case "-o" or "--out": outDir = Value(); break;
                case "-m" or "--mode":
                    options = options with
                    {
                        // --text and --placeholder add to the mode, whichever comes first
                        Outputs = (options.Outputs & ~ThumbnailOutputs.All) | Value().ToLowerInvariant() switch
                        {
                            "images" or "image" => ThumbnailOutputs.Images,
                            "pdf" => ThumbnailOutputs.PreviewPdf,
                            "both" or "all" => ThumbnailOutputs.All,
                            "none" => 0,
                            var other => throw new UsageException($"option --mode: '{other}' is not images, pdf, both or none"),
                        },
                    };
                    break;
                case "-s" or "--sizes":
                    var (sizes, full) = ParseSizes(Value());
                    options = options with { Sizes = sizes, IncludeFullSize = full };
                    break;
                case "-f" or "--format":
                    options = options with
                    {
                        Format = Value().ToLowerInvariant() switch
                        {
                            "png" => ThumbnailFormat.Png,
                            "webp" => ThumbnailFormat.WebP,
                            "jpeg" or "jpg" => ThumbnailFormat.Jpeg,
                            var other => throw new UsageException($"option --format: '{other}' is not png, webp or jpeg"),
                        },
                    };
                    break;
                case "--fit":
                    options = options with
                    {
                        Fit = Value().ToLowerInvariant() switch
                        {
                            "contain" => ThumbnailFit.Contain,
                            "cover" => ThumbnailFit.Cover,
                            var other => throw new UsageException($"option --fit: '{other}' is not contain or cover"),
                        },
                    };
                    break;
                case "--aspect": options = options with { AspectRatio = ParseAspect(name, Value()) }; break;
                case "--max-pages": options = options with { MaxPages = Int(1, int.MaxValue) }; break;
                case "--text": options = options with { Outputs = options.Outputs | ThumbnailOutputs.Text }; break;
                case "--placeholder": options = options with { Outputs = options.Outputs | ThumbnailOutputs.Placeholder }; break;
                case "-q" or "--quality": options = options with { Quality = Int(1, 100) }; break;
                case "--png-compression": options = options with { PngCompressionLevel = Int(0, 9) }; break;
                case "--video-position": options = options with { Video = options.Video with { Position = Number(0, 1) } }; break;
                case "--video-timeout": options = options with { Video = options.Video with { Timeout = TimeSpan.FromSeconds(Number(1, 86400)) } }; break;
                case "-j" or "--parallel": parallel = Int(1, 256); break;
                case "--no-parallel-encode": options = options with { EncodeInParallel = false }; break;
                case "-i" or "--include":
                    include = Value().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(e => "." + e.TrimStart('.').ToLowerInvariant()).ToHashSet();
                    break;
                case "--skip-existing": skipExisting = true; break;
                case "--json": json = true; break;
                case "--quiet": quiet = true; break;
                case "-v" or "--verbose": verbose = true; break;
                case "--no-color": color = false; break;
                default: throw new UsageException($"unknown option '{arg}'");
            }
        }

        if (outDir == null) throw new UsageException("missing --out <dir>");
        if (inputs.Count == 0) throw new UsageException("no input file or directory");
        try { options.Validate(); }
        catch (ArgumentException e) { throw new UsageException(e.Message.Split(" (Parameter")[0]); }

        var files = new List<(string, string)>();
        foreach (var input in inputs)
        {
            if (Directory.Exists(input))
                files.AddRange(Directory.EnumerateFiles(input, "*", SearchOption.AllDirectories)
                    .Where(f => include == null || include.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .Order(StringComparer.Ordinal)
                    .Select(f => (f, Path.GetRelativePath(input, f).Replace('\\', '/').Replace("/", "__"))));
            else if (File.Exists(input)) files.Add((input, Path.GetFileName(input)));
            else throw new UsageException($"not found: {input}");
        }
        // "a/b.docx" and "a__b.docx" (or one name under two inputs) would write the same outputs: one would silently replace the other
        var clash = files.GroupBy(f => f.Item2, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (clash != null)
            throw new UsageException($"two inputs would write the same output name '{clash.Key}': {string.Join(", ", clash.Select(f => f.Item1))}");
        return new RunSettings(files, outDir, options, parallel, json, quiet, verbose, color && !json, skipExisting);
    }

    /// <summary>"full,2500,1000" → sizes [2500, 1000] + full; "none" → no thumbnail size (only "full", if listed).</summary>
    /// <summary>"16:9", "1:1" or a plain number: width divided by height.</summary>
    private static double ParseAspect(string name, string text)
    {
        var parts = text.Split(':');
        double value = double.NaN;
        if (parts.Length == 1) double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        else if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var width)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var height) && height > 0)
            value = width / height;
        return value is >= 0.01 and <= 100 ? value : throw new UsageException($"option {name}: '{text}' is not a ratio like 16:9 between 0.01 and 100");
    }

    private static (IReadOnlyList<int>, bool) ParseSizes(string text)
    {
        var sizes = new List<int>();
        var full = false;
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Equals("full", StringComparison.OrdinalIgnoreCase)) full = true;
            else if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) && size is >= 1 and <= 65500) { if (!sizes.Contains(size)) sizes.Add(size); }
            else throw new UsageException($"option --sizes: '{part}' is not a size in px (1–65500) or 'full'");
        }
        if (sizes.Count == 0 && !full) throw new UsageException("option --sizes: no size given");
        sizes.Sort((a, b) => b.CompareTo(a));
        return (sizes, full);
    }
}

/// <summary>What happened to one input file.</summary>
sealed record FileResult(string Name, string Status, DocumentFormat? Format, int Pages, int Images, long ImageBytes, long PdfBytes, double Ms,
    ThumbnailTimings? Timings, string? Message, long TextBytes = 0, ThumbnailPlaceholder? Placeholder = null, bool MorePages = false);

static class Cli
{
    private static string Version(Assembly assembly) =>
        (assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? assembly.GetName().Version?.ToString() ?? "?").Split('+')[0];

    public static int PrintVersion()
    {
        Console.WriteLine($"fastthumb {Version(typeof(Cli).Assembly)} (FastThumb {Version(typeof(ThumbnailGenerator).Assembly)}, .NET {Environment.Version}, {RuntimeInformation.RuntimeIdentifier})");
        return 0;
    }

    public static int Help()
    {
        Console.WriteLine($"""
            fastthumb {Version(typeof(Cli).Assembly)} — thumbnails and preview PDFs for Office documents, PDF, images and video

            Usage:
              fastthumb run <input>... -o <dir> [options]   make thumbnails and/or preview PDFs
              fastthumb inspect <file>...                   format, pages, size and properties, one JSON line per file
              fastthumb info                                platform, native libraries, how Office fonts resolve
              fastthumb fonts <family>...                   width and line spacing of installed font families
              fastthumb --version | --help

            <input> is a file or a directory (read recursively). Outputs are named
              <dir>/<name>.<size>.<png|webp|jpg>   and   <dir>/<name>.preview.pdf
            where <name> is the path below the input directory with '/' replaced by '__'.

            Options for run:
              -o, --out <dir>              output directory, created if missing                 (required)
              -m, --mode <what>            images | pdf | both | none                              [both]
                                           pdf = preview PDF of every page; documents only (a PDF,
                                           an image or a video has none)
                  --text                   also write the text of every page to <dir>/<name>.txt
                                           (pages separated by a form feed)
                  --placeholder            also make a ThumbHash and average colour (shown with
                                           --json and --verbose)
                  --max-pages <n>          lay out, write and read at most n pages             [all]
              -s, --sizes <list>           long side of each image in px, comma separated; "full" is
                                           the page at 96 DPI, a PDF page at 72 DPI, an image or video
                                           at its own size; images are never enlarged
                                                                                   [full,2500,1000,500,300,100]
                  --fit <how>              contain: whole page, long side = size; cover: fill
                                           the box and crop (top of a page, centre of a picture)  [contain]
                  --aspect <w:h>           box shape for --fit cover, e.g. 1:1, 16:9, 0.75        [1:1]
              -f, --format <format>        png | webp | jpeg                                       [png]
              -q, --quality <1-100>        webp / jpeg quality                                     [80]
                  --png-compression <0-9>  zlib level; lower is faster and larger                  [3]
                  --video-position <0-1>   where the video frame is taken (0.5 = the middle)       [0.5]
                  --video-timeout <sec>    give up on a video after this long                      [60]
              -j, --parallel <n>           files processed at the same time                        [1]
                  --no-parallel-encode     encode the sizes of a file one after another: less CPU,
                                           more time per file (worth it with a high --parallel)
              -i, --include <exts>         only these extensions, e.g. docx,xlsx,pdf               [all]
                  --skip-existing          skip a file whose outputs are all there already
                  --json                   one JSON object per file on stdout (summary on stderr)
                  --quiet                  print the summary only
              -v, --verbose                stage timings per file: render · pdf · images
                  --no-color               no ANSI colours (also when NO_COLOR is set or output is redirected)

            Exit status: 0 all files done, 1 some failed, 2 bad command line, 130 interrupted.

            Examples:
              fastthumb run report.docx -o out
              fastthumb run in/ -o out -m images -s 1000,300 -f webp -q 75 -j 4
              fastthumb run in/ -o out -m pdf -i docx,doc,xlsx,pptx
              fastthumb run in/ -o out --json > run.jsonl
              fastthumb run in/ -o out -m none --text --max-pages 50
              fastthumb run photos/ -o out -m images -s 300 --fit cover --placeholder --json
            """);
        return 0;
    }

    /// <summary>One JSON line per file: what ThumbnailGenerator.Inspect reads, without rendering anything.</summary>
    public static int Inspect(IEnumerable<string> files)
    {
        var failed = false;
        foreach (var file in files)
        {
            if (!File.Exists(file)) throw new UsageException($"input not found: {file}");
            object line;
            try
            {
                var info = ThumbnailGenerator.Default.InspectFile(file);
                line = new
                {
                    file,
                    format = info.Format.ToString(),
                    pages = info.PageCount,
                    width = info.Width,
                    height = info.Height,
                    title = info.Title,
                    author = info.Author,
                    subject = info.Subject,
                    keywords = info.Keywords,
                    created = info.Created,
                    modified = info.Modified,
                    durationSeconds = info.Duration?.TotalSeconds,
                    videoCodec = info.VideoCodec,
                };
            }
            catch (Exception e) when (e is ThumbnailException or IOException or TimeoutException or UnauthorizedAccessException)
            {
                failed = true;
                line = new { file, error = $"{e.GetType().Name}: {e.Message}" };
            }
            Console.WriteLine(JsonSerializer.Serialize(line, JsonOptions));
        }
        return failed ? 1 : 0;
    }

    public static int Fonts(IEnumerable<string> families)
    {
        const string text = "The quick brown fox jumps over the lazy dog. Status of the 71 Critical/High line items: naïve façade, Ærøskøbing, Zürich.";
        Console.WriteLine($"{"family",-24} {"width",7} {"ascent",7} {"descent",7} {"leading",7}   (per em; ascent as a positive number)");
        foreach (var family in families)
        {
            using var face = SKTypeface.FromFamilyName(family);
            if (face == null || !string.Equals(face.FamilyName, family, StringComparison.OrdinalIgnoreCase)) { Console.WriteLine($"{family,-24} not installed"); continue; }
            using var font = new SKFont(face, 1) { LinearMetrics = true, Hinting = SKFontHinting.None };
            using var big = new SKFont(face, 100) { LinearMetrics = true, Hinting = SKFontHinting.None };
            var m = font.Metrics;
            Console.WriteLine(FormattableString.Invariant($"{family,-24} {big.MeasureText(text) / 100,7:0.0} {-m.Ascent,7:0.0000} {m.Descent,7:0.0000} {m.Leading,7:0.0000}"));
        }
        return 0;
    }

    public static int Info()
    {
        Console.WriteLine($"fastthumb   {Version(typeof(Cli).Assembly)}, FastThumb {Version(typeof(ThumbnailGenerator).Assembly)}");
        Console.WriteLine($"os         {RuntimeInformation.OSDescription} ({RuntimeInformation.RuntimeIdentifier}), .NET {Environment.Version}, {Environment.ProcessorCount} CPUs");
        Console.WriteLine($"libvips    {NetVips.NetVips.Version(0)}.{NetVips.NetVips.Version(1)}.{NetVips.NetVips.Version(2)}");
        Console.WriteLine($"skia       {SkiaSharpVersion.Native}");
        Console.WriteLine($"thumbvideo {FastThumb.Video.VideoFrame.Version}");
        using (var pdf = SmallPdf())
            Console.WriteLine($"pdfium     renders a page: {(FastThumb.Scene.Pdfium.RenderFirstPage(pdf.ToArray(), 100, out _, out _) is { Width: > 0 } ? "yes" : "no")}");
        Console.WriteLine("fonts      requested -> used (= same metrics, ~ look-alike, ! platform default)");
        foreach (var family in new[] { "Calibri", "Calibri Light", "Cambria", "Arial", "Times New Roman", "Courier New", "Aptos", "Aptos Narrow",
                     "Verdana", "Tahoma", "Segoe UI", "Segoe UI Semibold", "Arial Narrow", "Consolas", "Georgia", "Symbol", "SimSun" })
        {
            var props = new RunProps { Font = family };
            var face = FontCache.Get(props);
            var mark = FontCache.IsFaithful(props) ? '=' : string.Equals(face.FamilyName, SKTypeface.Default.FamilyName, StringComparison.OrdinalIgnoreCase) ? '!' : '~';
            Console.WriteLine($"           {family,-18} {mark} {face.FamilyName} (weight {face.FontWeight}, width {face.FontWidth})");
        }
        return 0;
    }

    /// <summary>A one-page PDF drawn with Skia, to prove PDFium loads and renders on this platform without a sample file.</summary>
    private static MemoryStream SmallPdf()
    {
        var stream = new MemoryStream();
        using (var document = SKDocument.CreatePdf(stream))
        {
            var canvas = document.BeginPage(200, 100);
            using var paint = new SKPaint { Color = SKColors.Blue };
            canvas.DrawRect(10, 10, 180, 80, paint);
            document.EndPage();
        }
        return stream;
    }

    // ---- run ------------------------------------------------------------------------------------------------

    public static int Run(RunSettings run)
    {
        Directory.CreateDirectory(run.OutDir);
        var o = run.Options;
        var summaryOut = run.Json ? Console.Error : Console.Out;
        var nameWidth = Math.Clamp(run.Files.Count == 0 ? 10 : run.Files.Max(f => f.Name.Length), 10, 44);

        if (!run.Json && !run.Quiet)
        {
            var what = (o.Outputs & ThumbnailOutputs.All) switch { ThumbnailOutputs.Images => "images", ThumbnailOutputs.PreviewPdf => "preview PDF", 0 => "", _ => "images + preview PDF" };
            if ((o.Outputs & ThumbnailOutputs.Text) != 0) what = what.Length == 0 ? "text" : what + " + text";
            if ((o.Outputs & ThumbnailOutputs.Placeholder) != 0) what = what.Length == 0 ? "placeholder" : what + " + placeholder";
            if (o.Fit == ThumbnailFit.Cover) what += $", cover {o.AspectRatio.ToString("0.###", CultureInfo.InvariantCulture)}:1";
            if (o.MaxPages is { } maxPages) what += $", at most {Plural(maxPages, "page")}";
            var sizes = string.Join(",", (o.IncludeFullSize ? new[] { "full" } : []).Concat(o.Sizes.Select(s => s.ToString(CultureInfo.InvariantCulture))));
            var format = o.Format == ThumbnailFormat.Png ? $"png (compression {o.PngCompressionLevel})" : $"{o.FileExtension} (quality {o.Quality})";
            Console.WriteLine($"fastthumb {Version(typeof(Cli).Assembly)} · .NET {Environment.Version} · {RuntimeInformation.RuntimeIdentifier}");
            Console.WriteLine($"{run.Files.Count} file(s) → {Path.GetFullPath(run.OutDir)}");
            Console.WriteLine((o.Outputs & ThumbnailOutputs.Images) != 0
                ? $"{what}: {sizes} as {format}, {run.Parallel} at a time"
                : $"{what}, {run.Parallel} at a time");
            Console.WriteLine();
        }

        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
        var results = new ConcurrentQueue<FileResult>();
        var print = new Lock();
        var clock = Stopwatch.StartNew();
        var interrupted = false;
        try
        {
            Parallel.ForEach(run.Files, new ParallelOptions { MaxDegreeOfParallelism = run.Parallel, CancellationToken = cancel.Token }, file =>
            {
                var result = Process(file.Path, file.Name, run);
                results.Enqueue(result);
                lock (print) Report(result, run, nameWidth);
            });
        }
        catch (OperationCanceledException) { interrupted = true; }

        Summary(summaryOut, [.. results], clock.Elapsed, run, interrupted);
        return interrupted ? 130 : results.Any(r => r.Status == "error") ? 1 : 0;
    }

    private static FileResult Process(string path, string name, RunSettings run)
    {
        var o = run.Options;
        var wantImages = (o.Outputs & ThumbnailOutputs.Images) != 0;
        var wantPdf = (o.Outputs & ThumbnailOutputs.PreviewPdf) != 0;
        string ImagePath(string size, string? extension = null) => Path.Combine(run.OutDir, $"{name}.{size}.{extension ?? o.FileExtension}");
        var pdfPath = Path.Combine(run.OutDir, $"{name}.preview.pdf");
        var wantText = (o.Outputs & ThumbnailOutputs.Text) != 0;
        var textPath = Path.Combine(run.OutDir, $"{name}.txt");

        if (run.SkipExisting)
        {
            var names = (o.IncludeFullSize ? new[] { "full" } : []).Concat(o.Sizes.Select(s => s.ToString(CultureInfo.InvariantCulture)));
            var imagesThere = !wantImages || names.All(n => File.Exists(ImagePath(n)));
            var pdfThere = !wantPdf || File.Exists(pdfPath) || !HasPreviewPdf(path);
            var textThere = !wantText || File.Exists(textPath);
            if (imagesThere && pdfThere && textThere) return new FileResult(name, "skipped", null, 0, 0, 0, 0, 0, null, "outputs already there");
        }

        var clock = Stopwatch.StartNew();
        try
        {
            var set = run.Generator.GenerateFromFile(path);
            long imageBytes = 0;
            foreach (var image in set.Images)
            {
                // an image too large for WebP or JPEG comes back as PNG: the file is named after what it holds
                WriteFile(ImagePath(image.Name, image.Format.FileExtension()), image.Data);
                imageBytes += image.Data.Length;
            }
            long pdfBytes = 0;
            if (set.PreviewPdf != null)
            {
                WriteFile(pdfPath, set.PreviewPdf);
                pdfBytes = set.PreviewPdf.Length;
            }
            long textBytes = 0;
            if (set.PageTexts is { } pages)
            {
                var text = System.Text.Encoding.UTF8.GetBytes(string.Join('\f', pages));
                WriteFile(textPath, text);
                textBytes = text.Length;
            }
            var nothing = set.Images.Count == 0 && set.PreviewPdf == null && set.PageTexts == null && set.Placeholder == null;
            return new FileResult(name, nothing ? "skipped" : "ok", set.Format, set.PageCount, set.Images.Count, imageBytes, pdfBytes,
                clock.Elapsed.TotalMilliseconds, set.Timings, nothing ? $"no preview PDF for {set.Format}" : null, textBytes, set.Placeholder, set.HasMorePages);
        }
        catch (Exception e)
        {
            return new FileResult(name, "error", null, 0, 0, 0, 0, clock.Elapsed.TotalMilliseconds, null, $"{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// Written next to the target and renamed over it: a crash or Ctrl+C never leaves a truncated output behind that
    /// --skip-existing would then take as done, and an existing link at the target is replaced, not followed.
    /// </summary>
    private static void WriteFile(string path, byte[] bytes)
    {
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>A PDF, an image or a video never gets a preview PDF; told from the first bytes, as the library does.</summary>
    private static bool HasPreviewPdf(string path)
    {
        Span<byte> head = stackalloc byte[512];
        using var file = File.OpenRead(path);
        head = head[..file.ReadAtLeast(head, head.Length, throwOnEndOfStream: false)];
        var extension = Path.GetExtension(path);
        return !head.StartsWith("%PDF-"u8) && !FormatDetector.IsImage(head, extension) && !FormatDetector.IsVideo(head, extension);
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    private static void Report(FileResult r, RunSettings run, int nameWidth)
    {
        if (run.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                file = r.Name,
                status = r.Status,
                format = r.Format?.ToString(),
                pages = r.Format == null ? (int?)null : r.Pages,
                ms = Math.Round(r.Ms),
                bytes = r.ImageBytes,
                images = r.Images,
                pdfBytes = r.PdfBytes,
                textBytes = r.TextBytes > 0 ? r.TextBytes : (long?)null,
                morePages = r.MorePages ? true : (bool?)null,
                placeholder = r.Placeholder is { } p ? new { thumbHash = p.ThumbHashBase64, color = p.AverageColor } : null,
                timings = r.Timings == null ? null : new { render = Math.Round(r.Timings.Value.Render.TotalMilliseconds, 1), pdf = Math.Round(r.Timings.Value.PreviewPdf.TotalMilliseconds, 1), images = Math.Round(r.Timings.Value.Encode.TotalMilliseconds, 1) },
                error = r.Status == "error" ? r.Message : null,
                note = r.Status == "skipped" ? r.Message : null,
            }, JsonOptions));
            return;
        }
        if (run.Quiet && r.Status != "error") return;

        var (tag, color) = r.Status switch { "ok" => ("ok  ", "32"), "skipped" => ("skip", "33"), _ => ("FAIL", "31") };
        var name = r.Name.Length > nameWidth ? "…" + r.Name[^(nameWidth - 1)..] : r.Name.PadRight(nameWidth);
        var line = $"  {Paint(tag, color, run.Color)}  {name}  ";
        if (r.Status == "ok")
        {
            line += $"{r.Format,-6} {Plural(r.Pages, "page"),9} {Ms(r.Ms),8}";
            if (r.Images > 0) line += $"   {r.Images} img {Size(r.ImageBytes),8}";
            if (r.PdfBytes > 0) line += $"   pdf {Size(r.PdfBytes),8}";
            if (r.TextBytes > 0) line += $"   txt {Size(r.TextBytes),8}";
            if (r.MorePages) line += "   (more pages)";
            if (run.Verbose && r.Placeholder is { } placeholder) line += $"   {placeholder}";
            if (run.Verbose && r.Timings is { } t)
                line += Paint($"   [render {Ms(t.Render.TotalMilliseconds)} · pdf {Ms(t.PreviewPdf.TotalMilliseconds)} · images {Ms(t.Encode.TotalMilliseconds)}]", "90", run.Color);
        }
        else line += Paint(r.Message ?? "", r.Status == "error" ? "31" : "33", run.Color);
        Console.WriteLine(line);
    }

    private static void Summary(TextWriter output, FileResult[] results, TimeSpan elapsed, RunSettings run, bool interrupted)
    {
        int ok = results.Count(r => r.Status == "ok"), failed = results.Count(r => r.Status == "error"), skipped = results.Count(r => r.Status == "skipped");
        var color = run.Color && output == Console.Out;
        if (!run.Json) output.WriteLine();
        var rate = elapsed.TotalSeconds > 0 ? results.Length / elapsed.TotalSeconds : 0;
        output.WriteLine($"{results.Length} of {run.Files.Count} file(s) in {Ms(elapsed.TotalMilliseconds)} ({rate.ToString("0.0", CultureInfo.InvariantCulture)} files/s): "
            + $"{Paint($"{ok} ok", "32", color && ok > 0)}, {Paint($"{failed} failed", "31", color && failed > 0)}, {skipped} skipped"
            + (interrupted ? Paint(" — interrupted", "33", color) : ""));
        if (ok == 0) return;
        var images = results.Sum(r => r.Images);
        var pdfs = results.Count(r => r.PdfBytes > 0);
        output.WriteLine($"written: {Plural(images, "image")} ({Size(results.Sum(r => r.ImageBytes))}), {Plural(pdfs, "preview PDF")} ({Size(results.Sum(r => r.PdfBytes))})");
        var byFormat = results.Where(r => r.Status == "ok").GroupBy(r => r.Format!.Value).OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key} {g.Count()} (median {Ms(Median(g.Select(r => r.Ms)))})");
        output.WriteLine($"by format: {string.Join(" · ", byFormat)}");
        if (results.Length > 1)
        {
            var slowest = results.Where(r => r.Status == "ok").MaxBy(r => r.Ms)!;
            output.WriteLine($"slowest: {slowest.Name} ({Ms(slowest.Ms)})");
        }
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? 0 : sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }

    private static string Paint(string text, string code, bool on) => on ? $"\e[{code}m{text}\e[0m" : text;
    private static string Plural(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";

    private static string Ms(double ms) => ms >= 10_000 ? $"{(ms / 1000).ToString("0.0", CultureInfo.InvariantCulture)} s"
        : ms >= 1000 ? $"{(ms / 1000).ToString("0.00", CultureInfo.InvariantCulture)} s" : $"{ms.ToString("0", CultureInfo.InvariantCulture)} ms";

    private static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{(bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture)} GB",
        >= 1L << 20 => $"{(bytes / (double)(1L << 20)).ToString("0.0", CultureInfo.InvariantCulture)} MB",
        >= 1L << 10 => $"{(bytes / (double)(1L << 10)).ToString("0", CultureInfo.InvariantCulture)} KB",
        _ => $"{bytes} B",
    };
}
