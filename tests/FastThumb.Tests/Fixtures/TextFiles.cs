using System.Text;

namespace FastThumb.Tests.Fixtures;

/// <summary>Text-based inputs written as strings.</summary>
internal static class TextFiles
{
    public static byte[] Rtf() => Encoding.ASCII.GetBytes(
        """
        {\rtf1\ansi\deff0{\fonttbl{\f0 Arial;}}
        \f0\fs48\b RTF heading\b0\par
        \fs24 The quick brown fox jumps over the lazy dog.\par
        Second paragraph 0123456789.\par
        }
        """);

    public static byte[] Html() => Encoding.UTF8.GetBytes(
        """
        <!DOCTYPE html>
        <html><head><meta charset="utf-8"><title>Test</title></head>
        <body><h1>HTML heading</h1><p>The quick brown fox jumps over the lazy dog.</p>
        <table border="1"><tr><td>a</td><td>b</td></tr></table></body></html>
        """);

    public static byte[] Csv()
    {
        var text = new StringBuilder("Name,Quantity,Price\n");
        for (var i = 1; i <= 25; i++) text.Append($"Item {i},{i},{i * 1.5:0.00}\n");
        return Encoding.UTF8.GetBytes(text.ToString());
    }

    public static byte[] Markdown() => Encoding.UTF8.GetBytes(
        """
        # Markdown heading

        Some **bold** and *italic* text.

        - first item
        - second item
        """);

    public static byte[] PlainText() => Encoding.UTF8.GetBytes(
        "Plain text file.\nThe quick brown fox jumps over the lazy dog.\n0123456789\n");

    public static string Svg(int width = 200, int height = 100) =>
        $"""
        <svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" viewBox="0 0 200 100">
          <rect width="200" height="100" fill="#ffffff"/>
          <circle cx="60" cy="50" r="40" fill="#d02020"/>
          <rect x="110" y="20" width="70" height="60" fill="#2050d0"/>
        </svg>
        """;
}
