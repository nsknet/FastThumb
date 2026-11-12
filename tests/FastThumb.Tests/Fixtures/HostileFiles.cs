using System.Buffers.Binary;
using System.Text;

namespace FastThumb.Tests.Fixtures;

/// <summary>Inputs crafted to exhaust memory, stack or time, or to read files they should not.</summary>
internal static class HostileFiles
{
    /// <summary>A DOCX whose word/document.xml inflates to <paramref name="megabytes"/> MB of spaces; the file itself stays small.</summary>
    public static byte[] ZipBombDocx(int megabytes = 300) => OfficeFiles.DocxStreamed(stream =>
    {
        stream.Write("<?xml version=\"1.0\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>"u8);
        var block = new byte[1 << 20];
        block.AsSpan().Fill((byte)' ');
        for (var i = 0; i < megabytes; i++) stream.Write(block);
        stream.Write("</w:body></w:document>"u8);
    });

    /// <summary>A DOCX whose body nests <paramref name="levels"/> elements deep.</summary>
    public static byte[] DeepXmlDocx(int levels = 100_000)
    {
        var xml = new StringBuilder(levels * 16);
        xml.Append("<?xml version=\"1.0\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>");
        for (var i = 0; i < levels; i++) xml.Append("<w:sdt>");
        for (var i = 0; i < levels; i++) xml.Append("</w:sdt>");
        xml.Append("</w:body></w:document>");
        return OfficeFiles.DocxFromDocumentXml(xml.ToString());
    }

    /// <summary>The classic "billion laughs": ten levels of entities, each ten copies of the previous one.</summary>
    public static byte[] BillionLaughsDocx()
    {
        var dtd = new StringBuilder("<!DOCTYPE w:document [<!ENTITY lol0 \"lol\">");
        for (var i = 1; i <= 9; i++) dtd.Append($"<!ENTITY lol{i} \"{string.Concat(Enumerable.Repeat($"&lol{i - 1};", 10))}\">");
        dtd.Append("]>");
        return OfficeFiles.DocxFromDocumentXml(OfficeFiles.DocumentXml(OfficeFiles.Paragraph("&lol9;").Replace("&amp;lol9;", "&lol9;"), dtd.ToString()));
    }

    /// <summary>An XML external entity pointing at <paramref name="path"/>, used in the document text.</summary>
    public static byte[] ExternalEntityDocx(string path)
    {
        var uri = new Uri(path).AbsoluteUri;
        var dtd = $"<!DOCTYPE w:document [<!ENTITY xxe SYSTEM \"{uri}\">]>";
        return OfficeFiles.DocxFromDocumentXml(OfficeFiles.DocumentXml(OfficeFiles.Paragraph("&xxe;").Replace("&amp;xxe;", "&xxe;"), dtd));
    }

    /// <summary>
    /// A compound file (.doc container) with a valid signature and sector sizes whose header claims far more FAT sectors
    /// than the file holds.
    /// </summary>
    public static byte[] CompoundFileHugeFat()
    {
        var file = new byte[1536];
        BinaryPrimitives.WriteUInt64LittleEndian(file, 0xE11AB1A1E011CFD0);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(24), 0x003E);        // minor version
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(26), 0x0003);        // major version 3
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(28), 0xFFFE);        // little-endian
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(30), 9);             // 512-byte sectors
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(32), 6);             // 64-byte mini sectors
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(44), 0x7FFFFFFF);    // number of FAT sectors: absurd
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(48), 1);             // first directory sector
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(56), 4096);          // mini stream cutoff
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(60), 0xFFFFFFFE);    // no mini FAT
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(68), 0xFFFFFFFE);    // no DIFAT sectors
        for (var i = 0; i < 109; i++) BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(76 + i * 4), i == 0 ? 0u : 0xFFFFFFFF);
        return file;
    }

    /// <summary>RTF with <paramref name="count"/> nested groups that never close.</summary>
    public static byte[] DeepRtf(int count = 1_000_000)
    {
        var data = new byte[5 + count];
        "{\\rtf"u8.CopyTo(data);
        data.AsSpan(5).Fill((byte)'{');
        return data;
    }

    public static byte[] Garbage(int seed, int length)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }
}
