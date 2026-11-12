using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using FastThumb.Layout;

namespace FastThumb.Words;

/// <summary>
/// Text-level readers for three binary formats. They recover paragraphs (and, for WordPerfect, bold / italic /
/// underline) but no layout. NOT verified against real files: no sample of these formats was available.
/// </summary>
internal static class LegacyTextReaders
{
    private static IPageSource FromParagraphs(IEnumerable<string> paragraphs, string font, float size)
    {
        var run = new RunProps { Font = font, Size = size };
        var blocks = new List<Block>();
        foreach (var text in paragraphs)
        {
            var paragraph = new Paragraph { Props = new ParaProps { SpaceAfter = 6 }, MarkProps = run };
            if (text.Length > 0) paragraph.Inlines.Add(new TextInline { Props = run, Text = text });
            blocks.Add(paragraph);
        }
        return new StaticSource(new PageSetup(595.3f, 841.9f, 72, 72, 72, 72), blocks);
    }

    /// <summary>HWP 5.x: compound file; BodyText/SectionN are raw-deflate record streams, tag 67 carries paragraph text.</summary>
    public static IPageSource ReadHwp(byte[] data)
    {
        var container = new CompoundFile(data);
        var header = container.Stream("FileHeader") ?? throw new InvalidDataException("HWP FileHeader not found");
        var flags = header.Length >= 40 ? BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(36)) : 0;
        if ((flags & 2) != 0) throw new NotSupportedException("Password-protected HWP");

        var paragraphs = new List<string>();
        // sections are disjoint in a real file: directory entries that share sectors cannot make the reader go over it again
        // and again; and all sections together inflate to at most one part's limit
        long raw = 0, inflatedBudget = Guard.MaxPartBytes;
        for (var section = 0; section < 512; section++)
        {
            var stream = container.Stream("Section" + section);
            if (stream == null || (raw += stream.Length) > data.Length) break;
            if ((flags & 1) != 0)
            {
                using var inflated = new MemoryStream();
                using (var deflate = Guard.Limit(new DeflateStream(new MemoryStream(stream), CompressionMode.Decompress), inflatedBudget, "BodyText/Section" + section)) deflate.CopyTo(inflated);
                stream = inflated.ToArray();
                inflatedBudget -= stream.Length;
            }
            for (var at = 0; at + 4 <= stream.Length;)
            {
                var record = BinaryPrimitives.ReadUInt32LittleEndian(stream.AsSpan(at));
                at += 4;
                int tag = (int)(record & 0x3FF), size = (int)(record >> 20);
                if (size == 0xFFF) { if (at + 4 > stream.Length) break; size = BinaryPrimitives.ReadInt32LittleEndian(stream.AsSpan(at)); at += 4; }
                if (size < 0 || at + size > stream.Length) break;
                if (tag == 67)
                {
                    var text = new StringBuilder();
                    for (var i = at; i + 2 <= at + size; i += 2)
                    {
                        var ch = (char)BinaryPrimitives.ReadUInt16LittleEndian(stream.AsSpan(i));
                        if (ch >= 32) text.Append(ch);
                        else if (ch == 10) text.Append(' ');
                        else if (ch == 9) { text.Append("    "); i += 14; }
                        else if (ch is (>= (char)1 and <= (char)8) or (char)11 or (char)12 or (>= (char)14 and <= (char)23)) i += 14; // 8-unit control
                    }
                    paragraphs.Add(text.ToString());
                }
                at += size;
            }
        }
        if (paragraphs.Count == 0 && container.Stream("PrvText") is { } preview)
            paragraphs.AddRange(Encoding.Unicode.GetString(preview).Split('\n').Select(line => line.TrimEnd('\r')));
        return FromParagraphs(paragraphs, "Malgun Gothic", 10);
    }

    /// <summary>WordPerfect 6+: function codes skipped by their declared length, text and hard returns kept.</summary>
    public static IPageSource ReadWordPerfect(byte[] data)
    {
        if (data.Length < 16) throw new InvalidDataException("Not a WordPerfect document");
        if (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(12)) != 0) throw new NotSupportedException("Encrypted WordPerfect document");
        if (data[10] == 0) throw new NotSupportedException("WordPerfect 5.x documents are not supported");
        var at = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
        int[] fixedSizes = [4, 5, 3, 3, 3, 3, 4, 4, 4, 5, 5, 6, 6, 8, 8];

        var blocks = new List<Block>();
        var run = new RunProps { Size = 12 };
        var paragraph = new Paragraph { Props = new ParaProps { SpaceAfter = 6 }, MarkProps = run };
        var text = new StringBuilder();
        void FlushText() { if (text.Length > 0) paragraph.Inlines.Add(new TextInline { Props = run, Text = text.ToString() }); text.Clear(); }
        void EndParagraph() { FlushText(); blocks.Add(paragraph); paragraph = new Paragraph { Props = new ParaProps { SpaceAfter = 6 }, MarkProps = run }; }

        while (at >= 0 && at < data.Length)
        {
            var b = data[at];
            if (b is >= 0x20 and <= 0x7E) { text.Append((char)b); at++; }
            else if (b == 0x80) { text.Append(' '); at++; }
            else if (b == 0x81) { text.Append(' '); at++; }
            else if (b == 0x84) { text.Append('-'); at++; }
            else if (b < 0xD0) at++;                                      // other single-byte functions
            else if (b < 0xF0)
            {
                // variable-length function: group, subgroup, total size (u16), ..., size, group
                if (at + 4 > data.Length) break;
                var size = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 2));
                if (b == 0xD0) { if (data[at + 1] >= 4) EndParagraph(); else text.Append(' '); } // end-of-line group: hard vs soft
                at += Math.Max((int)size, 4);
            }
            else if (b == 0xFF) at++;
            else
            {
                var size = fixedSizes[b - 0xF0];
                if (at + size > data.Length) break;
                if (b == 0xF0) text.Append('?');                          // extended character set: not mapped
                else if (b is 0xF2 or 0xF3)
                {
                    FlushText();
                    run = run.Clone();
                    var on = b == 0xF2;
                    switch (data[at + 1]) { case 12: run.Bold = on; break; case 8: run.Italic = on; break; case 14: run.Underline = on; break; }
                }
                at += size;
            }
        }
        if (text.Length > 0 || paragraph.Inlines.Count > 0) EndParagraph();
        return new StaticSource(PageSetup.Default, blocks);
    }

    /// <summary>Microsoft Works: the longest run of plausible text in the document stream (UTF-16 for Works 2000+, 8-bit before).</summary>
    public static IPageSource ReadWorks(byte[] data)
    {
        var container = new CompoundFile(data);
        var stream = container.Stream("CONTENTS") ?? container.Stream("MN0") ?? container.Stream("MatOST") ?? throw new InvalidDataException("Works document stream not found");

        string best = "";
        // UTF-16LE candidate
        for (var parity = 0; parity < 2; parity++)
        {
            var current = new StringBuilder();
            for (var i = parity; i + 2 <= stream.Length; i += 2)
            {
                var ch = (char)BinaryPrimitives.ReadUInt16LittleEndian(stream.AsSpan(i));
                if (ch is '\r' or '\n' or '\t' || (ch >= 32 && ch < 0xD800 && (ch < 0x7F || char.IsLetterOrDigit(ch) || char.IsPunctuation(ch)))) current.Append(ch);
                else { if (current.Length > best.Length) best = current.ToString(); current.Clear(); }
            }
            if (current.Length > best.Length) best = current.ToString();
        }
        // 8-bit candidate
        var bytes = new StringBuilder();
        var latin = Encoding.GetEncoding(1252);
        var start = 0;
        for (var i = 0; i <= stream.Length; i++)
        {
            var printable = i < stream.Length && (stream[i] is 13 or 10 or 9 || stream[i] >= 32 && stream[i] != 127);
            if (printable) continue;
            if (i - start > best.Length * 2 && i - start > 200) best = latin.GetString(stream, start, i - start);
            start = i + 1;
        }
        if (best.Length == 0) throw new InvalidDataException("No text found in Works document");
        return FromParagraphs(best.Split('\r', '\n').Where((line, index) => line.Length > 0 || index % 2 == 0), "Times New Roman", 12);
    }
}
