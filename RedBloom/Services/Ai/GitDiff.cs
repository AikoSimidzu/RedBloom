using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace RedBloom.Services.Ai;

/// <summary>
/// Works out what a command changed in a git repository, so it can be shown with per-line +/-.
/// </summary>
/// <remarks>
/// Commands run from the user's home in a fresh shell each time, so the repository a command touched
/// is guessed from the paths in the command line. The change itself is attributed by taking the
/// repository's diff before the command and again after: a file whose diff moved, or a new untracked
/// file, is what this command did. Everything runs git read-only — <c>diff</c> and <c>status</c> —
/// so it can never alter the tree it is reporting on.
/// </remarks>
public static partial class GitDiff
{
    private const int MaxUntrackedBytes = 40_000;

    /// <summary>The repository's changes at one moment, enough to attribute the next command's edits.</summary>
    public readonly record struct Snapshot(string? Root, Dictionary<string, string> Files, HashSet<string> Untracked)
    {
        public bool InRepo => Root is not null;
    }

    /// <summary>Reads the repository a command works in, ready to compare against once it has run.</summary>
    public static Snapshot Before(string command)
    {
        var root = RepoFor(command);
        return root is null
            ? new Snapshot(null, [], [])
            : new Snapshot(root, DiffByFile(root), UntrackedFiles(root));
    }

    /// <summary>
    /// The unified diff of what changed since the snapshot, or empty when nothing did. Only the
    /// files whose diff moved, and files newly created, are included — the rest of the tree's
    /// uncommitted state is left out so the card shows this command's work, not the backlog.
    /// </summary>
    public static string After(Snapshot before)
    {
        if (before.Root is not { } root)
        {
            return string.Empty;
        }

        var now = DiffByFile(root);
        var body = new StringBuilder();

        foreach (var (path, text) in now)
        {
            if (!before.Files.TryGetValue(path, out var was) || was != text)
            {
                body.Append(text);
            }
        }

        // New files are untracked, so they are not in `git diff`; show their contents as all-added.
        foreach (var path in UntrackedFiles(root))
        {
            if (before.Untracked.Contains(path))
            {
                continue;
            }

            body.Append(NewFileHunk(root, path));
        }

        if (body.Length == 0)
        {
            return string.Empty;
        }

        // A machine-readable header carries the repo root, so a "+++ b/path" can be turned into a
        // full path for the "go to file" action even after the chat is reopened. It is not rendered.
        return "# repo: " + root + "\n" + body;
    }

    /// <summary>The repository root recorded in a diff by <see cref="After"/>, or null.</summary>
    public static string? RootOf(string diff)
    {
        if (diff.StartsWith("# repo: ", StringComparison.Ordinal))
        {
            var end = diff.IndexOf('\n');
            return end > 8 ? diff[8..end].Trim() : null;
        }

        return null;
    }

    /// <summary>
    /// Renders a unified diff as page-safe HTML: each file a block carrying its full path for the
    /// "go to file" action, each line coloured by its + / - / context. Empty when nothing changed.
    /// </summary>
    public static string RenderHtml(string diff)
    {
        if (string.IsNullOrEmpty(diff))
        {
            return string.Empty;
        }

        var root = RootOf(diff) ?? string.Empty;
        var html = new StringBuilder();
        var open = false;

        // The running line numbers, read from each "@@" header, so every line can show the number
        // it sits at — the new-file line for added and context lines, the old-file line for removed.
        int oldLine = 0, newLine = 0;

        foreach (var raw in diff.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.StartsWith("# repo: ", StringComparison.Ordinal) || raw.StartsWith("--- ", StringComparison.Ordinal))
            {
                continue;
            }

            if (raw.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                if (open)
                {
                    html.Append("</div>");
                    open = false;
                }

                continue;
            }

            if (raw.StartsWith("+++ b/", StringComparison.Ordinal))
            {
                if (open)
                {
                    html.Append("</div>");
                }

                var rel = raw[6..];
                var full = root.Length > 0 ? Path.Combine(root, rel.Replace('/', '\\')) : rel;

                html.Append("<div class=\"dfile\" data-file=\"").Append(Markdown.Escape(full)).Append("\">")
                    .Append("<div class=\"dhead\">").Append(Markdown.Escape(rel)).Append("</div>");
                open = true;
                continue;
            }

            if (!open)
            {
                continue;
            }

            if (raw.StartsWith("@@", StringComparison.Ordinal))
            {
                (oldLine, newLine) = HunkStart(raw);
                html.Append("<div class=\"dl dhunk\"><span class=\"dnum\"></span><span class=\"dtext\">")
                    .Append(Markdown.Escape(raw)).Append("</span></div>");
                continue;
            }

            var (cls, number) = raw.StartsWith('+') ? ("dadd", newLine++)
                : raw.StartsWith('-') ? ("ddel", oldLine++)
                : Both(ref oldLine, ref newLine);

            html.Append("<div class=\"dl ").Append(cls).Append("\"><span class=\"dnum\">")
                .Append(number > 0 ? number.ToString() : string.Empty)
                .Append("</span><span class=\"dtext\">")
                .Append(Markdown.Escape(raw.Length == 0 ? " " : raw))
                .Append("</span></div>");
        }

        if (open)
        {
            html.Append("</div>");
        }

        return html.ToString();
    }

    /// <summary>A context line advances both files; it is numbered by the new file, like the additions.</summary>
    private static (string Class, int Number) Both(ref int oldLine, ref int newLine)
    {
        var number = newLine;
        oldLine++;
        newLine++;
        return ("dctx", number);
    }

    /// <summary>The old and new starting line a hunk header names, or zeros when it cannot be read.</summary>
    private static (int Old, int New) HunkStart(string header)
    {
        var match = HunkHeader().Match(header);

        if (!match.Success)
        {
            return (0, 0);
        }

        int.TryParse(match.Groups[1].Value, out var old);
        int.TryParse(match.Groups[2].Value, out var fresh);
        return (old, fresh);
    }

    [GeneratedRegex(@"@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@")]
    private static partial Regex HunkHeader();

    // ---- repository ----

    private static string? RepoFor(string command)
    {
        foreach (var candidate in Candidates(command))
        {
            if (Root(candidate) is { } root)
            {
                return root;
            }
        }

        return Root(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    private static IEnumerable<string> Candidates(string command)
    {
        // Absolute Windows paths in the command line, longest first so the deepest folder wins.
        var paths = AbsolutePath().Matches(command)
            .Select(m => m.Value.Trim().Trim('"', '\''))
            .Distinct()
            .OrderByDescending(p => p.Length)
            .ToList();

        foreach (var path in paths)
        {
            string? dir = null;
            try
            {
                dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            }
            catch (ArgumentException)
            {
                // A malformed path; skip it.
            }

            if (dir is { Length: > 0 })
            {
                yield return dir;
            }
        }
    }

    private static string? Root(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            return null;
        }

        var top = Run(dir, "rev-parse --show-toplevel");
        return string.IsNullOrWhiteSpace(top) ? null : top.Trim().Replace('/', '\\');
    }

    // ---- git reads ----

    private static Dictionary<string, string> DiffByFile(string root)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var diff = Run(root, "diff --no-color");

        if (string.IsNullOrEmpty(diff))
        {
            return map;
        }

        // Split into per-file sections, keyed by the "+++ b/path" the section names.
        string? path = null;
        var section = new StringBuilder();

        foreach (var line in diff.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                Flush(map, path, section);
                path = null;
                section.Clear();
            }

            if (line.StartsWith("+++ b/", StringComparison.Ordinal))
            {
                path = line[6..];
            }
            else if (line.StartsWith("+++ ", StringComparison.Ordinal) && line.Contains("/dev/null"))
            {
                path ??= "(deleted)";
            }

            section.Append(line).Append('\n');
        }

        Flush(map, path, section);
        return map;

        static void Flush(Dictionary<string, string> into, string? key, StringBuilder text)
        {
            if (key is { Length: > 0 } && text.Length > 0)
            {
                into[key] = text.ToString();
            }
        }
    }

    private static HashSet<string> UntrackedFiles(string root)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var status = Run(root, "status --porcelain --untracked-files=all");

        if (string.IsNullOrEmpty(status))
        {
            return set;
        }

        foreach (var line in status.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith("?? ", StringComparison.Ordinal))
            {
                set.Add(line[3..].Trim().Trim('"'));
            }
        }

        return set;
    }

    private static string NewFileHunk(string root, string relativePath)
    {
        var full = Path.Combine(root, relativePath.Replace('/', '\\'));
        var body = new StringBuilder();
        body.Append("diff --git a/").Append(relativePath).Append(" b/").Append(relativePath).Append('\n');
        body.Append("--- /dev/null\n+++ b/").Append(relativePath).Append('\n');

        try
        {
            var info = new FileInfo(full);

            if (!info.Exists || info.Length > MaxUntrackedBytes)
            {
                body.Append("@@ new file @@\n");
                return body.ToString();
            }

            var text = File.ReadAllText(full).Replace("\r\n", "\n");

            if (text.IndexOf('\0') >= 0)
            {
                body.Append("@@ binary file @@\n");
                return body.ToString();
            }

            var lines = text.Split('\n');
            body.Append("@@ -0,0 +1,").Append(lines.Length).Append(" @@\n");

            foreach (var line in lines)
            {
                body.Append('+').Append(line).Append('\n');
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            body.Append("@@ new file @@\n");
        }

        return body.ToString();
    }

    private static string? Run(string workingDir, string arguments)
    {
        try
        {
            var start = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };

            using var process = Process.Start(start);

            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();

            if (!process.WaitForExit(6000))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return null;
            }

            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            // git is not installed, or could not be started; there is simply no diff to show.
            return null;
        }
    }

    [GeneratedRegex(@"[A-Za-z]:[\\/][^""'|;&<>\r\n]*")]
    private static partial Regex AbsolutePath();
}
