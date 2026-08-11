using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using RedBloom.Models;

namespace RedBloom.Services;

/// <summary>
/// Every saved room, one file per room.
/// </summary>
/// <remarks>
/// A sibling of <see cref="ChatStore"/> with the same shape and the same reasons — a room grows
/// without limit and is written after every turn, so it lives in its own file rather than in the
/// settings. Kept a store apart from the chats because a room is listed and opened in its own part
/// of the sidebar, and mixing the two lists would only need untangling everywhere they are shown.
/// </remarks>
public static class RoomStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RedBloom", "rooms");

    private static bool _loaded;

    /// <summary>All known rooms, newest first.</summary>
    public static ObservableCollection<ChatRoom> Rooms { get; } = [];

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

            var loaded = new List<ChatRoom>();

            foreach (var file in Directory.EnumerateFiles(Folder, "*.json"))
            {
                try
                {
                    if (JsonSerializer.Deserialize<ChatRoom>(File.ReadAllText(file), SerializerOptions)
                        is { } room && room.Id.Length > 0)
                    {
                        loaded.Add(room);
                    }
                }
                catch (Exception ex) when (ex is JsonException or IOException)
                {
                    Debug.WriteLine($"Skipping unreadable room {file}: {ex.Message}");
                }
            }

            foreach (var room in loaded.OrderByDescending(r => r.UpdatedAt))
            {
                Rooms.Add(room);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Could not read the room folder: {ex.Message}");
        }
    }

    /// <summary>Writes a room, adding it to the list the first time it is saved.</summary>
    public static void Save(ChatRoom room)
    {
        if (!Rooms.Contains(room))
        {
            Rooms.Insert(0, room);
        }

        try
        {
            Directory.CreateDirectory(Folder);
            var path = Path.Combine(Folder, room.Id + ".json");
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(room, SerializerOptions));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Could not save room {room.Id}: {ex.Message}");
        }
    }

    public static void Delete(ChatRoom room)
    {
        Rooms.Remove(room);

        try
        {
            var path = Path.Combine(Folder, room.Id + ".json");

            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Could not delete room {room.Id}: {ex.Message}");
        }
    }
}
