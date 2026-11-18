using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Xml;
using System.Xml.Linq;
using SkiaSharp;

namespace FastThumb;

/// <summary>
/// Limits for untrusted input, used by every reader instead of trusting the sizes, counts and nesting a file declares.
/// A file that breaks one fails with <see cref="ResourceLimitExceededException"/> instead of exhausting memory, spinning forever or overflowing the stack, which would end the process.
/// </summary>
internal static class Guard
{
    /// <summary>Largest decompressed part of a container (zip entry, OLE stream, deflate section).</summary>
    public const long MaxPartBytes = 256L << 20;

    /// <summary>Largest embedded picture decoded at full size; a larger one is decoded scaled down (JPEG) or left out.</summary>
    public const long MaxEmbeddedPixels = 64L << 20;

    /// <summary>
    /// Call at the top of every recursive reader/layout function: throws <see cref="ResourceLimitExceededException"/>
    /// while there is still stack left, so deep nesting (or a part that includes itself) fails one file, not the process.
    /// </summary>
    public static void Deeper()
    {
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
            throw new ResourceLimitExceededException("The document is nested too deeply.");
        t_cancellation.ThrowIfCancellationRequested();
    }

    [ThreadStatic] private static CancellationToken t_cancellation;

    /// <summary>
    /// The cancellation of the call running on this thread. Set by <see cref="ThumbnailGenerator"/> around one input, and checked
    /// by <see cref="Deeper"/>, <see cref="Check"/> and every limited stream, so readers need no token parameter.
    /// </summary>
    public static CancellationToken Cancellation
    {
        get => t_cancellation;
        set => t_cancellation = value;
    }

    /// <summary>Throws <see cref="OperationCanceledException"/> if the current call was cancelled.</summary>
    public static void Check() => t_cancellation.ThrowIfCancellationRequested();

    /// <summary>The whole entry, at most <paramref name="max"/> bytes whatever its header says.</summary>
    public static byte[] ReadAll(ZipArchiveEntry entry, long max = MaxPartBytes)
    {
        if (entry.Length > max) throw TooLarge(entry.FullName, max);
        using var stream = Open(entry, max);
        using var buffer = new MemoryStream((int)Math.Max(0, Math.Min(entry.Length, max)));
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>The entry as a stream that fails once more than <paramref name="max"/> bytes come out of it.</summary>
    public static Stream Open(ZipArchiveEntry entry, long max = MaxPartBytes)
    {
        if (entry.Length > max) throw TooLarge(entry.FullName, max);
        return new LimitedStream(entry.Open(), max, entry.FullName);
    }

    /// <summary>Wraps a decompression stream (deflate, LZX, ...) so it cannot inflate past <paramref name="max"/> bytes.</summary>
    public static Stream Limit(Stream inner, long max = MaxPartBytes, string name = "stream") => new LimitedStream(inner, max, name);

    /// <summary>No DTD processing at all (Office/ODF/XPS parts never need one): no external entities, no entity expansion.</summary>
    public static readonly XmlReaderSettings Xml = new() { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };

    // what XDocument.Load / Parse use by default (whitespace-only text dropped), minus the DTD
    private static readonly XmlReaderSettings Tree = new() { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, IgnoreWhitespace = true };

    public static XDocument LoadXml(Stream stream)
    {
        using var reader = Depth(XmlReader.Create(stream, Tree));
        return XDocument.Load(reader);
    }

    public static XDocument ParseXml(string text)
    {
        using var reader = Depth(XmlReader.Create(new StringReader(text), Tree));
        return XDocument.Load(reader);
    }

    /// <summary>Deepest element nesting accepted in an XML part; real documents stay far below it.</summary>
    public const int MaxXmlDepth = 256;

    /// <summary>
    /// The reader, failing once elements nest deeper than <see cref="MaxXmlDepth"/>. Needed before building a tree
    /// (XDocument.Load, XNode.ReadFrom): each node added walks up to the root, so a deep part costs depth squared.
    /// </summary>
    public static XmlReader Depth(XmlReader reader) => new DepthLimitedReader(reader);

    private sealed class DepthLimitedReader(XmlReader inner) : XmlReader
    {
        public override bool Read()
        {
            var more = inner.Read();
            if (more && inner.Depth > MaxXmlDepth) throw new ResourceLimitExceededException($"XML nested deeper than {MaxXmlDepth} levels");
            return more;
        }

        public override int AttributeCount => inner.AttributeCount;
        public override string BaseURI => inner.BaseURI;
        public override int Depth => inner.Depth;
        public override bool EOF => inner.EOF;
        public override bool HasValue => inner.HasValue;
        public override bool IsDefault => inner.IsDefault;
        public override bool IsEmptyElement => inner.IsEmptyElement;
        public override string LocalName => inner.LocalName;
        public override string Name => inner.Name;
        public override string NamespaceURI => inner.NamespaceURI;
        public override XmlNameTable NameTable => inner.NameTable;
        public override XmlNodeType NodeType => inner.NodeType;
        public override string Prefix => inner.Prefix;
        public override ReadState ReadState => inner.ReadState;
        public override string Value => inner.Value;
        public override XmlSpace XmlSpace => inner.XmlSpace;
        public override string XmlLang => inner.XmlLang;
        public override XmlReaderSettings? Settings => inner.Settings;
        public override string GetAttribute(int i) => inner.GetAttribute(i);
        public override string? GetAttribute(string name) => inner.GetAttribute(name);
        public override string? GetAttribute(string name, string? namespaceURI) => inner.GetAttribute(name, namespaceURI);
        public override string? LookupNamespace(string prefix) => inner.LookupNamespace(prefix);
        public override void MoveToAttribute(int i) => inner.MoveToAttribute(i);
        public override bool MoveToAttribute(string name) => inner.MoveToAttribute(name);
        public override bool MoveToAttribute(string name, string? ns) => inner.MoveToAttribute(name, ns);
        public override bool MoveToElement() => inner.MoveToElement();
        public override bool MoveToFirstAttribute() => inner.MoveToFirstAttribute();
        public override bool MoveToNextAttribute() => inner.MoveToNextAttribute();
        public override bool ReadAttributeValue() => inner.ReadAttributeValue();
        public override void ResolveEntity() => inner.ResolveEntity();
        public override void Close() => inner.Close();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// An embedded picture for drawing: Skia decodes lazily at full size, so the header is checked first. Too large: a JPEG is
    /// decoded at 1/2, 1/4 or 1/8 scale until it fits, anything else is left out (null), as an undecodable picture is.
    /// </summary>
    public static SKImage? Image(byte[]? encoded)
    {
        if (encoded == null || encoded.Length == 0) return null;
        using var data = SKData.CreateCopy(encoded);
        return Image(data);
    }

    public static SKImage? Image(SKData data)
    {
        try
        {
            using var codec = SKCodec.Create(data);
            if (codec == null) return null;
            var info = codec.Info;
            if (info.Width <= 0 || info.Height <= 0) return null;
            if ((long)info.Width * info.Height <= MaxEmbeddedPixels) return SKImage.FromEncodedData(data);
            for (var scale = 0.5f; scale >= 0.125f; scale /= 2)
            {
                var size = codec.GetScaledDimensions(scale);
                if ((long)size.Width * size.Height > MaxEmbeddedPixels) continue;
                if (size.Width >= info.Width) return null;              // codec cannot scale while decoding
                using var bitmap = new SKBitmap(new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
                if (codec.GetPixels(bitmap.Info, bitmap.GetPixels()) is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) return null;
                return SKImage.FromBitmap(bitmap);
            }
            return null;
        }
        catch (ArgumentException) { return null; }
    }

    /// <summary>
    /// True for the <see cref="ArgumentException"/> that .NET Framework throws while reading the directory of a ZIP whose
    /// entry names hold characters a path cannot ("Illegal characters in path"): a damaged package, which .NET reads fine.
    /// </summary>
    public static bool IsDamagedZipName(Exception e)
    {
#if NETFRAMEWORK
        return e is ArgumentException && !(e is ArgumentOutOfRangeException)
            && new System.Diagnostics.StackTrace(e).GetFrames()?.Any(f => f.GetMethod()?.DeclaringType == typeof(ZipArchive)) == true;
#else
        return false;
#endif
    }

    private static ResourceLimitExceededException TooLarge(string name, long max) =>
        new($"'{name}' is larger than {max >> 20} MB once decompressed");

    private sealed class LimitedStream(Stream inner, long max, string name) : Stream
    {
        private long _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

#if NETFRAMEWORK
        public int Read(Span<byte> buffer)   // Stream has no span overload to override before .NET Core 2.1
#else
        public override int Read(Span<byte> buffer)
#endif
        {
            t_cancellation.ThrowIfCancellationRequested();
            var n = inner.Read(buffer);
            _read += n;
            if (_read > max) throw TooLarge(name, max);
            return n;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
