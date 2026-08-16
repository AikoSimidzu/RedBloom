using System.Diagnostics;
using System.IO;
using System.Text;

namespace RedBloom.Services.Ai;

/// <summary>Runs one shell command for an agent and hands back what it printed.</summary>
/// <remarks>
/// Deliberately not the tab's own terminal: an agent's command must not be able to type into a
/// session the user is in the middle of, and its output has to come back as a value rather than
/// as pixels. So each command is its own short-lived <c>cmd.exe</c> with its pipes captured.
/// </remarks>
public static class CommandRunner
{
    /// <summary>Long enough for a build, short enough that a hung command does not wedge the turn.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Output past this is cut. A command that prints a whole file would otherwise fill the
    /// model's context with one result and leave no room for the work it was gathered for.
    /// </summary>
    private const int MaxOutput = 20000;

    /// <summary>A command's output together with the directory the shell ended up in.</summary>
    public readonly record struct RunResult(string Output, string Cwd);

    // Markers a tracked run prints so the ending directory and exit code can be read back. Printed
    // with delayed expansion on, so !CD! is the directory the command LEFT the shell in — a
    // parse-time %CD% would give the one it started in — and !ERRORLEVEL! is the command's own
    // code, which the appended echo would otherwise mask with its success.
    private const string CwdMarker = "__RBCWD__";
    private const string ExitMarker = "__RBX__";

    /// <summary>
    /// Runs a command in a chat's own working directory and reports both its output and the
    /// directory the shell ended in — so a <c>cd</c> inside the command carries to the next call.
    /// </summary>
    public static async Task<RunResult> RunInAsync(string command, string workingDir, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return new RunResult("The command was empty.", workingDir);
        }

        var wrapped = $"{command} & echo {CwdMarker}!CD!{ExitMarker}!ERRORLEVEL!";
        var (raw, _) = await ExecuteAsync(wrapped, workingDir, delayed: true, cancellationToken).ConfigureAwait(false);

        var cwd = workingDir;
        var exit = 0;
        var at = raw.LastIndexOf(CwdMarker, StringComparison.Ordinal);

        if (at >= 0)
        {
            var end = raw.IndexOf('\n', at);
            var line = end < 0 ? raw[at..] : raw[at..end];
            var body = line[CwdMarker.Length..];
            var split = body.LastIndexOf(ExitMarker, StringComparison.Ordinal);

            if (split >= 0)
            {
                cwd = body[..split].Trim();
                int.TryParse(body[(split + ExitMarker.Length)..].Trim(), out exit);
            }
            else
            {
                cwd = body.Trim();
            }

            // Cut the marker line out of what the model sees, the newline before it included.
            var before = at > 0 ? raw.LastIndexOf('\n', at - 1) : -1;
            raw = before >= 0 ? raw[..before] : string.Empty;
        }

        if (cwd.Length == 0 || !Directory.Exists(cwd))
        {
            cwd = workingDir;
        }

        return new RunResult(Finish(raw.TrimEnd(), exit), cwd);
    }

    public static async Task<string> RunAsync(string command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return "The command was empty.";
        }

        var (raw, exit) = await ExecuteAsync(
            command,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            delayed: false,
            cancellationToken).ConfigureAwait(false);

        return Finish(raw, exit);
    }

    /// <summary>States the exit code and caps the length — what the model actually reads.</summary>
    private static string Finish(string text, int exitCode)
    {
        // The exit code is worth stating outright: a failing command often prints nothing at all,
        // and silence reads to the model as success.
        var result = exitCode == 0
            ? text.Length > 0 ? text : "(the command printed nothing; it exited normally)"
            : $"{text}\n(exit code {exitCode})".TrimStart();

        return result.Length > MaxOutput
            ? result[..MaxOutput] + $"\n(output cut after {MaxOutput} characters)"
            : result;
    }

    private static async Task<(string Text, int ExitCode)> ExecuteAsync(
        string command, string workingDir, bool delayed, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workingDir) || !Directory.Exists(workingDir))
        {
            workingDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        // Detect GUI applications and launch them asynchronously without waiting
        if (IsGuiApplication(command))
        {
            return await LaunchGuiApplicationAsync(command, workingDir, cancellationToken).ConfigureAwait(false);
        }

        var start = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Arguments = delayed ? $"/v:on /c {command}" : $"/c {command}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,

            // Read as bytes and decide afterwards. Which encoding comes back is not knowable in
            // advance: cmd's own messages arrive in the OEM code page, while most modern tools
            // write UTF-8 whatever the console is set to.
            StandardOutputEncoding = Encoding.Latin1,
            StandardErrorEncoding = Encoding.Latin1,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDir,
        };

        using var process = new Process { StartInfo = start };

        try
        {
            if (!process.Start())
            {
                return ("The command could not be started.", 0);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return ($"The command could not be started: {ex.Message}", 0);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout);

        // Read output with early detection of "silent but alive" processes
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();
        var outputTask = ReadStreamAsync(process.StandardOutput, outputBuilder, deadline.Token);
        var errorTask = ReadStreamAsync(process.StandardError, errorBuilder, deadline.Token);

        // Wait up to 3 seconds for process to either exit or produce output
        var silenceThreshold = TimeSpan.FromSeconds(3);
        var started = DateTime.UtcNow;
        var hasOutput = false;

        while (!process.HasExited)
        {
            // Check if we have any output
            if (outputBuilder.Length > 0 || errorBuilder.Length > 0)
            {
                hasOutput = true;
                break;
            }

            // If process is silent for 3 seconds, assume it's a GUI app
            if (!hasOutput && DateTime.UtcNow - started > silenceThreshold)
            {
                // Let it run in background
                return ("Application launched and running in background.", 0);
            }

            try
            {
                await Task.Delay(100, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        // Process either exited or produced output - wait for completion
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);

            return (cancellationToken.IsCancellationRequested
                ? "The command was interrupted."
                : $"The command was still running after {Timeout.TotalMinutes:0} minutes and was stopped.", 0);
        }

        // Finish reading streams
        await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);

        var output = new StringBuilder();
        output.Append(Decode(outputBuilder.ToString()));

        var errors = Decode(errorBuilder.ToString());
        if (errors.Length > 0)
        {
            output.Append(output.Length > 0 ? "\n" : string.Empty).Append(errors);
        }

        return (output.ToString().TrimEnd(), process.ExitCode);
    }

    /// <summary>
    /// Reads from a stream into a StringBuilder for monitoring output.
    /// </summary>
    private static async Task ReadStreamAsync(StreamReader reader, StringBuilder output, CancellationToken cancellationToken)
    {
        try
        {
            var buffer = new char[4096];
            int read;
            while ((read = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                output.Append(buffer, 0, read);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // Stream closed or cancelled
        }
    }

    /// <summary>
    /// The console code page, for output that is not UTF-8.
    /// </summary>
    /// <remarks>
    /// A child started without a console still writes in the OEM code page — 866 on a Russian
    /// Windows — and the usual <c>chcp 65001</c> cannot change that, because there is no console
    /// for it to change. So the code page is read here instead and used to decode what comes back.
    /// </remarks>
    private static readonly Lazy<Encoding> Oem = new(() =>
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            return Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            // No such code page on this machine; Latin-1 at least keeps every byte distinct.
            return Encoding.Latin1;
        }
    });

    /// <summary>
    /// Turns what a command printed into text, in whichever of the two encodings it used.
    /// </summary>
    /// <remarks>
    /// UTF-8 first, strictly: its multi-byte sequences are structured enough that text in a
    /// single-byte code page almost never passes for valid UTF-8, so a failure to decode is a
    /// reliable sign of the other case rather than a coin toss. Pure ASCII — most output — reads
    /// the same either way.
    /// </remarks>
    private static string Decode(string raw)
    {
        // Latin-1 was asked for above precisely so that this round-trip returns the original
        // bytes: every byte maps to the code point of the same value and back.
        var bytes = Encoding.Latin1.GetBytes(raw);

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Oem.Value.GetString(bytes);
        }
    }

    private static async Task<string> Safe(Task<string> read)
    {
        try
        {
            return await read.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            return string.Empty;
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone between the check and the kill; nothing left to stop.
        }
    }

    /// <summary>
    /// Detects if a command is launching a GUI application that should run asynchronously.
    /// </summary>
    private static bool IsGuiApplication(string command)
    {
        var trimmed = command.Trim();
        var lower = trimmed.ToLowerInvariant();
        
        // Exclude commands that redirect output or pipe, as those are meant to be captured
        if (lower.Contains(">") || lower.Contains("|"))
        {
            return false;
        }

        // Check if command explicitly uses 'start' command which is meant for GUI apps
        if (lower.StartsWith("start "))
        {
            return true;
        }

        // Known GUI applications (executable name only, not path-based)
        var guiApps = new[]
        {
            "notepad.exe",
            "notepad",
            "mspaint.exe",
            "calc.exe",
            "explorer.exe",
            "code.exe",
            "code",
            "devenv.exe",
            "winword.exe",
            "excel.exe",
            "powerpnt.exe",
            "chrome.exe",
            "firefox.exe",
            "msedge.exe",
            "iexplore.exe",
        };

        // Extract the executable name from the command (handle quoted paths)
        var executableName = ExtractExecutableName(trimmed);
        
        foreach (var app in guiApps)
        {
            if (executableName.Equals(app, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Extracts the executable name from a command line, handling quotes and paths.
    /// </summary>
    private static string ExtractExecutableName(string command)
    {
        var trimmed = command.Trim();
        
        // Handle quoted paths
        if (trimmed.StartsWith("\""))
        {
            var endQuote = trimmed.IndexOf("\"", 1, StringComparison.Ordinal);
            if (endQuote > 0)
            {
                trimmed = trimmed[1..endQuote];
            }
        }
        else
        {
            // Take first token before space
            var spaceIndex = trimmed.IndexOf(' ');
            if (spaceIndex > 0)
            {
                trimmed = trimmed[..spaceIndex];
            }
        }

        // Get just the filename without path
        var lastSlash = Math.Max(trimmed.LastIndexOf('\\'), trimmed.LastIndexOf('/'));
        if (lastSlash >= 0)
        {
            trimmed = trimmed[(lastSlash + 1)..];
        }

        return trimmed.ToLowerInvariant();
    }

    /// <summary>
    /// Launches a GUI application asynchronously without waiting for it to close.
    /// </summary>
    private static async Task<(string Text, int ExitCode)> LaunchGuiApplicationAsync(
        string command, string workingDir, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Arguments = $"/c {command}",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDir,
        };

        try
        {
            using var process = new Process { StartInfo = start };
            
            if (!process.Start())
            {
                return ("The application could not be started.", 1);
            }

            // Wait a short moment to detect immediate failures (like file not found)
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);

            if (process.HasExited && process.ExitCode != 0)
            {
                return ($"The application failed to start (exit code {process.ExitCode}).", process.ExitCode);
            }

            // Return immediately - the GUI app is now running independently
            return ("Application launched successfully.", 0);
        }
        catch (OperationCanceledException)
        {
            return ("The launch was interrupted.", 1);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return ($"The application could not be started: {ex.Message}", 1);
        }
    }
}
