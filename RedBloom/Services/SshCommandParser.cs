using System.Diagnostics.CodeAnalysis;
using RedBloom.Models;

namespace RedBloom.Services;

/// <summary>
/// Turns a pasted OpenSSH command line into a session, so an invocation someone already has
/// in their notes does not have to be retyped field by field.
/// </summary>
public static class SshCommandParser
{
    /// <summary>ssh options that consume the following argument.</summary>
    private const string OptionsWithValue = "BbcDEeFIiJLlmOopQRSWw";

    public static bool TryParse(string commandLine, [NotNullWhen(true)] out SshSession? session, out string? error)
    {
        session = null;
        error = null;

        var tokens = Tokenize(commandLine);
        if (tokens.Count == 0)
        {
            error = "Nothing to parse.";
            return false;
        }

        if (string.Equals(tokens[0], "ssh", StringComparison.OrdinalIgnoreCase)
            || tokens[0].EndsWith("\\ssh.exe", StringComparison.OrdinalIgnoreCase)
            || tokens[0].EndsWith("/ssh", StringComparison.Ordinal))
        {
            tokens.RemoveAt(0);
        }

        var result = new SshSession { Port = 22 };
        string? target = null;
        var forwards = new List<PortForward>();

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];

            if (token.Length == 0)
            {
                continue;
            }

            if (token[0] != '-' || token.Length == 1)
            {
                // First bare word is [user@]host; anything after it is a remote command.
                target ??= token;
                continue;
            }

            // A cluster like -vvN, possibly ending in a value-taking option such as -Np 22.
            for (var c = 1; c < token.Length; c++)
            {
                var flag = token[c];
                if (OptionsWithValue.IndexOf(flag) < 0)
                {
                    continue;
                }

                // The value is either glued on (-p22) or the next token (-p 22).
                string? value;
                if (c + 1 < token.Length)
                {
                    value = token[(c + 1)..];
                }
                else if (i + 1 < tokens.Count)
                {
                    value = tokens[++i];
                }
                else
                {
                    error = $"Option -{flag} is missing its value.";
                    return false;
                }

                switch (flag)
                {
                    case 'p':
                        if (!int.TryParse(value, out var port) || port is < 1 or > 65535)
                        {
                            error = $"Invalid port: {value}";
                            return false;
                        }

                        result.Port = port;
                        break;

                    case 'l':
                        result.Username = value;
                        break;

                    case 'i':
                        result.PrivateKeyPath = value;
                        result.AuthKind = SshAuthKind.PrivateKey;
                        break;

                    case 'L' or 'R' or 'D':
                        if (!TryParseForward(flag, value, out var forward, out error))
                        {
                            return false;
                        }

                        forwards.Add(forward);
                        break;

                    // -o, -J, -F and friends are accepted and ignored: RedBloom has no
                    // equivalent for them, and refusing the whole paste over one would be worse.
                }

                break; // the rest of the cluster was the value
            }
        }

        if (target is null)
        {
            error = "No host found in the command.";
            return false;
        }

        var at = target.LastIndexOf('@');
        if (at >= 0)
        {
            result.Username = target[..at];
            result.Host = target[(at + 1)..];
        }
        else
        {
            result.Host = target;
        }

        if (string.IsNullOrWhiteSpace(result.Host))
        {
            error = "No host found in the command.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(result.Username))
        {
            result.Username = Environment.UserName;
        }

        result.Name = result.Host;
        result.Forwards = forwards;
        session = result;
        return true;
    }

    /// <summary>
    /// Parses -L/-R "[bind:]port:host:hostport" and -D "[bind:]port". A two-part -L is the
    /// bare "port:host:hostport" form with the bind address left implicit.
    /// </summary>
    private static bool TryParseForward(
        char flag,
        string value,
        [NotNullWhen(true)] out PortForward? forward,
        out string? error)
    {
        forward = null;
        error = null;

        var parts = value.Split(':');
        var kind = flag switch
        {
            'R' => PortForwardKind.Remote,
            'D' => PortForwardKind.Dynamic,
            _ => PortForwardKind.Local,
        };

        if (kind == PortForwardKind.Dynamic)
        {
            var (bind, portText) = parts.Length switch
            {
                1 => ("127.0.0.1", parts[0]),
                2 => (parts[0], parts[1]),
                _ => (null, null),
            };

            if (portText is null || !int.TryParse(portText, out var socksPort) || socksPort is < 1 or > 65535)
            {
                error = $"Could not read -D {value}";
                return false;
            }

            forward = new PortForward
            {
                Kind = kind,
                BoundHost = NormalizeBind(bind!),
                BoundPort = socksPort,
            };
            return true;
        }

        string bindHost;
        string boundText;
        string destinationHost;
        string destinationText;

        switch (parts.Length)
        {
            case 3:
                bindHost = "127.0.0.1";
                boundText = parts[0];
                destinationHost = parts[1];
                destinationText = parts[2];
                break;

            case 4:
                bindHost = parts[0];
                boundText = parts[1];
                destinationHost = parts[2];
                destinationText = parts[3];
                break;

            default:
                error = $"Could not read -{flag} {value}";
                return false;
        }

        if (!int.TryParse(boundText, out var listenPort) || listenPort is < 1 or > 65535
            || !int.TryParse(destinationText, out var targetPort) || targetPort is < 1 or > 65535)
        {
            error = $"Could not read -{flag} {value}";
            return false;
        }

        forward = new PortForward
        {
            Kind = kind,
            BoundHost = NormalizeBind(bindHost),
            BoundPort = listenPort,
            DestinationHost = destinationHost,
            DestinationPort = targetPort,
        };
        return true;
    }

    /// <summary>ssh spells "all interfaces" as an empty bind address or '*'.</summary>
    private static string NormalizeBind(string bind) =>
        bind.Length == 0 || bind == "*" ? "0.0.0.0" : bind;

    private static List<string> Tokenize(string commandLine)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var quote = '\0';

        foreach (var c in commandLine)
        {
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
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
