using System.IO;
using System.Net.Sockets;
using Renci.SshNet;
using Renci.SshNet.Common;
using RedBloom.Models;

namespace RedBloom.Terminal;

/// <summary>
/// One authenticated SSH connection, shared by every shell opened on it. Ctrl+Alt splits open
/// another shell channel on the same connection rather than logging in again, so this holds the
/// <see cref="SshClient"/> and the tunnels, and hands out shells against a reference count: the
/// connection tears down only once the last shell using it is gone.
/// </summary>
public sealed class SshConnection : IDisposable
{
    private readonly SshSession _session;
    private readonly List<ForwardedPort> _forwards = [];
    private SshClient _client;
    private int _shellCount;
    private bool _disposed;

    private SshConnection(SshSession session, SshClient client)
    {
        _session = session;
        _client = client;
    }

    /// <summary>Host-side notices — tunnel problems and the like — for a shell to surface.</summary>
    public event Action<string>? Notice;

    /// <summary>Raised when the whole connection drops, so every shell on it can report the loss.</summary>
    public event Action<string>? ConnectionLost;

    public SshSession Session => _session;

    public bool IsConnected => !_disposed && _client.IsConnected;

    /// <summary>
    /// Connects and authenticates, verifying the host key, then starts the session's tunnels.
    /// </summary>
    public static async Task<SshConnection> EstablishAsync(
        SshSession session,
        string? secret,
        Func<SshHostKey, bool> isTrusted,
        Func<SshHostKey, Task<bool>> approveAsync,
        CancellationToken cancellationToken)
    {
        var client = await ConnectAsync(session, secret, isTrusted, approveAsync, cancellationToken)
            .ConfigureAwait(false);

        var connection = new SshConnection(session, client);
        client.ErrorOccurred += connection.OnClientError;
        connection.StartForwards();
        return connection;
    }

    /// <summary>
    /// Runs one command on the far machine and returns what it printed, with its exit code.
    /// </summary>
    /// <remarks>
    /// A command channel rather than the interactive shell: a shell echoes the prompt, paints
    /// the banner and edits the line, so reading a command's own output back out of it means
    /// guessing where one ends and the next begins. This gets the output and nothing else.
    /// </remarks>
    public async Task<(int ExitCode, string Output)> RunAsync(
        string command, CancellationToken cancellationToken = default)
    {
        using var channel = _client.CreateCommand(command);

        var text = await Task.Factory.FromAsync(
            channel.BeginExecute(), channel.EndExecute).WaitAsync(cancellationToken).ConfigureAwait(false);

        var complaint = channel.Error;

        return (channel.ExitStatus ?? -1,
            (text + (complaint.Length > 0 ? "\n" + complaint : string.Empty)).TrimEnd());
    }

    /// <summary>Opens another shell channel on this connection.</summary>
    public ShellStream OpenShell(int columns, int rows)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Interlocked.Increment(ref _shellCount);

        // Width/height in pixels are advertised as 0, so the server derives geometry from the
        // character cell counts instead.
        return _client.CreateShellStream(
            terminalName: "xterm-256color",
            columns: (uint)Math.Max(1, columns),
            rows: (uint)Math.Max(1, rows),
            width: 0,
            height: 0,
            bufferSize: 8192);
    }

    /// <summary>Disposes a shell and, when it was the last one, the whole connection.</summary>
    public void ReleaseShell(ShellStream? stream)
    {
        try
        {
            stream?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SshException)
        {
            // Already gone with the connection.
        }

        if (Interlocked.Decrement(ref _shellCount) <= 0)
        {
            Dispose();
        }
    }

    private void OnClientError(object? sender, ExceptionEventArgs e) =>
        ConnectionLost?.Invoke($"SSH error: {e.Exception.Message}");

    private void StartForwards()
    {
        foreach (var forward in _session.Forwards)
        {
            if (!forward.IsValid)
            {
                Notice?.Invoke($"Skipped malformed tunnel: {forward.Display}");
                continue;
            }

            try
            {
                ForwardedPort port = forward.Kind switch
                {
                    PortForwardKind.Dynamic =>
                        new ForwardedPortDynamic(forward.BoundHost, (uint)forward.BoundPort),
                    PortForwardKind.Remote =>
                        new ForwardedPortRemote(
                            forward.BoundHost, (uint)forward.BoundPort,
                            forward.DestinationHost, (uint)forward.DestinationPort),
                    _ =>
                        new ForwardedPortLocal(
                            forward.BoundHost, (uint)forward.BoundPort,
                            forward.DestinationHost, (uint)forward.DestinationPort),
                };

                var described = forward.Display;
                port.Exception += (_, e) => Notice?.Invoke($"Tunnel {described}: {e.Exception.Message}");

                _client.AddForwardedPort(port);
                port.Start();
                _forwards.Add(port);

                if (forward.Kind == PortForwardKind.Local)
                {
                    _ = Task.Run(() => VerifyLocalForwardAsync(forward));
                }
            }
            catch (Exception ex) when (ex is SshException or SocketException or ObjectDisposedException)
            {
                Notice?.Invoke($"Could not open tunnel {forward.Display}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Makes one connection through a freshly opened tunnel to find out whether it carries
    /// traffic, since SSH.NET never surfaces a channel the server refuses.
    /// </summary>
    private async Task VerifyLocalForwardAsync(PortForward forward)
    {
        var probeHost = forward.BoundHost is "0.0.0.0" or "*" or "" ? "127.0.0.1" : forward.BoundHost;

        try
        {
            using var probe = new TcpClient();
            await probe.ConnectAsync(probeHost, forward.BoundPort)
                .WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);

            await using var stream = probe.GetStream();
            var buffer = new byte[1];

            var read = await stream.ReadAsync(buffer)
                .AsTask()
                .WaitAsync(TimeSpan.FromMilliseconds(1500))
                .ConfigureAwait(false);

            if (read == 0 && !_disposed)
            {
                Notice?.Invoke($"Tunnel {forward.Display} is not carrying traffic — the server may forbid "
                               + "forwarding (AllowTcpForwarding no), or nothing is listening on "
                               + $"{forward.DestinationHost}:{forward.DestinationPort}.");
            }
        }
        catch (TimeoutException)
        {
            // Held open past the read timeout, so the tunnel is working.
        }
        catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException)
        {
            if (!_disposed)
            {
                Notice?.Invoke($"Tunnel {forward.Display} could not be verified: {ex.Message}");
            }
        }
    }

    // ---- connecting ----

    private static async Task<SshClient> ConnectAsync(
        SshSession session,
        string? secret,
        Func<SshHostKey, bool> isTrusted,
        Func<SshHostKey, Task<bool>> approveAsync,
        CancellationToken cancellationToken)
    {
        SshHostKey? unverified = null;

        try
        {
            return await AttemptAsync(
                session, secret,
                key =>
                {
                    if (isTrusted(key))
                    {
                        return true;
                    }

                    unverified = key;
                    return false;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (unverified is not null)
        {
            // Expected: the key was refused on purpose to get out of the live handshake.
        }

        var approved = await approveAsync(unverified!).ConfigureAwait(false);
        if (!approved)
        {
            throw new SshConnectionException(
                $"Connection to {session.Host} refused: the server's host key was not trusted.");
        }

        var approvedFingerprint = SshHostKey.NormalizeFingerprint(unverified!.Sha256Fingerprint);

        return await AttemptAsync(
            session, secret,
            key => string.Equals(
                SshHostKey.NormalizeFingerprint(key.Sha256Fingerprint),
                approvedFingerprint,
                StringComparison.Ordinal),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<SshClient> AttemptAsync(
        SshSession session,
        string? secret,
        Func<SshHostKey, bool> trust,
        CancellationToken cancellationToken)
    {
        var client = new SshClient(BuildConnectionInfo(session, secret));

        void OnHostKey(object? sender, HostKeyEventArgs e)
        {
            var key = new SshHostKey(
                session.Host, ResolvePort(session), e.HostKeyName, e.FingerPrintSHA256, e.KeyLength);

            try
            {
                e.CanTrust = trust(key);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Host key check failed: {ex}");
                e.CanTrust = false;
            }
        }

        client.HostKeyReceived += OnHostKey;

        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            client.HostKeyReceived -= OnHostKey;
            client.Dispose();
            throw;
        }

        client.HostKeyReceived -= OnHostKey;
        return client;
    }

    private static int ResolvePort(SshSession session) =>
        session.Port is > 0 and <= 65535 ? session.Port : 22;

    private static ConnectionInfo BuildConnectionInfo(SshSession session, string? secret)
    {
        var host = session.Host;
        var user = session.Username;

        if (string.IsNullOrWhiteSpace(host))
        {
            throw new InvalidOperationException("The session has no host.");
        }

        if (string.IsNullOrWhiteSpace(user))
        {
            throw new InvalidOperationException("The session has no username.");
        }

        AuthenticationMethod method;
        if (session.AuthKind == SshAuthKind.PrivateKey)
        {
            var keyPath = session.PrivateKeyPath;
            if (string.IsNullOrWhiteSpace(keyPath) || !File.Exists(keyPath))
            {
                throw new FileNotFoundException($"Private key not found: {keyPath}");
            }

            var keyFile = string.IsNullOrEmpty(secret)
                ? new PrivateKeyFile(keyPath)
                : new PrivateKeyFile(keyPath, secret);
            method = new PrivateKeyAuthenticationMethod(user, keyFile);
        }
        else
        {
            method = new PasswordAuthenticationMethod(user, secret ?? string.Empty);
        }

        return new ConnectionInfo(host, ResolvePort(session), user, method)
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Stop tunnels before the client goes away, or their listeners can outlive the session
        // and keep the local ports bound.
        foreach (var forward in _forwards)
        {
            try
            {
                if (forward.IsStarted)
                {
                    forward.Stop();
                }

                forward.Dispose();
            }
            catch (Exception ex) when (ex is SshException or SocketException or ObjectDisposedException)
            {
                // Already torn down with the connection.
            }
        }

        _forwards.Clear();

        _client.ErrorOccurred -= OnClientError;
        _client.Dispose();
    }
}
