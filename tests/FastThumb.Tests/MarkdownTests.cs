using System.Text;
using FastThumb.Layout;
using FastThumb.Tests.Fixtures;
using FastThumb.Words;

namespace FastThumb.Tests;

/// <summary>The Markdown reader (CommonMark + GFM, simplified): the block and run model it hands to the layout engine.</summary>
public class MarkdownTests
{
    /// <summary>The blocks, without the empty spacers that follow code blocks and tables.</summary>
    private static List<Block> Blocks(string markdown) => MarkdownReader.Read(markdown.ReplaceLineEndings("\n")).Blocks()
        .Where(b => b is not Paragraph { Inlines.Count: 0, Props.BorderBottom: false }).ToList();

    private static Paragraph Single(string markdown) => Assert.IsType<Paragraph>(Assert.Single(Blocks(markdown)));

    private static string Plain(Paragraph p) =>
        string.Concat(p.Inlines.Select(i => i switch { TextInline t => t.Text, BreakInline => "\n", _ => "" }));

    private static TextInline Run(Paragraph p, string text) =>
        p.Inlines.OfType<TextInline>().FirstOrDefault(t => t.Text == text)
        ?? throw new Xunit.Sdk.XunitException($"No run \"{text}\" in [{string.Join(" | ", p.Inlines.OfType<TextInline>().Select(t => t.Text))}]");

    // ---- blocks ---------------------------------------------------------------------------------

    [Fact]
    public void Atx_and_setext_headings()
    {
        var blocks = Blocks("# One #\n\nTwo\n===\n\nThree\n---\n\n###### six");
        var headings = blocks.Cast<Paragraph>().ToList();
        Assert.Equal(["One", "Two", "Three", "six"], headings.Select(Plain));
        Assert.All(headings, h => Assert.True(h.MarkProps.Bold));
        Assert.True(headings[0].MarkProps.Size > headings[2].MarkProps.Size);
        Assert.True(headings[1].Props.BorderBottom);
    }

    [Fact]
    public void Not_headings()
    {
        Assert.Equal("#hashtag", Plain(Single("#hashtag")));
        Assert.Equal("####### seven", Plain(Single("####### seven")));
    }

    [Fact]
    public void Thematic_break_is_a_rule_and_needs_no_paragraph_above()
    {
        var blocks = Blocks("a\n\n* * *\n\nb");
        Assert.Equal(3, blocks.Count);
        Assert.True(((Paragraph)blocks[1]).Props.BorderBottom);
    }

    [Fact]
    public void Paragraph_lines_join_with_soft_and_hard_breaks()
    {
        Assert.Equal("one two\nthree\nfour", Plain(Single("one\ntwo  \nthree\\\nfour")));
    }

    [Fact]
    public void Fenced_and_indented_code_are_shaded_boxes_kept_verbatim()
    {
        var blocks = Blocks("```cs\nvar x = **1**;\n\n  y\n```\n\n    indented *code*\n\ntext");
        Assert.Equal(3, blocks.Count);
        var fenced = Assert.IsType<Table>(blocks[0]);
        var cell = Assert.Single(Assert.Single(fenced.Rows).Cells);
        Assert.NotNull(cell.Fill);
        Assert.Equal(["var x = **1**;", "", "  y"], cell.Blocks.Cast<Paragraph>().Select(Plain));
        Assert.Equal("Courier New", ((Paragraph)cell.Blocks[0]).MarkProps.Font);
        var indented = Assert.IsType<Table>(blocks[1]);
        Assert.Equal("indented *code*", Plain((Paragraph)indented.Rows[0].Cells[0].Blocks[0]));
    }

    [Fact]
    public void Block_quote_is_indented_and_muted_with_lazy_lines()
    {
        var blocks = Blocks("> # Title\n> quoted **text**\nlazy line\n\nafter");
        Assert.Equal(3, blocks.Count);
        var quoted = (Paragraph)blocks[1];
        Assert.Equal("quoted text lazy line", Plain(quoted));
        Assert.True(quoted.Props.IndentLeft > 0);
        Assert.NotEqual(Run(quoted, "quoted ").Props.Color, ((Paragraph)blocks[2]).Inlines.OfType<TextInline>().First().Props.Color);
        Assert.Equal(0, ((Paragraph)blocks[2]).Props.IndentLeft);
    }

    [Fact]
    public void Bullet_and_nested_lists()
    {
        var blocks = Blocks("- one\n- two\n  - nested\n* three").Cast<Paragraph>().ToList();
        Assert.Equal(["one", "two", "nested", "three"], blocks.Select(Plain));
        Assert.Equal("•", blocks[0].Props.Bullet);
        Assert.Equal("◦", blocks[2].Props.Bullet);
        Assert.True(blocks[2].Props.IndentLeft > blocks[1].Props.IndentLeft);
    }

    [Fact]
    public void Ordered_lists_count_from_their_start()
    {
        var blocks = Blocks("3. a\n4. b\n9. c\n\n1) x").Cast<Paragraph>().ToList();
        Assert.Equal(["3.", "4.", "5.", "1)"], blocks.Select(b => b.Props.Bullet));
    }

    [Fact]
    public void Task_items_get_boxes()
    {
        var blocks = Blocks("- [ ] todo\n- [x] done").Cast<Paragraph>().ToList();
        Assert.Equal(["todo", "done"], blocks.Select(Plain));
        Assert.NotEqual(blocks[0].Props.Bullet, blocks[1].Props.Bullet);
    }

    [Fact]
    public void List_item_with_code_and_continuation_paragraph()
    {
        var blocks = Blocks("1. first\n\n   ```\n   code\n   ```\n\n   more\n2. second");
        Assert.Equal(4, blocks.Count);
        Assert.IsType<Table>(blocks[1]);
        Assert.True(((Table)blocks[1]).Indent > 0);
        Assert.Equal("more", Plain((Paragraph)blocks[2]));
        Assert.Equal("2.", ((Paragraph)blocks[3]).Props.Bullet);
    }

    [Fact]
    public void Ordered_list_only_interrupts_a_paragraph_at_one()
    {
        Assert.Single(Blocks("The year\n2024. was good"));
        Assert.Equal(2, Blocks("Steps:\n1. go").Count);
        Assert.Equal("-not a list", Plain(Single("-not a list")));
    }

    [Fact]
    public void Gfm_table_with_alignment_header_and_escaped_pipe()
    {
        var table = Assert.IsType<Table>(Assert.Single(Blocks("| a | b | c |\n|:--|:-:|--:|\n| 1 | `x|y` | a \\| b |\n| only one |")));
        Assert.Equal(3, table.Grid.Length);
        Assert.Equal(3, table.Rows.Count);
        var header = table.Rows[0].Cells.Select(c => (Paragraph)c.Blocks[0]).ToList();
        Assert.True(header[0].MarkProps.Bold);
        Assert.Equal([Align.Left, Align.Center, Align.Right], header.Select(p => p.Props.Align));
        Assert.Equal(["1", "x|y", "a | b"], table.Rows[1].Cells.Select(c => Plain((Paragraph)c.Blocks[0])));
        Assert.Equal(["only one", "", ""], table.Rows[2].Cells.Select(c => Plain((Paragraph)c.Blocks[0])));
    }

    [Fact]
    public void Html_blocks_show_their_text_and_comments_vanish()
    {
        var blocks = Blocks("<!-- hidden\nstill hidden -->\n\n<p align=\"center\">\n  <b>Hello</b> <img src=\"x.png\">\n</p>\n\ntext");
        Assert.Equal(2, blocks.Count);
        var html = (Paragraph)blocks[0];
        Assert.Equal("Hello", Plain(html).Trim());
        Assert.Equal(Align.Center, html.Props.Align);
    }

    [Fact]
    public void Link_reference_definitions_are_not_shown()
    {
        var p = Single("See [the docs][docs] and [Docs].\n\n[docs]: https://example.com \"Title\"");
        Assert.Equal("See the docs and Docs.", Plain(p));
        Assert.NotEqual(Run(p, "See ").Props.Color, Run(p, "the docs").Props.Color);
    }

    // ---- inlines --------------------------------------------------------------------------------

    [Fact]
    public void Emphasis_strong_and_strikethrough()
    {
        var p = Single("**bold** *it* ***both*** ~~gone~~ _u_ snake_case_word");
        Assert.Equal("bold it both gone u snake_case_word", Plain(p));
        Assert.True(Run(p, "bold").Props.Bold);
        Assert.True(Run(p, "it").Props.Italic);
        Assert.True(Run(p, "both").Props is { Bold: true, Italic: true });
        Assert.True(Run(p, "gone").Props.Strike);
        Assert.True(Run(p, "u").Props.Italic);
        Assert.False(p.Inlines.OfType<TextInline>().Last().Props.Italic);
    }

    [Fact]
    public void Unmatched_and_spaced_delimiters_stay_literal()
    {
        Assert.Equal("a * b ** c", Plain(Single("a * b ** c")));
        Assert.Equal("*open", Plain(Single("*open")));
        Assert.Equal("2*3*4", Plain(Single("2\\*3\\*4")));
    }

    [Fact]
    public void Nested_emphasis()
    {
        var p = Single("**bold *both* bold**");
        Assert.Equal("bold both bold", Plain(p));
        Assert.True(Run(p, "both").Props is { Bold: true, Italic: true });
        Assert.True(Run(p, "bold ").Props is { Bold: true, Italic: false });
    }

    [Fact]
    public void Code_spans_are_verbatim()
    {
        var p = Single("use `a *b*` and `` x ` y `` here");
        Assert.Equal("use a *b* and x ` y here", Plain(p));
        Assert.Equal("Courier New", Run(p, "a *b*").Props.Font);
        Assert.Equal("`unclosed", Plain(Single("`unclosed")));
    }

    [Fact]
    public void Links_autolinks_and_images()
    {
        var p = Single("A [**link**](https://x.org/a_(b) \"t\"), <https://y.org>, www.z.com. ![alt *text*](i.png)");
        Assert.Equal("A link, https://y.org, www.z.com. alt text", Plain(p));
        var link = Run(p, "link");
        Assert.True(link.Props.Bold);
        Assert.Equal(link.Props.Color, Run(p, "https://y.org").Props.Color);
        Assert.Equal(link.Props.Color, Run(p, "www.z.com").Props.Color);
        Assert.NotEqual(link.Props.Color, Run(p, "A ").Props.Color);
        Assert.True(Run(p, "alt text").Props.Italic);
    }

    [Fact]
    public void Brackets_that_are_not_links_stay_literal()
    {
        Assert.Equal("[x] and [y](", Plain(Single("[x] and [y](")));
    }

    [Fact]
    public void Entities_escapes_and_inline_html()
    {
        Assert.Equal("*x* & © é <b> ok", Plain(Single("\\*x\\* &amp; &copy; &#233; \\<b> <span class=\"a\">ok</span>")));
        Assert.Equal("a\nb", Plain(Single("a<br>b")));
    }

    [Fact]
    public void Sample_document_renders()
    {
        var result = new ThumbnailGenerator(ThumbnailOptions.Default with { Sizes = [300] })
            .Generate(Encoding.UTF8.GetBytes(Readme), "README.md");
        Assert.Equal(DocumentFormat.Markdown, result.Format);
        Assert.NotNull(result.Find(300));
        Assert.NotNull(result.PreviewPdf);
    }

    // ---- syntax highlighting ----------------------------------------------------------------------

    /// <summary>The kind of the piece holding <paramref name="text"/> on the given line.</summary>
    private static Highlighter.Kind KindOf(List<(string Text, Highlighter.Kind Kind)> line, string text) =>
        line.FirstOrDefault(p => p.Text.Contains(text)) is { Text: not null } piece ? piece.Kind
        : throw new Xunit.Sdk.XunitException($"No \"{text}\" in [{string.Join(" | ", line.Select(p => $"{p.Text}:{p.Kind}"))}]");

    [Theory]
    [InlineData("cs", "public static int Add(int a) => a + 42; // sum", "public", "Add", "42", "// sum")]
    [InlineData("python", "def greet(name): return f\"hi {name}\"  # say", "def", "greet", "hi", "# say")]
    [InlineData("js", "const n = parse('x', 3) /* c */", "const", "parse", "3", "/* c */")]
    public void Code_keywords_names_strings_numbers_comments(string lang, string code, string keyword, string name, string literal, string comment)
    {
        var line = Highlighter.For(lang)!.Line(code);
        Assert.Equal(Highlighter.Kind.Keyword, KindOf(line, keyword));
        Assert.Equal(Highlighter.Kind.Entity, KindOf(line, name));
        Assert.Contains(KindOf(line, literal), new[] { Highlighter.Kind.String, Highlighter.Kind.Number });
        Assert.Equal(Highlighter.Kind.Comment, KindOf(line, comment));
        Assert.Equal(code, string.Concat(line.Select(p => p.Text)));
    }

    [Fact]
    public void Block_comments_and_multiline_strings_carry_to_the_next_line()
    {
        var cs = Highlighter.For("csharp")!;
        cs.Line("int a; /* open");
        Assert.Equal(Highlighter.Kind.Comment, KindOf(cs.Line("still */ int b;"), "still"));
        var py = Highlighter.For("py")!;
        py.Line("doc = \"\"\"first");
        var second = py.Line("second\"\"\" if x else None");
        Assert.Equal(Highlighter.Kind.String, KindOf(second, "second"));
        Assert.Equal(Highlighter.Kind.Keyword, KindOf(second, "if"));
    }

    [Fact]
    public void Markup_tags_attributes_and_comments()
    {
        var html = Highlighter.For("html")!;
        var line = html.Line("<a href=\"/x\" class=btn>Go &amp; see</a> <!-- note -->");
        Assert.Equal(Highlighter.Kind.Tag, KindOf(line, "a"));
        Assert.Equal(Highlighter.Kind.Attr, KindOf(line, "href"));
        Assert.Equal(Highlighter.Kind.String, KindOf(line, "\"/x\""));
        Assert.Equal(Highlighter.Kind.Plain, KindOf(line, "Go"));
        Assert.Equal(Highlighter.Kind.Comment, KindOf(line, "note"));
    }

    [Fact]
    public void Css_selectors_properties_and_values()
    {
        var css = Highlighter.For("css")!;
        var head = css.Line("@media screen { .card:hover, h1 {");
        Assert.Equal(Highlighter.Kind.Keyword, KindOf(head, "@media"));
        Assert.Equal(Highlighter.Kind.Entity, KindOf(head, ".card"));
        Assert.Equal(Highlighter.Kind.Entity, KindOf(head, ":hover"));
        Assert.Equal(Highlighter.Kind.Tag, KindOf(head, "h1"));
        var body = css.Line("  color: #fff; margin: 4px !important;");
        Assert.Equal(Highlighter.Kind.Attr, KindOf(body, "color"));
        Assert.Equal(Highlighter.Kind.Number, KindOf(body, "#fff"));
        Assert.Equal(Highlighter.Kind.Number, KindOf(body, "4px"));
        Assert.Equal(Highlighter.Kind.Keyword, KindOf(body, "!important"));
    }

    [Fact]
    public void Shell_commands_variables_and_comments()
    {
        var line = Highlighter.For("bash")!.Line("if [ -n \"$X\" ]; then echo ${HOME} $1; fi # done");
        Assert.Equal(Highlighter.Kind.Keyword, KindOf(line, "if"));
        Assert.Equal(Highlighter.Kind.Attr, KindOf(line, "echo"));
        Assert.Equal(Highlighter.Kind.Variable, KindOf(line, "${HOME}"));
        Assert.Equal(Highlighter.Kind.Variable, KindOf(line, "$1"));
        Assert.Equal(Highlighter.Kind.Comment, KindOf(line, "# done"));
        Assert.DoesNotContain(Highlighter.For("sh")!.Line("echo ${#a} a#b"), p => p.Kind == Highlighter.Kind.Comment);
    }

    [Fact]
    public void Fenced_code_is_coloured_only_for_a_known_language()
    {
        var coloured = Blocks("```cs\nvar x = \"s\";\n```").OfType<Table>().Single();
        var runs = ((Paragraph)coloured.Rows[0].Cells[0].Blocks[0]).Inlines.OfType<TextInline>().ToList();
        Assert.Equal("var x = \"s\";", string.Concat(runs.Select(r => r.Text)));
        Assert.True(runs.Select(r => r.Props.Color).Distinct().Count() >= 2);
        Assert.Null(Highlighter.For("brainfuck"));
        Assert.Null(Highlighter.For(""));
        var plain = Blocks("```\nvar x = 1;\n```").OfType<Table>().Single();
        Assert.Single(((Paragraph)plain.Rows[0].Cells[0].Blocks[0]).Inlines);
    }

    // ---- hostile input --------------------------------------------------------------------------

    public static TheoryData<string, string> Hostile() => new()
    {
        { "brackets", new string('[', 200_000) + "a" + new string(']', 1000) },
        { "link openers", string.Concat(Enumerable.Repeat("[a](", 50_000)) },
        { "emphasis", string.Concat(Enumerable.Repeat("*a _b ", 50_000)) },
        { "backticks", string.Concat(Enumerable.Range(1, 2000).Select(n => new string('`', n % 50 + 1) + "x")) },
        { "comments", string.Concat(Enumerable.Repeat("<!-- ", 50_000)) },
        { "tags", string.Concat(Enumerable.Repeat("<a b='", 50_000)) },
        { "quotes", new string('>', 100_000) + " deep" },
        { "lists", string.Concat(Enumerable.Range(0, 3000).Select(n => new string(' ', n * 2) + "- item\n")) },
        { "entities", string.Concat(Enumerable.Repeat("&#x", 100_000)) },
        { "table", "|a|\n|-|\n" + string.Concat(Enumerable.Repeat("|" + new string('`', 3) + "|\n", 20_000)) },
        { "code words", "```cs\n" + string.Concat(Enumerable.Repeat("a ", 500_000)) + "\n```" },
        { "code strings", "```py\n" + string.Concat(Enumerable.Repeat("'\\'", 200_000)) + "\n```" },
        { "css values", "```css\na{" + string.Concat(Enumerable.Repeat("b ", 300_000)) + ":\n```" },
        { "markup tags", "```html\n" + string.Concat(Enumerable.Repeat("<a b=\"", 100_000)) + "\n```" },
    };

    [Theory]
    [MemberData(nameof(Hostile))]
    public void Hostile_markdown_ends_in_time(string name, string markdown)
    {
        _ = name;
        var generator = new ThumbnailGenerator(ThumbnailOptions.Default with { Sizes = [200], Outputs = ThumbnailOutputs.Images });
        var error = TestUtil.Within(TimeSpan.FromSeconds(20), () => generator.Generate(Encoding.UTF8.GetBytes(markdown), "x.md"));
        if (error != null) Assert.IsAssignableFrom<ThumbnailException>(error);
    }

    private const string Readme =
        """
        # FastThumb

        <p align="center"><b>Thumbnails for 40+ formats.</b></p>

        > **Note**: free and open source.

        ## Features

        - [x] **Fast**: one read, one layout
        - [ ] _Slow_? ~~never~~
          1. nested `code`
          2. [link](https://example.com)

        | Format | Pages |
        |:--|--:|
        | DOCX | yes |

        ```csharp
        var r = ThumbnailGenerator.Default.GenerateFromFile("a.docx");
        ```

        ---

        Done &mdash; see <https://example.com>.
        """;
}
