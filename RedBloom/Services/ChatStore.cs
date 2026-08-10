using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using RedBloom.Models;

namespace RedBloom.Services;

/// <summary>
/// Every saved conversation, one file per chat.
/// </summary>
/// <remarks>
/// A single store for the whole app rather than one per view: the sidebar lists chats, a tab
/// writes to one, and deleting from the list has to be seen by both. Files are written whole
/// through a temporary name so a crash mid-write cannot leave a half-parsed conversation.
/// </remarks>
public static class ChatStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RedBloom",
        "chats");

    private static bool _loaded;

    /// <summary>All known chats, newest first.</summary>
    public static ObservableCollection<ChatSession> Chats { get; } = [];

    public static void Load()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;

        try
        {
            if (!Directory.Exists(Folder))
            {
                return;
            }

            var loaded = new List<ChatSession>();

            foreach (var file in Directory.EnumerateFiles(Folder, "*.json"))
            {
                try
                {
                    if (JsonSerializer.Deserialize<ChatSession>(File.ReadAllText(file), SerializerOptions)
                        is { } chat && chat.Id.Length > 0)
                    {
                        loaded.Add(chat);
                    }
                }
                catch (Exception ex) when (ex is JsonException or IOException)
                {
                    // One unreadable chat must not cost the rest of them.
                    Debug.WriteLine($"Skipping unreadable chat {file}: {ex.Message}");
                }
            }

            foreach (var chat in loaded.OrderByDescending(c => c.UpdatedAt))
            {
                Chats.Add(chat);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Could not read the chat folder: {ex.Message}");
        }
    }

    /// <summary>The chats belonging to one agent, newest first.</summary>
    public static IEnumerable<ChatSession> ForAgent(string agentId) =>
        Chats.Where(c => c.AgentId == agentId).OrderByDescending(c => c.UpdatedAt);

    /// <summary>Writes a chat, adding it to the list the first time it has anything to say.</summary>
    public static void Save(ChatSession chat)
    {
        if (chat.IsEmpty)
        {
            // An untouched chat is not worth a file; it exists only as the empty tab someone
            // opened and may yet close without asking anything.
            return;
        }

        if (!Chats.Contains(chat))
        {
            Chats.Insert(0, chat);
        }

        try
        {
            Directory.CreateDirectory(Folder);
            var path = Path.Combine(Folder, chat.Id + ".json");
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(chat, SerializerOptions));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Could not save chat {chat.Id}: {ex.Message}");
        }
    }

    public static void Delete(ChatSession chat)
    {
        Chats.Remove(chat);

        try
        {
            var path = Path.Combine(Folder, chat.Id + ".json");

            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Could not delete chat {chat.Id}: {ex.Message}");
        }
    }

    /// <summary>Drops every chat belonging to an agent that no longer exists.</summary>
    public static void DeleteForAgent(string agentId)
    {
        foreach (var chat in Chats.Where(c => c.AgentId == agentId).ToList())
        {
            Delete(chat);
        }
    }
}
