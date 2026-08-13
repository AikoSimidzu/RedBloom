using System.IO;

namespace RedBloom.Services;

/// <summary>
/// A working folder of its own for each chat and room — where its agent's commands run and its
/// files are made, kept apart from the user's home and from every other conversation.
/// </summary>
/// <remarks>
/// Before this, every command ran from the user's profile folder, so two chats working on
/// different things trod on each other and their scratch files piled up in the home directory.
/// A folder per conversation makes each one's work self-contained: it is the default working
/// directory, so "write a file" and "clone a repo" land somewhere that belongs to this chat, and
/// deleting the chat can take its workspace with it.
/// </remarks>
public static class Workspace
{
    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RedBloom",
        "workspaces");

    /// <summary>
    /// The working folder for a chat, made if it is not there yet. Falls back to the user's profile
    /// only if the folder cannot be created, so a command always has somewhere valid to run.
    /// </summary>
    /// <param name="id">The chat's id for a one-to-one chat, or <c>room-&lt;id&gt;</c> for a room.</param>
    public static string For(string id)
    {
        var safe = Sanitize(id);
        var path = Path.Combine(Root, safe);

        try
        {
            Directory.CreateDirectory(path);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
    }

    /// <summary>The workspace for a room, under a name that cannot collide with a chat's.</summary>
    public static string ForRoom(string roomId) => For("room-" + roomId);

    private static string Sanitize(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return "default";
        }

        var chars = id.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray();
        var name = new string(chars).Trim();
        return name.Length > 0 ? name : "default";
    }
}
