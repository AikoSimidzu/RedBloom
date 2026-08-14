using System.IO;
using System.Text;
using System.Windows.Media.Imaging;
using RedBloom.Models;

namespace RedBloom.Services.Ai;

/// <summary>
/// Turns a chat's attachments into text the model can read, and measures how full the
/// conversation is getting.
/// </summary>
/// <remarks>
/// Attachments are rebuilt on every send rather than stored with the conversation: a file the
/// user edits between turns should reach the model as it is now, not as it was when they
/// attached it. That also keeps a large file out of the saved chat, which only ever holds what
/// was actually said.
/// </remarks>
public static class ChatContext
{
    /// <summary>Per file. Past this a file is cut, with a line saying so.</summary>
    private const int MaxFileChars = 60000;

    /// <summary>Across all attachments together.</summary>
    private const int MaxTotalChars = 240000;

    /// <summary>How many entries a folder listing shows before it stops.</summary>
    private const int MaxFolderEntries = 200;

    /// <summary>Per picture. The endpoints refuse anything larger.</summary>
    private const long MaxImageBytes = 5 * 1024 * 1024;

    /// <summary>Formats both wire formats accept as they are, and the type name to send.</summary>
    private static readonly Dictionary<string, string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
    };

    /// <summary>
    /// Pictures no endpoint accepts, but which Windows can already decode, so they are re-encoded
    /// as PNG rather than refused.
    /// </summary>
    private static readonly string[] Reencoded = [".bmp", ".ico", ".tif", ".tiff"];

    /// <summary>
    /// The attachment block for a chat, or null when it has none.
    /// </summary>
    public static string? Build(IReadOnlyList<string> attachments)
    {
        if (attachments.Count == 0)
        {
            return null;
        }

        var text = new StringBuilder();
        text.AppendLine("The user has attached the following for reference. Items introduced as "
            + "\"file\" or \"folder\" are contents to read; an \"ssh connection\" is not a file at "
            + "all — it is a machine described so you can write commands for it.");

        foreach (var path in attachments)
        {
            if (text.Length > MaxTotalChars)
            {
                text.AppendLine().AppendLine("(the remaining attachments were left out for length)");
                break;
            }

            text.AppendLine();

            if (Attachments.IsSshSession(path))
            {
                AppendSession(text, path);
            }
            else if (Directory.Exists(path))
            {
                AppendFolder(text, path);
            }
            else if (File.Exists(path))
            {
                AppendFile(text, path);
            }
            else
            {
                text.AppendLine($"=== {path} ===").AppendLine("(missing — it may have been moved or deleted)");
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// The attached pictures, encoded for sending. Anything that is not one is left to
    /// <see cref="Build"/>.
    /// </summary>
    public static IReadOnlyList<AgentImage> Images(IReadOnlyList<string> attachments)
    {
        List<AgentImage>? images = null;

        foreach (var path in attachments)
        {
            if (IsImage(path) && File.Exists(path) && Encode(path) is { } image)
            {
                (images ??= []).Add(image);
            }
        }

        return images ?? (IReadOnlyList<AgentImage>)[];
    }

    private static bool IsImage(string path)
    {
        var extension = Path.GetExtension(path);

        return ImageTypes.ContainsKey(extension) || Reencoded.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether a picture will be refused for its size, judged without encoding it.
    /// </summary>
    /// <remarks>
    /// Only the formats that go up untouched can be judged this way. A re-encoded one is assumed
    /// to fit, because turning it into PNG almost always makes it smaller — a bitmap loses about
    /// nine tenths of its size — and encoding it here to be sure would repeat, on every keystroke
    /// that redraws the counter, the work this whole path exists to avoid.
    /// </remarks>
    private static bool TooLarge(string path)
    {
        try
        {
            return ImageTypes.ContainsKey(Path.GetExtension(path))
                && new FileInfo(path).Length > MaxImageBytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static AgentImage? Encode(string path)
    {
        try
        {
            if (ImageTypes.TryGetValue(Path.GetExtension(path), out var media))
            {
                return new FileInfo(path).Length > MaxImageBytes
                    ? null
                    : new AgentImage(media, Convert.ToBase64String(File.ReadAllBytes(path)));
            }

            return AsPng(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    private static AgentImage? AsPng(string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(
            new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad));

        using var buffer = new MemoryStream();
        encoder.Save(buffer);

        return buffer.Length > MaxImageBytes
            ? null
            : new AgentImage("image/png", Convert.ToBase64String(buffer.ToArray()));
    }

    private static void AppendFile(StringBuilder text, string path)
    {
        if (IsImage(path))
        {
            // The bytes go up as a picture instead; naming it here is what ties the two
            // together when the user says "the second screenshot".
            text.AppendLine($"=== image: {path} ===")
                .AppendLine(TooLarge(path)
                    ? $"(too large to send — the limit is {MaxImageBytes / (1024 * 1024)} MB)"
                    : "(attached as a picture)");

            return;
        }

        text.AppendLine($"=== file: {path} ===");

        try
        {
            if (LooksBinary(path))
            {
                text.AppendLine($"(binary, {new FileInfo(path).Length} bytes — not readable as text)");
                return;
            }

            var content = File.ReadAllText(path);

            if (content.Length > MaxFileChars)
            {
                text.AppendLine(content[..MaxFileChars])
                    .AppendLine($"(cut after {MaxFileChars} characters of {content.Length})");
            }
            else
            {
                text.AppendLine(content);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            text.AppendLine($"(could not be read: {ex.Message})");
        }
        catch (ArgumentException)
        {
            // Not text at all — a binary file read as UTF-8. Its name is still worth knowing.
            text.AppendLine("(not readable as text)");
        }
    }

    /// <summary>
    /// Whether a file is binary, judged the way every diff tool judges it: a zero byte near the
    /// start.
    /// </summary>
    /// <remarks>
    /// Reading one as UTF-8 does not fail — it succeeds and yields replacement characters, so
    /// without this check an archive or an executable is sent to the model as pages of mojibake
    /// that it will try in good faith to interpret.
    /// </remarks>
    private static bool LooksBinary(string path)
    {
        using var file = File.OpenRead(path);

        Span<byte> head = stackalloc byte[8000];
        var read = file.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);

        return head[..read].Contains<byte>(0);
    }

    /// <summary>
    /// A saved SSH connection, described well enough to be acted on.
    /// </summary>
    /// <remarks>
    /// The password and the key passphrase are never included, and the model is told so outright
    /// rather than left to discover it: a model that knows a secret is withheld writes a command
    /// that prompts for it, while one that thinks it simply lacks the detail invents a plausible
    /// one. The private key's path is named because it is a path, not a secret — its contents
    /// stay on disk.
    /// </remarks>
    private static void AppendSession(StringBuilder text, string path)
    {
        var session = SessionCatalog.Find(path);

        if (session is null)
        {
            text.AppendLine("=== ssh connection ===")
                .AppendLine("(this saved connection has been deleted)");

            return;
        }

        // Introduced as a machine, not as a path. Named like a file — which the first version
        // did — a model reads it as something to open, and answers about its contents.
        text.AppendLine($"=== ssh connection: {session.Name} ===")
            .AppendLine("This is a remote machine the user has saved, not a file. Nothing here")
            .AppendLine("is to be read or opened; it is where work is to be done.")
            .AppendLine($"host: {session.Host}")
            .AppendLine($"port: {session.Port}")
            .AppendLine($"user: {session.Username}")
            .AppendLine($"to connect: ssh {SshCommandLine(session)}");

        text.AppendLine(session.UsesPrivateKey
            ? $"authentication: private key at {session.PrivateKeyPath}"
            : "authentication: password");

        foreach (var forward in session.Forwards)
        {
            text.AppendLine($"tunnel: {forward.Display}  ({forward.SshFlag})");
        }

        // Why it was attached at all. Without this a model treats it as background detail and
        // answers about the local machine, or asks which host was meant when it has been told.
        text.AppendLine()
            .AppendLine("The user attached this because the task concerns this machine, so your "
                + "tools now act on it directly: run_command runs on this remote over a live "
                + "connection, and the file tools read and write its files. Do NOT wrap your "
                + "commands in ssh yourself and do not use the ssh line above as a command — just "
                + "write the commands as if you were already on the machine. Unless the user says "
                + "otherwise, assume this is where things are to be inspected, changed or run.");

        AppendSecret(text, path, session);
    }

    /// <summary>
    /// The password, when the user chose to send it with this attachment.
    /// </summary>
    /// <remarks>
    /// Withheld by default and included only on request, because it leaves the machine: it
    /// becomes part of the prompt and reaches whatever endpoint the agent is pointed at, in the
    /// clear, and is kept in whatever logs that endpoint keeps. The user is told this at the
    /// moment they choose, in the attach dialog — the choice is theirs, and this only carries it
    /// out. Nothing is written into the saved chat; the password is read fresh from the session
    /// each time the conversation is sent.
    /// </remarks>
    private static void AppendSecret(StringBuilder text, string path, Models.SshSession session)
    {
        if (!SessionCatalog.CarriesSecret(path))
        {
            text.AppendLine()
                .AppendLine("The password and any key passphrase are withheld. Do not guess at "
                    + "them or put a placeholder in a command as though it were real; if one is "
                    + "needed, write the command so that it asks for it, and say so.");

            return;
        }

        if (session.Secret is not { Length: > 0 } secret)
        {
            text.AppendLine()
                .AppendLine("The user chose to share the password, but none is saved for this "
                    + "connection. Write commands so that they ask for it.");

            return;
        }

        text.AppendLine()
            .AppendLine(session.UsesPrivateKey
                ? $"key passphrase: {secret}"
                : $"password: {secret}");

        text.AppendLine(
            "The user shared this deliberately so you can connect without stopping to ask. Use "
            + "it only for this machine, and do not repeat it back in your answer.");
    }

    private static string SshCommandLine(Models.SshSession session)
    {
        var parts = new List<string>();

        foreach (var forward in session.Forwards)
        {
            parts.Add(forward.SshFlag);
        }

        if (session.UsesPrivateKey && !string.IsNullOrWhiteSpace(session.PrivateKeyPath))
        {
            parts.Add($"-i \"{session.PrivateKeyPath}\"");
        }

        if (session.Port != 22)
        {
            parts.Add($"-p {session.Port}");
        }

        parts.Add(session.DisplayTarget.Split(':')[0]);

        return string.Join(' ', parts);
    }

    private static void AppendFolder(StringBuilder text, string path)
    {
        text.AppendLine($"=== folder: {path} ===");

        try
        {
            var entries = Directory
                .EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories)
                .Take(MaxFolderEntries + 1)
                .ToList();

            foreach (var entry in entries.Take(MaxFolderEntries))
            {
                text.AppendLine(Path.GetRelativePath(path, entry));
            }

            if (entries.Count > MaxFolderEntries)
            {
                text.AppendLine($"(listing stopped at {MaxFolderEntries} entries)");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            text.AppendLine($"(could not be listed: {ex.Message})");
        }
    }

    /// <summary>
    /// Roughly how many tokens a conversation takes up.
    /// </summary>
    /// <remarks>
    /// An estimate, deliberately. Counting exactly means asking the endpoint, which costs a
    /// round trip per keystroke-sized change and is not offered at all by most of the
    /// OpenAI-compatible ones. Four characters per token is the usual rule of thumb for English
    /// prose and code; it runs high on other alphabets, which errs on the safe side for a gauge
    /// whose job is to warn before a limit rather than to bill anyone.
    /// </remarks>
    /// <summary>
    /// What one picture costs, near enough.
    /// </summary>
    /// <remarks>
    /// A flat charge: the real figure is width × height ÷ 750, and the endpoints shrink anything
    /// larger to about 1568px on its long side, which puts a big picture near this number. A small
    /// one is overcharged, which suits a gauge meant to warn early.
    /// </remarks>
    public const int TokensPerImage = 1600;

    /// <summary>
    /// How many of these attachments will go up as pictures.
    /// </summary>
    /// <remarks>
    /// Counted rather than encoded because the counter runs whenever the chat changes, and
    /// base64ing every screenshot in the history to arrive at a flat per-picture charge would be
    /// megabytes of work for a number that does not depend on it.
    /// </remarks>
    public static int CountImages(IReadOnlyList<string> attachments) =>
        attachments.Count(path => IsImage(path) && File.Exists(path));

    public static int EstimateTokens(IEnumerable<string> texts)
    {
        var characters = 0L;

        foreach (var text in texts)
        {
            // A few tokens of envelope per message, which matters once there are many short ones.
            characters += text.Length + 16;
        }

        return (int)Math.Min(int.MaxValue, characters / 4);
    }
}
