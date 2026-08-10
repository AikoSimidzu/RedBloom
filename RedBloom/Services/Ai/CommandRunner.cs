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

    public static async Task<string> RunAsync(string command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return "The command was empty.";
        }

        var start = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Arguments = $"/c {command}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,

            // Read as bytes and decide afterwards. Which encoding comes back is not knowable in
            // advance: cmd's own messages arrive in the OEM code page, while most modern tools
            // write UTF-8 whatever the console is set to.
            StandardOutputEncoding = Encoding.Latin1,
            StandardErrorEncoding = Encoding.Latin1,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };

        using var process = new Process { StartInfo = start };

        try
        {
            if (!process.Start())
            {
                return "The command could not be started.";
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return $"The command could not be started: {ex.Message}";
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout);

        var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var stderr = process.StandardError.ReadToEndAsync(deadline.Token);

        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);

            return cancellationToken.IsCancellationRequested
                ? "The command was interrupted."
                : $"The command was still running after {Timeout.TotalMinutes:0} minutes and was stopped.";
        }

        var output = new StringBuilder();
        output.Append(Decode(await Safe(stdout).ConfigureAwait(false)));

        var errors = Decode(await Safe(stderr).ConfigureAwait(false));
        if (errors.Length > 0)
        {
            output.Append(output.Length > 0 ? "\n" : string.Empty).Append(errors);
        }

        // The exit code is worth stating outright: a failing command often prints nothing at
        // all, and silence reads to the model as success.
        var text = output.ToString().TrimEnd();
        var result = process.ExitCode == 0
            ? text.Length > 0 ? text : "(the command printed nothing; it exited normally)"
            : $"{text}\n(exit code {process.ExitCode})".TrimStart();

        return result.Length > MaxOutput
            ? result[..MaxOutput] + $"\n(output cut after {MaxOutput} characters)"
            : result;
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
}
