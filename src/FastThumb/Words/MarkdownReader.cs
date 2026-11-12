using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using FastThumb.Layout;
using SkiaSharp;

namespace FastThumb.Words;

/// <summary>
/// Markdown: CommonMark and the GitHub (GFM) extensions, simplified for a thumbnail. Blocks follow the spec's two-phase
/// strategy (headings, setext headings, rules, block quotes, nested lists, task items, fenced and indented code, tables,
/// HTML blocks); inline emphasis uses the spec's delimiter algorithm (<c>*</c>, <c>_</c>, <c>~~</c>). Links and
/// autolinks are drawn as link-coloured text, images as their alt text, HTML tags are removed and entities decoded.
/// No dependency beyond the BCL; every scan is linear, and top-level blocks are produced lazily so only page 1 is parsed.
/// </summary>
internal static partial class MarkdownReader
{
    private const string Sans = "Arial", Mono = "Courier New";
    private const int MaxNesting = 32;                     // block quotes / lists deeper than this are read as text
    private const float BodySize = 11f, ListIndent = 20f;
    private static readonly SKColor Ink = new(0xFF1F2328), Muted = new(0xFF59636E), Link = new(0xFF0969DA),
        Shade = new(0xFFF6F8FA), Rule = new(0xFFD1D9E0);

    public static IPageSource Read(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\t", "    ").Split('\n');
        var refs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines)
            if (RefDef().Match(line) is { Success: true } m) refs.Add(Label(m.Groups[1].Value));
        return new LazySource(new BlockParser(refs).Parse(lines, 0));
    }

    private sealed class LazySource(IEnumerable<Block> blocks) : IPageSource
    {
        public PageSetup Page => PageSetup.Default;
        public IEnumerable<Block> Blocks() => blocks;
        public SKImage? LoadImage(string id) => null;
    }

    // ---- blocks ---------------------------------------------------------------------------------

    private sealed class BlockParser(HashSet<string> refs)
    {
        private static readonly RunProps Body = new() { Font = Sans, Size = BodySize, Color = Ink };

        public IEnumerable<Block> Parse(IReadOnlyList<string> lines, int depth)
        {
            Guard.Deeper();
            var paragraph = new List<string>();
            string? listKey = null;                        // marker kind of the list being continued ("-", "1." ...)
            var listNumber = 0;

            for (var i = 0; i < lines.Count;)
            {
                var line = lines[i];
                var indent = Indent(line);
                var t = line.TrimStart(' ');

                if (t.Length == 0)
                {
                    foreach (var b in Flush(paragraph)) yield return b;
                    i++;
                    continue;
                }

                if (paragraph.Count > 0 && indent < 4 && Setext().Match(t) is { Success: true } s)
                {
                    yield return Heading(string.Join('\n', paragraph), s.Groups[1].Value[0] == '=' ? 1 : 2);
                    paragraph.Clear();
                    i++;
                    continue;
                }

                // a continuation (or lazy) line of the open paragraph
                if (paragraph.Count > 0 && (indent >= 4 || !StartsBlock(t, interrupting: true)) && TableStart(lines, i) == null)
                {
                    paragraph.Add(t);
                    i++;
                    continue;
                }
                foreach (var b in Flush(paragraph)) yield return b;
                if (!IsListItem(t, out _, out _, out _)) listKey = null;

                if (indent >= 4)                           // indented code
                {
                    var code = new List<string>();
                    for (; i < lines.Count && (Indent(lines[i]) >= 4 || lines[i].Trim().Length == 0); i++)
                        code.Add(lines[i].Length >= 4 ? lines[i][4..] : "");
                    while (code.Count > 0 && code[^1].Trim().Length == 0) code.RemoveAt(code.Count - 1);
                    yield return CodeBlock(code);
                    yield return Spacer();
                    continue;
                }

                if (Fence().Match(t) is { Success: true } fence)
                {
                    var marker = fence.Groups[1].Value;
                    var code = new List<string>();
                    i++;
                    while (i < lines.Count)
                    {
                        var l = lines[i++];
                        var lt = l.TrimStart(' ');
                        if (Indent(l) < 4 && lt.StartsWith(marker, StringComparison.Ordinal) && lt.TrimEnd().All(c => c == marker[0])) break;
                        code.Add(StripIndent(l, indent));
                    }
                    yield return CodeBlock(code);
                    yield return Spacer();
                    continue;
                }

                if (Atx().Match(t) is { Success: true } atx)
                {
                    var content = ClosingHashes().Replace(atx.Groups[2].Value.Trim(), "").Trim();
                    yield return Heading(content, atx.Groups[1].Length);
                    i++;
                    continue;
                }

                if (ThematicBreak().IsMatch(t))
                {
                    yield return new Paragraph
                    {
                        Props = new ParaProps { SpaceBefore = 6, SpaceAfter = 12, BorderBottom = true, BorderColor = Rule },
                        MarkProps = new RunProps { Size = 2 },
                    };
                    i++;
                    continue;
                }

                if (t[0] == '>' && depth < MaxNesting)
                {
                    var quoted = new List<string>();
                    while (i < lines.Count)
                    {
                        var l = lines[i];
                        var lt = l.TrimStart(' ');
                        if (Indent(l) < 4 && lt.StartsWith('>')) quoted.Add(lt.Length > 1 && lt[1] == ' ' ? lt[2..] : lt[1..]);
                        else if (lt.Length > 0 && quoted.Count > 0 && quoted[^1].Trim().Length > 0 && !StartsBlock(lt, interrupting: true)) quoted.Add(lt);
                        else break;
                        i++;
                    }
                    var blocks = Parse(quoted, depth + 1).ToList();
                    Shift(blocks, 14, recolor: Muted);
                    foreach (var b in blocks) yield return b;
                    continue;
                }

                if (depth < MaxNesting && IsListItem(t, out var key, out var number, out var markerWidth))
                {
                    var contentIndent = indent + markerWidth;
                    var item = new List<string> { t.Length > markerWidth ? t[markerWidth..] : "" };
                    i++;
                    var blank = false;
                    while (i < lines.Count)
                    {
                        var l = lines[i];
                        if (l.Trim().Length == 0) { item.Add(""); blank = true; i++; continue; }
                        if (Indent(l) >= contentIndent) { item.Add(l[contentIndent..]); blank = false; i++; continue; }
                        var lt = l.TrimStart(' ');
                        if (!blank && !StartsBlock(lt, interrupting: false)) { item.Add(lt); i++; continue; }  // lazy line; any marker starts a sibling
                        break;
                    }
                    var loose = false;
                    while (item.Count > 1 && item[^1].Length == 0) { item.RemoveAt(item.Count - 1); loose = true; }
                    loose |= item.Contains("");

                    if (key != listKey) { listKey = key; listNumber = number; } else listNumber++;
                    foreach (var b in ListItem(item, key, listNumber, loose, depth)) yield return b;
                    continue;
                }

                if (TableStart(lines, i) is { } columns)
                {
                    i += 2;
                    var rows = new List<List<string>>();
                    while (i < lines.Count && lines[i].Trim().Length > 0 && !StartsBlock(lines[i].TrimStart(' '), interrupting: true))
                        rows.Add(SplitRow(lines[i++]));
                    yield return BuildTable(columns, rows);
                    yield return Spacer();
                    continue;
                }

                if (HtmlBlock().Match(t) is { Success: true } html)
                {
                    var tag = html.Groups[1].Value.ToLowerInvariant();
                    var collected = new List<string>();
                    string? end = tag == "!--" ? "-->" : tag is "script" or "style" or "pre" or "textarea" ? "</" + tag : null;
                    while (i < lines.Count)
                    {
                        var l = lines[i++];
                        if (end == null && l.Trim().Length == 0) break;
                        collected.Add(l);
                        if (end != null && l.Contains(end, StringComparison.OrdinalIgnoreCase)) break;
                    }
                    if (tag is "!--" or "script" or "style") continue;
                    var centered = AlignCenter().IsMatch(collected[0]);
                    foreach (var b in Flush(collected.Select(l => l.Trim()).ToList(), centered ? Align.Center : Align.Left)) yield return b;
                    continue;
                }

                if (RefDef().IsMatch(line)) { i++; continue; }

                paragraph.Add(t);
                i++;
            }
            foreach (var b in Flush(paragraph)) yield return b;
        }

        private IEnumerable<Block> Flush(List<string> lines, Align align = Align.Left)
        {
            if (lines.Count == 0) yield break;
            var p = new Paragraph { Props = new ParaProps { SpaceAfter = 9, Line = 276, Align = align }, MarkProps = Body };
            new InlineParser(refs).Parse(string.Join('\n', lines).Trim(), Body, p.Inlines);
            lines.Clear();
            if (p.Inlines.Any(i => i is not BreakInline && (i is not TextInline t || !string.IsNullOrWhiteSpace(t.Text)))) yield return p;
        }

        private Paragraph Heading(string text, int level)
        {
            var run = new RunProps
            {
                Font = Sans, Bold = true, Color = level == 6 ? Muted : Ink,
                Size = level switch { 1 => 21, 2 => 16.5f, 3 => 13.5f, 4 => 11.5f, 5 => 10.5f, _ => 10 },
            };
            var p = new Paragraph
            {
                Props = new ParaProps { SpaceBefore = 14, SpaceAfter = level <= 2 ? 10 : 6, BorderBottom = level <= 2, BorderColor = Rule },
                MarkProps = run,
            };
            new InlineParser(refs).Parse(text.Trim(), run, p.Inlines);
            return p;
        }

        /// <summary>Tables carry no spacing of their own: this gap follows code blocks and tables.</summary>
        private static Paragraph Spacer() => new() { Props = new ParaProps { SpaceAfter = 9 }, MarkProps = new RunProps { Size = 1 } };

        private static Table CodeBlock(List<string> code)
        {
            var run = new RunProps { Font = Mono, Size = 9.5f, Color = Ink };
            var cell = new Cell { Fill = Shade };
            foreach (var line in code)
            {
                var p = new Paragraph { Props = new ParaProps(), MarkProps = run };
                if (line.Length > 0) p.Inlines.Add(new TextInline { Props = run, Text = line });
                cell.Blocks.Add(p);
            }
            if (cell.Blocks.Count == 0) cell.Blocks.Add(new Paragraph { Props = new ParaProps(), MarkProps = run });
            var table = new Table { Grid = [0], PadLeft = 9, PadRight = 9, PadTop = 7, PadBottom = 7 };
            table.Rows.Add(new Row { Cells = { cell } });
            return table;
        }

        private IEnumerable<Block> ListItem(List<string> lines, string key, int number, bool loose, int depth)
        {
            var task = TaskBox().Match(lines[0]);
            if (task.Success) lines[0] = lines[0][task.Length..];
            var blocks = Parse(lines, depth + 1).ToList();
            if (blocks.Count == 0 || blocks[0] is not Paragraph { Props.Bullet: null } first)
            {
                first = new Paragraph { Props = new ParaProps(), MarkProps = Body };
                first.Inlines.Add(new TextInline { Props = Body, Text = " " });
                blocks.Insert(0, first);
            }
            if (!loose)
                foreach (var p in blocks.OfType<Paragraph>()) p.Props.SpaceAfter = Math.Min(p.Props.SpaceAfter, 3);
            Shift(blocks, ListIndent, recolor: null);
            first.Props.FirstLine = -ListIndent;
            first.Props.Bullet = task.Success
                ? (task.Groups[1].Value == " " ? "□" : "■")   // WGL4 glyphs: present in Arial and Liberation Sans
                : key.Length == 1 ? (depth % 2 == 0 ? "•" : "◦") : number + key[^1..];
            first.Props.BulletProps = Body;
            return blocks;
        }

        private Table BuildTable(List<(string Text, Align Align)> header, List<List<string>> rows)
        {
            var table = new Table
            {
                Grid = new float[header.Count],
                Borders = new Borders { Top = true, Left = true, Bottom = true, Right = true, InsideH = true, InsideV = true },
                PadTop = 3, PadBottom = 3,
            };
            var bold = Body.Clone();
            bold.Bold = true;
            table.Rows.Add(Row(header.Select(h => h.Text).ToList(), bold, Shade));
            foreach (var row in rows) table.Rows.Add(Row(row, Body, null));
            return table;

            Row Row(List<string> cells, RunProps run, SKColor? fill)
            {
                var result = new Row();
                for (var c = 0; c < header.Count; c++)
                {
                    var p = new Paragraph { Props = new ParaProps { Align = header[c].Align }, MarkProps = run };
                    if (c < cells.Count) new InlineParser(refs).Parse(cells[c], run, p.Inlines);
                    result.Cells.Add(new Cell { Fill = fill, Blocks = { p } });
                }
                return result;
            }
        }

        /// <summary>The header columns if lines[i] is a table header followed by a matching delimiter row.</summary>
        private static List<(string, Align)>? TableStart(IReadOnlyList<string> lines, int i)
        {
            if (i + 1 >= lines.Count || !lines[i].Contains('|') || !DelimiterRow().IsMatch(lines[i + 1])) return null;
            var header = SplitRow(lines[i]);
            var delimiters = SplitRow(lines[i + 1]);
            if (header.Count != delimiters.Count) return null;
            return header.Zip(delimiters, (h, d) => (h, d.StartsWith(':') && d.EndsWith(':') ? Align.Center : d.EndsWith(':') ? Align.Right : Align.Left)).ToList();
        }

        private static List<string> SplitRow(string line)
        {
            var t = line.Trim();
            if (t.StartsWith('|')) t = t[1..];
            if (t.EndsWith('|') && !t.EndsWith("\\|", StringComparison.Ordinal)) t = t[..^1];
            var cells = new List<string>();
            var cell = new StringBuilder();
            var code = false;
            for (var k = 0; k < t.Length; k++)
            {
                var c = t[k];
                if (c == '\\' && k + 1 < t.Length && t[k + 1] == '|') { cell.Append('|'); k++; }
                else if (c == '`') { code = !code; cell.Append(c); }
                else if (c == '|' && !code) { cells.Add(cell.ToString().Trim()); cell.Clear(); }
                else cell.Append(c);
            }
            cells.Add(cell.ToString().Trim());
            return cells;
        }

        /// <summary>Indents blocks (quote and list content) and optionally greys their text.</summary>
        private static void Shift(List<Block> blocks, float amount, SKColor? recolor)
        {
            foreach (var block in blocks)
            {
                if (block is Table table) { table.Indent += amount; continue; }
                if (block is not Paragraph p) continue;
                p.Props.IndentLeft += amount;
                if (recolor is not { } color) continue;
                foreach (var inline in p.Inlines)
                    if (inline.Props.Color == Ink) { inline.Props = inline.Props.Clone(); inline.Props.Color = color; }
            }
        }
    }

    // ---- block helpers --------------------------------------------------------------------------

    private static int Indent(string line)
    {
        var n = 0;
        while (n < line.Length && line[n] == ' ') n++;
        return n;
    }

    private static string StripIndent(string line, int count)
    {
        var n = Math.Min(count, Indent(line));
        return line[n..];
    }

    /// <summary>Whether a (left-trimmed) line opens a block that may interrupt a paragraph.</summary>
    private static bool StartsBlock(string t, bool interrupting)
    {
        if (t.Length == 0) return false;
        if (t[0] == '>' || Atx().IsMatch(t) || Fence().IsMatch(t) || ThematicBreak().IsMatch(t)) return true;
        if (t[0] == '<' && HtmlBlock().IsMatch(t)) return true;
        if (!IsListItem(t, out var key, out var number, out var width) || width >= t.Length) return false;
        return !interrupting || key.Length == 1 || number == 1;  // an ordered list interrupts a paragraph only at 1
    }

    private static bool IsListItem(string t, out string key, out int number, out int width)
    {
        key = ""; number = 0; width = 0;
        var m = ListMarker().Match(t);
        if (!m.Success) return false;
        var marker = m.Groups[1].Value;
        var spaces = m.Groups[2].Length;
        if (marker.Length > 1) { number = int.Parse(marker[..^1]); key = "1" + marker[^1]; } else key = marker;
        width = marker.Length + (spaces is 0 or > 4 ? 1 : spaces);
        return true;
    }

    private static string Label(string label) => Regex.Replace(label.Trim(), @"\s+", " ").ToUpperInvariant();

    [GeneratedRegex(@"^ {0,3}\[((?:[^\]\\]|\\.){1,999})\]:\s*\S")] private static partial Regex RefDef();
    [GeneratedRegex(@"^(#{1,6})(?:[ ]+(.*))?$")] private static partial Regex Atx();
    [GeneratedRegex(@"(?:^|[ ]+)#+[ ]*$")] private static partial Regex ClosingHashes();
    [GeneratedRegex(@"^(=+|-+)[ ]*$")] private static partial Regex Setext();
    [GeneratedRegex(@"^(?:(?:\*[ ]*){3,}|(?:-[ ]*){3,}|(?:_[ ]*){3,})$")] private static partial Regex ThematicBreak();
    [GeneratedRegex(@"^(`{3,}(?=[^`]*$)|~{3,})")] private static partial Regex Fence();
    [GeneratedRegex(@"^([-+*]|\d{1,9}[.)])(?:( +)|$)")] private static partial Regex ListMarker();
    [GeneratedRegex(@"^\[([ xX])\](?:[ ]+|$)")] private static partial Regex TaskBox();
    [GeneratedRegex(@"^\|?[ ]*:?-+:?[ ]*(?:\|[ ]*:?-+:?[ ]*)*\|?[ ]*$")] private static partial Regex DelimiterRow();
    [GeneratedRegex(@"^<(!--|/?[a-zA-Z][a-zA-Z0-9-]*)(?=[\s/>]|$)")] private static partial Regex HtmlBlock();
    [GeneratedRegex(@"align\s*=\s*[""']?center", RegexOptions.IgnoreCase)] private static partial Regex AlignCenter();

    // ---- inlines --------------------------------------------------------------------------------

    [Flags]
    private enum Style { None = 0, Bold = 1, Italic = 2, Strike = 4, Code = 8, Link = 16, Image = 32 }

    private sealed class Token
    {
        public string Text = "";
        public char Delim;                                 // '*', '_' or '~' for a delimiter run, else 0
        public int Count, Original;
        public bool Open, Close, Break;
        public Style Style;
    }

    private sealed class InlineParser(HashSet<string> refs)
    {
        public void Parse(string text, RunProps baseRun, List<Inline> output)
        {
            var tokens = Tokens(text, Style.None);
            var cache = new Dictionary<Style, RunProps>();
            var pending = new StringBuilder();
            var pendingStyle = Style.None;

            foreach (var token in tokens)
            {
                if (token.Break)
                {
                    Emit();
                    output.Add(new BreakInline { Props = baseRun });
                    continue;
                }
                var value = token.Delim != 0 ? new string(token.Delim, token.Count) : token.Text;
                if (value.Length == 0) continue;
                if (token.Style != pendingStyle) Emit();
                pendingStyle = token.Style;
                pending.Append(value);
            }
            Emit();

            void Emit()
            {
                if (pending.Length == 0) return;
                output.Add(new TextInline { Props = Props(pendingStyle), Text = pending.ToString() });
                pending.Clear();
            }

            RunProps Props(Style style)
            {
                if (style == Style.None) return baseRun;
                if (cache.TryGetValue(style, out var cached)) return cached;
                var run = baseRun.Clone();
                if (style.HasFlag(Style.Bold)) run.Bold = true;
                if (style.HasFlag(Style.Italic) || style.HasFlag(Style.Image)) run.Italic = true;
                if (style.HasFlag(Style.Strike)) run.Strike = true;
                if (style.HasFlag(Style.Code)) { run.Font = Mono; run.Size = baseRun.Size * 0.92f; }
                if (style.HasFlag(Style.Image)) run.Color = Muted;
                if (style.HasFlag(Style.Link)) run.Color = Link;
                return cache[style] = run;
            }
        }

        /// <summary>Scans text into tokens and resolves emphasis; links recurse on their (shorter) text.</summary>
        private List<Token> Tokens(string text, Style style)
        {
            Guard.Deeper();
            var tokens = new List<Token>();
            var buffer = new StringBuilder();
            var brackets = Matching(text, '[', ']');
            var parens = Matching(text, '(', ')');
            var backtickRuns = new Dictionary<int, Queue<int>>();
            for (var k = 0; k < text.Length;)
            {
                if (text[k] != '`') { k++; continue; }
                var start = k;
                while (k < text.Length && text[k] == '`') k++;
                if (start > 0 && text[start - 1] == '\\') continue;
                if (!backtickRuns.TryGetValue(k - start, out var queue)) backtickRuns[k - start] = queue = new Queue<int>();
                queue.Enqueue(start);
            }

            void Literal() { if (buffer.Length > 0) tokens.Add(new Token { Text = buffer.ToString(), Style = style }); buffer.Clear(); }
            void Add(Token token) { Literal(); tokens.Add(token); }

            var i = 0;
            while (i < text.Length)
            {
                var c = text[i];
                switch (c)
                {
                    case '\\' when i + 1 < text.Length && text[i + 1] == '\n':
                        Add(new Token { Break = true });
                        i += 2;
                        continue;
                    case '\\' when i + 1 < text.Length && text[i + 1] < 128 && IsPunctuation(text[i + 1]):
                        buffer.Append(text[i + 1]);
                        i += 2;
                        continue;

                    case '`':
                    {
                        var n = 0;
                        while (i + n < text.Length && text[i + n] == '`') n++;
                        var close = -1;
                        if (backtickRuns.TryGetValue(n, out var queue))
                        {
                            while (queue.Count > 0 && queue.Peek() <= i) queue.Dequeue();
                            if (queue.Count > 0) close = queue.Dequeue();
                        }
                        if (close < 0) { buffer.Append('`', n); i += n; continue; }
                        var code = text[(i + n)..close].Replace('\n', ' ');
                        if (code.Length > 1 && code[0] == ' ' && code[^1] == ' ' && code.Trim().Length > 0) code = code[1..^1];
                        Add(new Token { Text = code, Style = style | Style.Code });
                        i = close + n;
                        continue;
                    }

                    case '*' or '_' or '~':
                    {
                        var n = 0;
                        while (i + n < text.Length && text[i + n] == c) n++;
                        var before = i == 0 ? ' ' : text[i - 1];
                        var after = i + n >= text.Length ? ' ' : text[i + n];
                        var left = !char.IsWhiteSpace(after) && (!IsPunctuation(after) || char.IsWhiteSpace(before) || IsPunctuation(before));
                        var right = !char.IsWhiteSpace(before) && (!IsPunctuation(before) || char.IsWhiteSpace(after) || IsPunctuation(after));
                        var token = new Token { Delim = c, Count = n, Original = n, Style = style };
                        if (c == '_') { token.Open = left && (!right || IsPunctuation(before)); token.Close = right && (!left || IsPunctuation(after)); }
                        else { token.Open = left; token.Close = right; }
                        if (c == '~' && n > 2) token.Open = token.Close = false;
                        Add(token);
                        i += n;
                        continue;
                    }

                    case '!' when i + 1 < text.Length && text[i + 1] == '[' && TryLink(i + 1, image: true):
                    case '[' when TryLink(i, image: false):
                        continue;

                    case '<':
                    {
                        var auto = Autolink().Match(text, i);
                        if (auto.Success && auto.Index == i)
                        {
                            Add(new Token { Text = auto.Groups[1].Value, Style = style | Style.Link });
                            i += auto.Length;
                            continue;
                        }
                        var tag = InlineTag().Match(text, i);
                        if (tag.Success && tag.Index == i)
                        {
                            if (tag.Groups[1].Value.Equals("br", StringComparison.OrdinalIgnoreCase)) Add(new Token { Break = true });
                            i += tag.Length;
                            continue;
                        }
                        break;
                    }

                    case '&':
                    {
                        var entity = Entity().Match(text, i);
                        if (entity.Success && entity.Index == i)
                        {
                            buffer.Append(WebUtility.HtmlDecode(entity.Value));
                            i += entity.Length;
                            continue;
                        }
                        break;
                    }

                    case 'h' or 'w' when (i == 0 || char.IsWhiteSpace(text[i - 1]) || text[i - 1] is '(' or '*' or '_' or '~'):
                    {
                        var bare = BareLink().Match(text, i);
                        if (bare.Success && bare.Index == i)
                        {
                            var url = bare.Value.TrimEnd('?', '!', '.', ',', ':', '*', '_', '~', '\'', '"');
                            while (url.EndsWith(')') && url.Count(ch => ch == ')') > url.Count(ch => ch == '(')) url = url[..^1];
                            Add(new Token { Text = url, Style = style | Style.Link });
                            i += url.Length;
                            continue;
                        }
                        break;
                    }

                    case '\n':
                    {
                        var spaces = 0;
                        while (buffer.Length > 0 && buffer[^1] == ' ') { buffer.Length--; spaces++; }
                        if (spaces >= 2) Add(new Token { Break = true }); else buffer.Append(' ');
                        i++;
                        while (i < text.Length && text[i] == ' ') i++;
                        continue;
                    }
                }
                buffer.Append(c);
                i++;
            }
            Literal();
            Emphasis(tokens);
            return tokens;

            // [text](dest "title"), [text][label], [text][], [text]; with image: ![alt](...)
            bool TryLink(int open, bool image)
            {
                var close = brackets.GetValueOrDefault(open, -1);
                if (close < 0) return false;
                var inner = text[(open + 1)..close];
                var end = -1;
                if (close + 1 < text.Length && text[close + 1] == '(' && parens.GetValueOrDefault(close + 1, -1) is var p and >= 0) end = p + 1;
                else if (close + 1 < text.Length && text[close + 1] == '[' && brackets.GetValueOrDefault(close + 1, -1) is var b and >= 0)
                {
                    var label = text[(close + 2)..b];
                    if (refs.Contains(Label(label.Length == 0 ? inner : label))) end = b + 1;
                }
                else if (refs.Contains(Label(inner))) end = close + 1;
                if (end < 0) return false;

                Literal();
                var flags = style | (image ? Style.Image : Style.Link);
                foreach (var t in Tokens(inner, flags))
                {
                    if (image && t.Delim != 0) continue;   // alt text is plain: emphasis markers are dropped
                    if (image && !t.Break) t.Style = flags;
                    t.Open = t.Close = false;              // resolved inside the link: never matched outside it
                    tokens.Add(t);
                }
                i = end;
                return true;
            }
        }

        /// <summary>The spec's "process emphasis" over the delimiter runs, with an openers-bottom per character.</summary>
        private static void Emphasis(List<Token> tokens)
        {
            var bottom = new Dictionary<char, int>();
            for (var c = 0; c < tokens.Count; c++)
            {
                var closer = tokens[c];
                if (closer.Delim == 0 || !closer.Close || closer.Count == 0) continue;
                var found = -1;
                for (var o = c - 1; o >= bottom.GetValueOrDefault(closer.Delim, 0); o--)
                {
                    var opener = tokens[o];
                    if (opener.Delim != closer.Delim || !opener.Open || opener.Count == 0) continue;
                    if (closer.Delim == '~' ? opener.Count != closer.Count
                        : (opener.Close || closer.Open) && (opener.Original + closer.Original) % 3 == 0 && (opener.Original % 3 != 0 || closer.Original % 3 != 0))
                        continue;
                    found = o;
                    break;
                }
                if (found < 0)
                {
                    bottom[closer.Delim] = c;
                    if (!closer.Open) closer.Close = false;
                    continue;
                }

                var start = tokens[found];
                var use = closer.Delim == '~' ? closer.Count : start.Count >= 2 && closer.Count >= 2 ? 2 : 1;
                var flag = closer.Delim == '~' ? Style.Strike : use == 2 ? Style.Bold : Style.Italic;
                for (var k = found + 1; k < c; k++)
                {
                    tokens[k].Style |= flag;
                    if (tokens[k].Delim != 0) tokens[k].Open = tokens[k].Close = false;
                }
                start.Count -= use;
                closer.Count -= use;
                if (closer.Count > 0) c--;                 // the rest of this closer may close an earlier opener
            }
        }

        /// <summary>Position of the matching close for every open bracket (backslash-escaped ones are skipped); O(n).</summary>
        private static Dictionary<int, int> Matching(string text, char open, char close)
        {
            var result = new Dictionary<int, int>();
            var stack = new Stack<int>();
            for (var k = 0; k < text.Length; k++)
            {
                if (text[k] == '\\') { k++; continue; }
                if (text[k] == open) stack.Push(k);
                else if (text[k] == close && stack.Count > 0) result[stack.Pop()] = k;
            }
            return result;
        }

        private static bool IsPunctuation(char c) => char.IsPunctuation(c) || char.IsSymbol(c);
    }

    [GeneratedRegex(@"\G<([a-zA-Z][a-zA-Z0-9+.\-]{1,31}:[^\s<>]*|[a-zA-Z0-9.!#$%&'*+/=?^_`{|}~\-]+@[a-zA-Z0-9](?:[a-zA-Z0-9\-]{0,61}[a-zA-Z0-9])?(?:\.[a-zA-Z0-9](?:[a-zA-Z0-9\-]{0,61}[a-zA-Z0-9])?)*)>")]
    private static partial Regex Autolink();
    [GeneratedRegex(@"\G(?:<!--[\s\S]{0,4096}?-->|</?([a-zA-Z][a-zA-Z0-9-]*)(?:\s[^<>]*)?/?>)")]   // bounded: linear on unclosed tags
    private static partial Regex InlineTag();
    [GeneratedRegex(@"\G&(?:#[0-9]{1,7}|#[xX][0-9a-fA-F]{1,6}|[a-zA-Z][a-zA-Z0-9]{1,31});")] private static partial Regex Entity();
    [GeneratedRegex(@"\G(?:https?://|www\.)[^\s<]+")] private static partial Regex BareLink();
}
