using System.Buffers.Binary;
using System.Text;

namespace FastThumb.Tests.Fixtures;

/// <summary>One layer of a generated PSD: a solid colour over a rectangle, with the record fields the compositor reads.</summary>
internal sealed record PsdLayer(int Left, int Top, int Right, int Bottom, byte R, byte G, byte B)
{
    public byte Opacity { get; init; } = 255;
    public string Blend { get; init; } = "norm";
    public bool Hidden { get; init; }
    public bool Clipped { get; init; }

    /// <summary>A user mask over the layer's own rectangle: 255 on the left half, 0 on the right.</summary>
    public bool HalfMask { get; init; }

    /// <summary>lsct: 0 a plain layer, 1 a group's own record (above its children), 3 a group's divider (below them).</summary>
    public int Section { get; init; }

    /// <summary>Alpha of the layer's pixels; null for none (opaque).</summary>
    public byte? Alpha { get; init; }
}

/// <summary>
/// Layered RGB 8-bit PSD files saved as with "Maximize Compatibility" off: the stored composite is white and image resource
/// 1057 says it is not the real picture.
/// </summary>
internal static class PsdLayerFiles
{
    public static byte[] Psd(int width, int height, IReadOnlyList<PsdLayer> layers, bool realComposite = false, byte[]? thumbnail = null)
    {
        var o = new MemoryStream();
        void U16(int v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v); o.Write(b); }
        void U32(long v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, (uint)v); o.Write(b); }
        void Ascii(string s) => o.Write(Encoding.ASCII.GetBytes(s));

        Ascii("8BPS"); U16(1); o.Write(new byte[6]); U16(3); U32(height); U32(width); U16(8); U16(3);
        U32(0);                                                                         // colour mode data

        // image resources: version info (1057), and a thumbnail (1036) when given
        var resources = new MemoryStream();
        void Resource(int id, byte[] data)
        {
            resources.Write("8BIM"u8);
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)id); resources.Write(b[..2]);
            resources.Write(new byte[2]);                                               // empty name, padded
            BinaryPrimitives.WriteUInt32BigEndian(b, (uint)data.Length); resources.Write(b);
            resources.Write(data);
            if (data.Length % 2 == 1) resources.WriteByte(0);
        }
        var version = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(version, 1);
        version[4] = realComposite ? (byte)1 : (byte)0;
        Resource(1057, version);
        if (thumbnail != null)
        {
            var t = new byte[28 + thumbnail.Length];
            BinaryPrimitives.WriteUInt32BigEndian(t, 1);
            thumbnail.CopyTo(t, 28);
            Resource(1036, t);
        }
        U32(resources.Length);
        o.Write(resources.ToArray());

        // layer and mask information
        var info = new MemoryStream();
        void IU16(int v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v); info.Write(b); }
        void IU32(long v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, (uint)v); info.Write(b); }
        IU16(layers.Count);
        var channelData = new List<byte[]>();
        foreach (var layer in layers)
        {
            int w = layer.Right - layer.Left, h = layer.Bottom - layer.Top;
            IU32(layer.Top); IU32(layer.Left); IU32(layer.Bottom); IU32(layer.Right);
            var channels = new List<(short Id, byte[] Data)>();
            byte[] Plane(byte value) { var p = new byte[2 + w * h]; p.AsSpan(2).Fill(value); return p; }   // raw, compression 0
            if (layer.Alpha is { } a) channels.Add((-1, Plane(a)));
            channels.Add((0, Plane(layer.R)));
            channels.Add((1, Plane(layer.G)));
            channels.Add((2, Plane(layer.B)));
            if (layer.HalfMask)
            {
                var mask = new byte[2 + w * h];
                for (var y = 0; y < h; y++) for (var x = 0; x < w / 2; x++) mask[2 + y * w + x] = 255;
                channels.Add((-2, mask));
            }
            IU16(channels.Count);
            foreach (var (id, data) in channels) { IU16(id); IU32(data.Length); channelData.Add(data); }
            info.Write("8BIM"u8);
            info.Write(Encoding.ASCII.GetBytes(layer.Blend));
            info.WriteByte(layer.Opacity);
            info.WriteByte(layer.Clipped ? (byte)1 : (byte)0);
            info.WriteByte(layer.Hidden ? (byte)2 : (byte)0);
            info.WriteByte(0);

            var extra = new MemoryStream();
            void EU32(long v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, (uint)v); extra.Write(b); }
            if (layer.HalfMask)
            {
                EU32(20); EU32(layer.Top); EU32(layer.Left); EU32(layer.Bottom); EU32(layer.Right);
                extra.WriteByte(0); extra.WriteByte(0); extra.Write(new byte[2]);
            }
            else EU32(0);
            EU32(0);                                                                    // blending ranges
            extra.Write([1, (byte)'L', 0, 0]);                                          // name "L", padded to 4
            if (layer.Section != 0)
            {
                extra.Write("8BIMlsct"u8); EU32(4); EU32(layer.Section);
            }
            IU32(extra.Length);
            info.Write(extra.ToArray());
        }
        foreach (var data in channelData) info.Write(data);
        if (info.Length % 2 == 1) info.WriteByte(0);

        U32(4 + info.Length + 4);
        U32(info.Length);
        o.Write(info.ToArray());
        U32(0);                                                                         // global layer mask

        // the stored composite: white, raw
        U16(0);
        o.Write(Enumerable.Repeat((byte)255, width * height * 3).ToArray());
        return o.ToArray();
    }
}
