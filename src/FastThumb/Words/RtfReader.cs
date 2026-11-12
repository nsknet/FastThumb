using System.Globalization;
using System.Text;
using FastThumb.Layout;
using SkiaSharp;

namespace FastThumb.Words;

/// <summary>
/// RTF → blocks: groups, control words, \'hh and \u escapes, font and colour tables, character and paragraph
/// formatting, simple tables (\trowd … \cell … \row), page size and margins. Pictures, shapes, headers,
/// footnotes and field instructions are skipped.
/// </summary>
internal static class RtfReader
{
    private sealed class State
    {
        public RunProps Run = new() { Size = 12 };
        public ParaProps Para = new();
        public bool InTable, Hidden;
        public int UnicodeSkip = 1;
        public string Destination = "";   // "" = document text, otherwise the name of the destination being read
        public State Clone() => new() { Run = Run.Clone(), Para = Para.Clone(), InTable = InTable, Hidden = Hidden, UnicodeSkip = UnicodeSkip, Destination = Destination };
    }

    private static readonly HashSet<string> SkippedDestinations = new(StringComparer.Ordinal)
    {
        "stylesheet", "info", "pict", "header", "headerl", "headerr", "headerf", "footer", "footerl", "footerr", "footerf", "footnote", "fldinst",
        "object", "datafield", "themedata", "colorschememapping", "latentstyles", "datastore", "generator", "listtable", "listoverridetable",
        "rsidtbl", "xmlnstbl", "pgdsctbl", "mmathPr", "shp", "shpinst", "shppict", "nonshppict", "bkmkstart", "bkmkend", "comment", "annotation",
        "filetbl", "revtbl", "operator", "author", "title", "subject", "keywords", "doccomm", "private", "panose", "falt", "listtext", "pntext", "wgrffmtfilter",
    };

    public static IPageSource Read(byte[] data)
    {
        var text = Encoding.Latin1.GetString(data); // byte-transparent: \'hh bytes are decoded with the document code page
        var codePage = Encoding.GetEncoding(1252);
        var fonts = new Dictionary<int, string>();
        var colors = new List<SKColor?>();
        float paperWidth = 12240, paperHeight = 15840, left = 1800, right = 1800, top = 1440, bottom = 1440;
        var defaultFont = 0;

        var stack = new Stack<State>();
        var state = new State();
        var blocks = new List<Block>();
        var cells = new List<Cell>();
        var cellBlocks = new List<Block>();
        var edges = new List<float>();
        var rowBorders = false;
        var paragraph = new List<Inline>();
        var buffer = new StringBuilder();
        var pendingBytes = new List<byte>();
        var skipChars = 0;
        var fontIndex = -1;
        var fontName = new StringBuilder();
        int red = -1, green = -1, blue = -1;

        void FlushBytes()
        {
            if (pendingBytes.Count == 0) return;
            Append(codePage.GetString(pendingBytes.ToArray()));
            pendingBytes.Clear();
        }
        void Append(string value)
        {
            if (state.Destination == "fonttbl") { fontName.Append(value); return; }
            if (state.Destination.Length > 0 || state.Hidden) return;
            buffer.Append(value);
        }
        void FlushText()
        {
            FlushBytes();
            if (buffer.Length == 0) return;
            paragraph.Add(new TextInline { Props = state.Run.Clone(), Text = buffer.ToString() });
            buffer.Clear();
        }
        void EndParagraph()
        {
            FlushText();
            var block = new Paragraph { Props = state.Para.Clone(), MarkProps = state.Run.Clone(), Inlines = [.. paragraph] };
            paragraph.Clear();
            if (state.InTable) cellBlocks.Add(block); else { FlushOrphanCells(); blocks.Add(block); }
        }
        void FlushOrphanCells()
        {
            foreach (var cell in cells) blocks.AddRange(cell.Blocks);
            blocks.AddRange(cellBlocks);
            cells.Clear();
            cellBlocks.Clear();
        }

        // each group saves a copy of the state: past this depth groups are flattened into the one that holds them
        const int MaxDepth = 1000;
        var flattened = 0;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '{')
            {
                FlushText();
                if (stack.Count < MaxDepth) stack.Push(state.Clone()); else flattened++;
                i++;
                continue;
            }
            if (c == '}' && flattened > 0) { FlushText(); flattened--; i++; continue; }
            if (c == '}')
            {
                FlushText();
                var closing = state.Destination;
                if (stack.Count > 0) state = stack.Pop();
                if (closing == "fonttbl" && state.Destination != "fonttbl") fontName.Clear();
                i++;
                continue;
            }
            if (c is '\r' or '\n') { i++; continue; }
            if (c != '\\')
            {
                if (skipChars > 0) { skipChars--; i++; continue; }
                FlushBytes();
                if (state.Destination == "fonttbl")
                {
                    if (c == ';') { if (fontIndex >= 0) fonts[fontIndex] = fontName.ToString().Trim(); fontName.Clear(); }
                    else fontName.Append(c);
                }
                else if (state.Destination == "colortbl")
                {
                    if (c == ';') { colors.Add(red < 0 ? null : new SKColor((byte)red, (byte)Math.Max(0, green), (byte)Math.Max(0, blue))); red = green = blue = -1; }
                }
                else Append(c.ToString());
                i++;
                continue;
            }

            // control symbol or control word
            i++;
            if (i >= text.Length) break;
            c = text[i];
            if (!char.IsAsciiLetter(c))
            {
                i++;
                switch (c)
                {
                    case '\'':
                        if (i + 2 <= text.Length && byte.TryParse(text.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
                        {
                            i += 2;
                            if (skipChars > 0) skipChars--; else pendingBytes.Add(value);
                        }
                        break;
                    case '\\' or '{' or '}': FlushBytes(); Append(c.ToString()); break;
                    case '~': FlushBytes(); Append(" "); break;
                    case '_': FlushBytes(); Append("-"); break;
                    case '*':
                        // {\*\destination ...}: ignorable. Skip the whole group unless it is one we read.
                        var peek = i;
                        if (peek < text.Length && text[peek] == '\\')
                        {
                            var end = peek + 1;
                            while (end < text.Length && char.IsAsciiLetter(text[end])) end++;
                            var word = text[(peek + 1)..end];
                            if (word is not ("fldrslt" or "shptxt")) state.Destination = "skip";
                        }
                        break;
                    case '\r' or '\n': EndParagraph(); break; // "\<newline>" is \par
                }
                continue;
            }

            var startWord = i;
            while (i < text.Length && char.IsAsciiLetter(text[i])) i++;
            var name = text[startWord..i];
            var hasParameter = false;
            var parameter = 0;
            var startNumber = i;
            if (i < text.Length && (text[i] == '-' || char.IsAsciiDigit(text[i])))
            {
                i++;
                while (i < text.Length && char.IsAsciiDigit(text[i])) i++;
                hasParameter = int.TryParse(text.AsSpan(startNumber, i - startNumber), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out parameter);
            }
            if (i < text.Length && text[i] == ' ') i++;
            var on = !hasParameter || parameter != 0;

            if (state.Destination == "skip") continue;
            if (SkippedDestinations.Contains(name)) { state.Destination = "skip"; continue; }
            if (name is "fonttbl" or "colortbl") { state.Destination = name; continue; }
            if (state.Destination == "fonttbl") { if (name == "f") fontIndex = parameter; continue; }
            if (state.Destination == "colortbl")
            {
                switch (name) { case "red": red = parameter; break; case "green": green = parameter; break; case "blue": blue = parameter; break; }
                continue;
            }

            if (name is not ("u" or "uc")) FlushBytes();
            switch (name)
            {
                // document
                case "ansicpg": try { codePage = Encoding.GetEncoding(parameter); } catch (Exception) { } break;
                case "deff": defaultFont = parameter; break;
                case "paperw": paperWidth = parameter; break;
                case "paperh": paperHeight = parameter; break;
                case "margl": left = parameter; break;
                case "margr": right = parameter; break;
                case "margt": top = parameter; break;
                case "margb": bottom = parameter; break;

                // characters
                case "u":
                    FlushBytes();
                    Append(((char)(ushort)(short)parameter).ToString());
                    skipChars = state.UnicodeSkip;
                    break;
                case "uc": state.UnicodeSkip = parameter; break;
                case "plain": FlushText(); state.Run = new RunProps { Size = 12, Font = fonts.GetValueOrDefault(defaultFont, "Times New Roman") }; state.Hidden = false; break;
                case "b": FlushText(); state.Run.Bold = on; break;
                case "i": FlushText(); state.Run.Italic = on; break;
                case "ul": FlushText(); state.Run.Underline = on; break;
                case "ulnone": FlushText(); state.Run.Underline = false; break;
                case "fs": FlushText(); state.Run.Size = parameter / 2f; break;
                case "f": FlushText(); if (fonts.TryGetValue(parameter, out var family) && family.Length > 0) state.Run.Font = family; break;
                case "cf": FlushText(); state.Run.Color = parameter > 0 && parameter < colors.Count && colors[parameter] is { } color ? color : SKColors.Black; break;
                case "v": FlushText(); state.Hidden = on; break;
                case "tab": FlushText(); if (state.Destination.Length == 0) paragraph.Add(new TabInline { Props = state.Run.Clone() }); break;
                case "line": FlushText(); if (state.Destination.Length == 0) paragraph.Add(new BreakInline { Props = state.Run.Clone() }); break;
                case "page": FlushText(); if (state.Destination.Length == 0) paragraph.Add(new BreakInline { Props = state.Run.Clone(), Page = true }); break;
                case "emdash": Append("—"); break;
                case "endash": Append("–"); break;
                case "lquote": Append("‘"); break;
                case "rquote": Append("’"); break;
                case "ldblquote": Append("“"); break;
                case "rdblquote": Append("”"); break;
                case "bullet": Append("•"); break;

                // paragraphs
                case "par": if (state.Destination.Length == 0) EndParagraph(); break;
                case "pard": state.Para = new ParaProps(); state.InTable = false; break;
                case "qc": state.Para.Align = Align.Center; break;
                case "qr": state.Para.Align = Align.Right; break;
                case "qj": state.Para.Align = Align.Justify; break;
                case "ql": state.Para.Align = Align.Left; break;
                case "li": state.Para.IndentLeft = parameter / 20f; break;
                case "ri": state.Para.IndentRight = parameter / 20f; break;
                case "fi": state.Para.FirstLine = parameter / 20f; break;
                case "sb": state.Para.SpaceBefore = parameter / 20f; break;
                case "sa": state.Para.SpaceAfter = parameter / 20f; break;
                case "sbauto": state.Para.AutoBefore = on; break;
                case "saauto": state.Para.AutoAfter = on; break;
                case "pagebb": state.Para.PageBreakBefore = on; break;
                case "sl":
                    if (parameter == 0) { state.Para.Line = 240; state.Para.LineRule = "auto"; }
                    else if (parameter < 0) { state.Para.Line = -parameter; state.Para.LineRule = "exact"; }
                    else { state.Para.Line = parameter; state.Para.LineRule = "atLeast"; }
                    break;
                case "slmult": if (parameter == 1 && state.Para.Line > 0) state.Para.LineRule = "auto"; break;

                // tables
                case "intbl": state.InTable = true; break;
                case "trowd": edges.Clear(); rowBorders = false; break;
                case "cellx": edges.Add(parameter / 20f); break;
                case "brdrs" or "brdrdb" or "brdrth" or "brdrhair" or "brdrdot" or "brdrdash": rowBorders = true; break;
                case "cell":
                    if (state.Destination.Length > 0) break;
                    state.InTable = true;
                    EndParagraph();
                    cells.Add(new Cell { Blocks = [.. cellBlocks] });
                    cellBlocks.Clear();
                    break;
                case "row":
                    if (state.Destination.Length > 0 || cells.Count == 0) break;
                    var table = new Table();
                    if (rowBorders) table.Borders = new Borders { Top = true, Left = true, Bottom = true, Right = true, InsideH = true, InsideV = true };
                    var row = new Row();
                    row.Cells.AddRange(cells);
                    table.Rows.Add(row);
                    table.Grid = new float[cells.Count];
                    if (edges.Count >= cells.Count)
                    {
                        float previous = 0;
                        for (var k = 0; k < cells.Count; k++) { table.Grid[k] = Math.Max(0, edges[k] - previous); previous = edges[k]; }
                    }
                    blocks.Add(table);
                    cells.Clear();
                    cellBlocks.Clear();
                    break;
            }
        }

        FlushText();
        if (paragraph.Count > 0) EndParagraph();
        FlushOrphanCells();
        return new StaticSource(new PageSetup(paperWidth / 20, paperHeight / 20, top / 20, right / 20, bottom / 20, left / 20), blocks);
    }
}
