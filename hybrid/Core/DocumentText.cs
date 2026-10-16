using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ClaudeChat.Core;

// claude.ai は Word や Excel もそのまま添付できる。API は読めないので、手元で文字だけ取り出して渡す
public static partial class DocumentText
{
    public static readonly string[] Extensions = [".docx", ".xlsx", ".pptx", ".odt", ".ods", ".odp", ".epub", ".rtf"];
    // 取り出した文字の上限。添付のテキストと同じくらいに抑える
    const int MaxChars = 2_000_000;

    public static string Extract(string fileName, byte[] bytes)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var text = extension switch
        {
            ".rtf" => FromRtf(Encoding.Latin1.GetString(bytes)),
            _ => FromZip(extension, bytes),
        };
        text = MultipleBlankLines().Replace(text.Replace("\r\n", "\n"), "\n\n").Trim();
        if (text.Length == 0) throw new InvalidOperationException($"{fileName} から文字を取り出せませんでした");
        return text.Length > MaxChars ? text[..MaxChars] : text;
    }

    static string FromZip(string extension, byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var builder = new StringBuilder();
        switch (extension)
        {
            case ".docx":
                AppendWordXml(zip, "word/document.xml", builder);
                break;
            case ".pptx":
                // スライドの順番どおりに並べる
                foreach (var entry in zip.Entries.Where(e => SlidePath().IsMatch(e.FullName)).OrderBy(e => int.Parse(SlidePath().Match(e.FullName).Groups[1].Value)))
                {
                    builder.Append($"## スライド {SlidePath().Match(entry.FullName).Groups[1].Value}\n");
                    AppendParagraphs(Load(entry), "p", "t", builder);
                    builder.Append('\n');
                }
                break;
            case ".xlsx":
                AppendWorkbook(zip, builder);
                break;
            case ".odt" or ".ods" or ".odp":
                if (zip.GetEntry("content.xml") is { } content) AppendParagraphs(Load(content), "p", null, builder);
                break;
            case ".epub":
                foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase) || e.FullName.EndsWith(".html", StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.FullName))
                {
                    AppendParagraphs(Load(entry), "p", null, builder);
                }
                break;
            default:
                throw new InvalidOperationException($"{extension} は読めない形式です");
        }
        return builder.ToString();
    }

    static XDocument Load(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        return XDocument.Load(stream);
    }

    static void AppendWordXml(ZipArchive zip, string path, StringBuilder builder)
    {
        if (zip.GetEntry(path) is not { } entry) return;
        AppendParagraphs(Load(entry), "p", "t", builder);
    }

    // 段落ごとに改行する。textName を指定すると、その要素の文字だけを拾う
    static void AppendParagraphs(XDocument document, string paragraphName, string? textName, StringBuilder builder)
    {
        foreach (var paragraph in document.Descendants().Where(e => e.Name.LocalName == paragraphName))
        {
            var text = textName is null
                ? paragraph.Value
                : string.Concat(paragraph.Descendants().Where(e => e.Name.LocalName == textName || e.Name.LocalName == "tab").Select(e => e.Name.LocalName == "tab" ? "\t" : e.Value));
            builder.Append(text).Append('\n');
        }
    }

    // シートごとに、セルをタブ区切りで並べる
    static void AppendWorkbook(ZipArchive zip, StringBuilder builder)
    {
        var shared = zip.GetEntry("xl/sharedStrings.xml") is { } sharedEntry
            ? Load(sharedEntry).Descendants().Where(e => e.Name.LocalName == "si").Select(si => string.Concat(si.Descendants().Where(e => e.Name.LocalName == "t").Select(t => t.Value))).ToList()
            : [];
        foreach (var sheet in zip.Entries.Where(e => SheetPath().IsMatch(e.FullName)).OrderBy(e => int.Parse(SheetPath().Match(e.FullName).Groups[1].Value)))
        {
            builder.Append($"## シート {SheetPath().Match(sheet.FullName).Groups[1].Value}\n");
            foreach (var row in Load(sheet).Descendants().Where(e => e.Name.LocalName == "row"))
            {
                var cells = row.Elements().Where(e => e.Name.LocalName == "c").Select(c =>
                {
                    var value = c.Elements().FirstOrDefault(e => e.Name.LocalName == "v")?.Value ?? string.Concat(c.Descendants().Where(e => e.Name.LocalName == "t").Select(t => t.Value));
                    return (string?)c.Attribute("t") == "s" && int.TryParse(value, out var index) && index < shared.Count ? shared[index] : value;
                });
                builder.Append(string.Join('\t', cells)).Append('\n');
            }
            builder.Append('\n');
        }
    }

    // 制御語と書式のグループを外した素朴な読み方。日本語の \'xx は Shift_JIS として読む
    static string FromRtf(string rtf)
    {
        var withoutGroups = RtfIgnoredGroup().Replace(rtf, "");
        var bytes = new List<byte>();
        var builder = new StringBuilder();
        void FlushBytes()
        {
            if (bytes.Count == 0) return;
            builder.Append(Encoding.GetEncoding(932, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback).GetString(bytes.ToArray()));
            bytes.Clear();
        }
        for (var i = 0; i < withoutGroups.Length; i++)
        {
            var c = withoutGroups[i];
            if (c == '\\' && i + 3 < withoutGroups.Length && withoutGroups[i + 1] == '\'')
            {
                bytes.Add(Convert.ToByte(withoutGroups.Substring(i + 2, 2), 16));
                i += 3;
                continue;
            }
            FlushBytes();
            if (c == '\\')
            {
                var match = RtfControl().Match(withoutGroups, i);
                if (match.Success && match.Index == i)
                {
                    if (match.Groups[1].Value is "par" or "line") builder.Append('\n');
                    else if (match.Groups[1].Value == "tab") builder.Append('\t');
                    i += match.Length - 1;
                    continue;
                }
                if (i + 1 < withoutGroups.Length) builder.Append(withoutGroups[++i]);
                continue;
            }
            if (c is '{' or '}' or '\r' or '\n') continue;
            builder.Append(c);
        }
        FlushBytes();
        return builder.ToString();
    }

    [GeneratedRegex(@"^ppt/slides/slide(\d+)\.xml$")]
    private static partial Regex SlidePath();

    [GeneratedRegex(@"^xl/worksheets/sheet(\d+)\.xml$")]
    private static partial Regex SheetPath();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex MultipleBlankLines();

    [GeneratedRegex(@"\{\\(?:\*|fonttbl|colortbl|stylesheet|info|pict)[^{}]*(?:\{[^{}]*\}[^{}]*)*\}")]
    private static partial Regex RtfIgnoredGroup();

    [GeneratedRegex(@"\\([a-z]+)(-?\d+)? ?")]
    private static partial Regex RtfControl();
}
