using System.Text;
using RedBloom.Models;

namespace RedBloom.Services;

/// <summary>Writes a chat out as a plain Markdown transcript, for keeping or sharing outside the app.</summary>
public static class ChatExport
{
    /// <summary>A sensible file name for a chat's export, from its title.</summary>
    public static string SuggestName(ChatSession chat)
    {
        var title = string.IsNullOrWhiteSpace(chat.Title) ? "chat" : chat.Title;

        foreach (var bad in System.IO.Path.GetInvalidFileNameChars())
        {
            title = title.Replace(bad, ' ');
        }

        title = title.Trim();
        return (title.Length > 0 ? title : "chat") + ".md";
    }

    /// <summary>The whole conversation as Markdown: a heading, then each turn under who said it.</summary>
    public static string ToMarkdown(ChatSession chat)
    {
        var sb = new StringBuilder();

        sb.Append("# ").AppendLine(string.IsNullOrWhiteSpace(chat.Title) ? "Chat" : chat.Title);
        sb.Append("_").Append(chat.UpdatedAt.ToString("yyyy-MM-dd HH:mm")).AppendLine("_").AppendLine();

        foreach (var turn in chat.Turns)
        {
            switch (turn.Role)
            {
                case "user":
                    sb.AppendLine("## You").AppendLine();
                    sb.AppendLine(turn.Text.Trim()).AppendLine();
                    AppendAttachments(sb, turn);
                    break;

                case "assistant":
                    sb.Append("## ").AppendLine(Speaker(chat, turn));
                    sb.AppendLine();
                    sb.AppendLine(turn.Text.Trim()).AppendLine();
                    break;

                case "command":
                    // The command the agent ran, with its diff, kept as a fenced block.
                    sb.AppendLine("### Command").AppendLine();
                    sb.AppendLine("```").AppendLine(turn.Command.Trim()).AppendLine("```").AppendLine();

                    if (turn.Diff.Length > 0)
                    {
                        sb.AppendLine("```diff").AppendLine(StripHeader(turn.Diff).Trim()).AppendLine("```").AppendLine();
                    }

                    break;
            }
        }

        return sb.ToString().TrimEnd() + "\n";
    }

    private static string Speaker(ChatSession chat, ChatTurn turn) =>
        turn.Speaker.Length > 0 ? turn.Speaker
        : chat.BotName.Length > 0 ? chat.BotName
        : "Agent";

    private static void AppendAttachments(StringBuilder sb, ChatTurn turn)
    {
        if (turn.Attachments.Count == 0)
        {
            return;
        }

        sb.AppendLine("_Attached:_");
        foreach (var path in turn.Attachments)
        {
            sb.Append("- `").Append(path).AppendLine("`");
        }

        sb.AppendLine();
    }

    /// <summary>Drops the internal <c># repo:</c> header line a diff carries for the jump-to-file action.</summary>
    private static string StripHeader(string diff) =>
        diff.StartsWith("# repo: ", StringComparison.Ordinal) && diff.IndexOf('\n') is var nl and >= 0
            ? diff[(nl + 1)..]
            : diff;
}
