#if NETFRAMEWORK
using System.Runtime.InteropServices;

namespace FastThumb;

/// <summary>
/// .NET Framework has neither the runtimes/{rid}/native probing of .NET nor a DllImport resolver. Loading a native library
/// by its full path before the first call makes every later [DllImport] of the same file name bind to the loaded module,
/// so the libraries are looked for where the package puts them: an x86/x64/arm64 folder (buildTransitive targets), the
/// runtimes folder, or flat next to the application (SDK-style projects copy the assets of the inferred runtime there).
/// The bin folder of an ASP.NET application is searched too.
/// </summary>
internal static class NativePreload
{
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string path);

    /// <summary>Loads <paramref name="file"/> (for example "pdfium.dll") from the first folder that has a loadable copy.</summary>
    public static void Load(string file)
    {
        var architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();   // x86, x64, arm64
        foreach (var folder in Folders())
        {
            foreach (var candidate in new[]
            {
                Path.Combine(folder, architecture, file),
                Path.Combine(folder, "runtimes", "win-" + architecture, "native", file),
                Path.Combine(folder, file),
            })
            {
                // a copy for another architecture fails to load: go on to the next
                if (File.Exists(candidate) && LoadLibraryW(candidate) != IntPtr.Zero) return;
            }
        }
        // nothing found: the [DllImport] falls back to the default search order and reports DllNotFoundException
    }

    private static IEnumerable<string> Folders()
    {
        var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
        var relative = AppDomain.CurrentDomain.RelativeSearchPath;   // "bin" for ASP.NET, null for most applications
        if (!string.IsNullOrEmpty(relative))
            foreach (var part in relative!.Split(';'))
                if (part.Length > 0) yield return Path.Combine(baseDirectory, part);
        yield return baseDirectory;
        var assembly = Path.GetDirectoryName(typeof(NativePreload).Assembly.Location);
        if (!string.IsNullOrEmpty(assembly)) yield return assembly!;
    }
}
#endif
