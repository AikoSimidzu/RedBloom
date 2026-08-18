using System.IO;
using System.Text.Json;

namespace RedBloom.Services;

/// <summary>
/// Writes files dropped onto a chat to disk so they can be attached like any other. A drop reaches
/// the page as data (a windowed WebView2 does not hand paths across), so the bytes are saved under
/// the chat's workspace and the saved path is what gets pinned.
/// </summary>
public static class DroppedFiles
{
    /// <summary>Anything larger than this is skipped — a drop travels as base64 through one message.</summary>
    private const long MaxBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Saves the files a "drop" message carried into the workspace's <c>dropped</c> folder and
    /// returns their paths, ready to attach. Skips anything unreadable or too large.
    /// </summary>
    public static List<string> Save(JsonElement message, string workspaceFolder)
    {
        var saved = new List<string>();

        if (!message.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
        {
            return saved;
        }

        string folder;
        try
        {
            folder = Path.Combine(workspaceFolder, "dropped");
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return saved;
        }

        foreach (var file in files.EnumerateArray())
        {
            if (file.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = file.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString() ?? string.Empty
                : string.Empty;

            var data = file.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString() ?? string.Empty
                : string.Empty;

            if (Decode(data) is not { } bytes || bytes.LongLength == 0 || bytes.LongLength > MaxBytes)
            {
                continue;
            }

            if (Write(folder, name, bytes) is { } path)
            {
                saved.Add(path);
            }
        }

        return saved;
    }

    /// <summary>The bytes out of a <c>data:</c> URL, or null when it is not one.</summary>
    private static byte[]? Decode(string dataUrl)
    {
        var comma = dataUrl.IndexOf(',');

        if (comma < 0 || !dataUrl.StartsWith("data:", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(dataUrl[(comma + 1)..]);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string? Write(string folder, string name, byte[] bytes)
    {
        var safe = SafeName(name);

        try
        {
            var path = Path.Combine(folder, safe);

            // A second drop of the same name gets a numbered sibling rather than overwriting the first.
            if (File.Exists(path))
            {
                var stem = Path.GetFileNameWithoutExtension(safe);
                var ext = Path.GetExtension(safe);

                for (var i = 2; File.Exists(path); i++)
                {
                    path = Path.Combine(folder, $"{stem} ({i}){ext}");
                }
            }

            File.WriteAllBytes(path, bytes);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or PathTooLongException)
        {
            return null;
        }
    }

    private static string SafeName(string name)
    {
        name = Path.GetFileName(name.Trim());

        foreach (var bad in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(bad, '_');
        }

        return name.Length > 0 ? name : "dropped-file";
    }
}
