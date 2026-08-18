using System.IO;
using RedBloom.Models;

namespace RedBloom.Services;

/// <summary>
/// The things a project can drop onto its relationship tree: its chats and rooms, the files and
/// folders in the project itself and in each linked source, and the sources themselves. Built once
/// and shared by the inline graph and the expanded one so both offer the same items.
/// </summary>
public static class ProjectPalette
{
    public readonly record struct Item(string Kind, string RefId, string Label);

    /// <summary>A folder in the tree shown for a source: its name, path, and its own subfolders.</summary>
    public sealed record FolderNode(string Name, string Path, List<FolderNode> Children);

    /// <summary>One source's folder tree — the source's name and the folders beneath it.</summary>
    public sealed record SourceFolders(string Source, List<FolderNode> Folders);

    private static readonly string[] Skip = [".git", ".vs", "bin", "obj", "node_modules", "packages", ".chats", ".rooms"];

    /// <summary>
    /// The folders inside the project and each local source, as a nested tree, so the connection menu
    /// can offer sub-projects hierarchically. Capped in depth and breadth so a huge repo stays light.
    /// </summary>
    public static List<SourceFolders> FolderTree(Project project)
    {
        var forest = new List<SourceFolders>();
        var budget = new int[] { 1500 };

        void AddRoot(string label, string path)
        {
            if (path.Length == 0 || !Directory.Exists(path))
            {
                return;
            }

            var folders = ChildFolders(path, 0, budget);
            if (folders.Count > 0)
            {
                forest.Add(new SourceFolders(label, folders));
            }
        }

        AddRoot(project.Name, project.Folder);
        foreach (var source in project.Sources)
        {
            AddRoot(source.Name, source.Path);
        }

        return forest;
    }

    private static List<FolderNode> ChildFolders(string dir, int depth, int[] budget)
    {
        var nodes = new List<FolderNode>();
        if (depth >= 5 || budget[0] <= 0)
        {
            return nodes;
        }

        try
        {
            foreach (var sub in Directory.EnumerateDirectories(dir)
                         .Where(d => !Skip.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
                         .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                if (budget[0]-- <= 0)
                {
                    break;
                }

                nodes.Add(new FolderNode(Path.GetFileName(sub), sub, ChildFolders(sub, depth + 1, budget)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder we cannot read contributes no children.
        }

        return nodes;
    }

    public static List<Item> Build(Project project)
    {
        var list = new List<Item>();

        foreach (var chat in ChatStore.Chats.Where(c => c.ProjectId == project.Id))
        {
            list.Add(new Item("chat", chat.Id, chat.Title));
        }

        foreach (var room in RoomStore.Rooms.Where(r => r.ProjectId == project.Id))
        {
            list.Add(new Item("room", room.Id, room.Title));
        }

        foreach (var file in TopFiles(project.Folder))
        {
            list.Add(new Item("file", file, Path.GetFileName(file)));
        }

        foreach (var source in project.Sources)
        {
            list.Add(new Item("source", source.Id, source.Name));
        }

        // The folders inside each source are offered as a tree instead (see FolderTree), so the
        // connection menu can show sub-projects hierarchically rather than as one flat list.
        return list;
    }

    private static IEnumerable<string> TopFiles(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateFiles(folder).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Take(200).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

}
