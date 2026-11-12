using System.Globalization;
using System.Text;

namespace FastThumb.Cells;

/// <summary>
/// Excel number-format codes → display text for the common cases: General, fixed decimals, thousands
/// separators, percent, scientific, currency/literal prefixes and suffixes, negative and zero sections,
/// and date/time patterns. Fractions and conditional sections fall back to General. en-US symbols are used.
/// </summary>
internal static class NumberFormat
{
    private static readonly Dictionary<int, string> BuiltIn = new()
    {
        [0] = "General",
        [1] = "0",
        [2] = "0.00",
        [3] = "#,##0",
        [4] = "#,##0.00",
        [9] = "0%",
        [10] = "0.00%",
        [11] = "0.00E+00",
        [12] = "# ?/?",
        [13] = "# ??/??",
        [14] = "m/d/yyyy",
        [15] = "d-mmm-yy",
        [16] = "d-mmm",
        [17] = "mmm-yy",
        [18] = "h:mm AM/PM",
        [19] = "h:mm:ss AM/PM",
        [20] = "h:mm",
        [21] = "h:mm:ss",
        [22] = "m/d/yyyy h:mm",
        [37] = "#,##0;(#,##0)",
        [38] = "#,##0;(#,##0)",
        [39] = "#,##0.00;(#,##0.00)",
        [40] = "#,##0.00;(#,##0.00)",
        [41] = "#,##0;(#,##0)",
        [42] = "$#,##0;($#,##0)",
        [43] = "#,##0.00;(#,##0.00)",
        [44] = "$#,##0.00;($#,##0.00)",
        [45] = "mm:ss",
        [46] = "[h]:mm:ss",
        [47] = "mm:ss.0",
        [48] = "##0.0E+0",
        [49] = "@",
    };
    private static readonly string[] Months = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static string Code(int id, Dictionary<int, string>? custom)
        => custom != null && custom.TryGetValue(id, out var code) ? code : BuiltIn.GetValueOrDefault(id, "General");

    public static string General(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return "";
        if (value == Math.Floor(value) && Math.Abs(value) < 1e11) return value.ToString("0", Invariant);
        var text = value.ToString("G10", Invariant);
        return text.Contains('E') ? value.ToString("0.#####E+00", Invariant) : text;
    }

    public static string Format(double value, string code, bool date1904)
    {
        if (string.IsNullOrEmpty(code) || code.Equals("General", StringComparison.OrdinalIgnoreCase)) return General(value);
        if (code.Length > 255) return General(value);   // Excel's limit; the code comes from the file and is parsed per cell
        try
        {
            // sections: positive ; negative ; zero ; text
            var sections = new List<string>();
            var current = new StringBuilder();
            for (var i = 0; i < code.Length; i++)
            {
                var c = code[i];
                if (c == '"') { var end = code.IndexOf('"', i + 1); if (end < 0) end = code.Length - 1; current.Append(code, i, end - i + 1); i = end; }
                else if (c == '\\' && i + 1 < code.Length) { current.Append(c).Append(code[++i]); }
                else if (c == ';') { sections.Add(current.ToString()); current.Clear(); }
                else current.Append(c);
            }
            sections.Add(current.ToString());

            var section = sections[0];
            var negative = value < 0;
            if (sections.Count >= 3 && value == 0) section = sections[2];
            else if (sections.Count >= 2 && negative) { section = sections[1]; value = -value; negative = false; }
            if (section.Length == 0) return "";

            // strip colours / conditions / locale markers, keep currency symbols and elapsed-time markers
            var clean = new StringBuilder();
            var elapsed = false;
            for (var i = 0; i < section.Length; i++)
            {
                var c = section[i];
                if (c == '[')
                {
                    var end = section.IndexOf(']', i);
                    if (end < 0) break;
                    var inner = section[(i + 1)..end];
                    if (inner.StartsWith('$')) { var dash = inner.IndexOf('-'); var symbol = dash > 0 ? inner[1..dash] : inner[1..]; if (symbol.Length > 0) clean.Append('"').Append(symbol).Append('"'); }
                    else if (inner.Length > 0 && inner.All(ch => ch is 'h' or 'H' or 'm' or 'M' or 's' or 'S')) { clean.Append(inner); elapsed = true; }
                    i = end;
                }
                else clean.Append(c);
            }
            section = clean.ToString();
            if (section.Equals("General", StringComparison.OrdinalIgnoreCase)) return (negative ? "-" : "") + General(Math.Abs(value));

            // tokens: literals vs pattern characters
            var tokens = new List<(bool Literal, string Text)>();
            for (var i = 0; i < section.Length; i++)
            {
                var c = section[i];
                if (c == '"') { var end = section.IndexOf('"', i + 1); if (end < 0) end = section.Length; tokens.Add((true, section[(i + 1)..end])); i = end; }
                else if (c == '\\' && i + 1 < section.Length) tokens.Add((true, section[++i].ToString()));
                else if (c == '_' && i + 1 < section.Length) { tokens.Add((true, " ")); i++; }
                else if (c == '*' && i + 1 < section.Length) i++;
                else tokens.Add((false, c.ToString()));
            }
            var pattern = string.Concat(tokens.Where(t => !t.Literal).Select(t => t.Text));
            var isDate = pattern.Any(ch => "yYdDhHsS".Contains(ch)) || (pattern.Any(ch => ch is 'm' or 'M') && !pattern.Any(ch => ch is '0' or '#'));
            if (isDate) return FormatDate(value, tokens, date1904, elapsed);
            if (pattern.Contains('/') || pattern.Contains('@')) return (negative ? "-" : "") + General(Math.Abs(value));

            int first = tokens.FindIndex(t => !t.Literal && t.Text[0] is '0' or '#' or '?'), last = tokens.FindLastIndex(t => !t.Literal && t.Text[0] is '0' or '#' or '?');
            if (first < 0) return string.Concat(tokens.Select(t => t.Text));
            string Outside(int from, int to) => string.Concat(tokens.Skip(from).Take(to - from).Where(t => t.Literal || !",".Contains(t.Text[0])).Select(t => t.Text));
            var middle = string.Concat(tokens.Skip(first).Take(last - first + 1).Where(t => !t.Literal).Select(t => t.Text));
            var prefix = Outside(0, first);
            var suffix = Outside(last + 1, tokens.Count);

            foreach (var ch in pattern) if (ch == '%') value *= 100;
            for (var i = last + 1; i < tokens.Count && !tokens[i].Literal && tokens[i].Text == ","; i++) value /= 1000; // trailing commas scale by thousands

            var exponent = middle.IndexOf('E');
            var mantissa = exponent >= 0 ? middle[..exponent] : middle;
            var dot = mantissa.IndexOf('.');
            var integerPart = dot >= 0 ? mantissa[..dot] : mantissa;
            var decimalPart = dot >= 0 ? mantissa[(dot + 1)..] : "";
            int decimals = decimalPart.Count(ch => ch is '0' or '#' or '?'), required = decimalPart.Count(ch => ch == '0');
            var decimalFormat = decimals > 0 ? "." + new string('0', required) + new string('#', decimals - required) : "";
            string body;
            if (exponent >= 0) body = value.ToString("0" + decimalFormat + "E+00", Invariant);
            else
            {
                var zeros = Math.Max(1, integerPart.Count(ch => ch == '0'));
                var integerFormat = integerPart.Contains(',') ? "#,##" + new string('0', Math.Min(zeros, 1)) : new string('0', zeros);
                body = Math.Round(value, Math.Min(decimals, 15), MidpointRounding.AwayFromZero).ToString(integerFormat + decimalFormat, Invariant);
            }
            return (negative ? "-" : "") + prefix + body + suffix;
        }
        catch (Exception)
        {
            return General(value);
        }
    }

    private static string FormatDate(double serial, List<(bool Literal, string Text)> tokens, bool date1904, bool elapsed)
    {
        if (serial < 0 || serial > 2958465) return General(serial);
        var date = (date1904 ? new DateTime(1904, 1, 1) : new DateTime(1899, 12, 30)).AddDays(serial);
        // round to the nearest second so 0.5 renders as 12:00:00, not 11:59:59
        date = new DateTime((date.Ticks + TimeSpan.TicksPerSecond / 2) / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond);
        var meridiem = tokens.Any(t => !t.Literal && t.Text is "A" or "a");
        var result = new StringBuilder();
        var lastWasHour = false;
        for (var i = 0; i < tokens.Count; i++)
        {
            var (literal, text) = tokens[i];
            if (literal) { result.Append(text); continue; }
            var c = char.ToLowerInvariant(text[0]);
            var run = 1;
            while (i + run < tokens.Count && !tokens[i + run].Literal && char.ToLowerInvariant(tokens[i + run].Text[0]) == c) run++;
            switch (c)
            {
                case 'y': result.Append(run <= 2 ? (date.Year % 100).ToString("00") : date.Year.ToString("0000")); break;
                case 'd': result.Append(run switch { 1 => date.Day.ToString(), 2 => date.Day.ToString("00"), 3 => date.DayOfWeek.ToString()[..3], _ => date.DayOfWeek.ToString() }); break;
                case 'h':
                    var hour = elapsed ? (int)Math.Floor(serial * 24) : meridiem ? (date.Hour % 12 == 0 ? 12 : date.Hour % 12) : date.Hour;
                    result.Append(run == 1 ? hour.ToString() : hour.ToString("00"));
                    break;
                case 's': result.Append(run == 1 ? date.Second.ToString() : date.Second.ToString("00")); break;
                case 'm':
                    // "m" means minutes right after hours or right before seconds, otherwise month
                    var next = tokens.Skip(i + run).FirstOrDefault(t => !t.Literal && char.IsLetter(t.Text[0])).Text;
                    if (lastWasHour || (next != null && char.ToLowerInvariant(next[0]) == 's')) result.Append(run == 1 ? date.Minute.ToString() : date.Minute.ToString("00"));
                    else result.Append(run switch { 1 => date.Month.ToString(), 2 => date.Month.ToString("00"), 3 => Months[date.Month - 1][..3], 5 => Months[date.Month - 1][..1], _ => Months[date.Month - 1] });
                    break;
                case 'a':
                    // AM/PM or A/P
                    var marker = string.Concat(tokens.Skip(i).TakeWhile(t => !t.Literal && "AaPpMm/".Contains(t.Text[0])).Select(t => t.Text));
                    result.Append(marker.Length >= 5 ? (date.Hour < 12 ? "AM" : "PM") : date.Hour < 12 ? "A" : "P");
                    run = marker.Length;
                    break;
                case '.' or '0' when c == '0': break; // fractional seconds are not shown
                default: result.Append(text); break;
            }
            lastWasHour = c == 'h' || (lastWasHour && !char.IsLetter(c));
            i += run - 1;
        }
        return result.ToString();
    }
}
