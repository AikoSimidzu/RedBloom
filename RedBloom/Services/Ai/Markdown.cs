using System.Text;
using System.Text.RegularExpressions;

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
            html.Append("<button class=\"more\" data-act=\"expand\">show all ")
                .Append(lineCount)
                .Append(" lines</button>");
        }

        // The text to copy rides on the element rather than being read back out of the DOM, so
        // what lands on the clipboard is exactly what the model wrote.
        html.Append("<button class=\"copy\" data-act=\"copy\" data-code=\"")
            .Append(Escape(text))
            .Append("\">copy</button></span></figcaption><pre><code>")
            .Append(Escape(text))
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
}
