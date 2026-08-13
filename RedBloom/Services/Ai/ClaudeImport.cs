using System.IO;
using System.Text.Json;
using RedBloom.Models;

namespace RedBloom.Services.Ai;

/// <summary>One Claude Code session on disk, read enough to list and import it.</summary>
public sealed record ImportedChat(string SourceId, string Title, string Cwd, DateTime Updated, List<ChatTurn> Turns)
{
    public int Messages => Turns.Count;
}

/// <summary>
/// Reads Claude Code's own session logs so past conversations can be brought into RedBloom.
/// </summary>
/// <remarks>
/// Claude Code keeps one JSON-per-line file per session under <c>~/.claude/projects/&lt;cwd&gt;/</c>.
/// Only the plain back-and-forth is taken — the user's messages and the assistant's text — while the
/// tool calls, reasoning, snapshots and the tool-result turns that fill the transcript are left out,
/// so what lands in RedBloom reads as the conversation rather than as a machine log. Imported chats
/// are filed under the Claude CLI agent and keyed by their source id, so importing twice does not
/// double them.
/// </remarks>
public static class ClaudeImport
{
    /// <summary>The prefix an imported chat's id carries, so a re-import is recognised and skipped.</summary>
    public const string IdPrefix = "cc_";

    private static string ProjectsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    /// <summary>Whether there is anything to import — a cheap check for showing the entry point.</summary>
    public static bool Available => Directory.Exists(ProjectsRoot);

    /// <summary>Every readable session, newest first.</summary>
    public static IReadOnlyList<ImportedChat> Discover()
    {
        if (!Directory.Exists(ProjectsRoot))
        {
            return [];
        }

        var chats = new List<ImportedChat>();

        foreach (var file in EnumerateSessions())
        {
            if (ReadSession(file) is { Turns.Count: > 0 } chat)
            {
                chats.Add(chat);
            }
        }

        return [.. chats.OrderByDescending(c => c.Updated)];
    }

    private static IEnumerable<string> EnumerateSessions()
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(ProjectsRoot, "*.jsonl", SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var file in files)
        {
            yield return file;
        }
    }

    private static ImportedChat? ReadSession(string file)
    {
        var turns = new List<ChatTurn>();
        var title = string.Empty;
        var cwd = string.Empty;
        var updated = DateTime.MinValue;

        string[] lines;
        try
        {
            lines = File.ReadAllLines(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        foreach (var line in lines)
        {
            if (line.Length == 0 || line[0] != '{')
            {
                continue;
            }

            JsonElement o;
            try
            {
                using var doc = JsonDocument.Parse(line);
                o = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                continue;
            }

            if (Str(o, "cwd") is { Length: > 0 } c)
            {
                cwd = c;
            }

            if (Str(o, "aiTitle") is { Length: > 0 } t)
            {
                title = t;
            }

            if (When(o) is { } when && when > updated)
            {
                updated = when;
            }

            var kind = Str(o, "type");

            if (kind is not ("user" or "assistant"))
            {
                continue;
            }

            // Meta rows, side chains and transcript-only lines are bookkeeping, not conversation.
            if (Flag(o, "isMeta") || Flag(o, "isSidechain") || Flag(o, "isVisibleInTranscriptOnly"))
            {
                continue;
            }

            if (!o.TryGetProperty("message", out var message)
                || !message.TryGetProperty("content", out var content))
            {
                continue;
            }

            var text = TextOf(content).Trim();

            if (text.Length == 0)
            {
                continue;
            }

            turns.Add(new ChatTurn
            {
                Role = kind == "assistant" ? "assistant" : "user",
                Text = text,
            });
        }

        if (turns.Count == 0)
        {
            return null;
        }

        if (title.Length == 0)
        {
            title = ChatSession.TitleFrom(turns.First(t => t.Role == "user").Text);
        }

        var id = Path.GetFileNameWithoutExtension(file);

        return new ImportedChat(id, title, cwd, updated == DateTime.MinValue ? File.GetLastWriteTime(file) : updated, turns);
    }

    /// <summary>
    /// Turns a session into a saved chat under the Claude CLI agent. Returns false when a chat with
    /// this source id already exists, so importing the same session twice is a no-op.
    /// </summary>
    public static bool Import(ImportedChat chat)
    {
        var id = IdPrefix + chat.SourceId;

        if (ChatStore.Chats.Any(c => c.Id == id))
        {
            return false;
        }

        var session = new ChatSession
        {
            Id = id,
            AgentId = ClaudeCli.AgentId,
            Title = chat.Title,
            CreatedAt = chat.Updated,
            UpdatedAt = chat.Updated,
            Turns = [.. chat.Turns],
        };

        ChatStore.Save(session);
        return true;
    }

    // ---- json helpers ----

    private static string TextOf(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? string.Empty;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var parts = new List<string>();

        foreach (var block in content.EnumerateArray())
        {
            // Only the spoken text; thinking, tool calls and tool results are left out.
            if (block.ValueKind == JsonValueKind.Object
                && block.TryGetProperty("type", out var bt)
                && bt.GetString() == "text"
                && block.TryGetProperty("text", out var text)
                && text.GetString() is { Length: > 0 } said)
            {
                parts.Add(said);
            }
        }

        return string.Join("\n\n", parts);
    }

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Flag(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static DateTime? When(JsonElement o) =>
        o.TryGetProperty("timestamp", out var v) && v.ValueKind == JsonValueKind.String
        && DateTime.TryParse(v.GetString(), out var when)
            ? when.ToLocalTime()
            : null;
}
