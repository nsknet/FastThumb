using System.Globalization;
using System.Text;
using FastThumb.Layout;

namespace FastThumb.Cells;

/// <summary>Delimited text (CSV / TSV): every value is text, numbers are right-aligned. The delimiter is sniffed from the first line.</summary>
internal static class CsvReader
{
    public static List<SheetSnapshot> Read(string text)
    {
        var sheet = new SheetSnapshot { Name = "Sheet1" };
        var newline = text.IndexOf('\n');
        var firstLine = newline < 0 ? text : text[..newline];
        var delimiter = new[] { '\t', ';', ',' }.OrderByDescending(d => firstLine.Count(c => c == d)).First();
        int row = 0, column = 0;
        var field = new StringBuilder();
        var quoted = false;
        void End()
        {
            if (field.Length > 0 && column <= SheetSnapshot.MaxColumn)   // columns past the kept ones are dropped
            {
                var cell = sheet.Cell(row, column);
                cell.Text = field.ToString();
                cell.Numeric = double.TryParse(cell.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
            }
            field.Clear();
            column++;
        }
        for (var i = 0; i < text.Length && row < 20000; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"' && field.Length == 0) quoted = true;
            else if (c == delimiter) End();
            else if (c == '\n') { End(); row++; column = 0; }
            else if (c != '\r') field.Append(c);
        }
        End();
        return [sheet];
    }
}
