using System.IO;
using System.Text;
using RedBloom.Models;

namespace RedBloom.Services.Ai;

/// <summary>
/// Orients an agent working inside a project: its name and description, the PROJECT.md notes, the
/// linked source folders, and a shallow listing of the project folder. Added to the system preamble
/// so the agent begins already knowing where it is, and can then read the specific files it needs
/// with its own tools rather than being handed every file's contents.
/// </summary>
public static class ProjectContext
{
    private const int MaxNotes = 4000;
    private const int MaxTreeLines = 140;

    private static readonly string[] Skip =
        [".git", ".vs", "bin", "obj", "node_modules", "packages", ".chats", ".rooms", ".redbloom"];

    /// <summary>The context block for a chat filed under a project, or null when it is not.</summary>
    public static string? Build(ChatSession chat)
    {
        if (string.IsNullOrEmpty(chat.ProjectId)
            || ProjectStore.Projects.FirstOrDefault(p => p.Id == chat.ProjectId) is not { } project)
        {
            return null;
        }

        var text = new StringBuilder();
        text.Append("You are working inside the user's project \"").Append(project.Name).AppendLine("\".");

        if (!string.IsNullOrWhiteSpace(project.Description))
        {
            text.Append("Project description: ").AppendLine(project.Description.Trim());
        }

        if (project.Folder.Length > 0)
        {
            text.Append("Project folder (your working directory): ").AppendLine(project.Folder);
        }

        if (ReadNotes(project.Folder) is { Length: > 0 } notes)
        {
            text.AppendLine().AppendLine("Project notes (PROJECT.md):").AppendLine(notes);
        }

        if (project.Sources.Count > 0)
        {
            text.AppendLine().AppendLine("Linked source folders:");
            foreach (var source in project.Sources)
            {
                var where = source.Path.Length > 0 ? source.Path : source.Repo.Length > 0 ? source.Repo : source.Url;
                text.Append("- ").Append(source.Name).Append(" (").Append(source.Kind).Append("): ").AppendLine(where);
            }
        }

        if (Tree(project.Folder) is { Length: > 0 } tree)
        {
            text.AppendLine().AppendLine("Project folder contents (orientation only):").AppendLine(tree);
        }

        text.AppendLine().AppendLine(
            "Work against this project directly with your file tools and run_command — the working "
            + "directory is the project folder. Read the specific files you need; the listing above "
            + "is only to orient you, not the whole of what is there.");

        return text.ToString();
    }

    /// <summary>The absolute working directory a project chat should use, or null.</summary>
    public static string? WorkingDirectory(ChatSession chat) =>
        !string.IsNullOrEmpty(chat.ProjectId)
        && ProjectStore.Projects.FirstOrDefault(p => p.Id == chat.ProjectId) is { Folder.Length: > 0 } project
        && Directory.Exists(project.Folder)
            ? project.Folder
            : null;

    private static string ReadNotes(string folder)
    {
        if (folder.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            var path = Path.Combine(folder, "PROJECT.md");
            if (!File.Exists(path))
            {
                return string.Empty;
            }

            var text = File.ReadAllText(path).Trim();
            return text.Length > MaxNotes ? text[..MaxNotes] + "\n(notes cut for length)" : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    /// <summary>A shallow, breadth-first listing of the project folder — names only, no contents.</summary>
    private static string Tree(string folder)
    {
        if (folder.Length == 0 || !Directory.Exists(folder))
        {
            return string.Empty;
        }

        var lines = new List<string>();
        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((folder, 0));

        try
        {
            while (stack.Count > 0 && lines.Count < MaxTreeLines)
            {
                var (dir, depth) = stack.Pop();

                foreach (var entry in Directory.EnumerateFileSystemEntries(dir).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                {
                    var nameOnly = Path.GetFileName(entry);
                    var isDir = Directory.Exists(entry);

                    if (isDir && Skip.Contains(nameOnly, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (lines.Count >= MaxTreeLines)
                    {
                        break;
                    }

                    lines.Add(new string(' ', depth * 2) + nameOnly + (isDir ? "/" : string.Empty));

                    if (isDir && depth < 2)
                    {
                        stack.Push((entry, depth + 1));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Whatever was gathered before the error is still useful.
        }

        return string.Join('\n', lines);
    }
}
