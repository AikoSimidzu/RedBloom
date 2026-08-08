namespace RedBloom.Terminal;

/// <summary>
/// A live terminal session the UI can pump bytes through, regardless of whether the
/// other end is a local ConPTY child process or a remote SSH shell.
/// </summary>
public interface ITerminalBackend : IDisposable
{
    /// <summary>Decoded output from the far end, ready to hand to xterm.js.</summary>
    event Action<string>? Output;

    /// <summary>Raised once when the far end goes away. Argument is a human-readable reason.</summary>
    event Action<string>? Closed;

    bool IsRunning { get; }

    /// <summary>Connects and starts pumping. Throws if the session cannot be established.</summary>
    Task StartAsync(int columns, int rows, CancellationToken cancellationToken = default);

    /// <summary>Sends user input to the far end.</summary>
    void Write(string data);

    /// <summary>Tells the far end the viewport changed size.</summary>
    void Resize(int columns, int rows);
}
