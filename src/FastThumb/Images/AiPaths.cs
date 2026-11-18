using System.Globalization;
using SkiaSharp;

namespace FastThumb.Images;

/// <summary>
/// The artwork of a PostScript-based Adobe Illustrator file (Illustrator 8 and older, and the EPS files Illustrator writes),
/// drawn from its own drawing operators, for a file that carries no preview. Illustrator writes its artwork in a small,
/// line-oriented language on top of PostScript: operands, then a one- or two-letter operator. This reads that language,
/// not PostScript: path construction (m, l, c, v, y), painting (f, s, b, n and their capital forms), compound paths
/// (*u ... *U), clipping groups (q, W, Q), fill and stroke colours (grey, CMYK, custom CMYK or RGB with a tint, RGB), line
/// width, joins, caps, miter limit and dashes, and layers (a hidden layer is not drawn). Text, gradients, patterns, placed
/// images and raster images are not drawn. The page is the bounding box, on white.
/// </summary>
internal static class AiPaths
{
    private const int MaxStack = 1024;
    private const int MaxPoints = 2_000_000;
    private const long MaxTokens = 20_000_000;
    private const int MaxSaves = 64;

    /// <summary>
    /// True for a file Illustrator wrote: its %%Creator names Illustrator, or one of Illustrator's own %AIn_ comments
    /// appears in the first few kilobytes.
    /// </summary>
    public static bool IsIllustrator(ReadOnlySpan<byte> ps)
    {
        var head = ps[..Math.Min(ps.Length, 4096)];
        var creator = head.IndexOf("%%Creator:"u8);
        if (creator >= 0)
        {
            var line = head[creator..];
            var end = line.IndexOfAny((byte)'\r', (byte)'\n');
            if ((end < 0 ? line : line[..end]).IndexOf("Illustrator"u8) >= 0) return true;
        }
        for (var at = head.IndexOf("%AI"u8); at >= 0 && at + 5 <= head.Length;)
        {
            if (head[at + 3] is >= (byte)'0' and <= (byte)'9' && head[at + 4] == '_') return true;
            var next = head[(at + 3)..].IndexOf("%AI"u8);
            at = next < 0 ? -1 : at + 3 + next;
        }
        return false;
    }

    /// <summary>The page box in points: %%HiResBoundingBox, else %%BoundingBox (either may be deferred to the trailer).</summary>
    public static (double Left, double Bottom, double Right, double Top)? Box(ReadOnlySpan<byte> ps)
    {
        foreach (var key in new[] { "%%HiResBoundingBox:"u8.ToArray(), "%%BoundingBox:"u8.ToArray() })
        {
            for (var at = ps.IndexOf(key); at >= 0;)
            {
                var values = Numbers(ps[(at + key.Length)..]);
                if (values is [var l, var b, var r, var t, ..] && r > l && t > b) return (l, b, r, t);
                var next = ps[(at + key.Length)..].IndexOf(key);
                at = next < 0 ? -1 : at + key.Length + next;
            }
        }
        return null;
    }

    public static NetVips.Image Render(ReadOnlySpan<byte> ps, DecodeRequest request)
    {
        var box = Box(ps) ?? throw new UnsupportedFormatException("The Illustrator file has no bounding box to draw its artwork in.");
        double pageWidth = box.Right - box.Left, pageHeight = box.Top - box.Bottom;

        // the smallest of 1, 2, 4, 8, 16 pixels a point that serves every requested size; the page at 1 px a point when the
        // full size is wanted (as a PDF page at 72 DPI), or when nothing larger fits
        var full = !request.BigEnough(int.MaxValue / 2, int.MaxValue / 2);
        var scale = 1;
        if (!full)
            for (var s = 1; s <= 16; s *= 2)
            {
                var (w, h) = Pixels(pageWidth, pageHeight, s);
                if ((long)w * h > request.MaxPixels || Math.Max(w, h) > 16384) break;
                scale = s;
                if (request.BigEnough(w, h)) break;
            }
        var (width, height) = Pixels(pageWidth, pageHeight, scale);
        request.Check(width, height);

        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            canvas.Scale(scale, -scale);
            canvas.Translate((float)-box.Left, (float)-box.Top);
            new Interpreter(canvas).Run(ps);
        }
        return ManagedImages.FromPixels(bitmap.Bytes, width, height, 4);
    }

    private static (int Width, int Height) Pixels(double width, double height, int scale) =>
        ((int)Math.Clamp(Math.Ceiling(width * scale), 1, int.MaxValue), (int)Math.Clamp(Math.Ceiling(height * scale), 1, int.MaxValue));

    /// <summary>The numbers at the start of the line that begins at the start of <paramref name="text"/>.</summary>
    private static List<double> Numbers(ReadOnlySpan<byte> text)
    {
        var end = text.IndexOfAny((byte)'\r', (byte)'\n');
        var line = System.Text.Encoding.ASCII.GetString(end < 0 ? text[..Math.Min(text.Length, 200)] : text[..end]);
        var numbers = new List<double>();
        foreach (var word in line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value) || Math.Abs(value) > 1e7) break;
            numbers.Add(value);
        }
        return numbers;
    }

    private sealed class Interpreter(SKCanvas canvas)
    {
        private readonly List<double> _stack = [];
        private float[]? _array;
        private int _arrayStart = -1;
        private SKPath _path = new();
        private SKPoint _current;
        private int _points;
        private long _tokens;
        private int _saves;
        private bool _compound, _compoundFill, _compoundStroke, _clip, _hiddenLayer, _text;
        private SKColor _fill = SKColors.Black, _stroke = SKColors.Black;
        private float _lineWidth = 1, _miter = 4;
        private SKStrokeCap _cap = SKStrokeCap.Butt;
        private SKStrokeJoin _join = SKStrokeJoin.Miter;
        private float[]? _dash;
        private float _dashPhase;

        /// <summary>DSC blocks whose content is not artwork in this language, by the comment that ends each.</summary>
        private static readonly (byte[] Begin, byte[] End)[] Skipped =
        [
            ("%%BeginProlog"u8.ToArray(), "%%EndProlog"u8.ToArray()),
            ("%%BeginSetup"u8.ToArray(), "%%EndSetup"u8.ToArray()),
            ("%%BeginData"u8.ToArray(), "%%EndData"u8.ToArray()),
            ("%%BeginBinary"u8.ToArray(), "%%EndBinary"u8.ToArray()),
            ("%%BeginResource"u8.ToArray(), "%%EndResource"u8.ToArray()),
            ("%%BeginProcSet"u8.ToArray(), "%%EndProcSet"u8.ToArray()),
            ("%%BeginDocument"u8.ToArray(), "%%EndDocument"u8.ToArray()),
            ("%%BeginPreview"u8.ToArray(), "%%EndPreview"u8.ToArray()),
            ("%%BeginPhotoshop"u8.ToArray(), "%EndPhotoshop"u8.ToArray()),
        ];

        public void Run(ReadOnlySpan<byte> ps)
        {
            var i = 0;
            while (i < ps.Length)
            {
                var c = ps[i];
                if (c is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'\f' or 0) { i++; continue; }
                if ((++_tokens & 0xFFFF) == 0)
                {
                    Guard.Check();
                    if (_tokens > MaxTokens) throw new ResourceLimitExceededException("The Illustrator artwork has too many tokens to draw.");
                }
                switch (c)
                {
                    case (byte)'%':
                        {
                            var line = Line(ps, i);
                            if (line.StartsWith("%%Trailer"u8) || line.StartsWith("%%PageTrailer"u8) || line.StartsWith("%%EOF"u8)) return;
                            i += line.Length;
                            foreach (var (begin, end) in Skipped)
                            {
                                if (!line.StartsWith(begin)) continue;
                                var to = ps[i..].IndexOf(end);
                                i = to < 0 ? ps.Length : i + to + Line(ps, i + to).Length;
                                break;
                            }
                            continue;
                        }
                    case (byte)'(':
                        i = SkipString(ps, i);
                        Push(double.NaN);
                        continue;
                    case (byte)'<':
                        if (i + 1 < ps.Length && ps[i + 1] == '<') { i += 2; continue; }
                        var close = ps[i..].IndexOf((byte)'>');
                        i = close < 0 ? ps.Length : i + close + 1;
                        Push(double.NaN);
                        continue;
                    case (byte)'[':
                        _arrayStart = _stack.Count;
                        i++;
                        continue;
                    case (byte)']':
                        if (_arrayStart >= 0 && _arrayStart <= _stack.Count)
                        {
                            _array = [.. _stack.Skip(_arrayStart).Where(double.IsFinite).Select(v => (float)v)];
                            _stack.RemoveRange(_arrayStart, _stack.Count - _arrayStart);
                        }
                        _arrayStart = -1;
                        i++;
                        continue;
                    case (byte)'>' or (byte)')' or (byte)'{' or (byte)'}':
                        i++;
                        continue;
                }
                var start = i;
                if (c == '/') i++;
                while (i < ps.Length && ps[i] is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'\f' or 0
                    or (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%')) i++;
                var token = ps[start..i];
                if (c == '/') { Push(double.NaN); continue; }
                if (token.Length <= 32 && double.TryParse(System.Text.Encoding.ASCII.GetString(token), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                {
                    Push(double.IsFinite(number) ? Math.Clamp(number, -1e7, 1e7) : double.NaN);
                    continue;
                }
                if (token.Length <= 3) Execute(System.Text.Encoding.ASCII.GetString(token));
                _stack.Clear();
                _arrayStart = -1;
            }
        }

        private void Push(double value)
        {
            if (_stack.Count >= MaxStack) _stack.Clear();
            _stack.Add(value);
        }

        /// <summary>The last <paramref name="count"/> operands, when they are all numbers.</summary>
        private bool Take(int count, out float[] values)
        {
            values = [];
            if (_stack.Count < count) return false;
            values = new float[count];
            for (var k = 0; k < count; k++)
            {
                var v = _stack[_stack.Count - count + k];
                if (!double.IsFinite(v)) return false;
                values[k] = (float)v;
            }
            return true;
        }

        /// <summary>Every numeric operand, strings and names left out.</summary>
        private float[] Numbers() => [.. _stack.Where(double.IsFinite).Select(v => (float)v)];

        private void Execute(string op)
        {
            switch (op)
            {
                case "m":
                    if (Take(2, out var m)) { Point(); _path.MoveTo(m[0], m[1]); _current = new(m[0], m[1]); }
                    break;
                case "l" or "L":
                    if (Take(2, out var l)) { Point(); _path.LineTo(l[0], l[1]); _current = new(l[0], l[1]); }
                    break;
                case "c" or "C":
                    if (Take(6, out var cv)) { Point(); _path.CubicTo(cv[0], cv[1], cv[2], cv[3], cv[4], cv[5]); _current = new(cv[4], cv[5]); }
                    break;
                case "v" or "V":
                    // the first control point is the current point
                    if (Take(4, out var vv)) { Point(); _path.CubicTo(_current.X, _current.Y, vv[0], vv[1], vv[2], vv[3]); _current = new(vv[2], vv[3]); }
                    break;
                case "y" or "Y":
                    // the second control point is the end point
                    if (Take(4, out var yv)) { Point(); _path.CubicTo(yv[0], yv[1], yv[2], yv[3], yv[2], yv[3]); _current = new(yv[2], yv[3]); }
                    break;
                case "f" or "F" or "s" or "S" or "b" or "B" or "n" or "N" or "h" or "H":
                    Paint(op[0]);
                    break;
                case "*u":
                    _compound = true;
                    _compoundFill = _compoundStroke = false;
                    break;
                case "*U":
                    _compound = false;
                    Draw(_compoundFill, _compoundStroke, evenOdd: true);
                    break;
                case "g": if (Take(1, out var g)) _fill = Grey(g[0]); break;
                case "G": if (Take(1, out var gs)) _stroke = Grey(gs[0]); break;
                case "k": if (Take(4, out var k)) _fill = Cmyk(k[0], k[1], k[2], k[3], 0); break;
                case "K": if (Take(4, out var ks)) _stroke = Cmyk(ks[0], ks[1], ks[2], ks[3], 0); break;
                case "x" or "X":
                    // c m y k (name) tint, where a tint of 0 is the full colour
                    if (Numbers() is [.., var c1, var m1, var y1, var k1, var t1])
                    {
                        if (op == "x") _fill = Cmyk(c1, m1, y1, k1, t1); else _stroke = Cmyk(c1, m1, y1, k1, t1);
                    }
                    break;
                case "Xx" or "XX":
                    // a custom colour with its kind: c m y k (name) tint 0, or r g b (name) tint 1
                    {
                        var v = Numbers();
                        SKColor? colour = v switch
                        {
                            [.., var r, var gr, var bl, var t, 1] => Tint(Rgb(r, gr, bl), t),
                            [.., var cc, var mm, var yy, var kk, var t, 0] => Cmyk(cc, mm, yy, kk, t),
                            _ => null,
                        };
                        if (colour is { } set) { if (op == "Xx") _fill = set; else _stroke = set; }
                        break;
                    }
                case "Xa": if (Take(3, out var xa)) _fill = Rgb(xa[0], xa[1], xa[2]); break;
                case "XA": if (Take(3, out var xs)) _stroke = Rgb(xs[0], xs[1], xs[2]); break;
                case "w": if (Take(1, out var w)) _lineWidth = Math.Clamp(w[0], 0, 10000); break;
                case "j": if (Take(1, out var j)) _join = j[0] switch { 1 => SKStrokeJoin.Round, 2 => SKStrokeJoin.Bevel, _ => SKStrokeJoin.Miter }; break;
                case "J": if (Take(1, out var cp)) _cap = cp[0] switch { 1 => SKStrokeCap.Round, 2 => SKStrokeCap.Square, _ => SKStrokeCap.Butt }; break;
                case "M": if (Take(1, out var mi)) _miter = Math.Clamp(mi[0], 1, 100); break;
                case "d":
                    _dash = _array is { Length: > 0 } a && a.All(x => x >= 0) && a.Any(x => x > 0) ? (a.Length % 2 == 0 ? a : [.. a, .. a]) : null;
                    _dashPhase = Take(1, out var ph) ? ph[0] : 0;
                    _array = null;
                    break;
                case "q":
                    if (_saves < MaxSaves) { canvas.Save(); _saves++; }
                    break;
                case "Q":
                    if (_saves > 0) { canvas.Restore(); _saves--; }
                    break;
                case "W":
                    _clip = true;
                    break;
                case "Lb":
                    // visible preview enabled printing dimmed ...: the first says whether the layer shows
                    if (_stack.Count > 0 && double.IsFinite(_stack[0])) _hiddenLayer = _stack[0] == 0;
                    break;
                case "LB":
                    _hiddenLayer = false;
                    break;
                case "To":
                    _text = true;
                    break;
                case "TO":
                    _text = false;
                    _path.Reset();
                    break;
                default:
                    // a guide is painted with a starred operator (*f, *S...): never shown
                    if (op.Length == 2 && op[0] == '*' && op[1] is 'f' or 'F' or 's' or 'S' or 'b' or 'B' or 'n' or 'N' or 'h' or 'H')
                        _path.Reset();
                    break;
            }
        }

        private void Point()
        {
            if (++_points > MaxPoints) throw new ResourceLimitExceededException("The Illustrator artwork has too many path points to draw.");
        }

        private void Paint(char op)
        {
            if (char.IsLower(op)) _path.Close();
            var fill = op is 'f' or 'F' or 'b' or 'B';
            var stroke = op is 's' or 'S' or 'b' or 'B';
            if (_compound)
            {
                // the subpaths gather until *U, which paints them as one
                _compoundFill |= fill;
                _compoundStroke |= stroke;
                return;
            }
            Draw(fill, stroke, evenOdd: false);
        }

        private void Draw(bool fill, bool stroke, bool evenOdd)
        {
            var path = _path;
            _path = new SKPath();
            using (path)
            {
                if (_clip)
                {
                    _clip = false;
                    path.FillType = SKPathFillType.Winding;
                    canvas.ClipPath(path, antialias: true);
                    return;
                }
                if (_hiddenLayer || _text || path.IsEmpty) return;
                path.FillType = evenOdd ? SKPathFillType.EvenOdd : SKPathFillType.Winding;
                if (fill)
                {
                    using var paint = new SKPaint { Color = _fill, Style = SKPaintStyle.Fill, IsAntialias = true };
                    canvas.DrawPath(path, paint);
                }
                if (stroke)
                {
                    using var paint = new SKPaint
                    {
                        Color = _stroke,
                        Style = SKPaintStyle.Stroke,
                        IsAntialias = true,
                        StrokeWidth = _lineWidth,
                        StrokeCap = _cap,
                        StrokeJoin = _join,
                        StrokeMiter = _miter,
                    };
                    using var dash = _dash != null ? SKPathEffect.CreateDash(_dash, _dashPhase) : null;
                    paint.PathEffect = dash;
                    canvas.DrawPath(path, paint);
                }
            }
        }

        private static byte Byte(float value) => (byte)Math.Clamp(MathF.Round(value * 255), 0, 255);

        private static SKColor Grey(float g) => new(Byte(g), Byte(g), Byte(g));

        private static SKColor Rgb(float r, float g, float b) => new(Byte(r), Byte(g), Byte(b));

        /// <summary>A plain conversion without a colour profile; <paramref name="tint"/> 0 is the full colour, 1 none of it.</summary>
        private static SKColor Cmyk(float c, float m, float y, float k, float tint)
        {
            var amount = 1 - Math.Clamp(tint, 0, 1);
            float Ink(float v) => (1 - Math.Clamp(v * amount, 0, 1)) * (1 - Math.Clamp(k * amount, 0, 1));
            return new SKColor(Byte(Ink(c)), Byte(Ink(m)), Byte(Ink(y)));
        }

        private static SKColor Tint(SKColor colour, float tint)
        {
            var amount = 1 - Math.Clamp(tint, 0, 1);
            byte Mix(byte v) => (byte)Math.Round(255 - (255 - v) * amount);
            return new SKColor(Mix(colour.Red), Mix(colour.Green), Mix(colour.Blue));
        }

        private static ReadOnlySpan<byte> Line(ReadOnlySpan<byte> ps, int at)
        {
            var rest = ps[at..];
            var end = rest.IndexOfAny((byte)'\r', (byte)'\n');
            return end < 0 ? rest : rest[..end];
        }

        /// <summary>Past a PostScript string: balanced parentheses, backslash escapes.</summary>
        private static int SkipString(ReadOnlySpan<byte> ps, int at)
        {
            var depth = 0;
            for (var i = at; i < ps.Length; i++)
            {
                switch (ps[i])
                {
                    case (byte)'\\': i++; break;
                    case (byte)'(': depth++; break;
                    case (byte)')':
                        if (--depth == 0) return i + 1;
                        break;
                }
            }
            return ps.Length;
        }
    }
}
