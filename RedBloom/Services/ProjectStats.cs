using System.Diagnostics;
using System.IO;
using RedBloom.Models;

namespace RedBloom.Services;

/// <summary>
/// The numbers behind a project's activity: its chats and rooms, the files in its folder, and its
/// git state — gathered for the monitoring panel on the project home.
/// </summary>
public static class ProjectStats
{
    /// <summary>A snapshot of a project's activity at one moment.</summary>
    public readonly record struct Stats(
        int Chats, int ChatMessages,
        int Rooms, int RoomMessages,
        int Files, long Bytes,
        DateTime? LastActivity,
        GitInfo? Git);

    /// <summary>The git state of the project folder, when it is a repository.</summary>
    public readonly record struct GitInfo(string Branch, int Changes, string LastCommit);

    /// <summary>Gathers a project's activity off the UI thread.</summary>
    public static Task<Stats> ComputeAsync(Project project) => Task.Run(() =>
    {
        var chats = ChatStore.Chats.Where(c => c.ProjectId == project.Id).ToList();
        var rooms = RoomStore.Rooms.Where(r => r.ProjectId == project.Id).ToList();

        var chatMessages = chats.Sum(c => c.Turns.Count(t => t.Role is "user" or "assistant"));
        var roomMessages = rooms.Sum(r => r.Turns.Count(t => t.Role is "user" or "assistant"));

        var (files, bytes, newestFile) = CountFiles(project.Folder);

        DateTime? last = null;
        foreach (var when in chats.Select(c => c.UpdatedAt)
                     .Concat(rooms.Select(r => r.UpdatedAt))
                     .Concat(newestFile is { } f ? [f] : Array.Empty<DateTime>()))
        {
            if (last is null || when > last)
            {
                last = when;
            }
        }

        return new Stats(chats.Count, chatMessages, rooms.Count, roomMessages, files, bytes, last, Git(project.Folder));
    });

    /// <summary>Counts the files under the folder, skipping git internals and our own workspaces.</summary>
    private static (int Files, long Bytes, DateTime? Newest) CountFiles(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return (0, 0, null);
        }

        var files = 0;
        long bytes = 0;
        DateTime? newest = null;
        var stack = new Stack<string>();
        stack.Push(folder);

        while (stack.Count > 0 && files < 100000)
        {
            var dir = stack.Pop();

            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    var name = Path.GetFileName(sub);
                    if (name is ".git" or "node_modules" or ".chats" or ".rooms" or "bin" or "obj")
                    {
                        continue;
                    }

                    stack.Push(sub);
                }

                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    files++;

                    try
                    {
                        var info = new FileInfo(file);
                        bytes += info.Length;
                        if (newest is null || info.LastWriteTime > newest)
                        {
                            newest = info.LastWriteTime;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Skip a file we cannot stat.
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Skip a folder we cannot read.
            }
        }

        return (files, bytes, newest);
    }

    private static GitInfo? Git(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return null;
        }

        var status = RunGit(folder, "status --porcelain=v1 -b");
        if (status is null)
        {
            return null;
        }

        var lines = status.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            return null;
        }

        // The first line is "## branch...tracking"; the rest are changed paths.
        var branch = "?";
        if (lines[0].StartsWith("## ", StringComparison.Ordinal))
        {
            branch = lines[0][3..].Split("...")[0].Trim();
        }

        var changes = lines.Length - 1;
        var commit = RunGit(folder, "log -1 --format=%h  %s")?.Trim() ?? string.Empty;

        return new GitInfo(branch, changes, commit);
    }

    /// <summary>Runs a git command in the folder, or null when git is missing or the folder is not a repo.</summary>
    private static string? RunGit(string folder, string arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("git", $"-C \"{folder}\" {arguments}")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            if (!process.Start())
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();

            if (!process.WaitForExit(4000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return null;
            }

            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
