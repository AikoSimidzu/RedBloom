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
            try
            {
                if (ReadSession(file) is { Turns.Count: > 0 } chat)
                {
                    chats.Add(chat);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OutOfMemoryException)
            {
                // One unreadable or enormous session must not sink the whole list — skip it and
                // keep the rest importable.
            }
        }

        return [.. chats.OrderByDescending(c => c.Updated)];
    }

    /// <summary>
    /// Discovers sessions off the UI thread, reporting how many of the total have been read so a
    /// dialog can show a progress bar rather than freeze while a big history is scanned.
    /// </summary>
    public static Task<IReadOnlyList<ImportedChat>> DiscoverAsync(
        IProgress<(int Done, int Total)>? progress = null, CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<ImportedChat>>(() =>
        {
            if (!Directory.Exists(ProjectsRoot))
            {
                return [];
            }

            var files = EnumerateSessions().ToList();
            var chats = new List<ImportedChat>();
            progress?.Report((0, files.Count));

            for (var i = 0; i < files.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    if (ReadSession(files[i]) is { Turns.Count: > 0 } chat)
                    {
                        chats.Add(chat);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OutOfMemoryException)
                {
                    // Skip the unreadable or enormous one; keep scanning the rest.
                }

                progress?.Report((i + 1, files.Count));
            }

            return [.. chats.OrderByDescending(c => c.Updated)];
        }, cancellationToken);
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

    /// <summary>
    /// A line longer than this is a tool dump — a whole file read back, a huge command output — not
    /// a spoken message, so it is skipped without parsing. This is what keeps a large session, whose
    /// weight is all in those lines, from exhausting memory the moment it is read.
    /// </summary>
    private const int MaxLineChars = 1_500_000;

    private static ImportedChat? ReadSession(string file)
    {
        var turns = new List<ChatTurn>();
        var title = string.Empty;
        var cwd = string.Empty;
        var updated = DateTime.MinValue;

        // Streamed line by line rather than read whole: a Claude Code session can be hundreds of
        // megabytes of embedded tool output, and loading it all at once — as ReadAllLines did — is
        // what made a big chat impossible to import.
        foreach (var line in File.ReadLines(file))
        {
            if (line.Length == 0 || line[0] != '{' || line.Length > MaxLineChars)
            {
                continue;
            }

            try
            {
                // Parsed and read inside the using — nothing is cloned out, so a huge line's memory
                // is freed as soon as the little text we want has been copied into a string.
                using var doc = JsonDocument.Parse(line);
                var o = doc.RootElement;

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
            catch (JsonException)
            {
                // A malformed line is skipped, the rest of the session still read.
            }
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
    public static bool Import(ImportedChat chat, string agentId)
    {
        if (Build(chat, agentId) is not { } session)
        {
            return false;
        }

        ChatStore.Save(session);
        return true;
    }

    /// <summary>
    /// Imports a session under the chosen agent, writing its file off the UI thread. Returns false
    /// when this session has already been imported under that agent.
    /// </summary>
    public static async Task<bool> ImportAsync(ImportedChat chat, string agentId)
    {
        if (Build(chat, agentId) is not { } session)
        {
            return false;
        }

        await ChatStore.SaveAsync(session).ConfigureAwait(true);
        return true;
    }

    /// <summary>
    /// The name imported assistant lines are attributed to in a room — one voice, no longer a
    /// participant, just a label so the transcript still reads as a back-and-forth.
    /// </summary>
    public static string AssistantName => LocalizationService.T("L_ImportAssistant");

    /// <summary>Appends a session to an existing room, its assistant lines under the imported name.</summary>
    public static async Task ImportToRoomAsync(ImportedChat chat, ChatRoom room)
    {
        AppendTo(room, chat);
        room.Touch();
        await RoomStore.SaveAsync(room).ConfigureAwait(true);
    }

    /// <summary>Makes a new room out of a session and returns its id, so the caller can land on it.</summary>
    public static async Task<string> ImportToNewRoomAsync(ImportedChat chat)
    {
        var room = new ChatRoom
        {
            Title = chat.Title,
            CreatedAt = chat.Updated,
            UpdatedAt = chat.Updated,
        };

        AppendTo(room, chat);
        await RoomStore.SaveAsync(room).ConfigureAwait(true);
        return room.Id;
    }

    private static void AppendTo(ChatRoom room, ImportedChat chat)
    {
        var assistant = AssistantName;

        foreach (var turn in chat.Turns)
        {
            room.Turns.Add(new ChatTurn
            {
                Role = turn.Role,
                Text = turn.Text,
                Speaker = turn.Role == "assistant" ? assistant : string.Empty,
            });
        }
    }

    private static ChatSession? Build(ImportedChat chat, string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            agentId = ClaudeCli.AgentId;
        }

        // Keyed by agent as well as source, so the same session can be brought in under more than
        // one agent, while importing it twice under the same one is still a no-op. The Claude CLI
        // agent keeps the old plain key, so chats imported before this stay recognised.
        var id = agentId == ClaudeCli.AgentId
            ? IdPrefix + chat.SourceId
            : IdPrefix + Sanitize(agentId) + "_" + chat.SourceId;

        if (ChatStore.Chats.Any(c => c.Id == id))
        {
            return null;
        }

        return new ChatSession
        {
            Id = id,
            AgentId = agentId,
            Title = chat.Title,
            CreatedAt = chat.Updated,
            UpdatedAt = chat.Updated,
            Turns = [.. chat.Turns],
        };
    }

    private static string Sanitize(string value)
    {
        var chars = value.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray();
        return chars.Length > 0 ? new string(chars) : "agent";
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
