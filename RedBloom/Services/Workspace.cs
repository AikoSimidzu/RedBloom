using System.IO;
using RedBloom.Models;

namespace RedBloom.Services;

/// <summary>
/// A working folder of its own for each chat and room — where its agent's commands run and its
/// files are made, kept apart from the user's home and from every other conversation.
/// </summary>
/// <remarks>
/// A loose chat's folder lives under the app data; once a chat belongs to a project, its folder
/// lives inside the project folder (under <c>.chats</c>/<c>.rooms</c>), so the work travels with the
/// project. Moving a chat into or out of a project moves the folder to match.
/// </remarks>
public static class Workspace
{
    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RedBloom",
        "workspaces");

    /// <summary>
    /// The working folder for a bare id, made if it is not there yet. Kept for callers that only
    /// have an id (dropped-file storage); a chat or room with a project should use the overloads.
    /// </summary>
    public static string For(string id) => Ensure(Path.Combine(Root, Sanitize(id)));

    /// <summary>The workspace for a room id, under a name that cannot collide with a chat's.</summary>
    public static string ForRoom(string roomId) => For("room-" + roomId);

    /// <summary>The working folder for a chat, inside its project when it has one.</summary>
    public static string ForChat(ChatSession chat) => Ensure(ChatDir(chat.ProjectId, chat.Id));

    /// <summary>The working folder for a room, inside its project when it has one.</summary>
    public static string ForRoom(ChatRoom room) => Ensure(RoomDir(room.ProjectId, room.Id));

    /// <summary>Moves a chat's folder when it changes project, so its files follow it.</summary>
    public static void MoveChat(string chatId, string fromProjectId, string toProjectId) =>
        MoveFolder(ChatDir(fromProjectId, chatId), ChatDir(toProjectId, chatId));

    /// <summary>Moves a room's folder when it changes project.</summary>
    public static void MoveRoom(string roomId, string fromProjectId, string toProjectId) =>
        MoveFolder(RoomDir(fromProjectId, roomId), RoomDir(toProjectId, roomId));

    private static string ChatDir(string projectId, string chatId) =>
        ProjectFolder(projectId) is { } folder
            ? Path.Combine(folder, ".chats", Sanitize(chatId))
            : Path.Combine(Root, Sanitize(chatId));

    private static string RoomDir(string projectId, string roomId) =>
        ProjectFolder(projectId) is { } folder
            ? Path.Combine(folder, ".rooms", Sanitize(roomId))
            : Path.Combine(Root, Sanitize("room-" + roomId));

    private static string? ProjectFolder(string projectId)
    {
        if (string.IsNullOrEmpty(projectId))
        {
            return null;
        }

        var project = ProjectStore.Projects.FirstOrDefault(p => p.Id == projectId);
        return project is { Folder.Length: > 0 } ? project.Folder : null;
    }

    private static string Ensure(string path)
    {
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

    private static void MoveFolder(string from, string to)
    {
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            if (!Directory.Exists(from))
            {
                return;
            }

            var parent = Path.GetDirectoryName(to);
            if (parent is { Length: > 0 })
            {
                Directory.CreateDirectory(parent);
            }

            // Never clobber an existing folder at the destination — a rare id clash keeps its files.
            if (!Directory.Exists(to))
            {
                Directory.Move(from, to);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Could not move workspace {from} -> {to}: {ex.Message}");
        }
    }

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
