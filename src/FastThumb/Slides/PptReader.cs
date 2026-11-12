using System.Buffers.Binary;
using System.Text;
using FastThumb.Layout;
using SkiaSharp;

namespace FastThumb.Slides;

/// <summary>
/// Binary PowerPoint 97-2003 (PPT / PPS / POT), written against the public [MS-PPT] / [MS-ODRAW] layouts.
/// Walks Current User → UserEditAtom → persist directory → Document → slides, and for every shape reads the
/// anchor (through nested group coordinate systems), fill, outline, picture and text with paragraph / character
/// runs on top of the main master's text styles. Master shapes and backgrounds are not drawn.
/// PowerPoint 95 files are a different format and are rejected.
/// </summary>
internal static class PptReader
{
    private readonly record struct Record(int Version, int Instance, int Type, int Start, int Length)
    {
        public int End => Start + Length;
        public bool Container => Version == 0xF;
    }

    private sealed class TextItem { public int Type = 4; public string Text = ""; public byte[]? Styles; }

    private sealed class Reader(byte[] document, byte[]? pictures)
    {
        public readonly byte[] D = document;
        public readonly byte[]? Pictures = pictures;
        public readonly List<string> Fonts = [];
        public readonly List<int> Blips = [];        // offsets into the Pictures stream, in BLIP-store order
        public readonly Dictionary<int, byte[]?> PictureBytes = [];   // by offset in Pictures: shapes that share a picture share the array
        public readonly Dictionary<int, (ParaProps Para, RunProps Run, bool Bullet)> MasterStyles = [];  // by text type
        public SKColor[] Scheme = [SKColors.White, SKColors.Black, SKColors.Gray, SKColors.Black, new(0xFF, 0xBB, 0xE0, 0xE3), new(0xFF, 0x33, 0x33, 0x99), new(0xFF, 0x00, 0x99, 0x99), new(0xFF, 0x99, 0xCC, 0x00)];

        public Record At(int offset)
        {
            var first = BinaryPrimitives.ReadUInt16LittleEndian(D.AsSpan(offset));
            return new Record(first & 0xF, first >> 4, BinaryPrimitives.ReadUInt16LittleEndian(D.AsSpan(offset + 2)), offset + 8,
                (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(D.AsSpan(offset + 4)), (uint)Math.Max(0, D.Length - offset - 8)));
        }

        public IEnumerable<Record> Children(Record parent)
        {
            for (var at = parent.Start; at + 8 <= parent.End;)
            {
                var record = At(at);
                yield return record;
                at = record.End;
            }
        }

        public IEnumerable<Record> Descendants(Record parent, int type)
        {
            Guard.Deeper();
            foreach (var child in Children(parent))
            {
                if (child.Type == type) yield return child;
                else if (child.Container) foreach (var inner in Descendants(child, type)) yield return inner;
            }
        }

        public int I32(int at) => at + 4 <= D.Length ? BinaryPrimitives.ReadInt32LittleEndian(D.AsSpan(at)) : 0;
        public int I16(int at) => at + 2 <= D.Length ? BinaryPrimitives.ReadInt16LittleEndian(D.AsSpan(at)) : 0;
    }

    public static SlideDeck Read(byte[] data)
    {
        var container = new CompoundFile(data);
        var document = container.Stream("PowerPoint Document") ?? throw new InvalidDataException("PowerPoint Document stream not found");
        var current = container.Stream("Current User") ?? throw new NotSupportedException("PowerPoint 95 and older presentations are not supported");
        var reader = new Reader(document, container.Stream("Pictures"));

        // persist directory: newest edit first, older edits only fill ids that are still missing
        var persist = new Dictionary<int, int>();
        var documentRef = -1;
        var visited = new HashSet<int>();                     // edits and directories: a chain that loops or shares one is read once
        for (int edit = BinaryPrimitives.ReadInt32LittleEndian(current.AsSpan(16)), guard = 0; edit > 0 && edit + 28 <= document.Length && guard < 1000 && visited.Add(edit); guard++)
        {
            var atom = reader.At(edit);
            if (atom.Type != 4085) break;
            if (documentRef < 0) documentRef = reader.I32(atom.Start + 16);
            var directory = reader.At(reader.I32(atom.Start + 12));
            if (!visited.Add(~directory.Start)) { edit = reader.I32(atom.Start + 8); continue; }
            for (var at = directory.Start; at + 4 <= directory.End;)
            {
                var entry = (uint)reader.I32(at);
                at += 4;
                int id = (int)(entry & 0xFFFFF), count = (int)(entry >> 20);
                for (var i = 0; i < count && at + 4 <= directory.End; i++, at += 4) persist.TryAdd(id + i, reader.I32(at));
            }
            edit = reader.I32(atom.Start + 8);
        }
        if (!persist.TryGetValue(documentRef, out var documentOffset)) throw new InvalidDataException("PPT document record not found");

        var deck = new SlideDeck();
        var root = reader.At(documentOffset);
        var slides = new List<(int Offset, List<TextItem> Texts)>();
        foreach (var record in reader.Children(root))
        {
            switch (record.Type)
            {
                case 1001: // DocumentAtom: slide size in master units (576 per inch)
                    deck.Width = reader.I32(record.Start) / 8f;
                    deck.Height = reader.I32(record.Start + 4) / 8f;
                    break;
                case 1010: // Environment → FontCollection → FontEntityAtom
                    foreach (var font in reader.Descendants(record, 4023))
                        reader.Fonts.Add(Encoding.Unicode.GetString(document, font.Start, Math.Min(64, font.Length)).Split('\0')[0]);
                    foreach (var style in reader.Descendants(record, 4003)) ReadMasterStyle(reader, style); // "other" text default
                    break;
                case 1035: // drawing group: the BLIP store lists every picture of the file
                    foreach (var blip in reader.Descendants(record, 0xF007)) reader.Blips.Add(reader.I32(blip.Start + 28));
                    break;
                case 4080 when record.Instance == 1: // masters: the first main master supplies default text styles and colours
                    foreach (var child in reader.Children(record))
                    {
                        if (child.Type != 1011 || !persist.TryGetValue(reader.I32(child.Start), out var masterOffset) || reader.MasterStyles.Count > 1) continue;
                        var master = reader.At(masterOffset);
                        if (reader.Children(master).FirstOrDefault(r => r.Type == 2032) is { Length: >= 32 } masterScheme)
                            reader.Scheme = Enumerable.Range(0, 8).Select(i => new SKColor(document[masterScheme.Start + i * 4], document[masterScheme.Start + i * 4 + 1], document[masterScheme.Start + i * 4 + 2])).ToArray();
                        foreach (var style in reader.Children(master).Where(r => r.Type == 4003)) ReadMasterStyle(reader, style);
                    }
                    break;
                case 4080 when record.Instance == 0: // SlideListWithText for the slides: persist refs + placeholder text
                    foreach (var child in reader.Children(record))
                    {
                        if (child.Type == 1011 && slides.Count >= SlideDeck.MaxSlides) break;
                        if (child.Type == 1011) slides.Add((persist.GetValueOrDefault(reader.I32(child.Start), -1), []));
                        else if (slides.Count > 0) CollectText(reader, child, slides[^1].Texts);
                    }
                    break;
            }
        }

        var built = new HashSet<int>();
        foreach (var (offset, texts) in slides)
        {
            if (offset >= 0 && !built.Add(offset)) continue;   // one slide record listed again: drawn once
            var slide = new SlideData();
            deck.Slides.Add(slide);
            if (offset < 0 || offset + 8 > document.Length) continue;
            var record = reader.At(offset);
            if (reader.Children(record).FirstOrDefault(r => r.Type == 2032) is { Length: >= 32 } scheme)
                reader.Scheme = Enumerable.Range(0, 8).Select(i => new SKColor(document[scheme.Start + i * 4], document[scheme.Start + i * 4 + 1], document[scheme.Start + i * 4 + 2])).ToArray();
            foreach (var drawing in reader.Descendants(record, 0xF002))
                foreach (var child in reader.Children(drawing))
                {
                    if (child.Type == 0xF003) WalkGroup(reader, deck, slide, child, new Transform(0.125f, 0.125f, 0, 0), texts);
                    else if (child.Type == 0xF004) AddShape(reader, deck, slide, child, texts, default); // the background shape
                }
        }
        return deck;
    }

    /// <summary>Maps a group's own coordinate system (raw units) onto slide points.</summary>
    private readonly record struct Transform(float ScaleX, float ScaleY, float OffsetX, float OffsetY)
    {
        public SKRect Apply(float left, float top, float right, float bottom) => new(left * ScaleX + OffsetX, top * ScaleY + OffsetY, right * ScaleX + OffsetX, bottom * ScaleY + OffsetY);
    }

    private static SKRect? AnchorOf(Reader reader, Record shape, Transform transform)
    {
        foreach (var record in reader.Children(shape))
        {
            if (record.Type == 0xF010 && record.Length >= 8) // client anchor: top, left, right, bottom on the slide, int16
                return new SKRect(reader.I16(record.Start + 2) / 8f, reader.I16(record.Start) / 8f, reader.I16(record.Start + 4) / 8f, reader.I16(record.Start + 6) / 8f);
            if (record.Type == 0xF00F && record.Length >= 16) // child anchor: left, top, right, bottom in the parent group's coordinates
                return transform.Apply(reader.I32(record.Start), reader.I32(record.Start + 4), reader.I32(record.Start + 8), reader.I32(record.Start + 12));
        }
        return null;
    }

    private static void WalkGroup(Reader reader, SlideDeck deck, SlideData slide, Record group, Transform transform, List<TextItem> texts)
    {
        Guard.Deeper();
        var first = true;
        foreach (var child in reader.Children(group))
        {
            if (child.Type == 0xF003) { WalkGroup(reader, deck, slide, child, transform, texts); continue; }
            if (child.Type != 0xF004) continue;
            if (!first) { AddShape(reader, deck, slide, child, texts, transform); continue; }
            first = false;
            // the first shape of a group describes the group: its coordinate box and where that box sits
            var box = reader.Children(child).FirstOrDefault(r => r.Type == 0xF009);
            if (box.Length >= 16 && AnchorOf(reader, child, transform) is { } placed)
            {
                float left = reader.I32(box.Start), top = reader.I32(box.Start + 4), width = reader.I32(box.Start + 8) - left, height = reader.I32(box.Start + 12) - top;
                if (width > 0 && height > 0)
                {
                    float sx = placed.Width / width, sy = placed.Height / height;
                    transform = new Transform(sx, sy, placed.Left - left * sx, placed.Top - top * sy);
                }
            }
        }
    }

    private static void CollectText(Reader reader, Record record, List<TextItem> texts)
    {
        switch (record.Type)
        {
            case 3999: texts.Add(new TextItem { Type = reader.I32(record.Start) }); break;
            case 4000 when texts.Count > 0: texts[^1].Text = Encoding.Unicode.GetString(reader.D, record.Start, record.Length); break;
            case 4008 when texts.Count > 0: texts[^1].Text = Encoding.Latin1.GetString(reader.D, record.Start, record.Length); break;
            case 4001 when texts.Count > 0: texts[^1].Styles = reader.D.AsSpan(record.Start, record.Length).ToArray(); break;
        }
    }

    private static SKColor? ColorOf(Reader reader, uint value)
    {
        var flags = value >> 24;
        if ((flags & 0x08) != 0) return reader.Scheme[Math.Min((int)(value & 0xFF), 7)]; // index into the slide colour scheme
        if ((flags & 0xF0) != 0) return null;                                              // system / computed colours: not resolved
        return new SKColor((byte)value, (byte)(value >> 8), (byte)(value >> 16));
    }

    /// <summary>Text colours use a different encoding from shape colours: red, green, blue, then an index byte (0xFE = literal RGB, 0-7 = scheme slot).</summary>
    private static SKColor? TextColor(Reader reader, uint value)
    {
        var index = value >> 24;
        if (index == 0xFE) return new SKColor((byte)value, (byte)(value >> 8), (byte)(value >> 16));
        return index < 8 ? reader.Scheme[index] : null;
    }

    private static void ReadParagraphException(Reader reader, byte[] s, ref int at, ParaProps para, ref bool? bullet)
    {
        int position = at;
        int U16() { var v = position + 2 <= s.Length ? BinaryPrimitives.ReadUInt16LittleEndian(s.AsSpan(position)) : 0; position += 2; return v; }
        uint U32() { var v = position + 4 <= s.Length ? BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(position)) : 0; position += 4; return v; }
        var mask = U32();
        if ((mask & 0x000F) != 0) { var flags = U16(); if ((mask & 0x1) != 0) bullet = (flags & 1) != 0; }
        if ((mask & 0x0080) != 0) U16();
        if ((mask & 0x0010) != 0) U16();
        if ((mask & 0x0040) != 0) U16();
        if ((mask & 0x0020) != 0) U32();
        if ((mask & 0x0800) != 0) para.Align = U16() switch { 1 => Align.Center, 2 => Align.Right, 3 => Align.Justify, _ => Align.Left };
        if ((mask & 0x1000) != 0) U16();
        if ((mask & 0x2000) != 0) U16();
        if ((mask & 0x4000) != 0) U16();
        float? margin = null, indent = null;
        if ((mask & 0x0100) != 0) margin = (short)U16() / 8f;
        if ((mask & 0x0400) != 0) indent = (short)U16() / 8f;
        if (margin != null) para.IndentLeft = margin.Value;
        if (indent != null) para.FirstLine = indent.Value - para.IndentLeft;
        if ((mask & 0x8000) != 0) U16();
        if ((mask & 0x100000) != 0) { var tabs = U16(); position += tabs * 4; }
        if ((mask & 0x10000) != 0) U16();
        if ((mask & 0xE0000) != 0) U16();
        if ((mask & 0x200000) != 0) U16();
        at = position;
    }

    private static void ReadCharacterException(Reader reader, byte[] s, ref int at, RunProps run)
    {
        int position = at;
        int U16() { var v = position + 2 <= s.Length ? BinaryPrimitives.ReadUInt16LittleEndian(s.AsSpan(position)) : 0; position += 2; return v; }
        uint U32() { var v = position + 4 <= s.Length ? BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(position)) : 0; position += 4; return v; }
        var mask = U32();
        if ((mask & 0xFFFF) != 0)
        {
            var style = U16();
            if ((mask & 1) != 0) run.Bold = (style & 1) != 0;
            if ((mask & 2) != 0) run.Italic = (style & 2) != 0;
            if ((mask & 4) != 0) run.Underline = (style & 4) != 0;
        }
        if ((mask & 0x10000) != 0) { var font = U16(); if (font < reader.Fonts.Count) run.Font = reader.Fonts[font]; }
        if ((mask & 0x200000) != 0) U16();
        if ((mask & 0x400000) != 0) { var ansi = U16(); if ((mask & 0x10000) == 0 && ansi < reader.Fonts.Count) run.Font = reader.Fonts[ansi]; } // Latin font when no main font is given
        if ((mask & 0x800000) != 0) U16();
        if ((mask & 0x20000) != 0) run.Size = U16();
        if ((mask & 0x40000) != 0) run.Color = TextColor(reader, U32()) ?? run.Color;
        if ((mask & 0x80000) != 0) U16();
        at = position;
    }

    /// <summary>TxMasterStyleAtom: per-level paragraph + character defaults for one text type; level 0 is kept.</summary>
    private static void ReadMasterStyle(Reader reader, Record record)
    {
        var s = reader.D.AsSpan(record.Start, record.Length).ToArray();
        if (s.Length < 10) return;
        var at = 2;
        if (record.Instance >= 5) at += 2;
        // types 5-8 (centre body, centre title, half / quarter body) only store what differs from their base type
        var parent = record.Instance switch { 6 => 0, 5 or 7 or 8 => 1, _ => -1 };
        var para = new ParaProps();
        var run = new RunProps { Font = reader.Fonts.FirstOrDefault() ?? "Arial", Size = 18, Color = reader.Scheme[record.Instance is 0 or 6 ? 3 : 1] };
        bool? bullet = null;
        if (parent >= 0 && reader.MasterStyles.TryGetValue(parent, out var inherited))
        {
            para = inherited.Para.Clone();
            run = inherited.Run.Clone();
            bullet = inherited.Bullet;
        }
        ReadParagraphException(reader, s, ref at, para, ref bullet);
        ReadCharacterException(reader, s, ref at, run);
        reader.MasterStyles.TryAdd(record.Instance, (para, run, bullet ?? false));
    }

    private static void AddShape(Reader reader, SlideDeck deck, SlideData slide, Record container, List<TextItem> slideTexts, Transform transform)
    {
        var properties = new Dictionary<int, uint>();
        var anchor = AnchorOf(reader, container, transform);
        int shapeType = 0, placeholder = -1;
        uint shapeFlags = 0;
        var texts = new List<TextItem>();
        int outlineRef = -1;

        foreach (var record in reader.Children(container))
        {
            switch (record.Type)
            {
                case 0xF00A: shapeType = record.Instance; shapeFlags = (uint)reader.I32(record.Start + 4); break;
                case 0xF00B or 0xF122:
                    for (var i = 0; i < record.Instance && record.Start + i * 6 + 6 <= record.End; i++)
                        properties[reader.I16(record.Start + i * 6) & 0x3FFF] = (uint)reader.I32(record.Start + i * 6 + 2);
                    break;
                case 0xF011:
                    if (reader.Children(record).FirstOrDefault(r => r.Type == 3011) is { Length: >= 5 } ph) placeholder = reader.D[ph.Start + 4];
                    break;
                case 0xF00D:
                    foreach (var child in reader.Children(record))
                    {
                        if (child.Type == 3998) outlineRef = reader.I32(child.Start);
                        else CollectText(reader, child, texts);
                    }
                    break;
            }
        }
        if ((shapeFlags & 0x1) != 0 || (shapeFlags & 0x4) != 0) return; // group frame / patriarch: not drawable themselves

        var text = outlineRef >= 0 && outlineRef < slideTexts.Count ? slideTexts[outlineRef] : texts.FirstOrDefault();
        var isPlaceholder = placeholder >= 0;
        var isTextBox = shapeType == 202;

        if ((shapeFlags & 0x400) != 0)
        {
            // the slide's background shape
            if (properties.TryGetValue(385, out var bg) && (!properties.TryGetValue(447, out var bgFlags) || (bgFlags & 0x10) != 0) && ColorOf(reader, bg) is { } color) slide.Background = color;
            return;
        }

        var bounds = anchor ?? DefaultPlaceholderBox(deck, text?.Type ?? 4);
        var shape = new SlideShape
        {
            Bounds = bounds,
            Kind = shapeType switch { 3 => ShapeKind.Ellipse, 2 => ShapeKind.RoundRect, 20 or 32 or 33 or 34 or 38 => ShapeKind.Line, _ => ShapeKind.Rect },
        };

        var filled = properties.TryGetValue(447, out var fillFlags) ? (fillFlags & 0x10) != 0 : !isTextBox && !isPlaceholder && shapeType != 75 && properties.ContainsKey(385);
        if (filled && shape.Kind != ShapeKind.Line) shape.Fill = properties.TryGetValue(385, out var fillColor) ? ColorOf(reader, fillColor) : SKColors.White;
        var lined = properties.TryGetValue(511, out var lineFlags) ? (lineFlags & 0x8) != 0 : !isTextBox && !isPlaceholder && shapeType != 75;
        if (lined || shape.Kind == ShapeKind.Line) shape.Stroke = properties.TryGetValue(448, out var lineColor) ? ColorOf(reader, lineColor) ?? SKColors.Black : SKColors.Black;
        if (properties.TryGetValue(459, out var lineWidth)) shape.StrokeWidth = Math.Max(0.25f, lineWidth / 12700f);
        if (properties.TryGetValue(260, out var blip)) shape.Image = Picture(reader, (int)blip);
        if (properties.TryGetValue(135, out var textAnchor)) shape.Anchor = textAnchor switch { 1 or 4 => TextAnchor.Middle, 2 or 5 => TextAnchor.Bottom, _ => TextAnchor.Top };
        else if (text?.Type is 0 or 6) shape.Anchor = TextAnchor.Middle;

        if (text != null && text.Text.Trim('\r', ' ').Length > 0) shape.Text = BuildText(reader, text);
        if (shape.Fill != null || shape.Stroke != null || shape.Image != null || shape.Text.Count > 0) slide.Shapes.Add(shape);
    }

    /// <summary>Placeholders that take their position from the master (not read here) get the usual layout boxes.</summary>
    private static SKRect DefaultPlaceholderBox(SlideDeck deck, int textType) => textType switch
    {
        0 => SKRect.Create(deck.Width * 0.075f, deck.Height * 0.04f, deck.Width * 0.85f, deck.Height * 0.167f),
        6 => SKRect.Create(deck.Width * 0.075f, deck.Height * 0.31f, deck.Width * 0.85f, deck.Height * 0.214f),
        5 => SKRect.Create(deck.Width * 0.15f, deck.Height * 0.567f, deck.Width * 0.7f, deck.Height * 0.256f),
        _ => SKRect.Create(deck.Width * 0.075f, deck.Height * 0.233f, deck.Width * 0.85f, deck.Height * 0.66f),
    };

    private static byte[]? Picture(Reader reader, int index)
    {
        var pictures = reader.Pictures;
        if (pictures == null || index < 1 || index > reader.Blips.Count) return null;
        var at = reader.Blips[index - 1];
        if (reader.PictureBytes.TryGetValue(at, out var hit)) return hit;
        return reader.PictureBytes[at] = ReadPicture(pictures, at);
    }

    private static byte[]? ReadPicture(byte[] pictures, int at)
    {
        if (at < 0 || at + 8 > pictures.Length) return null;
        int instance = BinaryPrimitives.ReadUInt16LittleEndian(pictures.AsSpan(at)) >> 4, type = BinaryPrimitives.ReadUInt16LittleEndian(pictures.AsSpan(at + 2));
        var length = BinaryPrimitives.ReadInt32LittleEndian(pictures.AsSpan(at + 4));
        if (type is not (0xF01D or 0xF01E or 0xF02A)) return null;           // only JPEG and PNG; metafiles and DIBs are skipped
        var header = 16 + ((instance & 1) != 0 ? 16 : 0) + 1;                // one or two UIDs, then a tag byte
        return length > header && at + 8 + length <= pictures.Length ? pictures.AsSpan(at + 8 + header, length - header).ToArray() : null;
    }

    // ---- text -----------------------------------------------------------------------------------

    private static List<Block> BuildText(Reader reader, TextItem item)
    {
        // defaults for this text type from the master (centre title falls back to title, the body variants to body)
        var type = item.Type;
        var fallback = type switch { 6 => 0, 5 or 7 or 8 => 1, _ => 4 };
        ParaProps basePara; RunProps baseRun; bool bulleted;
        if (reader.MasterStyles.TryGetValue(type, out var master) || reader.MasterStyles.TryGetValue(fallback, out master))
        {
            basePara = master.Para.Clone(); baseRun = master.Run.Clone(); bulleted = master.Bullet && type != 5;
            if (type is 5 or 6) basePara.Align = Align.Center;
        }
        else
        {
            baseRun = new RunProps { Font = reader.Fonts.FirstOrDefault() ?? "Arial", Size = type switch { 0 or 6 => 44, 1 or 5 => 32, 7 => 28, 8 => 24, _ => 18 }, Color = reader.Scheme[type is 0 or 6 ? 3 : 1] };
            basePara = new ParaProps { Align = type is 0 or 5 or 6 ? Align.Center : Align.Left };
            bulleted = type is 1 or 7 or 8;
        }

        if (Environment.GetEnvironmentVariable("THUMB_PPT_DEBUG") != null)
            Console.WriteLine($"TEXT type={item.Type} base={baseRun.Font}/{baseRun.Size}/{baseRun.Color} fonts=[{string.Join(",", reader.Fonts)}] masters=[{string.Join(",", reader.MasterStyles.Select(m => $"{m.Key}:{m.Value.Run.Font}/{m.Value.Run.Size}"))}] \"{(item.Text.Length > 24 ? item.Text[..24] : item.Text).Replace('\r', '|')}\" styles={(item.Styles == null ? "-" : Convert.ToHexString(item.Styles.AsSpan(0, Math.Min(item.Styles.Length, 48))))}");
        var text = item.Text;
        var length = text.Length + 1;
        var paraRuns = new List<(int Count, ParaProps Props, bool? Bullet)>();
        var charRuns = new List<(int Count, RunProps Props)>();
        if (item.Styles is { } s)
        {
            var at = 0;
            for (var covered = 0; covered < length && at + 10 <= s.Length;)
            {
                var count = BinaryPrimitives.ReadInt32LittleEndian(s.AsSpan(at));
                at += 6; // run length + indent level
                var para = basePara.Clone();
                bool? bullet = null;
                ReadParagraphException(reader, s, ref at, para, ref bullet);
                paraRuns.Add((count, para, bullet));
                covered += Math.Max(1, count);
            }
            for (var covered = 0; covered < length && at + 8 <= s.Length;)
            {
                var count = BinaryPrimitives.ReadInt32LittleEndian(s.AsSpan(at));
                at += 4;
                var run = baseRun.Clone();
                ReadCharacterException(reader, s, ref at, run);
                charRuns.Add((count, run));
                covered += Math.Max(1, count);
            }
        }

        var blocks = new List<Block>();
        int position = 0, paraIndex = 0, paraLeft = paraRuns.Count > 0 ? paraRuns[0].Count : int.MaxValue, charIndex = 0, charLeft = charRuns.Count > 0 ? charRuns[0].Count : int.MaxValue;
        Paragraph? paragraph = null;
        var buffer = new StringBuilder();
        RunProps CurrentRun() => charIndex < charRuns.Count ? charRuns[charIndex].Props : baseRun;
        void Start()
        {
            var (props, bullet) = paraIndex < paraRuns.Count ? (paraRuns[paraIndex].Props.Clone(), paraRuns[paraIndex].Bullet) : (basePara.Clone(), null);
            if (bullet ?? bulleted) { props.Bullet = "•"; if (props.IndentLeft < 1) { props.IndentLeft = 27; props.FirstLine = -27; } }
            else if (props.FirstLine < 0) { props.IndentLeft = 0; props.FirstLine = 0; }
            paragraph = new Paragraph { Props = props, MarkProps = CurrentRun() };
        }
        void FlushRun() { if (buffer.Length > 0) paragraph!.Inlines.Add(new TextInline { Props = CurrentRun(), Text = buffer.ToString() }); buffer.Clear(); }

        for (; position < text.Length; position++)
        {
            if (paragraph == null) Start();
            var ch = text[position];
            if (ch == '\r') { FlushRun(); blocks.Add(paragraph!); paragraph = null; }
            else if (ch == '\v') { FlushRun(); paragraph!.Inlines.Add(new BreakInline { Props = CurrentRun() }); }
            else if (ch >= ' ') buffer.Append(ch);

            if (--charLeft <= 0) { FlushRun(); charIndex++; charLeft = charIndex < charRuns.Count ? charRuns[charIndex].Count : int.MaxValue; }
            if (--paraLeft <= 0) { paraIndex++; paraLeft = paraIndex < paraRuns.Count ? paraRuns[paraIndex].Count : int.MaxValue; }
        }
        if (paragraph != null) { FlushRun(); blocks.Add(paragraph); }
        foreach (var block in blocks.OfType<Paragraph>()) if (block.Inlines.Count == 0) block.Props.Bullet = null;
        return blocks;
    }
}
