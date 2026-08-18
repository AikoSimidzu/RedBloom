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
        int Loc, IReadOnlyList<LangCount> Languages,
        DateTime? LastActivity,
        GitInfo? Git);

    /// <summary>Lines of code counted for one language.</summary>
    public readonly record struct LangCount(string Name, int Lines);

    /// <summary>The git state of the project folder, when it is a repository.</summary>
    public readonly record struct GitInfo(string Branch, int Changes, string LastCommit);

    /// <summary>Gathers a project's activity off the UI thread, over its folder and every source.</summary>
    public static Task<Stats> ComputeAsync(Project project) => Task.Run(() =>
    {
        var chats = ChatStore.Chats.Where(c => c.ProjectId == project.Id).ToList();
        var rooms = RoomStore.Rooms.Where(r => r.ProjectId == project.Id).ToList();

        var chatMessages = chats.Sum(c => c.Turns.Count(t => t.Role is "user" or "assistant"));
        var roomMessages = rooms.Sum(r => r.Turns.Count(t => t.Role is "user" or "assistant"));

        // The project's own folder and every linked source folder, each counted once.
        var roots = new List<string> { project.Folder };
        roots.AddRange(project.Sources.Select(s => s.Path).Where(p => p.Length > 0));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var files = 0;
        long bytes = 0;
        DateTime? newest = null;
        var lang = new Dictionary<string, int>();
        var loc = 0;

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root) || !seen.Add(Path.GetFullPath(root)))
            {
                continue;
            }

            Count(root, ref files, ref bytes, ref newest, ref loc, lang);
        }

        DateTime? last = null;
        foreach (var when in chats.Select(c => c.UpdatedAt)
                     .Concat(rooms.Select(r => r.UpdatedAt))
                     .Concat(newest is { } f ? [f] : Array.Empty<DateTime>()))
        {
            if (last is null || when > last)
            {
                last = when;
            }
        }

        var languages = lang.OrderByDescending(kv => kv.Value).Take(8).Select(kv => new LangCount(kv.Key, kv.Value)).ToList();

        return new Stats(chats.Count, chatMessages, rooms.Count, roomMessages, files, bytes, loc, languages, last, Git(project.Folder));
    });

    /// <summary>The language a file's extension names, for the code counter, or null to skip it.</summary>
    private static string? Language(string ext) => ext.ToLowerInvariant() switch
    {
        ".cs" => "C#", ".xaml" => "XAML", ".js" or ".jsx" or ".mjs" => "JavaScript", ".ts" or ".tsx" => "TypeScript",
        ".py" => "Python", ".html" or ".htm" => "HTML", ".css" or ".scss" or ".less" => "CSS",
        ".json" => "JSON", ".md" => "Markdown", ".c" or ".h" => "C", ".cpp" or ".cc" or ".cxx" or ".hpp" => "C++",
        ".java" => "Java", ".go" => "Go", ".rs" => "Rust", ".sql" => "SQL", ".sh" or ".bash" => "Shell",
        ".xml" => "XML", ".yml" or ".yaml" => "YAML", ".rb" => "Ruby", ".php" => "PHP", ".kt" => "Kotlin",
        ".swift" => "Swift", ".vue" => "Vue", ".razor" => "Razor",
        _ => null,
    };

    /// <summary>Walks one root, tallying files, bytes, the newest write, and lines of code by language.</summary>
    private static void Count(string root, ref int files, ref long bytes, ref DateTime? newest, ref int loc, Dictionary<string, int> lang)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0 && files < 200000)
        {
            var dir = stack.Pop();

            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    var name = Path.GetFileName(sub);
                    if (name is ".git" or "node_modules" or ".chats" or ".rooms" or "bin" or "obj" or "packages" or ".vs")
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

                        // Count lines only for source files, and never anything huge or generated.
                        if (info.Length is > 0 and < 2_000_000 && Language(info.Extension) is { } language)
                        {
                            var lines = CountLines(file);
                            if (lines > 0)
                            {
                                loc += lines;
                                lang[language] = lang.GetValueOrDefault(language) + lines;
                            }
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
    }

    private static int CountLines(string file)
    {
        try
        {
            var count = 0;
            foreach (var line in File.ReadLines(file))
            {
                if (line.Trim().Length > 0)
                {
                    count++;
                }
            }

            return count;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>The git state of a single folder, off the UI thread — for a source row's badge.</summary>
    public static Task<GitInfo?> GitAsync(string folder) => Task.Run(() => Git(folder));

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
