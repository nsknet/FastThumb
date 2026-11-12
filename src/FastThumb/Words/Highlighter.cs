namespace FastThumb.Words;

/// <summary>
/// A small GitHub-style highlighter for fenced code: keywords, strings, comments, numbers, called / declared names,
/// markup tags and attributes, CSS selectors and properties, shell variables. It is not a parser: one linear pass per
/// line, and the only state carried between lines is an open block comment, multi-line string, tag or CSS rule.
/// Languages it does not know (and code with no info string) come back as plain text.
/// </summary>
internal sealed class Highlighter
{
    internal enum Kind { Plain, Keyword, String, Comment, Number, Entity, Tag, Attr, Variable }

    private enum Mode { Code, Markup, Css }

    private sealed class Lang
    {
        public Mode Mode;
        public HashSet<string> Keywords = [], Constants = [], Builtins = [];
        public string? Line;                               // line comment
        public string? BlockOpen, BlockClose;              // block comment
        public string[] Multi = [];                        // strings that may span lines, closed by the same text
        public bool Hash, Preprocessor, Variables, Decorators;
    }

    private static HashSet<string> Words(string list) => new(list.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

    private static readonly Lang CSharp = new()
    {
        Keywords = Words("abstract as async await base bool break byte case catch char checked class const continue decimal default " +
            "delegate do double else enum event explicit extern file finally fixed float for foreach get global goto if implicit in init " +
            "int interface internal is lock long managed nameof namespace new not object operator or and out override params partial " +
            "private protected public readonly record ref required return sbyte scoped sealed set short sizeof stackalloc static string " +
            "struct switch this throw try typeof uint ulong unchecked unsafe ushort using value var virtual void volatile when where while with yield"),
        Constants = Words("true false null"),
        Line = "//", BlockOpen = "/*", BlockClose = "*/", Multi = ["\"\"\""], Preprocessor = true,
    };

    private static readonly Lang Python = new()
    {
        Keywords = Words("and as assert async await break case class continue def del elif else except finally for from global if " +
            "import in is lambda match nonlocal not or pass raise return try while with yield"),
        Constants = Words("True False None"),
        Builtins = Words("self cls print len range open int str float list dict set tuple bool type isinstance super enumerate zip map filter"),
        Hash = true, Multi = ["\"\"\"", "'''"], Decorators = true,
    };

    private static readonly Lang JavaScript = new()
    {
        Keywords = Words("abstract as async await break case catch class const constructor continue debugger declare default delete do else " +
            "enum export extends finally for from function get if implements import in instanceof interface keyof let new of private " +
            "protected public readonly return set static super switch this throw try type typeof var void while with yield"),
        Constants = Words("true false null undefined NaN Infinity"),
        Line = "//", BlockOpen = "/*", BlockClose = "*/", Multi = ["`"],
    };

    private static readonly Lang Shell = new()
    {
        Keywords = Words("if then else elif fi for while until do done case esac in function select return local export readonly declare " +
            "unset shift exit break continue source alias set trap eval exec"),
        Builtins = Words("echo printf cd pwd read test cat ls grep sed awk mkdir rm cp mv chmod chown sudo curl wget git dotnet npm npx " +
            "pip python node docker tar find xargs touch head tail sort"),
        Hash = true, Variables = true,
    };

    private static readonly Lang CLike = new()                  // Java, C, C++, Go, Rust, Kotlin, Swift: one shared word list
    {
        Keywords = Words("abstract auto bool break byte case catch char class const constexpr continue crate default defer delete do double " +
            "dyn else enum explicit extends extern final float fn for fun func go goto if impl implements import inline int interface let " +
            "long loop match mod move mut namespace new override package private protected pub public ref return self short signed sizeof " +
            "static struct super switch template this throw throws trait try type typedef typename union unsigned use using val var virtual " +
            "void volatile when where while"),
        Constants = Words("true false null nil nullptr None Some Ok Err"),
        Line = "//", BlockOpen = "/*", BlockClose = "*/", Preprocessor = true,
    };

    private static readonly Lang Markup = new() { Mode = Mode.Markup };
    private static readonly Lang Css = new() { Mode = Mode.Css };

    private static readonly Dictionary<string, Lang> Languages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cs"] = CSharp, ["c#"] = CSharp, ["csharp"] = CSharp,
        ["py"] = Python, ["python"] = Python, ["python3"] = Python,
        ["js"] = JavaScript, ["javascript"] = JavaScript, ["jsx"] = JavaScript, ["mjs"] = JavaScript, ["ts"] = JavaScript,
        ["typescript"] = JavaScript, ["tsx"] = JavaScript, ["json"] = JavaScript, ["jsonc"] = JavaScript,
        ["sh"] = Shell, ["bash"] = Shell, ["shell"] = Shell, ["zsh"] = Shell, ["console"] = Shell, ["shellscript"] = Shell,
        ["html"] = Markup, ["htm"] = Markup, ["xml"] = Markup, ["svg"] = Markup, ["xaml"] = Markup, ["vue"] = Markup,
        ["css"] = Css, ["scss"] = Css, ["less"] = Css,
        ["java"] = CLike, ["c"] = CLike, ["h"] = CLike, ["cpp"] = CLike, ["c++"] = CLike, ["go"] = CLike, ["rust"] = CLike,
        ["rs"] = CLike, ["kotlin"] = CLike, ["kt"] = CLike, ["swift"] = CLike,
    };

    private static readonly HashSet<string> Declarers = Words("class def function func fn fun struct interface enum record trait type");

    private readonly Lang lang;
    private string? close;                                 // an open comment / string, closed by this text
    private Kind closeKind;
    private bool inTag;                                    // markup: inside <tag ...>
    private int depth;                                     // css: brace depth

    private string s = "";
    private int i;
    private readonly List<(int End, Kind Kind)> output = [];     // pieces as end offsets: merging stays O(1)

    private Highlighter(Lang lang) => this.lang = lang;

    /// <summary>A highlighter for the fence's info string ("```cs title=x" → "cs"), or null when the language is unknown.</summary>
    public static Highlighter? For(string? info)
    {
        if (string.IsNullOrWhiteSpace(info)) return null;
        var name = info.Trim().Split([' ', '{', ','], 2)[0].TrimStart('.');
        return Languages.TryGetValue(name, out var lang) ? new Highlighter(lang) : null;
    }

    /// <summary>Splits one line into coloured pieces (adjacent pieces of the same kind are merged).</summary>
    public List<(string Text, Kind Kind)> Line(string line)
    {
        s = line; i = 0; output.Clear();
        if (close != null) Until(close, closeKind);
        switch (lang.Mode)
        {
            case Mode.Markup: ScanMarkup(); break;
            case Mode.Css: ScanCss(); break;
            default: ScanCode(); break;
        }
        var pieces = new List<(string, Kind)>(output.Count);
        var start = 0;
        foreach (var (end, kind) in output) { pieces.Add((s[start..end], kind)); start = end; }
        return pieces;
    }

    private void Emit(int end, Kind kind)
    {
        if (end <= i) return;
        if (output.Count > 0 && output[^1].Kind == kind) output[^1] = (end, kind);
        else output.Add((end, kind));
        i = end;
    }

    /// <summary>Emits up to and including <paramref name="end"/>, or to the end of the line and remembers it is still open.</summary>
    private void Until(string end, Kind kind)
    {
        var at = s.IndexOf(end, i, StringComparison.Ordinal);
        if (at < 0) { close = end; closeKind = kind; Emit(s.Length, kind); }
        else { close = null; Emit(at + end.Length, kind); }
    }

    private void Quoted(Kind kind, bool escapes = true)
    {
        var q = s[i];
        var j = i + 1;
        while (j < s.Length && s[j] != q) j += escapes && s[j] == '\\' ? 2 : 1;
        Emit(Math.Min(j + 1, s.Length), kind);
    }

    private bool At(string text) => string.CompareOrdinal(s, i, text, 0, text.Length) == 0;

    private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';

    private int WordEnd(int from, string extra = "")
    {
        while (from < s.Length && (IsWord(s[from]) || extra.Contains(s[from]))) from++;
        return from;
    }

    private char Next(int from)                            // the next non-space character from here, or '\0'
    {
        while (from < s.Length && s[from] == ' ') from++;
        return from < s.Length ? s[from] : '\0';
    }

    private void ScanCode()
    {
        if (lang.Preprocessor && s.AsSpan(i).TrimStart().StartsWith("#")) { Emit(s.Length, Kind.Keyword); return; }
        string? previous = null;
        while (i < s.Length)
        {
            var c = s[i];
            // shell: "#" starts a comment only at a word boundary ("${#a}", "a#b" are not comments)
            if (lang.Line != null && At(lang.Line) || lang.Hash && c == '#' && (!lang.Variables || i == 0 || s[i - 1] is ' ' or '\t' or ';'))
            { Emit(s.Length, Kind.Comment); return; }
            if (lang.BlockOpen != null && At(lang.BlockOpen)) { Until2(lang.BlockOpen, lang.BlockClose!, Kind.Comment); continue; }
            if (lang.Multi.FirstOrDefault(At) is { } multi) { Until2(multi, multi, Kind.String); continue; }
            if (c is '"' or '\'') { Quoted(Kind.String, escapes: !(i > 0 && s[i - 1] == '@')); continue; }
            if (c is '@' or '$' && i + 1 < s.Length && s[i + 1] is '"' or '@' or '$' && lang == CSharp)
            { var j = i; while (j < s.Length && s[j] is '@' or '$') j++; Emit(j, Kind.String); continue; }
            if (lang.Variables && c == '$' && i + 1 < s.Length)
            {
                if (s[i + 1] == '{') { var end = s.IndexOf('}', i); Emit(end < 0 ? s.Length : end + 1, Kind.Variable); continue; }
                if (IsWord(s[i + 1])) { Emit(WordEnd(i + 1), Kind.Variable); continue; }
                if ("@#?*!$-".Contains(s[i + 1])) { Emit(i + 2, Kind.Variable); continue; }
            }
            if (lang.Decorators && c == '@' && i + 1 < s.Length && char.IsLetter(s[i + 1])) { Emit(WordEnd(i + 1, "."), Kind.Entity); continue; }
            if (char.IsDigit(c) && (i == 0 || !IsWord(s[i - 1])))
            {
                var j = i + 1;
                while (j < s.Length && (IsWord(s[j]) || s[j] == '.' && j + 1 < s.Length && char.IsDigit(s[j + 1]))) j++;
                Emit(j, Kind.Number); continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                var end = WordEnd(i, lang.Variables ? "-" : "");
                // Python string prefixes: f"..", r'..', b"..", rb".."
                if (lang == Python && end < s.Length && s[end] is '"' or '\'' && end - i <= 2 && s[i..end].All(ch => "rRbBfFuU".Contains(ch)))
                { Emit(end, Kind.String); continue; }
                var word = s[i..end];
                var kind = lang.Keywords.Contains(word) ? Kind.Keyword
                    : lang.Constants.Contains(word) ? Kind.Number
                    : previous != null && Declarers.Contains(previous) || Next(end) == '(' ? Kind.Entity
                    : lang.Builtins.Contains(word) && (lang != Shell || IsCommandStart() || previous != null && lang.Keywords.Contains(previous)) ? Kind.Attr
                    : Kind.Plain;
                Emit(end, kind);
                previous = word;
                continue;
            }
            if (c != ' ') previous = c == '.' ? previous : null;
            var k = i + 1;                                 // a run of punctuation / spaces up to the next interesting character
            while (k < s.Length && !char.IsLetterOrDigit(s[k]) && "\"'`#/$@_".IndexOf(s[k]) < 0) k++;
            Emit(k, Kind.Plain);
        }
    }

    /// <summary>Shell: a builtin is coloured only where a command starts (line start, after | ; &amp;&amp; or sudo).</summary>
    private bool IsCommandStart()
    {
        var j = i - 1;
        while (j >= 0 && s[j] == ' ') j--;
        return j < 0 || s[j] is '|' or ';' or '&' or '(' or '`' or '$' || j >= 3 && s.AsSpan(j - 3, 4) is "sudo" && (j == 3 || s[j - 4] == ' ');
    }

    /// <summary>Opens a comment / string at <paramref name="open"/> and runs it to <paramref name="end"/> (maybe on a later line).</summary>
    private void Until2(string open, string end, Kind kind)
    {
        var start = i;
        i += open.Length;
        var at = s.IndexOf(end, i, StringComparison.Ordinal);
        i = start;
        if (at < 0) { close = end; closeKind = kind; Emit(s.Length, kind); }
        else Emit(at + end.Length, kind);
    }

    private void ScanMarkup()
    {
        while (i < s.Length)
        {
            var c = s[i];
            if (inTag)
            {
                if (c == '>') { inTag = false; Emit(i + 1, Kind.Plain); }
                else if (At("/>")) { inTag = false; Emit(i + 2, Kind.Plain); }
                else if (c is '"' or '\'') Quoted(Kind.String, escapes: false);
                else if (IsWord(c)) Emit(WordEnd(i, "-:."), Kind.Attr);
                else Emit(i + 1, Kind.Plain);
                continue;
            }
            if (At("<!--")) { Until2("<!--", "-->", Kind.Comment); continue; }
            if (c == '<' && i + 1 < s.Length && (char.IsLetter(s[i + 1]) || s[i + 1] is '/' or '!' or '?'))
            {
                Emit(i + (s[i + 1] is '/' or '!' or '?' ? 2 : 1), Kind.Plain);
                Emit(WordEnd(i, "-:."), Kind.Tag);
                inTag = true;
                continue;
            }
            if (c == '&')
            {
                var semi = s.IndexOf(';', i, Math.Min(12, s.Length - i));
                if (semi > i + 1) { Emit(semi + 1, Kind.Number); continue; }
            }
            var k = i + 1;
            while (k < s.Length && s[k] is not ('<' or '&')) k++;
            Emit(k, Kind.Plain);
        }
    }

    private void ScanCss()
    {
        // selector or declaration? A selector is followed by "{" before any ";" / "}" (this also finds rules nested in
        // @media). One backward pass gives, for each offset, whether that holds on the rest of the line.
        var selectorAt = new bool[s.Length + 1];
        var sawOpen = false;
        for (var j = s.Length - 1; j >= 0; j--)
        {
            if (s[j] == '{') sawOpen = true;
            else if (s[j] is ';' or '}') sawOpen = false;
            selectorAt[j] = sawOpen;
        }
        var paren = 0;
        while (i < s.Length)
        {
            var c = s[i];
            var selector = depth == 0 || selectorAt[i];
            if (c == '(') paren++;
            else if (c == ')') paren = Math.Max(0, paren - 1);
            if (At("/*")) { Until2("/*", "*/", Kind.Comment); continue; }
            if (At("//") && (i == 0 || s[i - 1] is ' ' or '\t')) { Emit(s.Length, Kind.Comment); continue; }   // scss / less
            if (c is '"' or '\'') { Quoted(Kind.String); continue; }
            if (c == '{') { depth++; Emit(i + 1, Kind.Plain); continue; }
            if (c == '}') { depth = Math.Max(0, depth - 1); Emit(i + 1, Kind.Plain); continue; }
            if (c == '@' && i + 1 < s.Length && char.IsLetter(s[i + 1])) { Emit(WordEnd(i + 1, "-"), Kind.Keyword); continue; }
            if (c == '!' && At("!important")) { Emit(i + 10, Kind.Keyword); continue; }
            if (c is '#' or '.' && i + 1 < s.Length && (IsWord(s[i + 1]) || s[i + 1] == '-') && !(c == '.' && char.IsDigit(s[i + 1])))
            { Emit(WordEnd(i + 1, "-"), !selector && c == '#' ? Kind.Number : Kind.Entity); continue; }
            if (c == ':' && selector && paren == 0 && i + 1 < s.Length && (char.IsLetter(s[i + 1]) || s[i + 1] == ':'))
            { Emit(WordEnd(i + (s[i + 1] == ':' ? 2 : 1), "-"), Kind.Entity); continue; }
            if (char.IsDigit(c) || c == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1]) ||
                c == '-' && i + 1 < s.Length && char.IsDigit(s[i + 1]) && (i == 0 || !IsWord(s[i - 1])))
            {
                var j = i + 1;
                while (j < s.Length && (IsWord(s[j]) || s[j] is '.' or '%')) j++;
                Emit(j, Kind.Number); continue;
            }
            if (char.IsLetter(c) || c is '-' or '_' or '$')
            {
                var end = WordEnd(i + 1, "-");
                var kind = c == '$' ? Kind.Variable
                    : Next(end) == '(' ? Kind.Entity
                    : selector && paren == 0 ? Kind.Tag
                    : Kind.Attr;                           // property names and values, media features
                Emit(end, kind);
                continue;
            }
            Emit(i + 1, Kind.Plain);
        }
    }
}
