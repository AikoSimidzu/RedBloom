using System.IO;

namespace RedBloom.Services.Ai;

/// <summary>How one attachment should look in the chat, and how to reach it again.</summary>
public sealed record AttachmentView(string Path, string Name, string Kind, string Glyph, string Preview);

/// <summary>
/// Describes attachments for the page: a thumbnail where one can be made, a glyph otherwise.
/// </summary>
public static class Attachments
{
    private static readonly string[] Images = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"];

    /// <summary>
    /// Big enough to be worth showing, small enough that a handful of them do not make the
    /// message that carries them enormous.
    /// </summary>
    private const long MaxPreviewBytes = 3 * 1024 * 1024;

    /// <summary>
    /// How a saved SSH session is written when it is attached.
    /// </summary>
    /// <remarks>
    /// A scheme rather than a second list: an attachment is a string everywhere it travels — the
    /// composer, the saved turn, the context builder — and giving sessions their own prefix keeps
    /// all of that unchanged. The id is the session's, so the details are read fresh at send time
    /// and a session edited between turns reaches the model as it is now.
    /// </remarks>
    public const string SshScheme = "ssh-session:";

    public static bool IsSshSession(string path) =>
        path.StartsWith(SshScheme, StringComparison.OrdinalIgnoreCase);

    public static AttachmentView Describe(string path)
    {
        if (IsSshSession(path))
        {
            var session = RedBloom.Services.SessionCatalog.Find(path);

            return new AttachmentView(
                path,
                session?.Name ?? "(deleted session)",
                "ssh",
                "\uE968",
                string.Empty);
        }

        var name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));

        if (name.Length == 0)
        {
            name = path;
        }

        if (Directory.Exists(path))
        {
            return new AttachmentView(path, name, "folder", "\uE8B7", string.Empty);
        }

        var extension = System.IO.Path.GetExtension(path).ToLowerInvariant();

        return new AttachmentView(
            path,
            name,
            "file",
            GlyphFor(extension),
            Images.Contains(extension) ? Thumbnail(path) : string.Empty);
    }

    /// <summary>
    /// The picture itself, inlined, or empty when there is none to show.
    /// </summary>
    /// <remarks>
    /// Inlined for the same reason the avatar is: the page is served from a virtual host mapped
    /// to the Assets folder, and mapping another one would hand it read access to wherever the
    /// user's files happen to live.
    /// </remarks>
    private static string Thumbnail(string path)
    {
        try
        {
            if (new FileInfo(path).Length > MaxPreviewBytes)
            {
                return string.Empty;
            }

            var media = System.IO.Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".bmp" => "image/bmp",
                _ => "image/png",
            };

            return $"data:{media};base64,{Convert.ToBase64String(File.ReadAllBytes(path))}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// A Segoe MDL2 glyph standing in for the kind of file this is.
    /// </summary>
    /// <remarks>
    /// A short list on purpose: the glyph only has to say "picture", "code", "sound" at a
    /// glance, and a page of near-identical document icons says less than one honest default.
    /// </remarks>
    private static string GlyphFor(string extension) => extension switch
    {
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".svg" or ".ico" => "\uEB9F",

        ".cs" or ".js" or ".ts" or ".py" or ".json" or ".xml" or ".html" or ".css" or ".xaml"
            or ".cpp" or ".c" or ".h" or ".go" or ".rs" or ".java" or ".ps1" or ".sh" or ".sql" => "\uE943",

        ".mp3" or ".wav" or ".flac" or ".ogg" or ".m4a" => "\uE8D6",
        ".mp4" or ".mkv" or ".avi" or ".mov" or ".webm" => "\uE714",
        ".zip" or ".7z" or ".rar" or ".tar" or ".gz" => "\uE7B8",
        ".exe" or ".msi" or ".dll" => "\uE756",

        _ => "\uE7C3",
    };
    /// <summary>Opens a file with whatever the system uses for it.</summary>
    public static void Open(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return;
        }

        Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
    }

    /// <summary>Opens Explorer with the item selected, or the folder itself for a folder.</summary>
    public static void Reveal(string path)
    {
        if (Directory.Exists(path))
        {
            Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            return;
        }

        if (!File.Exists(path))
        {
            return;
        }

        // The quotes matter: a path with a space would otherwise arrive as several arguments
        // and Explorer would open the user's documents instead.
        Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
        {
            UseShellExecute = true,
        });
    }

    private static void Start(System.Diagnostics.ProcessStartInfo start)
    {
        try
        {
            System.Diagnostics.Process.Start(start);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Nothing registered to handle it, or the shell refused; there is nothing to say.
        }
    }
}
