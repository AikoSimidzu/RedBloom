using System.Text;
using System.Text.RegularExpressions;
using RedBloom.Services;

namespace RedBloom.Services.Ai;

/// <summary>
/// Turns the markdown a model writes into the HTML the chat view shows.
/// </summary>
/// <remarks>
/// Written here rather than pulled in as a JavaScript library for one reason: everything that
/// reaches the page is model output, and model output is untrusted — it can carry text a web
/// page would happily execute. Doing the conversion in C# means every character is escaped
/// first and the tags are only ever ones this file emits, so there is no path from a reply to
/// running script. It also keeps the page free of a vendored dependency to update.
/// <para>
/// The subset covered is what replies actually contain: fenced and inline code, headings,
/// bullet and numbered lists, block quotes, bold, italic, links, and rules. Anything else is
/// left as the plain text it already is.
/// </para>
/// </remarks>
public static partial class Markdown
{
    /// <summary>Code longer than this is collapsed, with the rest behind a toggle.</summary>
    private const int CollapseAfterLines = 18;

    public static string ToHtml(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return string.Empty;
        }

        var html = new StringBuilder();
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var index = 0;

        while (index < lines.Length)
        {
            var line = lines[index];

            if (FenceStart().Match(line) is { Success: true } fence)
            {
                index = AppendCodeBlock(html, lines, index, fence.Groups[1].Value.Trim());
                continue;
            }

            if (line.Trim().Length == 0)
            {
                index++;
                continue;
            }

            if (HeadingLine().Match(line) is { Success: true } heading)
            {
                var level = Math.Min(6, heading.Groups[1].Value.Length);
                html.Append("<h").Append(level).Append('>')
                    .Append(Inline(heading.Groups[2].Value))
                    .Append("</h").Append(level).Append(">\n");
                index++;
                continue;
            }

            if (RuleLine().IsMatch(line))
            {
                html.Append("<hr>\n");
                index++;
                continue;
            }

            if (IsTableAt(lines, index))
            {
                index = AppendTable(html, lines, index);
                continue;
            }

            if (BulletLine().IsMatch(line) || NumberedLine().IsMatch(line))
            {
                index = AppendList(html, lines, index);
                continue;
            }

            if (line.TrimStart().StartsWith('>'))
            {
                index = AppendQuote(html, lines, index);
                continue;
            }

            index = AppendParagraph(html, lines, index);
        }

        return html.ToString();
    }

    private static int AppendCodeBlock(StringBuilder html, string[] lines, int index, string language)
    {
        var code = new StringBuilder();
        index++;

        while (index < lines.Length && !FenceEnd().IsMatch(lines[index]))
        {
            code.Append(lines[index]).Append('\n');
            index++;
        }

        // Step over the closing fence, if the model wrote one — a reply cut short by a token
        // limit often has not, and the block should still render.
        if (index < lines.Length)
        {
            index++;
        }

        var text = code.ToString().TrimEnd('\n');
        var lineCount = text.Length == 0 ? 0 : text.Count(c => c == '\n') + 1;
        var collapsed = lineCount > CollapseAfterLines;

        html.Append("<figure class=\"code")
            .Append(collapsed ? " collapsed" : string.Empty)
            .Append("\"><figcaption><span class=\"lang\">")
            .Append(Escape(language.Length > 0 ? language : "text"))
            .Append("</span><span class=\"tools\">");

        if (collapsed)
        {
            html.Append("<button class=\"more\" data-act=\"expand\">")
                .Append(Escape(string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    LocalizationService.T("L_ChatShowAll"),
                    lineCount)))
                .Append("</button>");
        }

        // The text to copy rides on the element rather than being read back out of the DOM, so
        // what lands on the clipboard is exactly what the model wrote.
        html.Append("<button class=\"copy\" data-act=\"copy\" data-code=\"")
            .Append(Escape(text))
            .Append("\">")
            .Append(Escape(LocalizationService.T("L_ChatCopy")))
            .Append("</button></span></figcaption><pre><code>")
            .Append(CodeHighlighter.Highlight(text))
            .Append("</code></pre></figure>\n");

        return index;
    }

    private static int AppendList(StringBuilder html, string[] lines, int index)
    {
        var numbered = NumberedLine().IsMatch(lines[index]);
        html.Append(numbered ? "<ol>\n" : "<ul>\n");

        while (index < lines.Length)
        {
            var line = lines[index];
            var bullet = BulletLine().Match(line);
            var number = NumberedLine().Match(line);

            if (!bullet.Success && !number.Success)
            {
                break;
            }

            var content = bullet.Success ? bullet.Groups[1].Value : number.Groups[1].Value;
            html.Append("<li>").Append(Inline(content)).Append("</li>\n");
            index++;
        }

        html.Append(numbered ? "</ol>\n" : "</ul>\n");
        return index;
    }

    /// <summary>
    /// A table is a header row followed by a row of dashes — the dashes are what tell it apart
    /// from an ordinary line that happens to contain a pipe.
    /// </summary>
    private static bool IsTableAt(string[] lines, int index) =>
        index + 1 < lines.Length
        && lines[index].Contains('|', StringComparison.Ordinal)
        && TableDivider().IsMatch(lines[index + 1]);

    /// <remarks>
    /// Wrapped in a scrolling box rather than squeezed to fit: a table of paths or of numbers is
    /// unreadable once the columns are narrower than their contents, and the chat is a narrow
    /// panel. Rows below the header are taken for as long as they keep coming, so a table still
    /// being streamed renders as far as it has got instead of waiting for its last line.
    /// </remarks>
    private static int AppendTable(StringBuilder html, string[] lines, int index)
    {
        var headers = SplitRow(lines[index]);
        var alignments = SplitRow(lines[index + 1]).ConvertAll(AlignmentOf);
        index += 2;

        html.Append("<div class=\"tablebox\"><table><thead><tr>");

        for (var column = 0; column < headers.Count; column++)
        {
            html.Append("<th").Append(AlignAttribute(alignments, column)).Append('>')
                .Append(Inline(headers[column]))
                .Append("</th>");
        }

        html.Append("</tr></thead><tbody>");

        while (index < lines.Length
            && lines[index].Contains('|', StringComparison.Ordinal)
            && lines[index].Trim().Length > 0)
        {
            var cells = SplitRow(lines[index]);
            html.Append("<tr>");

            // Ragged rows are common in model output; missing cells are drawn empty rather than
            // shifting everything after them one column to the left.
            for (var column = 0; column < headers.Count; column++)
            {
                html.Append("<td").Append(AlignAttribute(alignments, column)).Append('>')
                    .Append(column < cells.Count ? Inline(cells[column]) : string.Empty)
                    .Append("</td>");
            }

            html.Append("</tr>");
            index++;
        }

        html.Append("</tbody></table></div>\n");

        return index;
    }

    /// <summary>The cells of one row, without the pipes that fence them.</summary>
    private static List<string> SplitRow(string line)
    {
        // An escaped pipe is content, not a cell boundary, so it is carried past the split.
        const string Escaped = "\u0001";

        var trimmed = line.Trim().Replace("\\|", Escaped, StringComparison.Ordinal);

        if (trimmed.StartsWith('|'))
        {
            trimmed = trimmed[1..];
        }

        if (trimmed.EndsWith('|'))
        {
            trimmed = trimmed[..^1];
        }

        return [.. trimmed.Split('|').Select(cell => cell.Trim().Replace(Escaped, "|", StringComparison.Ordinal))];
    }

    private static string AlignmentOf(string divider)
    {
        var cell = divider.Trim();

        return cell.StartsWith(':') && cell.EndsWith(':') ? "center"
            : cell.EndsWith(':') ? "right"
            : string.Empty;
    }

    private static string AlignAttribute(List<string> alignments, int column) =>
        column < alignments.Count && alignments[column].Length > 0
            ? $" class=\"{alignments[column]}\""
            : string.Empty;

    private static int AppendQuote(StringBuilder html, string[] lines, int index)
    {
        var text = new StringBuilder();

        while (index < lines.Length && lines[index].TrimStart().StartsWith('>'))
        {
            text.Append(lines[index].TrimStart().TrimStart('>').TrimStart()).Append(' ');
            index++;
        }

        html.Append("<blockquote>").Append(Inline(text.ToString().TrimEnd())).Append("</blockquote>\n");
        return index;
    }

    private static int AppendParagraph(StringBuilder html, string[] lines, int index)
    {
        var text = new StringBuilder();

        while (index < lines.Length)
        {
            var line = lines[index];

            // A paragraph ends at a blank line or wherever another block begins.
            if (line.Trim().Length == 0
                || FenceStart().IsMatch(line)
                || HeadingLine().IsMatch(line)
                || IsTableAt(lines, index)
                || BulletLine().IsMatch(line)
                || NumberedLine().IsMatch(line)
                || RuleLine().IsMatch(line)
                || line.TrimStart().StartsWith('>'))
            {
                break;
            }

            if (text.Length > 0)
            {
                text.Append('\n');
            }

            text.Append(line);
            index++;
        }

        if (text.Length > 0)
        {
            html.Append("<p>").Append(Inline(text.ToString())).Append("</p>\n");
        }

        return index;
    }

    /// <summary>
    /// Formatting inside a line. Escaping happens first, so every tag below is one this method
    /// put there and nothing from the model can become markup.
    /// </summary>
    private static string Inline(string text)
    {
        var html = Escape(text);

        // Inline code first: whatever is inside it must not then be read as emphasis.
        html = InlineCode().Replace(html, m => $"<code>{m.Groups[1].Value}</code>");

        html = BoldText().Replace(html, "<strong>$1</strong>");
        html = ItalicText().Replace(html, "<em>$1</em>");
        html = StrikeText().Replace(html, "<del>$1</del>");

        // Only http(s) links become anchors; anything else stays as text, so a reply cannot
        // hand the page a javascript: or file: target.
        html = MarkdownLink().Replace(html, m =>
            $"<a href=\"{m.Groups[2].Value}\" target=\"_blank\" rel=\"noreferrer\">{m.Groups[1].Value}</a>");

        // An @-mention of an agent is drawn like inline code. Only one that opens a token — at the
        // start or after whitespace — so an address like "a@b" is left alone, and one already
        // inside a code span or a link is preceded by ">" and so is not matched a second time.
        html = MentionText().Replace(html, m => $"<code class=\"mention\">@{m.Groups[1].Value}</code>");

        return html.Replace("\n", "<br>", StringComparison.Ordinal);
    }

    public static string Escape(string text) => text
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);

    [GeneratedRegex(@"^\s*```+\s*([A-Za-z0-9_+-]*)\s*$")]
    private static partial Regex FenceStart();

    [GeneratedRegex(@"^\s*```+\s*$")]
    private static partial Regex FenceEnd();

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")]
    private static partial Regex HeadingLine();

    [GeneratedRegex(@"^\s*([-*_])\s*\1\s*\1[\s\1]*$")]
    private static partial Regex RuleLine();

    [GeneratedRegex(@"^\s*[-*+]\s+(.*)$")]
    private static partial Regex BulletLine();

    /// <summary>The dashed row under a table's header, with optional alignment colons.</summary>
    [GeneratedRegex(@"^\s*\|?(\s*:?-+:?\s*\|)+\s*:?-*:?\s*\|?\s*$")]
    private static partial Regex TableDivider();

    [GeneratedRegex(@"^\s*\d+[.)]\s+(.*)$")]
    private static partial Regex NumberedLine();

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex InlineCode();

    [GeneratedRegex(@"\*\*([^*]+)\*\*")]
    private static partial Regex BoldText();

    [GeneratedRegex(@"(?<![*\w])\*([^*\n]+)\*(?![*\w])")]
    private static partial Regex ItalicText();

    [GeneratedRegex(@"~~([^~]+)~~")]
    private static partial Regex StrikeText();

    [GeneratedRegex(@"\[([^\]]+)\]\((https?://[^\s)]+)\)")]
    private static partial Regex MarkdownLink();

    /// <summary>An @-mention token, opening at the start of a line or after whitespace.</summary>
    [GeneratedRegex(@"(?<=^|\s)@([\p{L}\p{N}_-]+)")]
    private static partial Regex MentionText();
}
