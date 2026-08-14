using System.IO;
using System.Text;
using System.Text.Json;

namespace RedBloom.Services.Ai;

/// <summary>
/// The file tools' actual work — read, write, edit and list — kept apart from the chat views so the
/// one-to-one chat and the room carry them out identically. Only the paths and the diff card are
/// the view's; the reading and writing is here.
/// </summary>
public static class FileTools
{
    /// <summary>Past this a read is cut, so one file cannot fill the model's whole window.</summary>
    private const int MaxRead = 60000;

    /// <summary>How many entries a listing shows before it stops.</summary>
    private const int MaxEntries = 400;

    /// <summary>
    /// The outcome of a change: the message the model reads, and the unified diff of what changed,
    /// so the chat can show the added and removed lines even outside a git repository.
    /// </summary>
    public readonly record struct Result(bool Ok, string Message, string Diff = "");

    /// <summary>The absolute path a tool call names, resolved against the chat's working directory.</summary>
    public static string? PathOf(string argumentsJson, string cwd)
    {
        var raw = Str(Parse(argumentsJson), AgentTransports.Files.Path);
        return Resolve(raw, cwd);
    }

    private static string? Resolve(string raw, string cwd)
    {
        raw = raw.Trim().Trim('"');

        if (raw.Length == 0)
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(Path.IsPathRooted(raw) ? raw : Path.Combine(cwd, raw));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return null;
        }
    }

    public static string Read(string argumentsJson, string cwd)
    {
        var root = Parse(argumentsJson);
        var path = Resolve(Str(root, AgentTransports.Files.Path), cwd);

        if (path is null)
        {
            return "No path was given.";
        }

        if (!File.Exists(path))
        {
            return $"There is no file at {path}.";
        }

        try
        {
            if (LooksBinary(path))
            {
                return $"{path} is a binary file ({new FileInfo(path).Length} bytes), not readable as text.";
            }

            var lines = File.ReadAllText(path).Replace("\r\n", "\n").Split('\n');

            var from = root.TryGetProperty(AgentTransports.Files.StartLine, out var s) && s.TryGetInt32(out var a) ? Math.Max(1, a) : 1;
            var to = root.TryGetProperty(AgentTransports.Files.EndLine, out var e) && e.TryGetInt32(out var b) ? Math.Min(lines.Length, b) : lines.Length;

            if (from > lines.Length)
            {
                return $"{path} has {lines.Length} lines; line {from} is past the end.";
            }

            var sb = new StringBuilder();

            for (var i = from; i <= to && sb.Length < MaxRead; i++)
            {
                sb.Append(i).Append('\t').Append(lines[i - 1]).Append('\n');
            }

            if (sb.Length >= MaxRead)
            {
                sb.Append($"(cut after {MaxRead} characters — read a line range to see more)\n");
            }

            return sb.Length == 0 ? "(the file is empty)" : sb.ToString().TrimEnd('\n');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Could not read {path}: {ex.Message}";
        }
    }

    public static Result Write(string argumentsJson, string cwd)
    {
        var root = Parse(argumentsJson);
        var path = Resolve(Str(root, AgentTransports.Files.Path), cwd);

        if (path is null)
        {
            return new Result(false, "No path was given.");
        }

        var content = Str(root, AgentTransports.Files.Content);

        try
        {
            var dir = Path.GetDirectoryName(path);

            if (dir is { Length: > 0 })
            {
                Directory.CreateDirectory(dir);
            }

            var existed = File.Exists(path);

            // Read the old text first so the change can be shown as a diff — skipped for a binary
            // file, whose bytes are not lines to compare.
            var old = existed && !LooksBinary(path) ? File.ReadAllText(path) : string.Empty;
            var next = content.Replace("\r\n", "\n");

            File.WriteAllText(path, next, new UTF8Encoding(false));

            var lines = content.Length == 0 ? 0 : next.Split('\n').Length;
            return new Result(true, $"{(existed ? "Replaced" : "Wrote")} {path} ({lines} lines).", TextDiff.Unified(path, old, next));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Result(false, $"Could not write {path}: {ex.Message}");
        }
    }

    public static Result Edit(string argumentsJson, string cwd)
    {
        var root = Parse(argumentsJson);
        var path = Resolve(Str(root, AgentTransports.Files.Path), cwd);

        if (path is null)
        {
            return new Result(false, "No path was given.");
        }

        if (!File.Exists(path))
        {
            return new Result(false, $"There is no file at {path}.");
        }

        var oldText = Str(root, AgentTransports.Files.Old).Replace("\r\n", "\n");
        var newText = Str(root, AgentTransports.Files.New).Replace("\r\n", "\n");
        var all = root.TryGetProperty(AgentTransports.Files.ReplaceAll, out var r) && r.ValueKind == JsonValueKind.True;

        if (oldText.Length == 0)
        {
            return new Result(false, "The old text to replace was empty.");
        }

        try
        {
            var text = File.ReadAllText(path).Replace("\r\n", "\n");
            var count = Occurrences(text, oldText);

            if (count == 0)
            {
                return new Result(false, "The old text was not found in the file. Copy it exactly, including indentation.");
            }

            if (count > 1 && !all)
            {
                return new Result(false, $"The old text appears {count} times. Add more surrounding lines to make it unique, or set replace_all.");
            }

            var updated = all ? text.Replace(oldText, newText) : ReplaceFirst(text, oldText, newText);
            File.WriteAllText(path, updated, new UTF8Encoding(false));

            return new Result(
                true,
                $"Edited {path} ({count} {(count == 1 ? "replacement" : "replacements")}).",
                TextDiff.Unified(path, text, updated));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Result(false, $"Could not edit {path}: {ex.Message}");
        }
    }

    public static string List(string argumentsJson, string cwd)
    {
        var raw = Str(Parse(argumentsJson), AgentTransports.Files.Path);
        var path = raw.Trim().Length == 0 ? cwd : Resolve(raw, cwd);

        if (path is null || !Directory.Exists(path))
        {
            return $"There is no folder at {path ?? raw}.";
        }

        try
        {
            var sb = new StringBuilder();
            sb.Append(path).Append('\n');

            var dirs = Directory.EnumerateDirectories(path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            var files = Directory.EnumerateFiles(path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            var shown = 0;

            foreach (var d in dirs)
            {
                if (shown++ >= MaxEntries) break;
                sb.Append("  ").Append(Path.GetFileName(d)).Append("/\n");
            }

            foreach (var f in files)
            {
                if (shown++ >= MaxEntries) break;
                sb.Append("  ").Append(Path.GetFileName(f)).Append('\n');
            }

            if (dirs.Count + files.Count == 0)
            {
                sb.Append("  (empty)\n");
            }
            else if (dirs.Count + files.Count > MaxEntries)
            {
                sb.Append($"  (listing stopped at {MaxEntries} entries)\n");
            }

            return sb.ToString().TrimEnd('\n');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Could not list {path}: {ex.Message}";
        }
    }

    private static int Occurrences(string text, string needle)
    {
        var count = 0;
        var at = 0;

        while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += needle.Length;
        }

        return count;
    }

    private static string ReplaceFirst(string text, string oldText, string newText)
    {
        var at = text.IndexOf(oldText, StringComparison.Ordinal);
        return at < 0 ? text : text[..at] + newText + text[(at + oldText.Length)..];
    }

    private static bool LooksBinary(string path)
    {
        using var file = File.OpenRead(path);
        Span<byte> head = stackalloc byte[8000];
        var read = file.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        return head[..read].Contains<byte>(0);
    }

    private static JsonElement Parse(string argumentsJson)
    {
        try
        {
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson).RootElement;
        }
        catch (JsonException)
        {
            return JsonDocument.Parse("{}").RootElement;
        }
    }

    private static string Str(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;
}
