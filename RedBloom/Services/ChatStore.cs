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

    /// <summary>Where chats used to live, and where they still go if the program folder is read-only.</summary>
    private static readonly string Roaming = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RedBloom",
        "chats");

    private static readonly string Folder = ChooseFolder();

    private static bool _loaded;

    /// <summary>
    /// A <c>Chats</c> folder beside the program, or the roaming one when that cannot be written.
    /// </summary>
    /// <remarks>
    /// Keeping conversations next to the executable makes the whole thing portable — copy the
    /// folder to a stick and the chats come along. It only works where the program folder is
    /// writable, which an installed copy under Program Files is not, so that case quietly keeps
    /// the old location rather than losing every chat to a permission error.
    /// </remarks>
    private static string ChooseFolder()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "Chats");

        try
        {
            Directory.CreateDirectory(beside);

            // Creating the folder does not prove a file can be written in it: under Program Files
            // an existing directory still refuses one, and that only shows up on the first save.
            var probe = Path.Combine(beside, ".writable");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);

            Migrate(beside);

            return beside;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Keeping chats in {Roaming}: {beside} is not writable ({ex.Message})");

            return Roaming;
        }
    }

    /// <summary>Moves chats saved by an earlier version into the folder beside the program.</summary>
    private static void Migrate(string destination)
    {
        if (!Directory.Exists(Roaming))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(Roaming, "*.json"))
        {
            try
            {
                var moved = Path.Combine(destination, Path.GetFileName(file));

                // Never overwrite: if both exist the one beside the program is the newer home
                // and the roaming copy is a leftover.
                if (!File.Exists(moved))
                {
                    File.Move(file, moved);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"Could not move {file}: {ex.Message}");
            }
        }

        try
        {
            if (!Directory.EnumerateFileSystemEntries(Roaming).Any())
            {
                Directory.Delete(Roaming);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Left {Roaming} in place: {ex.Message}");
        }
    }

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

        WriteFile(chat);
    }

    /// <summary>
    /// Registers a chat and writes it to disk off the UI thread, for importing where the file may
    /// be large — so a big chat does not freeze the window while it is serialised and written.
    /// </summary>
    /// <remarks>
    /// The list is an <see cref="ObservableCollection{T}"/> bound to the UI, so it is added to on the
    /// calling (UI) thread; only the serialising and writing, which is the slow part, goes to the
    /// background.
    /// </remarks>
    public static Task SaveAsync(ChatSession chat)
    {
        if (chat.IsEmpty)
        {
            return Task.CompletedTask;
        }

        if (!Chats.Contains(chat))
        {
            Chats.Insert(0, chat);
        }

        return Task.Run(() => WriteFile(chat));
    }

    private static void WriteFile(ChatSession chat)
    {
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
