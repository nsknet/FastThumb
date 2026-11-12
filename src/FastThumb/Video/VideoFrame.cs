using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FastThumb.Video;

/// <summary>Facts about the frame that was taken. Times are seconds, negative when the container does not tell.</summary>
internal sealed record VideoFrameInfo(int Width, int Height, int Rotation, double Duration, double FrameTime, string Codec, string Container);

/// <summary>
/// One still frame out of a video: the frame at a fraction of the duration (0.5 = the middle). The decoder
/// starts at the keyframe before it and walks forward for at most 1.5 s, so a heavy video with a long group of
/// pictures yields a frame somewhat earlier; a file that cannot be decoded there yields its first frame.
/// Runs in-process on the thumbvideo native library (native/thumbvideo: a three-function C shim over a
/// decode-only FFmpeg build plus dav1d), reading the stream through callbacks: no temp file, no child process,
/// and only the header plus one group of pictures is read, so a seekable network stream is cheap.
/// Set the THUMBVIDEO_LOG environment variable to get the decoder's diagnostics on stderr.
/// </summary>
internal static unsafe partial class VideoFrame
{
    private const string Library = "thumbvideo";

    [StructLayout(LayoutKind.Sequential)]
    private struct Info
    {
        public int Width, Height, Rotation, Reserved;
        public double Duration, FrameTime;
        public fixed byte Codec[32];
        public fixed byte Container[32];
    }

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int tv_frame(
        delegate* unmanaged[Cdecl]<nint, byte*, int, int> read, delegate* unmanaged[Cdecl]<nint, long, int, long> seek, nint opaque,
        double fraction, int timeoutMs, Info* info, byte** rgb, byte* error, int errorSize);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void tv_free(byte* rgb);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint tv_version();

    static VideoFrame() => NativeLibrary.SetDllImportResolver(typeof(VideoFrame).Assembly, Resolve);

    /// <summary>The library ships as runtimes/{rid}/native/ next to the application, for every platform at once.</summary>
    private static nint Resolve(string name, System.Reflection.Assembly assembly, DllImportSearchPath? path)
    {
        if (name != Library) return 0;
        var (system, file) = OperatingSystem.IsWindows() ? ("win", "thumbvideo.dll")
            : OperatingSystem.IsMacOS() ? ("osx", "libthumbvideo.dylib") : ("linux", "libthumbvideo.so");
        var rid = $"{system}-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}";
        foreach (var candidate in new[] { Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", file), Path.Combine(AppContext.BaseDirectory, file) })
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle)) return handle;
        return 0;   // fall back to the default probing
    }

    /// <summary>Name and FFmpeg release of the native library.</summary>
    public static string Version => Marshal.PtrToStringUTF8(tv_version()) ?? "";

    private sealed class Source(Stream stream)
    {
        public readonly Stream Stream = stream;
        public Exception? Error;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Read(nint opaque, byte* buffer, int size)
    {
        var source = (Source)GCHandle.FromIntPtr(opaque).Target!;
        try
        {
            Guard.Check();   // a cancelled call stops the decoder at its next read
            return source.Stream.Read(new Span<byte>(buffer, size));
        }
        catch (Exception e) { source.Error ??= e; return -1; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static long Seek(nint opaque, long offset, int whence)
    {
        var source = (Source)GCHandle.FromIntPtr(opaque).Target!;
        try
        {
            return whence switch
            {
                0x10000 => source.Stream.Length,   // size query
                0 => source.Stream.Seek(offset, SeekOrigin.Begin),
                1 => source.Stream.Seek(offset, SeekOrigin.Current),
                2 => source.Stream.Seek(offset, SeekOrigin.End),
                _ => -1,
            };
        }
        catch (Exception e)
        {
            if (whence != 0x10000) source.Error ??= e;   // a stream without a known length is fine
            return -1;
        }
    }

    /// <summary>
    /// Takes the frame as an upright sRGB image. The stream must be seekable and is read from its start;
    /// it is not disposed.
    /// </summary>
    /// <exception cref="NotSupportedException">not a video this build can read (container, codec, or no video stream at all)</exception>
    /// <exception cref="InvalidDataException">the video is damaged: no frame could be decoded</exception>
    public static NetVips.Image Grab(Stream stream, out VideoFrameInfo info, double fraction = 0.5, int timeoutMs = 60_000)
    {
        if (!stream.CanSeek) throw new ArgumentException("The video stream must be seekable.", nameof(stream));
        stream.Position = 0;

        var source = new Source(stream);
        var handle = GCHandle.Alloc(source);
        Info native;
        byte* rgb = null;
        var error = stackalloc byte[256];
        try
        {
            int code;
            try { code = tv_frame(&Read, &Seek, GCHandle.ToIntPtr(handle), fraction, timeoutMs, &native, &rgb, error, 256); }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                throw new UnsupportedFormatException("Video is not supported here: the thumbvideo native library for this platform was not found.", e);
            }
            if (code != 0)
            {
                if (source.Error is OperationCanceledException canceled) throw canceled;
                var message = Marshal.PtrToStringUTF8((nint)error) ?? "video error";
                throw code switch
                {
                    -2 or -3 or -4 => new UnsupportedFormatException($"Video: {message}."),
                    -6 => new TimeoutException($"Video: {message}."),
                    -7 => new OutOfMemoryException($"Video: {message}."),
                    -8 => new IOException($"Video: {message}.", source.Error),
                    _ => new InvalidDocumentException($"Video: {message}."),
                };
            }

            info = new VideoFrameInfo(native.Width, native.Height, native.Rotation, native.Duration, native.FrameTime,
                Marshal.PtrToStringUTF8((nint)native.Codec) ?? "", Marshal.PtrToStringUTF8((nint)native.Container) ?? "");
            using var packed = NetVips.Image.NewFromMemoryCopy((nint)rgb, (ulong)native.Width * (ulong)native.Height * 3, native.Width, native.Height, 3, NetVips.Enums.BandFormat.Uchar);
            using var srgb = packed.Copy(interpretation: NetVips.Enums.Interpretation.Srgb);
            return native.Rotation switch
            {
                90 => srgb.Rot(NetVips.Enums.Angle.D90),
                180 => srgb.Rot(NetVips.Enums.Angle.D180),
                270 => srgb.Rot(NetVips.Enums.Angle.D270),
                _ => srgb.Copy(),
            };
        }
        finally
        {
            if (rgb != null) tv_free(rgb);
            handle.Free();
        }
    }
}
