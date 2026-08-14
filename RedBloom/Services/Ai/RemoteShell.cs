using System.Text;
using RedBloom.Models;
using RedBloom.Terminal;

namespace RedBloom.Services.Ai;

/// <summary>
/// One live SSH connection an agent works over: <c>run_command</c> and the file tools act on the
/// remote machine instead of the local one, all across a single authenticated session that stays
/// up between calls.
/// </summary>
/// <remarks>
/// This is the whole answer to a model "crawling" poorly over SSH. Before, every command was its
/// own fresh <c>ssh user@host "…"</c>, which re-authenticated each time and forgot the working
/// directory; Windows OpenSSH cannot multiplex a connection to fix that. So the app keeps the
/// connection itself, through SSH.NET (the same client the terminal uses): one login, many
/// commands, a working directory that persists, and file tools that read and write over the same
/// link with base64 rather than fragile shell heredocs. The saved password is used to connect
/// locally and never travels to the model.
/// </remarks>
public sealed class RemoteShell : IDisposable
{
    private const string CwdMarker = "__RBCWD__";
    private const string ExitMarker = "__RBX__";

    /// <summary>Past this a read is cut, so one file cannot fill the model's whole window.</summary>
    private const int MaxRead = 60000;

    /// <summary>The largest file that can be written over the command channel, before base64.</summary>
    private const int MaxWrite = 512 * 1024;

    private readonly SshSession _session;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SshConnection? _conn;
    private bool _disposed;

    public RemoteShell(SshSession session) => _session = session;

    public string Host => _session.Host;

    public string Name => _session.Name;

    public string User => _session.Username;

    /// <summary>The remote working directory, tracked so a <c>cd</c> carries between commands.</summary>
    public string Cwd { get; private set; } = "~";

    // ---- commands ----

    /// <summary>Runs a command on the remote in the tracked directory; a cd in it carries onward.</summary>
    public async Task<string> RunAsync(string command, CancellationToken cancellationToken)
    {
        SshConnection conn;

        try
        {
            conn = await EnsureAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Could not connect to {_session.Host}: {ex.Message}";
        }

        // The command runs in a group with stderr folded into stdout so everything arrives in
        // order, then the ending directory and exit code are printed as a trailing marker line.
        var wrapped =
            $"cd {Quote(Cwd)} 2>/dev/null\n{{\n{command}\n}} 2>&1\n__rbx=$?\nprintf '\\n{CwdMarker}%s{ExitMarker}%s' \"$(pwd)\" \"$__rbx\"";

        string raw;

        try
        {
            (_, raw) = await conn.RunAsync(wrapped, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"The remote command failed: {ex.Message}";
        }

        var exit = 0;
        var at = raw.LastIndexOf(CwdMarker, StringComparison.Ordinal);

        if (at >= 0)
        {
            var line = raw[at..];
            var body = line[CwdMarker.Length..];
            var split = body.IndexOf(ExitMarker, StringComparison.Ordinal);

            if (split >= 0)
            {
                var dir = body[..split].Trim();
                if (dir.Length > 0) Cwd = dir;
                int.TryParse(body[(split + ExitMarker.Length)..].Trim(), out exit);
            }

            var before = at > 0 ? raw.LastIndexOf('\n', at - 1) : -1;
            raw = before >= 0 ? raw[..before] : string.Empty;
        }

        raw = raw.TrimEnd();
        var result = exit == 0
            ? raw.Length > 0 ? raw : "(the command printed nothing; it exited normally)"
            : $"{raw}\n(exit code {exit})".TrimStart();

        return result.Length > MaxRead ? result[..MaxRead] + "\n(output cut)" : result;
    }

    // ---- files ----

    public async Task<string> ReadFileAsync(string argumentsJson, CancellationToken cancellationToken)
    {
        var (ok, text, path) = await ReadRawAsync(PathArg(argumentsJson), cancellationToken).ConfigureAwait(false);

        if (!ok)
        {
            return text;
        }

        if (text.IndexOf('\0') >= 0)
        {
            return $"{path} is a binary file, not readable as text.";
        }

        var lines = text.Replace("\r\n", "\n").Split('\n');
        var root = System.Text.Json.JsonDocument.Parse(Args(argumentsJson)).RootElement;
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

        return sb.Length == 0 ? "(the file is empty)" : sb.ToString().TrimEnd('\n');
    }

    public async Task<string> WriteFileAsync(string argumentsJson, CancellationToken cancellationToken)
    {
        var root = System.Text.Json.JsonDocument.Parse(Args(argumentsJson)).RootElement;
        var path = Resolve(Str(root, AgentTransports.Files.Path));
        var content = Str(root, AgentTransports.Files.Content).Replace("\r\n", "\n");

        return await PutAsync(path, content, cancellationToken).ConfigureAwait(false)
            ? $"Wrote {path} ({(content.Length == 0 ? 0 : content.Split('\n').Length)} lines)."
            : $"Could not write {path} (see the connection).";
    }

    public async Task<string> EditFileAsync(string argumentsJson, CancellationToken cancellationToken)
    {
        var root = System.Text.Json.JsonDocument.Parse(Args(argumentsJson)).RootElement;
        var raw = Str(root, AgentTransports.Files.Path);
        var oldText = Str(root, AgentTransports.Files.Old).Replace("\r\n", "\n");
        var newText = Str(root, AgentTransports.Files.New).Replace("\r\n", "\n");
        var all = root.TryGetProperty(AgentTransports.Files.ReplaceAll, out var r) && r.ValueKind == System.Text.Json.JsonValueKind.True;

        if (oldText.Length == 0)
        {
            return "The old text to replace was empty.";
        }

        var (ok, text, path) = await ReadRawAsync(raw, cancellationToken).ConfigureAwait(false);

        if (!ok)
        {
            return text;
        }

        text = text.Replace("\r\n", "\n");
        var count = Occurrences(text, oldText);

        if (count == 0)
        {
            return "The old text was not found in the file. Copy it exactly, including indentation.";
        }

        if (count > 1 && !all)
        {
            return $"The old text appears {count} times. Add surrounding lines to make it unique, or set replace_all.";
        }

        var updated = all ? text.Replace(oldText, newText) : ReplaceFirst(text, oldText, newText);

        return await PutAsync(path, updated, cancellationToken).ConfigureAwait(false)
            ? $"Edited {path} ({count} {(count == 1 ? "replacement" : "replacements")})."
            : $"Could not write {path} (see the connection).";
    }

    public async Task<string> ListAsync(string argumentsJson, CancellationToken cancellationToken)
    {
        SshConnection conn;

        try
        {
            conn = await EnsureAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Could not connect to {_session.Host}: {ex.Message}";
        }

        var raw = PathArg(argumentsJson);
        var dir = raw.Trim().Length == 0 ? Cwd : Resolve(raw);
        var (code, output) = await conn.RunAsync($"ls -1Ap {Quote(dir)} 2>&1", cancellationToken).ConfigureAwait(false);

        return code == 0 ? $"{dir}\n{output.TrimEnd()}" : $"Could not list {dir}: {output.Trim()}";
    }

    /// <summary>The current path, absolute, for a tool call that resolves against the remote cwd.</summary>
    public string PathOf(string argumentsJson) => Resolve(PathArg(argumentsJson));

    // ---- internals ----

    private async Task<SshConnection> EnsureAsync(CancellationToken cancellationToken)
    {
        if (_conn is { IsConnected: true })
        {
            return _conn;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_conn is { IsConnected: true })
            {
                return _conn;
            }

            _conn?.Dispose();
            _conn = await SshConnection.EstablishAsync(
                _session,
                _session.Secret,
                AgentTunnel.IsTrusted ?? (_ => false),
                AgentTunnel.ApproveAsync ?? (_ => Task.FromResult(false)),
                cancellationToken).ConfigureAwait(false);

            // Anchor the working directory at the login home the first time.
            if (Cwd == "~")
            {
                var (_, pwd) = await _conn.RunAsync("pwd", cancellationToken).ConfigureAwait(false);
                var home = pwd.Trim();
                if (home.Length > 0)
                {
                    Cwd = home;
                }
            }

            return _conn;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Reads a file's whole contents over the connection via base64.</summary>
    private async Task<(bool Ok, string Text, string Path)> ReadRawAsync(string rawPath, CancellationToken cancellationToken)
    {
        var path = Resolve(rawPath);

        SshConnection conn;

        try
        {
            conn = await EnsureAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, $"Could not connect to {_session.Host}: {ex.Message}", path);
        }

        var (code, output) = await conn
            .RunAsync($"base64 {Quote(path)} 2>/dev/null; echo {ExitMarker}$?", cancellationToken)
            .ConfigureAwait(false);

        var status = TailCode(output, out output);

        if (status != 0)
        {
            return (false, $"There is no readable file at {path}.", path);
        }

        try
        {
            var bytes = Convert.FromBase64String(new string(output.Where(c => !char.IsWhiteSpace(c)).ToArray()));
            return (true, Encoding.UTF8.GetString(bytes), path);
        }
        catch (FormatException)
        {
            return (false, $"Could not decode {path}.", path);
        }
    }

    /// <summary>Writes contents to a remote path via base64, making parent folders first.</summary>
    private async Task<bool> PutAsync(string path, string content, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(content);

        if (bytes.Length > MaxWrite)
        {
            return false;
        }

        var conn = await EnsureAsync(cancellationToken).ConfigureAwait(false);
        var b64 = Convert.ToBase64String(bytes);
        var dir = PosixDir(path);

        var command =
            $"mkdir -p {Quote(dir)} && printf '%s' {Quote(b64)} | base64 -d > {Quote(path)}; echo {ExitMarker}$?";

        var (_, output) = await conn.RunAsync(command, cancellationToken).ConfigureAwait(false);
        return TailCode(output, out _) == 0;
    }

    /// <summary>
    /// Reads the trailing <c>__RBX__code</c> a file command prints, and hands back what came before
    /// it. Robust to any stderr the connection appends after the marker line.
    /// </summary>
    private static int TailCode(string output, out string body)
    {
        var at = output.LastIndexOf(ExitMarker, StringComparison.Ordinal);

        if (at < 0)
        {
            body = output;
            return -1;
        }

        var tail = output[(at + ExitMarker.Length)..];
        var newline = tail.IndexOf('\n');
        var digits = (newline >= 0 ? tail[..newline] : tail).Trim();

        body = output[..at];
        return int.TryParse(digits, out var code) ? code : -1;
    }

    /// <summary>Resolves a possibly-relative remote path against the tracked working directory.</summary>
    private string Resolve(string raw)
    {
        raw = raw.Trim().Trim('"');

        if (raw.Length == 0)
        {
            return Cwd;
        }

        if (raw.StartsWith('/') || raw.StartsWith('~'))
        {
            return raw;
        }

        return (Cwd.EndsWith('/') ? Cwd : Cwd + "/") + raw;
    }

    private static string PosixDir(string path)
    {
        var cut = path.LastIndexOf('/');
        return cut <= 0 ? "." : path[..cut];
    }

    /// <summary>Single-quotes a value for POSIX sh, so a path or blob cannot break out of the command.</summary>
    private static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

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

    private static string Args(string argumentsJson) =>
        string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson;

    private static string PathArg(string argumentsJson)
    {
        try
        {
            return Str(System.Text.Json.JsonDocument.Parse(Args(argumentsJson)).RootElement, AgentTransports.Files.Path);
        }
        catch (System.Text.Json.JsonException)
        {
            return string.Empty;
        }
    }

    private static string Str(System.Text.Json.JsonElement root, string name) =>
        root.ValueKind == System.Text.Json.JsonValueKind.Object
        && root.TryGetProperty(name, out var v)
        && v.ValueKind == System.Text.Json.JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _conn?.Dispose();
        _gate.Dispose();
    }
}
