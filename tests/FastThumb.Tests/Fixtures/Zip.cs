using System.IO.Compression;
using System.Text;

namespace FastThumb.Tests.Fixtures;

/// <summary>Builds ZIP packages in memory from (name, content) pairs, entries in the order given.</summary>
internal static class Zip
{
    public static byte[] Build(params (string Name, string Content)[] parts) =>
        Build(parts.Select(p => (p.Name, Encoding.UTF8.GetBytes(p.Content), CompressionLevel.Optimal)).ToArray());

    public static byte[] Build(params (string Name, byte[] Content, CompressionLevel Level)[] parts)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content, level) in parts)
            {
                var entry = zip.CreateEntry(name, level);
                using var stream = entry.Open();
                stream.Write(content);
            }
        }
        return buffer.ToArray();
    }

    /// <summary>A ZIP with one entry whose content is written by <paramref name="write"/> (streamed, so it can be huge).</summary>
    public static byte[] BuildStreamed(IEnumerable<(string Name, string Content)> small, string name, Action<Stream> write)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (n, c) in small)
            {
                using var s = zip.CreateEntry(n, CompressionLevel.Optimal).Open();
                s.Write(Encoding.UTF8.GetBytes(c));
            }
            using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
            write(stream);
        }
        return buffer.ToArray();
    }

    /// <summary>Every entry of a package, in order.</summary>
    public static List<(string Name, byte[] Content)> Read(byte[] package)
    {
        var parts = new List<(string, byte[])>();
        using var zip = new ZipArchive(new MemoryStream(package, false), ZipArchiveMode.Read);
        foreach (var entry in zip.Entries)
        {
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            parts.Add((entry.FullName, buffer.ToArray()));
        }
        return parts;
    }

    /// <summary>
    /// Replaces every occurrence of a 32-bit little-endian value: used to make the headers of a package declare a smaller
    /// uncompressed size than an entry really has.
    /// </summary>
    public static int Patch(byte[] package, uint from, uint to)
    {
        Span<byte> pattern = stackalloc byte[4], replacement = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(pattern, from);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(replacement, to);
        var count = 0;
        for (var at = 0; at + 4 <= package.Length; at++)
        {
            if (!package.AsSpan(at, 4).SequenceEqual(pattern)) continue;
            replacement.CopyTo(package.AsSpan(at));
            count++;
            at += 3;
        }
        return count;
    }
}
