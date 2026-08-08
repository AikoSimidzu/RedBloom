using System.IO;
using System.Net.Sockets;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;
using RedBloom.Models;

namespace RedBloom.Terminal;

/// <summary>Runs an interactive remote shell over SSH.</summary>
public sealed class SshBackend : ITerminalBackend
{
    private readonly SshSession _session;
    private readonly string? _secret;
    private readonly Func<SshHostKey, bool> _isTrusted;
    private readonly Func<SshHostKey, Task<bool>> _approveAsync;
    private readonly Lock _writeLock = new();
    private readonly List<ForwardedPort> _forwards = [];

    private SshClient? _client;
    private ShellStream? _stream;
    private int _closedRaised;
    private bool _disposed;

    /// <param name="secret">
    /// Decrypted password or key passphrase. Taken as a parameter rather than read from the
    /// session so a prompt-once flow can pass a secret that was never persisted.
    /// </param>
    /// <param name="isTrusted">
    /// Fast, non-blocking check against the known-hosts record. Called during the handshake,
    /// so it must not wait on anything.
    /// </param>
    /// <param name="approveAsync">
    /// Asks the user about a key that <paramref name="isTrusted"/> rejected. Called with no
    /// handshake in flight, so it may take as long as the user needs.
    /// </param>
    public SshBackend(
        SshSession session,
        string? secret,
        Func<SshHostKey, bool> isTrusted,
        Func<SshHostKey, Task<bool>> approveAsync)
    {
        _session = session;
        _secret = secret;
        _isTrusted = isTrusted;
        _approveAsync = approveAsync;
    }

    public event Action<string>? Output;
    public event Action<string>? Closed;

    public bool IsRunning => !_disposed && _client?.IsConnected == true;

    public async Task StartAsync(int columns, int rows, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var client = await ConnectAsync(cancellationToken).ConfigureAwait(false);

        if (_disposed)
        {
            client.Dispose();
            return;
        }

        _client = client;

        // Width/height in pixels are advertised as 0, which tells the server to derive the
        // terminal geometry from the character cell counts instead.
        _stream = client.CreateShellStream(
            terminalName: "xterm-256color",
            columns: (uint)Math.Max(1, columns),
            rows: (uint)Math.Max(1, rows),
            width: 0,
            height: 0,
            bufferSize: 8192);

        _stream.Closed += OnStreamClosed;
        _stream.ErrorOccurred += OnStreamError;

        _ = Task.Run(PumpOutputAsync, CancellationToken.None);

        StartForwards(client);
    }

    /// <summary>
    /// Opens the session's tunnels. A tunnel that cannot start — most often because the local
    /// port is already taken — is reported but does not take the shell down with it, matching
    /// what ssh does.
    /// </summary>
    private void StartForwards(SshClient client)
    {
        foreach (var forward in _session.Forwards)
        {
            if (!forward.IsValid)
            {
                Notice($"Skipped malformed tunnel: {forward.Display}");
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
                            forward.BoundHost,
                            (uint)forward.BoundPort,
                            forward.DestinationHost,
                            (uint)forward.DestinationPort),
                    _ =>
                        new ForwardedPortLocal(
                            forward.BoundHost,
                            (uint)forward.BoundPort,
                            forward.DestinationHost,
                            (uint)forward.DestinationPort),
                };

                var described = forward.Display;
                port.Exception += (_, e) =>
                    Notice($"Tunnel {described}: {e.Exception.Message}");

                client.AddForwardedPort(port);
                port.Start();
                _forwards.Add(port);

                if (forward.Kind == PortForwardKind.Local)
                {
                    _ = Task.Run(() => VerifyLocalForwardAsync(forward));
                }
            }
            catch (Exception ex) when (ex is SshException or SocketException or ObjectDisposedException)
            {
                Notice($"Could not open tunnel {forward.Display}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Connects in two phases so that a person reading a fingerprint can never run into the
    /// handshake timeout: the first attempt accepts only keys already on record and aborts
    /// immediately on anything else, and only then — with nothing connected — is the user asked.
    /// </summary>
    private async Task<SshClient> ConnectAsync(CancellationToken cancellationToken)
    {
        SshHostKey? unverified = null;

        try
        {
            return await AttemptAsync(
                key =>
                {
                    if (_isTrusted(key))
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
            // Expected: we refused the key on purpose to get out of the handshake.
        }

        var approved = await _approveAsync(unverified!).ConfigureAwait(false);
        if (!approved)
        {
            throw new SshConnectionException(
                $"Connection to {_session.Host} refused: the server's host key was not trusted.");
        }

        // Reconnect, accepting only the exact key the user just approved. Comparing again
        // rather than trusting blindly closes the gap between the two handshakes.
        var approvedFingerprint = SshHostKey.NormalizeFingerprint(unverified!.Sha256Fingerprint);

        return await AttemptAsync(
            key => string.Equals(
                SshHostKey.NormalizeFingerprint(key.Sha256Fingerprint),
                approvedFingerprint,
                StringComparison.Ordinal),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<SshClient> AttemptAsync(Func<SshHostKey, bool> trust, CancellationToken cancellationToken)
    {
        var client = new SshClient(BuildConnectionInfo());

        void OnHostKey(object? sender, HostKeyEventArgs e)
        {
            var key = new SshHostKey(
                _session.Host,
                ResolvePort(),
                e.HostKeyName,
                e.FingerPrintSHA256,
                e.KeyLength);

            try
            {
                e.CanTrust = trust(key);
            }
            catch (Exception ex)
            {
                // Failing to establish trust is not a reason to proceed without it.
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
        client.ErrorOccurred += OnClientError;
        return client;
    }

    /// <summary>
    /// Makes one connection through a freshly opened tunnel to find out whether it actually
    /// carries traffic.
    /// </summary>
    /// <remarks>
    /// SSH.NET never surfaces a refused channel: when a server answers "administratively
    /// prohibited" it just closes the accepted socket, so without this probe the user is left
    /// with a port that listens and silently swallows everything. A live tunnel either sends a
    /// banner or waits for the client to speak first — both leave the socket open — whereas a
    /// refused one is closed at once, and that is the difference this looks for.
    /// </remarks>
    private async Task VerifyLocalForwardAsync(PortForward forward)
    {
        // 0.0.0.0 means "every interface", which is not an address we can dial.
        var probeHost = forward.BoundHost is "0.0.0.0" or "*" or "" ? "127.0.0.1" : forward.BoundHost;

        try
        {
            using var probe = new TcpClient();
            await probe.ConnectAsync(probeHost, forward.BoundPort)
                .WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);

            await using var stream = probe.GetStream();
            var buffer = new byte[1];

            // Reaching the timeout is the good outcome: the connection is still standing.
            var read = await stream.ReadAsync(buffer)
                .AsTask()
                .WaitAsync(TimeSpan.FromMilliseconds(1500))
                .ConfigureAwait(false);

            if (read == 0 && !_disposed)
            {
                Notice($"Tunnel {forward.Display} is not carrying traffic — the server may forbid "
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
                Notice($"Tunnel {forward.Display} could not be verified: {ex.Message}");
            }
        }
    }

    public void Write(string data)
    {
        if (_disposed || _stream is null || string.IsNullOrEmpty(data))
        {
            return;
        }

        try
        {
            var bytes = Encoding.UTF8.GetBytes(data);
            lock (_writeLock)
            {
                _stream.Write(bytes, 0, bytes.Length);
                _stream.Flush();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SshException)
        {
            RaiseClosed($"Connection to {_session.Host} lost.");
        }
    }

    public void Resize(int columns, int rows)
    {
        if (_disposed || _stream is null)
        {
            return;
        }

        try
        {
            _stream.ChangeWindowSize((uint)Math.Max(1, columns), (uint)Math.Max(1, rows), 0, 0);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SshException)
        {
            // A resize racing a disconnect is not worth surfacing; the pump reports the drop.
        }
    }

    private int ResolvePort() => _session.Port is > 0 and <= 65535 ? _session.Port : 22;

    private ConnectionInfo BuildConnectionInfo()
    {
        var host = _session.Host;
        var user = _session.Username;

        if (string.IsNullOrWhiteSpace(host))
        {
            throw new InvalidOperationException("The session has no host.");
        }

        if (string.IsNullOrWhiteSpace(user))
        {
            throw new InvalidOperationException("The session has no username.");
        }

        AuthenticationMethod method;
        if (_session.AuthKind == SshAuthKind.PrivateKey)
        {
            var keyPath = _session.PrivateKeyPath;
            if (string.IsNullOrWhiteSpace(keyPath) || !File.Exists(keyPath))
            {
                throw new FileNotFoundException($"Private key not found: {keyPath}");
            }

            var keyFile = string.IsNullOrEmpty(_secret)
                ? new PrivateKeyFile(keyPath)
                : new PrivateKeyFile(keyPath, _secret);
            method = new PrivateKeyAuthenticationMethod(user, keyFile);
        }
        else
        {
            method = new PasswordAuthenticationMethod(user, _secret ?? string.Empty);
        }

        return new ConnectionInfo(host, ResolvePort(), user, method)
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
    }

    private async Task PumpOutputAsync()
    {
        var stream = _stream;
        if (stream is null)
        {
            return;
        }

        var buffer = new byte[8192];
        var decoder = Encoding.UTF8.GetDecoder();
        var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];

        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                var decoded = decoder.GetChars(buffer, 0, read, chars, 0);
                if (decoded > 0)
                {
                    Output?.Invoke(new string(chars, 0, decoded));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SshException)
        {
            // Normal on disconnect or teardown.
        }

        RaiseClosed($"Disconnected from {_session.Host}.");
    }

    /// <summary>
    /// Writes a host-side notice into the terminal, dim red and on its own line, so it can
    /// never be mistaken for output from the remote shell.
    /// </summary>
    private void Notice(string text) =>
        Output?.Invoke($"\r\n\u001b[38;2;255;123;133m{text}\u001b[0m\r\n");

    private void OnStreamClosed(object? sender, EventArgs e) =>
        RaiseClosed($"Disconnected from {_session.Host}.");

    private void OnStreamError(object? sender, ExceptionEventArgs e) =>
        RaiseClosed($"SSH error: {e.Exception.Message}");

    private void OnClientError(object? sender, ExceptionEventArgs e) =>
        RaiseClosed($"SSH error: {e.Exception.Message}");

    private void RaiseClosed(string reason)
    {
        if (Interlocked.Exchange(ref _closedRaised, 1) == 0)
        {
            Closed?.Invoke(reason);
        }
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

        if (_stream is not null)
        {
            _stream.Closed -= OnStreamClosed;
            _stream.ErrorOccurred -= OnStreamError;
            _stream.Dispose();
            _stream = null;
        }

        if (_client is not null)
        {
            _client.ErrorOccurred -= OnClientError;
            _client.Dispose();
            _client = null;
        }

        RaiseClosed($"Disconnected from {_session.Host}.");
    }
}
