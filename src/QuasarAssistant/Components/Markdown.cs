using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace QuasarAssistant.Components;

/// <summary>
/// The Markdown of the model's answers as HTML for the chat: headings, paragraphs, lists, block quotes, code,
/// tables, links and emphasis, close to GitHub's flavor. The output is safe to show whatever the model wrote:
/// every piece of text is HTML-encoded, so HTML in the input stays text, only a fixed set of tags is written,
/// and links go only to http, https and mailto URLs and to Quasar's own pages.
/// </summary>
public static partial class Markdown
{
    public static string ToHtml(string markdown)
    {
        var html = new StringBuilder();
        Blocks(markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'), html, tight: false);
        return html.ToString();
    }

    private static void Blocks(IReadOnlyList<string> lines, StringBuilder html, bool tight)
    {
        var paragraph = new List<string>();
        void Flush()
        {
            if (paragraph.Count == 0)
                return;
            var text = Inline(string.Join('\n', paragraph)).Replace("\n", "<br>");
            html.Append(tight ? text : $"<p>{text}</p>");
            paragraph.Clear();
        }

        var i = 0;
        while (i < lines.Count)
        {
            var line = lines[i];
            Match m;
            if (line.Trim().Length == 0)
            {
                Flush();
                i++;
            }
            else if ((m = Fence().Match(line)).Success)
            {
                Flush();
                i = Code(lines, i, m, html);
            }
            else if ((m = Heading().Match(line)).Success)
            {
                Flush();
                var level = m.Groups[1].Length;
                html.Append($"<h{level}>").Append(Inline(m.Groups[2].Value)).Append($"</h{level}>");
                i++;
            }
            else if (Rule().IsMatch(line))
            {
                Flush();
                html.Append("<hr>");
                i++;
            }
            else if (line.TrimStart().StartsWith('>'))
            {
                Flush();
                var quote = new List<string>();
                for (; i < lines.Count && lines[i].TrimStart().StartsWith('>'); i++)
                {
                    var rest = lines[i].TrimStart()[1..];
                    quote.Add(rest.StartsWith(' ') ? rest[1..] : rest);
                }
                html.Append("<blockquote>");
                Blocks(quote, html, tight: false);
                html.Append("</blockquote>");
            }
            else if (TableColumns(lines, i) is { } columns)
            {
                Flush();
                i = Table(lines, i, columns, html);
            }
            else if (ListItem().IsMatch(line))
            {
                Flush();
                i = List(lines, i, html);
            }
            else
            {
                paragraph.Add(line.Trim());
                i++;
            }
        }
        Flush();
    }

    private static int Code(IReadOnlyList<string> lines, int i, Match open, StringBuilder html)
    {
        var indent = open.Groups[1].Length;
        var fence = open.Groups[2].Value;
        var code = new List<string>();
        for (i++; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length >= fence.Length && trimmed.TrimEnd(fence[0]).Length == 0)
            {
                i++;
                break;
            }
            var line = lines[i];
            var strip = 0;
            while (strip < indent && strip < line.Length && line[strip] == ' ')
                strip++;
            code.Add(line[strip..]);
        }
        html.Append("<pre><code>").Append(Encode(string.Join('\n', code))).Append("</code></pre>");
        return i;
    }

    private static int List(IReadOnlyList<string> lines, int i, StringBuilder html)
    {
        var first = ListItem().Match(lines[i]);
        var indent = first.Groups[1].Length;
        var ordered = char.IsDigit(first.Groups[2].Value[0]);
        var items = new List<List<string>>();
        var loose = false;
        var blank = false;
        var content = 0;
        for (; i < lines.Count; i++)
        {
            var line = lines[i];
            var m = ListItem().Match(line);
            if (
                m.Success
                && m.Groups[1].Length == indent
                && char.IsDigit(m.Groups[2].Value[0]) == ordered
            )
            {
                loose |= blank && items.Count > 0;
                items.Add([m.Groups[4].Value]);
                content = indent + m.Groups[2].Length + Math.Clamp(m.Groups[3].Length, 1, 4);
                blank = false;
            }
            else if (line.Trim().Length == 0)
            {
                blank = true;
            }
            else if (LeadingSpaces(line) > indent || !blank && !m.Success && !StartsBlock(line))
            {
                // A nested line, or a lazy continuation of the item's last paragraph
                if (blank)
                {
                    loose |= LeadingSpaces(line) >= content;
                    items[^1].Add("");
                }
                items[^1].Add(line[Math.Min(LeadingSpaces(line), content)..]);
                blank = false;
            }
            else
            {
                break;
            }
        }
        var start = ordered ? first.Groups[2].Value[..^1].TrimStart('0') : "";
        html.Append(
            ordered ? (start is "" or "1" ? "<ol>" : $"<ol start=\"{int.Parse(start)}\">") : "<ul>"
        );
        foreach (var item in items)
        {
            html.Append("<li>");
            Blocks(item, html, tight: !loose);
            html.Append("</li>");
        }
        html.Append(ordered ? "</ol>" : "</ul>");
        return i;
    }

    private static bool StartsBlock(string line) =>
        Fence().IsMatch(line)
        || Heading().IsMatch(line)
        || Rule().IsMatch(line)
        || line.TrimStart().StartsWith('>');

    private static int LeadingSpaces(string line) => line.Length - line.TrimStart(' ').Length;

    /// <summary>The alignments of a table whose header row starts at line i, or null if none starts there.</summary>
    private static string?[]? TableColumns(IReadOnlyList<string> lines, int i)
    {
        if (
            i + 1 >= lines.Count
            || !lines[i].Contains('|')
            || !TableSeparator().IsMatch(lines[i + 1])
        )
            return null;
        var separator = Cells(lines[i + 1]);
        if (separator.Count != Cells(lines[i]).Count)
            return null;
        return separator
            .Select(cell =>
                (cell.StartsWith(':'), cell.EndsWith(':')) switch
                {
                    (true, true) => "center",
                    (false, true) => "right",
                    (true, false) => "left",
                    _ => null,
                }
            )
            .ToArray();
    }

    private static int Table(
        IReadOnlyList<string> lines,
        int i,
        string?[] columns,
        StringBuilder html
    )
    {
        void Row(string line, string tag)
        {
            var cells = Cells(line);
            html.Append("<tr>");
            for (var c = 0; c < columns.Length; c++)
            {
                html.Append('<').Append(tag);
                if (columns[c] is { } align)
                    html.Append($" style=\"text-align:{align}\"");
                html.Append('>')
                    .Append(c < cells.Count ? Inline(cells[c]) : "")
                    .Append("</")
                    .Append(tag)
                    .Append('>');
            }
            html.Append("</tr>");
        }

        html.Append("<div class=\"assistant-md-table\"><table><thead>");
        Row(lines[i], "th");
        html.Append("</thead><tbody>");
        for (i += 2; i < lines.Count && lines[i].Trim().Length > 0 && lines[i].Contains('|'); i++)
            Row(lines[i], "td");
        html.Append("</tbody></table></div>");
        return i;
    }

    private static List<string> Cells(string line)
    {
        var row = line.Trim();
        if (row.StartsWith('|'))
            row = row[1..];
        if (row.EndsWith('|') && !row.EndsWith("\\|"))
            row = row[..^1];
        return CellSplit().Split(row).Select(cell => cell.Trim().Replace("\\|", "|")).ToList();
    }

    private static string Inline(string text)
    {
        // Pieces of finished HTML wait in the stash, behind markers that can't occur in the text
        var stash = new List<string>();
        string Keep(string html)
        {
            stash.Add(html);
            return $"\u0001{stash.Count - 1}\u0002";
        }

        text = text.Replace("\u0001", "").Replace("\u0002", "");
        text = CodeSpan()
            .Replace(text, m => Keep($"<code>{Encode(m.Groups[2].Value.Trim())}</code>"));
        text = Escape().Replace(text, m => Keep(Encode(m.Groups[1].Value)));
        text = AutoLink()
            .Replace(text, m => Keep(Anchor(m.Groups[1].Value, Encode(m.Groups[1].Value))));
        text = Link()
            .Replace(
                text,
                m =>
                    IsSafeUrl(m.Groups[2].Value)
                        ? Keep(Anchor(m.Groups[2].Value, Emphasis(Encode(m.Groups[1].Value))))
                        : m.Groups[1].Value
            );
        text = BareUrl().Replace(text, m => Keep(Anchor(m.Value, Encode(m.Value))));
        text = Emphasis(Encode(text));
        while (text.Contains('\u0001'))
            text = Stashed().Replace(text, m => stash[int.Parse(m.Groups[1].Value)]);
        return text;
    }

    private static string Emphasis(string html)
    {
        html = StrongEmphasis().Replace(html, "<strong><em>$1</em></strong>");
        html = Strong().Replace(html, "<strong>$1$2</strong>");
        html = Emph().Replace(html, "<em>$1$2</em>");
        return Strike().Replace(html, "<del>$1</del>");
    }

    private static string Anchor(string url, string label) =>
        url.StartsWith('/')
            ? $"<a href=\"{Encode(url)}\">{label}</a>"
            : $"<a href=\"{Encode(url)}\" target=\"_blank\" rel=\"noopener noreferrer nofollow\">{label}</a>";

    /// <summary>http, https and mailto URLs, and paths on this site; never javascript:, data: or the like.</summary>
    internal static bool IsSafeUrl(string url) =>
        url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith('/') && !url.StartsWith("//") && !url.StartsWith("/\\");

    private static string Encode(string text) => WebUtility.HtmlEncode(text);

    [GeneratedRegex(@"^( {0,3})(`{3,}|~{3,})")]
    private static partial Regex Fence();

    [GeneratedRegex(@"^ {0,3}(#{1,6})[ \t]+(.*?)(?:[ \t]+#+)?[ \t]*$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^ {0,3}([-*_])(?:[ \t]*\1){2,}[ \t]*$")]
    private static partial Regex Rule();

    [GeneratedRegex(@"^( *)([-*+]|\d{1,9}[.)])( +|$)(.*)$")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"^\s*\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)*\|?\s*$")]
    private static partial Regex TableSeparator();

    [GeneratedRegex(@"(?<!\\)\|")]
    private static partial Regex CellSplit();

    [GeneratedRegex(@"(?<!`)(`+)(?!`)(.+?)(?<!`)\1(?!`)")]
    private static partial Regex CodeSpan();

    [GeneratedRegex(@"\\([\\`*_{}\[\]()#+\-.!|~<>])")]
    private static partial Regex Escape();

    [GeneratedRegex(@"<((?:https?://|mailto:)[^\s<>\u0001\u0002]+)>", RegexOptions.IgnoreCase)]
    private static partial Regex AutoLink();

    [GeneratedRegex(
        @"\[([^\[\]]+)\]\(\s*([^\s()]+(?:\([^\s()]*\)[^\s()]*)?)(?:\s+""[^""]*"")?\s*\)"
    )]
    private static partial Regex Link();

    [GeneratedRegex(
        @"(?<![\w/""=])https?://[^\s<>()\u0001\u0002]*[^\s<>().,;:!?'""*_\u0001\u0002]",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex BareUrl();

    [GeneratedRegex(@"\u0001(\d+)\u0002")]
    private static partial Regex Stashed();

    [GeneratedRegex(@"\*\*\*(?=\S)(.+?)(?<=\S)\*\*\*")]
    private static partial Regex StrongEmphasis();

    [GeneratedRegex(@"\*\*(?=\S)(.+?)(?<=\S)\*\*|(?<!\w)__(?=\S)(.+?)(?<=\S)__(?!\w)")]
    private static partial Regex Strong();

    [GeneratedRegex(
        @"(?<!\*)\*(?=[^\s*])([^*]+?)(?<=[^\s*])\*(?!\*)|(?<![\w_])_(?=[^\s_])([^_]+?)(?<=[^\s_])_(?![\w_])"
    )]
    private static partial Regex Emph();

    [GeneratedRegex(@"~~(?=\S)(.+?)(?<=\S)~~")]
    private static partial Regex Strike();
}
