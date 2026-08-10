using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using RedBloom.Services.Ai;

namespace RedBloom.Services;

/// <summary>
/// An administrator-level worker the running app hands commands to.
/// </summary>
/// <remarks>
/// Windows will not raise the privileges of a process that is already running: a token's
/// elevation is fixed when the process starts, which is why every program that offers to "run as
/// administrator" restarts itself. What can be done without a restart is to start a second copy
/// elevated and have the window that stays open send it work — the app keeps its tabs, its
/// connections and its scrollback, and the privileged part happens next door.
/// <para>
/// The two halves are the same executable: the helper is RedBloom started with
/// <see cref="Switch"/>, which runs <see cref="Serve"/> instead of showing a window. That way the
/// helper cannot drift out of step with the app that drives it, and there is no second binary to
/// sign, ship or keep honest.
/// </para>
/// <para>
/// While a helper is alive, anything that can reach it can run commands as administrator, so the
/// channel is deliberately narrow: one private pipe, one connection, a secret the helper must
/// present before it is spoken to, and a lifetime that ends with the app.
/// </para>
/// </remarks>
public static class ElevatedHost
{
    /// <summary>The command line that turns a copy of the app into the helper.</summary>
    public const string Switch = "--elevated-helper";

    /// <summary>Long enough for someone to find and answer the UAC prompt.</summary>
    private static readonly TimeSpan ConsentTimeout = TimeSpan.FromMinutes(2);

    /// <summary>One command at a time, so two turns cannot interleave on the one pipe.</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static NamedPipeServerStream? _pipe;
    private static StreamReader? _reader;
    private static StreamWriter? _writer;

    /// <summary>True while an elevated helper is connected and taking work.</summary>
    public static bool IsRunning => _pipe is { IsConnected: true };

    /// <summary>Raised when the helper connects or goes away, so the UI can say which it is.</summary>
    public static event Action? StateChanged;

    /// <summary>
    /// Starts the helper, prompting for consent. Returns null when it is ready, or the reason
    /// it is not.
    /// </summary>
    public static async Task<string?> StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning)
        {
            return null;
        }

        var exe = Environment.ProcessPath;

        if (string.IsNullOrEmpty(exe))
        {
            return "The program's own path could not be determined.";
        }

        Stop();

        var name = $"redbloom-elevated-{Guid.NewGuid():n}";
        var secret = Guid.NewGuid().ToString("n");

        // In before out: the helper is started only once there is something for it to connect to.
        var pipe = new NamedPipeServerStream(
            name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        try
        {
            Process.Start(new ProcessStartInfo(exe, $"{Switch} {name} {secret}")
            {
                UseShellExecute = true,
                Verb = "runas", // the UAC prompt
                WorkingDirectory = AppContext.BaseDirectory,
            });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);

            // 1223, "the operation was cancelled by the user" — they said no, which is an answer
            // rather than a fault.
            return "Elevation was declined.";
        }

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(ConsentTimeout);

            await pipe.WaitForConnectionAsync(deadline.Token).ConfigureAwait(false);

            var reader = new StreamReader(pipe, new UTF8Encoding(false));
            var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };

            // The helper says the secret first. Anything else got here by guessing the pipe name,
            // and is shown the door before it is sent a single command.
            var hello = await reader.ReadLineAsync(deadline.Token).ConfigureAwait(false);

            if (hello != $"HELLO {secret}")
            {
                await pipe.DisposeAsync().ConfigureAwait(false);

                return "Something other than the helper answered; nothing was run.";
            }

            _pipe = pipe;
            _reader = reader;
            _writer = writer;

            StateChanged?.Invoke();

            return null;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);

            return cancellationToken.IsCancellationRequested
                ? "Elevation was cancelled."
                : "The elevated helper did not start.";
        }
    }

    /// <summary>
    /// Runs a command as administrator and returns everything it printed.
    /// </summary>
    /// <remarks>
    /// The shape of the answer matches <see cref="CommandRunner"/> exactly, because it is
    /// <see cref="CommandRunner"/> — the helper runs the same code, one privilege level up.
    /// </remarks>
    public static async Task<string> RunAsync(string command, CancellationToken cancellationToken)
    {
        if (!IsRunning || _reader is not { } reader || _writer is not { } writer)
        {
            return "No elevated helper is running.";
        }

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await writer.WriteLineAsync($"RUN {Pack(command)}").ConfigureAwait(false);

            var answer = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

            if (answer is null)
            {
                Stop();

                return "The elevated helper stopped before the command finished.";
            }

            return answer.StartsWith("DONE ", StringComparison.Ordinal)
                ? Unpack(answer[5..])
                : "The elevated helper answered in a shape this version does not understand.";
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Stop();

            return $"The elevated helper could not be reached: {ex.Message}";
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Sends the helper away. It also leaves on its own when this process ends.</summary>
    public static void Stop()
    {
        var pipe = _pipe;
        var writer = _writer;

        _pipe = null;
        _reader = null;
        _writer = null;

        try
        {
            if (pipe is { IsConnected: true })
            {
                writer?.WriteLine("BYE");
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Already gone; there is nobody left to dismiss.
        }

        pipe?.Dispose();

        if (pipe is not null)
        {
            StateChanged?.Invoke();
        }
    }

    /// <summary>
    /// The helper's whole life: connect, prove who it is, then run what it is told until the app
    /// closes the pipe.
    /// </summary>
    /// <remarks>
    /// Runs in the elevated copy of the app, which shows no window at all. It takes commands only
    /// from the pipe it was given on its own command line, and dies with the process at the other
    /// end of it — there is no way to leave an elevated worker behind after the app has gone.
    /// </remarks>
    public static void Serve(string pipeName, string secret)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
            pipe.Connect((int)TimeSpan.FromSeconds(30).TotalMilliseconds);

            using var reader = new StreamReader(pipe, new UTF8Encoding(false));
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };

            writer.WriteLine($"HELLO {secret}");

            while (reader.ReadLine() is { } line)
            {
                if (!line.StartsWith("RUN ", StringComparison.Ordinal))
                {
                    // "BYE", or anything unrecognised: either way there is no more work.
                    return;
                }

                var command = Unpack(line[4..]);
                var output = CommandRunner.RunAsync(command, CancellationToken.None).GetAwaiter().GetResult();

                writer.WriteLine($"DONE {Pack(output)}");
            }
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Elevated helper stopping: {ex.Message}");
        }
    }

    /// <summary>
    /// Base64 so a command or its output can hold newlines without ending the line that carries
    /// it. The protocol is line-based; the payloads are not.
    /// </summary>
    private static string Pack(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private static string Unpack(string packed)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(packed));
        }
        catch (FormatException)
        {
            return string.Empty;
        }
    }
}
