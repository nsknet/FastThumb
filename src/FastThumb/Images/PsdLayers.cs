using System.Buffers.Binary;
using System.IO.Compression;

namespace FastThumb.Images;

/// <summary>
/// Photoshop layers composited bottom to top, for a file whose stored composite is only a placeholder: one saved with
/// "Maximize Compatibility" off, where image resource 1057 says the merged data is not real. What is drawn: the pixels each
/// layer stores, its transparency, user mask, opacity and fill opacity, visibility (its own and its groups'), clipping
/// to the layer below, groups (blended as one, or passed through) and the blend modes normal, multiply, screen, overlay,
/// darken and lighten (any other mode is drawn as normal). Not drawn: layer effects, adjustment and fill layers, vector
/// masks, and anything Photoshop renders from settings rather than stored pixels. 8 and 16 bits; 32-bit files are refused.
/// </summary>
internal static class PsdLayers
{
    /// <summary>Most layers read; Photoshop itself stops at 8000, real documents stay far below.</summary>
    private const int MaxLayers = 4096;

    /// <summary>Most nested groups that get a buffer of their own; deeper ones are passed through (blended straight down).</summary>
    private const int MaxGroupBuffers = 8;

    private sealed class Layer
    {
        public int Top, Left, Bottom, Right;
        public (short Id, long Length)[] Channels = [];
        public long Data;
        public uint Blend = Key("norm");
        public byte Opacity = 255, Fill = 255, Clipping, Flags;
        public int Section;                                  // lsct: 1 or 2 opens a group (its top record), 3 ends one (its bottom record)
        public bool PassThrough;
        public int MaskTop, MaskLeft, MaskBottom, MaskRight;
        public byte MaskDefault;
        public bool HasMask;

        public bool Hidden => (Flags & 2) != 0;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    /// <summary>A part of the picture being composited: the canvas, or one group's own buffer.</summary>
    private sealed class Frame(byte[] buffer, Layer? group, bool hidden, bool own)
    {
        public byte[] Buffer { get; } = buffer;
        public Layer? Group { get; } = group;
        public bool Hidden { get; } = hidden;
        public bool Own { get; } = own;

        /// <summary>Alpha (times mask) of the layer the following clipped layers are clipped to, or null when it shows nothing.</summary>
        public Plane? ClipBase { get; set; }
    }

    private sealed record Plane(byte[] Values, int Left, int Top, int Width, int Height, byte Outside)
    {
        public byte At(int x, int y)
        {
            x -= Left;
            y -= Top;
            return x >= 0 && y >= 0 && x < Width && y < Height ? Values[y * Width + x] : Outside;
        }
    }

    private static uint Key(string key) => BinaryPrimitives.ReadUInt32BigEndian(System.Text.Encoding.ASCII.GetBytes(key));

    private static readonly uint Multiply = Key("mul "), Screen = Key("scrn"), Overlay = Key("over"), Darken = Key("dark"), Lighten = Key("lite"), Pass = Key("pass");

    /// <summary>
    /// The layers composited, as RGBA, or an exception when they cannot be (32-bit samples, a colour mode without layers,
    /// damaged records): the caller then falls back to the thumbnail.
    /// </summary>
    public static NetVips.Image Compose(byte[] data, int width, int height, int mode, int depth, bool large, int section, int sectionLength, DecodeRequest request)
    {
        request.Check(width, height);
        if (depth is not (8 or 16)) throw new UnsupportedFormatException($"Photoshop layers at {depth} bits are not composited here.");
        if (mode is not (1 or 3 or 4 or 8 or 9)) throw new UnsupportedFormatException($"Photoshop colour mode {mode} layers are not composited here.");
        var layers = Read(data, section, section + sectionLength, large)
            ?? throw new UnsupportedFormatException("The PSD has no layers to composite.");

        // a group's records: its divider below the children, its own record (name, blend, opacity, mask) above them
        var groupOf = new int[layers.Count];
        Array.Fill(groupOf, -1);
        var open = new Stack<int>();
        for (var i = 0; i < layers.Count; i++)
        {
            if (layers[i].Section == 3) open.Push(i);
            else if (layers[i].Section is 1 or 2 && open.Count > 0) groupOf[open.Pop()] = i;
        }

        var canvas = new byte[(long)width * height * 4];
        var root = new Frame(canvas, null, hidden: false, own: true);
        var frames = new Stack<Frame>();
        var current = root;
        for (var i = 0; i < layers.Count; i++)
        {
            Guard.Check();
            var layer = layers[i];
            if (layer.Section == 3)
            {
                if (groupOf[i] < 0) continue;
                var group = layers[groupOf[i]];
                frames.Push(current);
                var hidden = current.Hidden || group.Hidden;
                var own = !hidden && !group.PassThrough && frames.Count <= MaxGroupBuffers;
                current = new Frame(own ? new byte[canvas.Length] : current.Buffer, group, hidden, own);
                continue;
            }
            if (layer.Section is 1 or 2)
            {
                if (frames.Count == 0 || current.Group != layer) continue;      // a group record whose divider was not found
                var done = current;
                current = frames.Pop();
                if (done.Own && !done.Hidden)
                {
                    var mask = Mask(data, layer, depth, large, width, height);
                    Blend(current.Buffer, width, height, done.Buffer, 0, 0, width, height, Opacity(layer), layer.Blend, mask, clip: null);
                }
                current.ClipBase = null;
                continue;
            }

            var clipped = layer.Clipping != 0;
            if (current.Hidden || layer.Hidden || layer.Width <= 0 || layer.Height <= 0 || (clipped && current.ClipBase == null))
            {
                if (!clipped) current.ClipBase = null;
                continue;
            }
            var (rgba, alpha) = Pixels(data, layer, mode, depth, large, request);
            var layerMask = Mask(data, layer, depth, large, width, height);
            if (!clipped)
            {
                // what the layers clipped to this one may show through: its own transparency and mask
                var plane = new byte[alpha.Length];
                for (var y = 0; y < layer.Height; y++)
                    for (var x = 0; x < layer.Width; x++)
                    {
                        var a = alpha[y * layer.Width + x];
                        plane[y * layer.Width + x] = layerMask == null ? a : (byte)(a * layerMask.At(layer.Left + x, layer.Top + y) / 255);
                    }
                current.ClipBase = new Plane(plane, layer.Left, layer.Top, layer.Width, layer.Height, 0);
            }
            Blend(current.Buffer, width, height, rgba, layer.Left, layer.Top, layer.Width, layer.Height, Opacity(layer), layer.Blend, layerMask,
                clipped ? current.ClipBase : null);
        }
        return ManagedImages.FromPixels(canvas, width, height, 4);
    }

    private static float Opacity(Layer layer) => layer.Opacity / 255f * (layer.Fill / 255f);

    /// <summary>
    /// <paramref name="source"/> (straight RGBA, placed at <paramref name="left"/>, <paramref name="top"/>) blended onto
    /// <paramref name="target"/> (straight RGBA, the canvas size), with the separable blend and source-over compositing of
    /// the W3C compositing model.
    /// </summary>
    private static void Blend(byte[] target, int width, int height, byte[] source, int left, int top, int sourceWidth, int sourceHeight,
        float opacity, uint mode, Plane? mask, Plane? clip)
    {
        if (opacity <= 0) return;
        int x0 = Math.Max(0, left), y0 = Math.Max(0, top), x1 = Math.Min(width, left + sourceWidth), y1 = Math.Min(height, top + sourceHeight);
        for (var y = y0; y < y1; y++)
        {
            if ((y & 63) == 0) Guard.Check();
            for (var x = x0; x < x1; x++)
            {
                var s = ((y - top) * sourceWidth + (x - left)) * 4;
                float a = source[s + 3];
                if (a == 0) continue;
                if (mask != null) a = a * mask.At(x, y) / 255f;
                if (clip != null) a = a * clip.At(x, y) / 255f;
                var alphaS = a / 255f * opacity;
                if (alphaS <= 0) continue;
                var t = ((long)y * width + x) * 4;
                var alphaB = target[t + 3] / 255f;
                var alphaO = alphaS + alphaB * (1 - alphaS);
                for (var c = 0; c < 3; c++)
                {
                    float cs = source[s + c], cb = target[t + c];
                    var mixed = (1 - alphaB) * cs + alphaB * Mix(mode, cs, cb);
                    target[t + c] = (byte)Math.Clamp(MathF.Round((alphaS * mixed + (1 - alphaS) * alphaB * cb) / alphaO), 0, 255);
                }
                target[t + 3] = (byte)Math.Clamp(MathF.Round(alphaO * 255), 0, 255);
            }
        }
    }

    /// <summary>A separable blend mode on 0-255 values: the source <paramref name="s"/> over the backdrop <paramref name="b"/>.</summary>
    private static float Mix(uint mode, float s, float b)
    {
        if (mode == Multiply) return s * b / 255;
        if (mode == Screen) return s + b - s * b / 255;
        if (mode == Overlay) return b < 128 ? 2 * s * b / 255 : 255 - 2 * (255 - s) * (255 - b) / 255;
        if (mode == Darken) return Math.Min(s, b);
        if (mode == Lighten) return Math.Max(s, b);
        return s;
    }

    /// <summary>The layer's colour and transparency as straight RGBA over its rectangle, and its transparency alone.</summary>
    private static (byte[] Rgba, byte[] Alpha) Pixels(byte[] data, Layer layer, int mode, int depth, bool large, DecodeRequest request)
    {
        int w = layer.Width, h = layer.Height;
        request.Check(w, h);
        var colours = mode switch { 3 or 9 => 3, 4 => 4, _ => 1 };
        var planes = new byte[colours][];
        byte[]? alpha = null;
        var at = layer.Data;
        foreach (var (id, length) in layer.Channels)
        {
            if (id >= 0 && id < colours) planes[id] = Channel(data, at, length, w, h, depth, large);
            else if (id == -1) alpha = Channel(data, at, length, w, h, depth, large);
            at += length;
        }
        if (alpha == null) { alpha = new byte[w * h]; Array.Fill(alpha, (byte)255); }
        for (var c = 0; c < colours; c++)
            if (planes[c] == null) { planes[c] = new byte[w * h]; if (mode == 4) Array.Fill(planes[c], (byte)255); }   // a missing CMYK ink is none

        var rgba = new byte[(long)w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            if ((i & 0xFFFF) == 0) Guard.Check();
            var o = i * 4;
            switch (mode)
            {
                case 3: rgba[o] = planes[0][i]; rgba[o + 1] = planes[1][i]; rgba[o + 2] = planes[2][i]; break;
                case 4:
                    // stored as 255 - ink, like the composite: a plain conversion without a colour profile
                    int k = planes[3][i];
                    rgba[o] = (byte)(planes[0][i] * k / 255); rgba[o + 1] = (byte)(planes[1][i] * k / 255); rgba[o + 2] = (byte)(planes[2][i] * k / 255);
                    break;
                case 9: PsdDecoder.Lab(planes[0][i], planes[1][i], planes[2][i], rgba.AsSpan(o, 3)); break;
                default: rgba[o] = rgba[o + 1] = rgba[o + 2] = planes[0][i]; break;
            }
            rgba[o + 3] = alpha[i];
        }
        return (rgba, alpha);
    }

    /// <summary>The layer's user mask (channel -2) over its own rectangle, or null when it has none or it is disabled.</summary>
    private static Plane? Mask(byte[] data, Layer layer, int depth, bool large, int canvasWidth, int canvasHeight)
    {
        if (!layer.HasMask) return null;
        int w = layer.MaskRight - layer.MaskLeft, h = layer.MaskBottom - layer.MaskTop;
        var at = layer.Data;
        foreach (var (id, length) in layer.Channels)
        {
            if (id == -2)
            {
                // an empty mask rectangle is all the default colour; one far larger than the canvas is not worth decoding
                if (w <= 0 || h <= 0) return new Plane([], 0, 0, 0, 0, layer.MaskDefault);
                if ((long)w * h > 4L * canvasWidth * canvasHeight + 1_000_000)
                    throw new UnsupportedFormatException("A PSD layer mask is far larger than the picture.");
                return new Plane(Channel(data, at, length, w, h, depth, large), layer.MaskLeft, layer.MaskTop, w, h, layer.MaskDefault);
            }
            at += length;
        }
        return null;
    }

    /// <summary>One channel of a layer, 8 bits a sample: its compression code, then raw, PackBits or ZIP (with or without prediction) rows.</summary>
    private static byte[] Channel(byte[] data, long at, long length, int width, int height, int depth, bool large)
    {
        if (length < 2 || at + length > data.Length) throw new InvalidDocumentException("A PSD layer channel runs past the end of the file.");
        var start = (int)at;
        var end = (int)(at + length);
        var d = data.AsSpan(0, end);
        var compression = BinaryPrimitives.ReadUInt16BigEndian(d[start..]);
        var body = start + 2;
        var rowBytes = width * (depth / 8);
        var plane = new byte[(long)width * height];
        var row = new byte[rowBytes];
        switch (compression)
        {
            case 0:
                if (body + (long)height * rowBytes > end) throw new InvalidDocumentException("A PSD layer channel is cut short.");
                for (var y = 0; y < height; y++) PsdDecoder.Store(d.Slice(body + y * rowBytes, rowBytes), depth, plane.AsSpan(y * width, width));
                break;
            case 1:
                {
                    var countSize = large ? 4 : 2;
                    long next = body + (long)height * countSize;
                    if (next > end) throw new InvalidDocumentException("A PSD layer channel is cut short.");
                    for (var y = 0; y < height; y++)
                    {
                        if ((y & 63) == 0) Guard.Check();
                        var index = body + y * countSize;
                        long count = countSize == 4 ? BinaryPrimitives.ReadUInt32BigEndian(d[index..]) : BinaryPrimitives.ReadUInt16BigEndian(d[index..]);
                        if (next + count > end) throw new InvalidDocumentException("A PSD layer row runs past its channel.");
                        PsdDecoder.PackBits(d.Slice((int)next, (int)count), row);
                        PsdDecoder.Store(row, depth, plane.AsSpan(y * width, width));
                        next += count;
                    }
                    break;
                }
            case 2 or 3:
                {
                    using var zip = Guard.Limit(new ZLibStream(new MemoryStream(data, body, end - body, false), CompressionMode.Decompress),
                        (long)height * rowBytes, "PSD layer channel");
                    for (var y = 0; y < height; y++)
                    {
                        if ((y & 63) == 0) Guard.Check();
                        zip.ReadExactly(row);
                        if (compression == 3) PsdDecoder.Unpredict(row, depth);
                        PsdDecoder.Store(row, depth, plane.AsSpan(y * width, width));
                    }
                    break;
                }
            default:
                throw new UnsupportedFormatException($"PSD compression {compression} is not known.");
        }
        return plane;
    }

    /// <summary>
    /// The layer records of the layer-and-mask section from <paramref name="start"/> to <paramref name="end"/> (after its
    /// length), with where each layer's channel data starts; the 16- and 32-bit layer blocks (Lr16, Lr32) of the section's
    /// additional information when the main layer information is empty. Null when there is no layer.
    /// </summary>
    private static List<Layer>? Read(byte[] data, int start, int end, bool large)
    {
        var d = data.AsSpan(0, end);
        var at = start;
        var infoLength = Length(d, ref at, large, end);
        var info = at;
        at += infoLength;
        if (infoLength >= 2 && Records(data, info, info + infoLength, large) is { Count: > 0 } layers) return layers;

        // global layer mask information, then tagged blocks
        if (at + 4 > end) return null;
        var globalLength = Length(d, ref at, false, end);
        at += globalLength;
        while (at + 12 <= end)
        {
            if (!d.Slice(at, 4).SequenceEqual("8BIM"u8) && !d.Slice(at, 4).SequenceEqual("8B64"u8)) { at++; continue; }   // padding
            var key = BinaryPrimitives.ReadUInt32BigEndian(d[(at + 4)..]);
            at += 8;
            var length = Length(d, ref at, large && LongKey(key), end);
            if ((key == Key("Lr16") || key == Key("Lr32")) && Records(data, at, at + length, large) is { Count: > 0 } deep) return deep;
            at += length;
        }
        return null;
    }

    /// <summary>The additional-information keys whose length is 8 bytes in a PSB.</summary>
    private static bool LongKey(uint key) =>
        key == Key("LMsk") || key == Key("Lr16") || key == Key("Lr32") || key == Key("Layr") || key == Key("Mt16") || key == Key("Mt32")
        || key == Key("Mtrn") || key == Key("Alph") || key == Key("FMsk") || key == Key("lnk2") || key == Key("FEid") || key == Key("FXid")
        || key == Key("PxSD");

    /// <summary>A layer count, the records, then every layer's channel data in the same order.</summary>
    private static List<Layer> Records(byte[] data, int start, int end, bool large)
    {
        var d = data.AsSpan(0, end);
        if (start + 2 > end) return [];
        var count = Math.Abs((int)BinaryPrimitives.ReadInt16BigEndian(d[start..]));   // negative: the composite's alpha is the first alpha channel
        if (count > MaxLayers) throw new UnsupportedFormatException($"The PSD has {count} layers, more than {MaxLayers} composited here.");
        var at = start + 2;
        var layers = new List<Layer>(count);
        for (var n = 0; n < count; n++)
        {
            Guard.Check();
            var layer = new Layer();
            Need(at, 18, end);
            layer.Top = BinaryPrimitives.ReadInt32BigEndian(d[at..]);
            layer.Left = BinaryPrimitives.ReadInt32BigEndian(d[(at + 4)..]);
            layer.Bottom = BinaryPrimitives.ReadInt32BigEndian(d[(at + 8)..]);
            layer.Right = BinaryPrimitives.ReadInt32BigEndian(d[(at + 12)..]);
            var channels = BinaryPrimitives.ReadUInt16BigEndian(d[(at + 16)..]);
            at += 18;
            if (channels > 56) throw new InvalidDocumentException($"A PSD layer declares {channels} channels.");
            // the rectangle as it is used: far outside any picture, or inside out, the layer shows nothing
            if (layer.Bottom < layer.Top || layer.Right < layer.Left || (long)layer.Right - layer.Left > 300_000 || (long)layer.Bottom - layer.Top > 300_000)
                layer.Top = layer.Bottom = layer.Left = layer.Right = 0;
            layer.Channels = new (short, long)[channels];
            var size = large ? 10 : 6;
            Need(at, channels * size + 16, end);
            for (var c = 0; c < channels; c++)
            {
                var id = BinaryPrimitives.ReadInt16BigEndian(d[at..]);
                long length = large ? (long)Math.Min(BinaryPrimitives.ReadUInt64BigEndian(d[(at + 2)..]), (ulong)data.Length) : BinaryPrimitives.ReadUInt32BigEndian(d[(at + 2)..]);
                layer.Channels[c] = (id, length);
                at += size;
            }
            if (!d.Slice(at, 4).SequenceEqual("8BIM"u8)) throw new InvalidDocumentException("A PSD layer record has no blend mode signature.");
            layer.Blend = BinaryPrimitives.ReadUInt32BigEndian(d[(at + 4)..]);
            layer.Opacity = d[at + 8];
            layer.Clipping = d[at + 9];
            layer.Flags = d[at + 10];
            at += 12;
            var extraLength = Length(d, ref at, false, end);
            var extraEnd = at + extraLength;
            Extra(d[..extraEnd], at, layer);
            at = extraEnd;
            layers.Add(layer);
        }

        // channel data: one block after another, in record order
        long next = at;
        foreach (var layer in layers)
        {
            layer.Data = next;
            foreach (var (_, length) in layer.Channels)
            {
                next += length;
                if (next > end) throw new InvalidDocumentException("The PSD layer channel data runs past its section.");
            }
        }
        return layers;
    }

    /// <summary>The extra data of a layer record: mask, blending ranges, name, then the additional information read here (lsct, lsdk, iOpa).</summary>
    private static void Extra(ReadOnlySpan<byte> d, int at, Layer layer)
    {
        var end = d.Length;
        var maskLength = Length(d, ref at, false, end);
        if (maskLength >= 18)
        {
            layer.MaskTop = BinaryPrimitives.ReadInt32BigEndian(d[at..]);
            layer.MaskLeft = BinaryPrimitives.ReadInt32BigEndian(d[(at + 4)..]);
            layer.MaskBottom = BinaryPrimitives.ReadInt32BigEndian(d[(at + 8)..]);
            layer.MaskRight = BinaryPrimitives.ReadInt32BigEndian(d[(at + 12)..]);
            layer.MaskDefault = d[at + 16];
            layer.HasMask = (d[at + 17] & 2) == 0;                                   // bit 1: the mask is disabled
            if (layer.MaskBottom < layer.MaskTop || layer.MaskRight < layer.MaskLeft) layer.MaskTop = layer.MaskBottom = layer.MaskLeft = layer.MaskRight = 0;
        }
        at += maskLength;
        var ranges = Length(d, ref at, false, end);
        at += ranges;
        Need(at, 1, end);
        at += (d[at] + 4) & ~3;                                                      // the Pascal name, padded to 4 bytes
        while (at + 12 <= end)
        {
            if (!d.Slice(at, 4).SequenceEqual("8BIM"u8) && !d.Slice(at, 4).SequenceEqual("8B64"u8)) break;
            var key = BinaryPrimitives.ReadUInt32BigEndian(d[(at + 4)..]);
            at += 8;
            var length = Length(d, ref at, false, end);
            if ((key == Key("lsct") || key == Key("lsdk")) && length >= 4)
            {
                layer.Section = BinaryPrimitives.ReadInt32BigEndian(d[at..]);
                if (length >= 12 && d.Slice(at + 4, 4).SequenceEqual("8BIM"u8)) layer.PassThrough = BinaryPrimitives.ReadUInt32BigEndian(d[(at + 8)..]) == Pass;
            }
            else if (key == Key("iOpa") && length >= 1) layer.Fill = d[at];
            at += length;
        }
        if (layer.Blend == Pass) layer.PassThrough = true;
    }

    private static int Length(ReadOnlySpan<byte> d, ref int at, bool wide, int end)
    {
        Need(at, wide ? 8 : 4, end);
        var length = wide ? BinaryPrimitives.ReadUInt64BigEndian(d[at..]) : BinaryPrimitives.ReadUInt32BigEndian(d[at..]);
        at += wide ? 8 : 4;
        if (length > (ulong)(end - at)) throw new InvalidDocumentException("A PSD layer block runs past its section.");
        return (int)length;
    }

    private static void Need(int at, int count, int end)
    {
        if (at < 0 || at + (long)count > end) throw new InvalidDocumentException("A PSD layer record runs past its section.");
    }
}
