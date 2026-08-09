using System.IO;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;
using RedBloom.Models;

namespace RedBloom.Terminal;

/// <summary>
/// One interactive shell over SSH. It either establishes its own <see cref="SshConnection"/>
/// (the ordinary case) or attaches to one already open (a Ctrl+Alt split, which shares the
/// login and tunnels), and pumps a single shell channel either way.
/// </summary>
public sealed class SshBackend : ITerminalBackend
{
    private readonly SshSession _session;
    private readonly string? _secret;
    private readonly Func<SshHostKey, bool>? _isTrusted;
    private readonly Func<SshHostKey, Task<bool>>? _approveAsync;
    private readonly Lock _writeLock = new();

    // Set when this shell attaches to a connection someone else owns; then Establish is skipped
    // and the connection is only released, never torn down here.
    private readonly SshConnection? _sharedConnection;

    private SshConnection? _connection;
    private ShellStream? _stream;
    private int _closedRaised;
    private bool _disposed;

    /// <summary>Establishes a new connection and opens the first shell on it.</summary>
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

    /// <summary>Opens another shell on a connection that is already established.</summary>
    public SshBackend(SshConnection sharedConnection)
    {
        _sharedConnection = sharedConnection;
        _session = sharedConnection.Session;
    }

    public event Action<string>? Output;
    public event Action<string>? Closed;

    public bool IsRunning => !_disposed && _connection?.IsConnected == true;

    /// <summary>The connection this shell runs on, so a split can open another shell on it.</summary>
    public SshConnection? Connection => _connection;

    public async Task StartAsync(int columns, int rows, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var connection = _sharedConnection
            ?? await SshConnection.EstablishAsync(
                _session, _secret, _isTrusted!, _approveAsync!, cancellationToken).ConfigureAwait(false);

        if (_disposed)
        {
            // We own the connection only when we made it; a shared one is left for its owner.
            if (_sharedConnection is null)
            {
                connection.Dispose();
            }

            return;
        }

        _connection = connection;
        connection.Notice += OnNotice;
        connection.ConnectionLost += OnConnectionLost;

        _stream = connection.OpenShell(columns, rows);
        _stream.Closed += OnStreamClosed;
        _stream.ErrorOccurred += OnStreamError;

        _ = Task.Run(PumpOutputAsync, CancellationToken.None);
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

    private void OnNotice(string text) =>
        Output?.Invoke($"\r\n[38;2;255;123;133m{text}[0m\r\n");

    private void OnConnectionLost(string reason) => RaiseClosed(reason);

    private void OnStreamClosed(object? sender, EventArgs e) =>
        RaiseClosed($"Disconnected from {_session.Host}.");

    private void OnStreamError(object? sender, ExceptionEventArgs e) =>
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

        if (_stream is not null)
        {
            _stream.Closed -= OnStreamClosed;
            _stream.ErrorOccurred -= OnStreamError;
        }

        if (_connection is not null)
        {
            _connection.Notice -= OnNotice;
            _connection.ConnectionLost -= OnConnectionLost;

            // Releasing the shell decrements the connection's use count and tears the whole
            // connection down once this was the last shell on it — shared or not.
            _connection.ReleaseShell(_stream);
            _connection = null;
        }

        _stream = null;

        RaiseClosed($"Disconnected from {_session.Host}.");
    }
}
