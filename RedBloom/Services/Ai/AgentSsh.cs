using System.Text;
using RedBloom.Models;

namespace RedBloom.Services.Ai;

/// <summary>
/// Recognises an <c>ssh</c>/<c>plink</c> command an agent tried to run locally and turns it into a
/// target the app can reach over its own SSH client (SSH.NET) instead. External ssh and plink both
/// run without a TTY here, so a password login hangs forever and the password would have to sit on
/// the command line; routing the same intent through the built-in client avoids both — it connects
/// with the saved session's stored credentials, which never touch the command line or the model.
/// </summary>
public static class AgentSsh
{
    private static readonly string[] Programs = ["ssh", "plink", "putty", "scp", "sftp", "pscp", "psftp"];

    /// <summary>The pieces pulled out of an ssh/plink command line.</summary>
    public readonly record struct Target(string Program, string Host, string User, int Port, string RemoteCommand, bool FileTransfer);

    /// <summary>Whether this local command is an external SSH-family invocation we should redirect.</summary>
    public static bool Looks(string command)
    {
        var tokens = Tokenize(command);
        var program = FirstProgram(tokens);
        return program is not null;
    }

    /// <summary>Parses the command into a target, or null if no host can be found.</summary>
    public static Target? Parse(string command)
    {
        var tokens = Tokenize(command);

        // Drop a leading "sshpass [-p x|-f x|-d n|-e]" wrapper, if any, to reach the real command.
        if (tokens.Count > 0 && Name(tokens[0]) == "sshpass")
        {
            tokens.RemoveAt(0);
            while (tokens.Count > 0 && tokens[0].StartsWith('-'))
            {
                var opt = tokens[0];
                tokens.RemoveAt(0);
                if ((opt is "-p" or "-f" or "-d") && tokens.Count > 0)
                {
                    tokens.RemoveAt(0);
                }
            }
        }

        if (tokens.Count == 0 || FirstProgram(tokens) is not { } program)
        {
            return null;
        }

        tokens.RemoveAt(0);

        var fileTransfer = program is "scp" or "sftp" or "pscp" or "psftp";
        if (fileTransfer)
        {
            return new Target(program, string.Empty, string.Empty, 22, string.Empty, true);
        }

        string? user = null, keyPath = null;
        var port = 22;
        string? target = null;
        var remainder = new List<string>();

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Length == 0)
            {
                continue;
            }

            if (target is not null)
            {
                // Everything after [user@]host is the remote command.
                remainder.Add(token);
                continue;
            }

            if (token[0] != '-')
            {
                target = token;
                continue;
            }

            // Options. ssh uses -p (port), plink uses -P; both use -l (user), -i (key), plink -pw.
            string? Glued(string prefix) => token.Length > prefix.Length && token.StartsWith(prefix, StringComparison.Ordinal) ? token[prefix.Length..] : null;
            string? Value(string prefix)
            {
                var glued = Glued(prefix);
                if (glued is not null) return glued;
                return i + 1 < tokens.Count ? tokens[++i] : null;
            }

            // Exact multi-character options are matched first, so e.g. "-pw" (a plink password) is
            // not mistaken for a glued "-p" (port). Their values are consumed and dropped — RedBloom
            // connects with the saved session's own credentials and settings.
            if (token is "-pw" or "-pwfile" or "-m" or "-o" or "-F" or "-J" or "-L" or "-R" or "-D"
                or "-W" or "-b" or "-c" or "-E" or "-e" or "-I" or "-O" or "-Q" or "-S" or "-w"
                or "-hostkey" or "-proxycmd" or "-sercfg" or "-loghost")
            {
                _ = Value(token);
            }
            else if (token is "-l" || Glued("-l") is not null)
            {
                user = Value("-l");
            }
            else if (token is "-i" || Glued("-i") is not null)
            {
                keyPath = Value("-i");
            }
            else if (token is "-p" || Glued("-p") is not null)
            {
                if (int.TryParse(Value("-p"), out var p) && p is > 0 and <= 65535) port = p;
            }
            else if (token is "-P" || Glued("-P") is not null)
            {
                if (int.TryParse(Value("-P"), out var p) && p is > 0 and <= 65535) port = p;
            }

            // Anything else is treated as a boolean flag (-v, -N, -t, -batch, -ssh, …) and skipped.
        }

        if (string.IsNullOrWhiteSpace(target))
        {
            return null;
        }

        var at = target.LastIndexOf('@');
        var host = at >= 0 ? target[(at + 1)..] : target;
        if (at >= 0 && user is null)
        {
            user = target[..at];
        }

        _ = keyPath;   // a command-line key is ignored on purpose — the saved session's auth is used

        return string.IsNullOrWhiteSpace(host)
            ? null
            : new Target(program, host, user ?? string.Empty, port, string.Join(' ', remainder).Trim(), false);
    }

    /// <summary>
    /// The saved session that best matches a target: same host, preferring one that also matches the
    /// user and port. Null when nothing matches — the caller then asks for the session to be added
    /// rather than falling back to an external ssh.
    /// </summary>
    public static SshSession? Match(Target target)
    {
        var byHost = SessionCatalog.All
            .Where(s => string.Equals(s.Host, target.Host, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (byHost.Count == 0)
        {
            return null;
        }

        return byHost.FirstOrDefault(s =>
                   (target.User.Length == 0 || string.Equals(s.Username, target.User, StringComparison.OrdinalIgnoreCase))
                   && s.Port == target.Port)
               ?? byHost.FirstOrDefault(s => target.User.Length == 0 || string.Equals(s.Username, target.User, StringComparison.OrdinalIgnoreCase))
               ?? byHost[0];
    }

    private static string? FirstProgram(List<string> tokens)
    {
        if (tokens.Count == 0)
        {
            return null;
        }

        var name = Name(tokens[0]);
        return Programs.Contains(name) ? name : null;
    }

    /// <summary>The bare program name of a token, lower-cased and without path or .exe.</summary>
    private static string Name(string token)
    {
        var slash = Math.Max(token.LastIndexOf('\\'), token.LastIndexOf('/'));
        var name = slash >= 0 ? token[(slash + 1)..] : token;
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        return name.ToLowerInvariant();
    }

    private static List<string> Tokenize(string commandLine)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var quote = '\0';

        foreach (var c in commandLine)
        {
            if (quote != '\0')
            {
                if (c == quote) quote = '\0'; else current.Append(c);
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }
}
