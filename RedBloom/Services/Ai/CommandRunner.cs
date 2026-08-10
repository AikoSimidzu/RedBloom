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

            // chcp first so the pipes carry UTF-8: cmd defaults to the OEM code page, which
            // turns every non-Latin character in the output into rubbish.
            Arguments = $"/c chcp 65001>nul & {command}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
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
        output.Append(await Safe(stdout).ConfigureAwait(false));

        var errors = await Safe(stderr).ConfigureAwait(false);
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
